# LEVEL GENERATOR — SPEC

> Verified against code. The faithful native
> algorithm below is IMPLEMENTED — the 1:1 rebuild landed in **43805a0** ("faithful 1:1 rebuild of the
> original world generator") + **88df518**. Port lives in `Assets/Diggles/Runtime/World/LevelGenerator.cs`
> (1583 lines) + the sm_/lg_ bindings in `Assets/Diggles/Runtime/Tcl/Engine/DigglesTclEngine.cs`
> (`CmdSmGetLevelInfo`, `CmdLgSetStartTemplatePro`, `CmdSmDrawStone`). The decompile laws (addresses,
> constants) are RE ground truth — do not edit. **Everything from the `## SCOPING` heading downward is
> the OBSOLETE tier-B "minimal grower" plan, superseded by this section (and now by the shipped
> rebuild); kept for provenance only.** For the ranked port-divergence state see WORLDGEN_ORIGINAL_RE.md.

## ⭐ FAITHFUL NATIVE ALGORITHM

Re-derived end-to-end from the decompile after the manual test. Implemented in
`Assets/Diggles/Runtime/World/LevelGenerator.cs` (each law carries its citation in the file too).

### ⭐ THE TRIGGER / PACING LAW
The Play-side driver law, re-derived after the manual test ("a new cave per dig
swing"). Every step cited; the whole chain runs on the UNCHANGED story.tcl.

1. **Dig pressure** — `CStoryMgr::DigNotify` = FUN_005fd060, called from DigApply
   (FUN_006347a0:103) with the rounded node BEFORE the carve. Gates: ≥8 tiles from every map
   border (:29-32). Every in-bounds dig: fires the sm_register'd story callback (`<cb> dig shut
   -1`,:33-64), CLEARS the latch (:66-70 — only the latest breakthrough survives), marks the
   dug story cell grown (:71-73). Then, gated on the dug tile's mode-1 dummy nibble
   ((n&8)==0 && n!=0,:75-77), it probes the four ±4 neighbours in +x, +y, −x, −y order; the
   FIRST direction whose story cell is un-grown AND whose tile passes the nibble gate bumps the
   weight `+0x98` (+2 ±x, +4 +y=down, +1 −y=up — FUN_005fcfb0:16-25) and returns.
   `weight > digcount(+0x9c)` latches `{dir,x,y}` (+0x88/+0x8c/+0x90, consumed=0).
   digcount: ctor default 5 (FUN_005fa7c0:32), campaign sets **32** (sm_set_digcount,
   CampaignInit.tcl:36; setter FUN_0065c750:233) ⇒ a breakthrough needs ~8 down-digs /
   ~16 side-digs of accumulated pressure.
2. **Consume** — `sm_scan` = FUN_006616d0: no latch / zero coords / already consumed → int 0;
   else returns the /4 cell pair and sets consumed (+0x94=1,:103). sm_scan does **NOT** reset
   the weight.
3. **THE RE-ARM** — `lg_get_level` = FUN_00652d10: `local_30 = FUN_005f8080` (:73) is the
   **StoryMgr singleton** (the same object DigNotify bumps). Inside the per-placement emit loop
   it marks each placement's story rect grown (:276-279, FUN_005fa510) **and zeroes the dig
   weight `*(local_30+0x98) = 0` (:280)**. So a generation that EMITS placements re-arms the
   digcount threshold — the next breakthrough needs a fresh 32 pressure. A FAILED (empty)
   generation skips the loop ⇒ weight survives ⇒ the very next dig re-latches = the story.tcl
   failcount retry channel (:212-222). ⚠️ The old port misread:280 as "clear the objcount
   table" and never reset the weight — after the first breakthrough EVERY dig stroke latched a
   new breakthrough ⇒ a new cave per swing, generations marching away from the dwarf, and
   multi-generation stamp bursts. Fixed in the
   lg_get_level engine binding (reset only when placements were emitted).
4. **Tick cadence** — StoryMgr `state idle` (story.tcl:1030-1133): 5×1.0 s StartCnt warm-up;
   then per tick: `if {[load_next]} { gamedelayannounce 15; return }` — **ONE template stamp
   per tick**, nothing else that tick; only with an EMPTY temp_buffer does it `sm_scan` →
   `generate_next`; every path ends `wait_time 0.5` (story.tcl:863 = state_disable + `action
   this wait` + state_enable — the action system IS the pacer; our WaitAction drives it
   headless too). `load_next` (story.tcl:291-327) pops ONE `{tcl x y}` triple, `call`s the
   template, `MapTemplateSet x y` under a get_ingame_loading guard. So the native NEVER stamps
   a whole level synchronously — a 15-piece level streams in over ~8 s.
5. **New-game reset** — FUN_005fa7c0:26-33 zeroes latch+weight and restores digcount=5; the
   port mirrors it in sm_create_map (CampaignInit re-runs sm_set_digcount 32 two lines later).

Net pacing: dig ~8-16 tiles → ONE breakthrough → ONE generation (one cluster, §grow core) →
its pieces stream one per 0.5 s tick → dig another ~8-16 tiles for the next cluster.

### ⭐ THE STAMP LAW — `map_template` = FUN_005992b0
How a template PMP lands in the world (boot and runtime growth use the same blit):
1. **Transparency** (:292): a source tile whose 2×2 terrain-corner window (this, +x, +y, +x+y;
   OOB corners ignored per:248/:265/:278) is ALL ZERO (&0x7f) skips the per-tile
   material/block/height writes (:292-454), so those pre-existing world fields survive.
   34 shipped PMPs carry such filler (all block 0), e.g. gng_029/030/032 — always the OUTER
   ring, never the bore (grids dumped).
2. **Verbatim copy** for non-transparent tiles: material from the terrain code (:317-377),
   block idx as-is (:379-415 — block 59 = an authored PRE-CUT open cell, rendered as dug
   floor; hol_023_cut vs hol_023), PMP heightmap byte → tile+0x07 (:453).
3. **HasTpl**: tile+0x13 |= 1 over the whole clipped rect (:461-529), including transparent padding.
4. **Solidity raster** → per-tile sub-grid via the independent whole-plane call to
   FUN_00596550 (:531-568; callee:31-141), including transparent seam tiles.
5. **Effect passes, NOT geometry** (:570-607): FUN_005a3dd0+FUN_005a3fd0 over the stamped
   SUB-CELL rect (flag 0x40000 → effects 6|0x10|0x40), the rect ±1 tile (flag 0x400) and a
   clipped band (flag 8) — the MapValidator re-derive machinery (regional re-mesh included).
   There is NO corner/block rewriting at seams: authored template data is coherent against
   virgin rock and against door-mated neighbours (the port's boot world — built by the same
   blits — renders watertight with zero reconciliation). Reconciling authored nodes mutated the
   templates, so the current port
   does not rewrite stamp geometry at the seam.

### ⭐ THE DIG-ANCHOR / JUNCTION LAW — `lg_set_starttemplatepro` = FUN_00650f90
How a generation is anchored at a player-dug breakthrough, and why the native carve↔stamp
junction is watertight. Full chain (every step byte-checked against Diggles.exe):
1. **Latch snap** (:179-180): the DigNotify latch {dir, x, y} (x/y = the ±4 NEIGHBOUR tile of
   the dug tile, raw) is snapped `&~3` to its 4-tile cell.
2. **THE PRE-STEP** (:182): `FUN_005f7a90` is `__thiscall` on **&StoryMgr+0x88 (the latch
   dir)** — verified `lea ecx,[edi+0x88]` at VA 0x6511eb — and steps the walk offset ONE ±4
   step along the dig direction BEFORE iteration 0. So walk iteration k probes
   `latchCell + (k+1) cells` along the dig dir: **the start template never overlaps the
   player's tunnel** — one full cell of virgin rock (the latch cell) always separates the
   breakthrough from the stamp. The tunnel then connects by the player DIGGING THROUGH that
   cell and the piece's closed wall into its authored bore.
3. **THE START-PIECE PICK** (:188 per iteration = FUN_005f7980 → FUN_00585d80): dig dir →
   door-side code (up→0 top, +x→1 right, down→2, −x→3 left; native labels). FUN_00585d80
   filters the ACTIVE pool to **W==4 && H==4** pieces with:
   • down-dig mode 1 (:58-110): exactly TWO doors, dirs {right,left} — the east-west
     THROUGH-CORRIDOR pieces (gng_001_a/b/c in Urwald): a shaft pierces a horizontal stratum
     corridor (the "perpendicular crossing corridor" seen in Play is AUTHENTIC);
   • every other case (:113-125): exactly ONE door whose dir equals the code — the door faces
     ONWARD along the dig, AWAY from the tunnel (up→gng_023, +x→gng_021, −x→gng_022, down
     mode 0→gng_024). No door ever faces the player's tunnel (it could never be docked — the
     closure law would fail).
   One match picked uniformly at random ((rand&0x7fff)·count>>15,:149-155); name
   path-stripped after '/'; empty match → "none" (resolve fails → walk fails).
4. **THE POSE PROBE** = FUN_0058a510: piece 4-aligned, whole footprint ≥8 tiles inside the
   MAP (DAT_0077241c+0x84..0x90), NO covered tile with HasTpl (tile+0x13 bit0). On success it
   APPENDS the forced record {tplIdx, areaRelX, areaRelY} to the +0x194 list (stride 0x10,
   via FUN_0058ef60/30). NO frontier validation here — a pose can pass and still fail inside
   lg_start (generation fails → story failcount retry).
5. **Walk failure** (:245-249): the nibble gate probes the CONSTANT latch-cell centre
   (latch+2, latch+2) — a miss fails all 4 iterations identically → `"nodigmark"` written
   into the utemp slot (str @0x7307fc) → story.tcl:191 aborts + marks the cell.
6. **Rect recompute** (:252 = FUN_005fc0a0(dir, |walked|+4)): the returned/lg rect is NOT the
   sm_get_level_info rect — it is re-derived around the STORY CURSOR sized past the stamped
   template; SetArea mode 0 (:260 = FUN_00583ac0) REBASES the forced records so their absolute
   poses survive the rect change. Cursor writers verified: **sm_get_level_info stores the scan
   cell into +0x48/+0x4c (FUN_005fac90:628-629, `param_1[0x12/0x13]`) and zeroes +0x68
   (`param_1[0x1a] = 0`)** — the +0x68 additive in FUN_005fc0a0's H formula is 0 for plain
   growth. Sizing (FUN_005fc0a0:83-90): W_cells = (utempW + Xextra + 8)/4 + 1,
   H_cells = (utempH + 8 + SM68 + Yextra)/4 + 1 with dist=|walked|+4 feeding Yextra (dirs 1/2)
   or Xextra (dirs 4/8); vertical digs X-align the rect to the utemp door column via the
   stoneidx cases (:104-115); FUN_005fa9a0 claims the rect (mode 4 vertical / 1 horizontal),
   failure ⇒ 1×1 rect + utemp cleared (:132-141). ⚠ Port keeps the sm_get_level_info rect
   (contains the scan cell) — the watermark clamps (+0x58..+0x64) and the FUN_005fa9a0 claim
   modes are the remaining unknowns; flagged in the binding.
7. **Near-border swap** (:261-273): if `latchX − rectX·4 + |walkedX|` < 5 or ≥ rectW·4−4, the
   record's template idx is swapped in place (:273) for the MODE-0 single-door pick (down:
   through-corridor → bottom cap — a side door against the area border could never dock).
8. **Utemp** (:281-503): placed AWAY from the breakthrough — random X,Y in the area with the
   dig-side band EXCLUDED by the walked distance (:290-302), ≤4 attempts (attempt 0
   deterministic by the utemp door column vs the story cursor); group pieces registered
   transactionally (FUN_005f7d10); mark-area rects appended to lg+0x17c; then FUN_005fc3b0
   registers start↔utemp corridor CONNECTIONS (weight 0.4) routed later by the UN-PORTED
   FUN_00587480. ⚠ Port deviation: the composite is registered at the walked pose instead
   (keeps it reachable without the router).
9. **Consumption** (FUN_005896a0): lg_start places the records IN ORDER at their stored
   area-relative poses VERBATIM via FUN_005848b0 (4-align, map inset ≥8, HasTpl re-probe,
   occupancy, frontier validator with rollback) — ANY failure fails the WHOLE generation.

**THE JUNCTION LATTICE**: standard marquee tunnels occupy tile pairs ≡{1,2} mod 4
(SnapAnchor = 4k+1); template pieces land 4-aligned and their authored bores sit at local
{1,2} (gng_001 bore rows y=1..2; vertical tubes at x=1 with frame at 0/2) — the player's
tunnel and the authored bore ALIGN BY CONSTRUCTION once the piece sits at latchCell+1 cell.
The blit then overwrites verbatim (destination-blind), dig marks survive (+0x08 untouched),
and the dwarf digs through the template mouth. NO carve-wins skip, NO seam reconciliation —
both were port inventions (removed; they created the half-buried corridor + the
mesh holes above it).

### ⭐ THE WORLD-BORDER LAW — `sm_draw_stone -border` = FUN_0065e090:930-941
What stops digging to the world edge in the original — TWO gates:
1. ±1-tile hard rim: markers/carves rejected outside [min+1, max−1) (FUN_00566870:19-22,
   painters FUN_005672d0:22-31 / FUN_005671a0:20-23, carve FUN_00596b80:42-43).
2. **A 32-tile probabilistic band of STONE CLASS 0 (undiggable)** painted at level boot
   (Campaign.tcl:3) over the TOP/LEFT/RIGHT edges (no bottom band): three FUN_005fc9d0 calls
   with material arg **0** — top (0,0)-(W,32) strength 2.5 falloff flag 1; left (0,0)-(32,H·4+4)
   strength 1.0 flag 4; right (W−32...) flag 8. Painter law (FUN_005fc9d0): rect clamped, class
   1↔2 swapped (0/3 pass), `p = 1 − (d/span)²/(strength/(span·0.2))` on the inner half, accept
   iff (rand&0x7fff) ≤ p·32768, tiles already class 0/3 skipped, last row/col excluded. Side
   bands: solid stone for the outer ~16 tiles, quadratic taper to zero at ~29 in. Each paint
   also STORY-MARKS its rect (FUN_005fc9d0:139-141 → FUN_005fa510) + the branch marks the top
   9 cell rows (:943) — growth never generates into the border/surface. Class 0 is enforced at
   marker validity (FUN_00566870:26-28), both painters (FUN_005672d0:41-46, FUN_005671a0:33-38),
   the fog reveal (marker erase, FUN_0057dfc0:44-48) and the carve itself (FUN_005976b0:35-38).
   Virgin land elsewhere = diggable class 2 (FUN_00598f10:154) — only the band stops you.
   `map move` re-paints the side bands over newly scrolled rows (FUN_006325f0:240-247).
Port: DrawStoneIntoWorld (terrain byte 4 = class 0) + the sm_draw_stone binding. Boot calls
are buffered until the arrays exist, then replayed with a `TemplateRecordWrites` skip mask:
only non-transparent PMP record writes suppress the delayed paint; transparent padding remains
painted even though its independent `HasTpl` bit and +0x3c plane are stamped (FUN_005992b0:292-568).
Live calls run in their real post-stamp order and use no template skip. The class gates were
already live via TileMaterialDiggable. The fifth gameplay branch, `-matlist`, consumes the
level's `mat {type x y w h}...` tail after `MapTemplateSet` (`mp_tools.tcl:26-28`):
`FUN_0065e090:190-206` adds argv offsets, grows each far edge by 2, and paints class `type`
with flags 0 / strength 2.5. Live paint synchronizes tile+0x18 then publishes EFFBLOCK +
render work over rect±1 (`FUN_005fc9d0:127-138`). **(The other four branches
-border/-funnel/-exlimit/-utmat are implemented — `CmdSmDrawStone`;
but the -exlimit granite band was DISABLED in 88df518 because it blocked digging past a story cave,
code kept commented, faithful rim deferred to Play tuning.)** PLUS the port-only RENDER APRON
(DiggableTerrain.EnsureRenderApron): the native renders the WHOLE 512×640 map as rock; the
port meshes only the render bbox, so digging into unmeshed virgin land showed void — the bbox
now grows 32 tiles ahead of any mark/carve near its edge.

### The grow core — `lg_start` = FUN_00589790
1. `FUN_005896a0` places the FORCED templates (+0x194 records — the start/unique template chosen
   by `lg_set_starttemplatepro`); any failure ⇒ generation fails.
2. Each connect record (+0xa4, stride 0x18) is routed via `FUN_00587480` (corridor router; ~8 KB,
   still un-RE'd — only exercised by `-connect/-digconnect` uniques, NOT by Std growth; open item).
3. `lg_mark_area` rects are painted 1 into the occupancy grid.
4. `do { SeedOrAdvance; while(frontier non-empty){ dock frontier[0] } } while (fill/total <
   ratioA && lastDockOk)` (:172-259). **story.tcl passes ratioA=0** (`lg_set_templateratio 0
   $optratio 1.0`; the arg→field mapping verified in FUN_00652830:140-154: argv1→+0x70=A,
   argv2→+0x74=OPT, argv3→+0x78=MAX) ⇒ the outer loop runs ONCE ⇒ **one seed cluster per
   generation**.
5. Docking: candidates cost-sorted ascending (`FUN_00586f40` + quicksort — INT_MAX "rejected"
   pieces stay in the list as LAST RESORTS, there is no cutoff); only edges with
   `(edgeDir − doorDir)&3 == 2` are tried; the first `TryPlace` success wins (:202-249).
6. **FULL-CLOSURE LAW** (:252/:262): if the head frontier door cannot be docked, the run ends with
   ok=false ⇒ the WHOLE generation FAILS and rolls back (`lg_get_level` returns empty ⇒
   story.tcl failcount++ ⇒ retried on a later scan with different RNG state). The only successful
   outcome is a cluster whose every door is mated or capped. This (plus the validator) is why the
   original never leaves doors into rock — not the finalize pass.
7. `FUN_00585390` finalize = a DEAD-END PRUNE (finds placed 1-door thin (W==4||H==4) object-less
   pieces, deletes them, swaps the neighbour for a piece with one fewer door via FUN_00585130 or
   docks a random 4×4 1-door cap); its failure only REVERTS to the pre-finalize placements (still
   success). NOT ported (cosmetic).

### Seeding — FUN_005862c0
State +0x64: −1 initial, −2 after a seed. If a forced template was placed, the first call succeeds
without seeding (:24). Otherwise: the first POOL piece with `W==4 && H==4 && edgeCount==2`
(:45-53; the 4×4 2-door `gng_001/007/012/...` connectors in real data), tried at ≤16 random
positions `((rand&0x7fff)%span &~3)+4` (:79-102); all misses ⇒ generation fails.

### Placement — TryPlace = FUN_005842c0
- `(x|y)&3 != 0` rejects; margin &= ~3 (:40-41). Bounds: piece fully inside the AREA (:47-55).
- Overlap: any grid cell ≠ 0 (:57-79).
- `MergeEdges` (FUN_00583f60) toggles frontier points by COORDINATE match only (no dir test):
  match ⇒ swap-remove (mated), else append.
- MARGIN (+0x80 = 4, FUN_00583650:59): the paint window extends 4 cells beyond the piece on each
  side WITHOUT a door (openSideFlags +0x18: bit1=top 2=right 4=bottom 8=left;:84-106). The
  window is painted 0x80 (tentative), then on success **the whole extended window = 2** — margins
  are PERMANENT occupancy, so unconnected cave walls keep ≥4 tiles of rock between them.
- Validator FUN_00584120 (run after every paint): EVERY frontier point must be ≥2 cells inside
  the area border (:35-43) and must keep ≥1 of its 4 diagonal grid cells free (:44-83).
- Success: fill(+0xa0) += W*H; per-class object counters += def counts; placedThisLevel(+0x24)++;
  placement record {tpl, areaX0+x, areaY0+y, flagByte} appended; object-bearing pieces also log a
  spacing record {x, y, W*H} (+0x18c).

### Forced/start placement — FUN_005848b0
4-aligned; the WHOLE template must sit ≥8 tiles inside the MAP (:37-46); every covered map tile is
probed against the live per-tile flag +0x13 bit0 (≈ our HasTpl — rejects stamping over existing
template geometry,:48-61); the paint window is CLAMPED to the area (partial overhang legal,
:63-79); flag byte = 1 (finalize skips such records). No margins, no objcounter bumps.

### Cost — FUN_00586710 (constants extracted from Diggles.exe.rdata)
Rejects (INT_MAX, but still last-resort dockable): per-class `placed+add > max` (lg_set_objcount);
object piece already placed this level, or another object piece with `|dx|*|dy| < 1.5*(W*H)`
(:58-91); fill overshoot `((filled+area)/total)/MAX > 1.0`; `levelcount ≤ placedThisLevel`;
tunnel-flag pieces on the wrong door axis. 4×4 pieces return `0x7ffffff9+edges` (seeds are
last resorts mid-growth). Tunnel pieces (−tunnel 1=vertical/2=horizontal) with occupied clearance
4 beyond either end → `0x7ffffffe`.
Otherwise, with `progress = (filled/total)/OPT` (OPT=0.10 ⇒ progress hits 1.0 at 10% fill):
```
sizeTerm = |20 − (W+H)| * (2.0 − progress)
band = progress<0.3: |3.0−edges|; <0.6: |2.5−edges|; <0.8: |2.0−edges|; ≥0.8: |1.0−edges|*10
objPress = no active constraints ? 1: |avg_i((|t_i−(placed_i+add_i)/overshoot| − |t_i−placed_i/progress|)/t_i) + 1|
cost = round((2*sizeTerm + band*100 + capTerm + placedThisLevel*250
                  + (rand()&0x7fff)/32768*(1−progress) + 0.5) * value * 100 * objPress)
capTerm = (objectBearing ? min(def+0x1c,5): 5) * 200 [def+0x1c approximated by obj total]
```
Constants: 1/32768 @00736f94, 0.3 @00737060, 0.5 @0073708c, 0.6 @0073709c, 0.8 @007370d4,
1.0 @00737138, 1.3 @00737180, 1.5 @007371c0, 2.0 @00737330, 2.5 @007373d8, 3.0 @00737428,
10 @00737534, 100 @0073760c, 200 @00737644, 250 @00737650, −1 @0073773c.
The `band` law is what CLOSES a cluster: past 80% of the OPT fill the 1-door caps get a ×10
preference, the frontier drains, the run succeeds. `value` (lg_set_templategroupvalue — Hol
0.287/0.185/0.077/0.105 per zone) multiplies the whole cost ⇒ Hol (cave) pieces are strongly
preferred until their levelcount caps reject them.

### Anti-duplicate occupancy — lg_get_level = FUN_00652d10
For EVERY emitted placement, `FUN_005fa510(x/4, y/4, ceil(W/4), ceil(H/4))` marks the story-map
rect grown (:276-279) — the next `sm_get_level_info` free rect can never overlap a previous
level. (`sm_set_temp` does NOT mark — verified FUN_00660a80→FUN_005fa320 only sets used-flags;
neither does the native `map_template`, FUN_0063bca0.) lg_get_level also clears the objcount
constraint table (:280). `sm_map_set x y w h` is likewise a RECT mark (FUN_00660110:179).
FUN_005fa510 gate: if ANY part of the rect is out of the story grid, NOTHING is marked (:18-19).

### Randomness
`rand` = a game-wide MT19937 (FUN_0049c670) lazily seeded with the CONSTANT 0x1105
(FUN_004454d0 — both call sites: FUN_0049c670:33 self-default + FUN_00598f10:38 map-init;
`FUN_006d470b(GetTickCount)` at boot seeds the CRT `rand`, a DIFFERENT generator, NOT this
MT). Variety across sessions comes from the shared stream being consumed by every other engine
system before each generation (the map-tile-variant init FUN_00598f10 alone burns ~512×640
draws). Port: ONE session-seeded `ctx.StoryRng` shared by the sm_* layer and the generator.
(The old port used private fixed seeds 12345/421 ⇒ identical caves every playthrough =.)

### ⭐ OBJECT-CAVE VARIETY LAWS
Byte-verified against Diggles.exe.rdata. The rect/RNG
work was faithful — the actual repetition came from TWO cost-heuristic port bugs, both in the
"object pieces" steps of `FUN_00586710`. Ruled faithful in the same pass (do NOT re-chase these):
the cost jitter magnitude/placement, the candidate sort + iteration order, the seed/start-piece
pick, and the per-generation rand DRAW RATE all match; the corridor router `FUN_00587480`
(6 draws) NEVER runs (no `lg route`/`-connect` command exists in any shipped script) so its
un-ported draws are moot; and `FUN_005fc0a0` recomputes the growth rect ONLY for utemp
generations (`+0x54 != -1`) — plain growth keeps the sm_get_level_info rect, which the port
already builds faithfully (deterministic 16×8, centred). Also NOT a bug: the start-cave SHAPE per
dig direction is a single pmp by design (gng_021 for +x, 022 −x, 023 up, 024/001 down) — in the
original too; the 3–11 same-direction variants differ only in object dressing. **Judge variety by
the GROWN CLUSTER, not the entry chamber.**

1. **capTerm read the wrong field (THE headline bug).** `FUN_00586710:28`:
   `capTerm = (objectBearing ? min(+0x1c, 5): 5) * 200`. **+0x1c is the -gamecount REMAINING**
   (ctor default INT_MAX; the sole decrementer `lg_dec_templatecount`=FUN_00654fa0 is never
   called by shipped scripts ⇒ static = the -gamecount arg). With shipped data capTerm is a FLAT
   1000 for every piece EXCEPT the two object-bearing set-piece caves `urw_gng_051_a`(Wuker) and
   `urw_gng_054_a`(Schatztonne) — lginit gives them -gamecount 1 → capTerm 200 (an intentional
   −800 discount). The old port read the object-SPAWN count (`min(ObjTotal,5)*200`) → 58 of 185
   in-pool pieces got a spurious −200..−800 discount that DWARFS every legitimate cost separation
   (band step 100, size step ~13), so the same low-object object pieces won every dock of every
   generation deterministically. Fix: `Piece.GameCap` (+0x1c, parsed from lg_set_templateprops
   -gamecount, default INT_MAX) → `capTerm = (ObjectBearing ? min(GameCap,5): 5)*200`.
2. **Object-piece once-gate had the wrong scope.** `FUN_00586710:58-61` rejects object-bearing
   templates with **+0x50 > 0**; +0x50 (placed-this-SESSION) is bumped ONLY at emission
   (`lg_get_level` FUN_00652d10:84-93, per emitted object-bearing placement) and is NEVER reset ⇒
   an object-cave variant ships at most ONCE per session, forcing later breakthroughs onto
   DIFFERENT object caves. The old port gated on `PlacedThisLevel` (reset every generation) ⇒ the
   same object caves reappeared every generation. Within a single level the native allows repeats,
   held back only by the spacing check (:63-89, `|dx|·|dy| < r·1.5`, faithful) and the explicit
   levelcount cap. Fix: `Piece.EmittedThisSession` (+0x50), bumped by the lg_get_level binding
   (`LevelGenerator.CommitSessionEmission`), gate step 2 on it; keep PlacedThisLevel for the
   levelcount cap only. Tests: LevelGenTests.Generate_RealUrwald_{GameCountFeedsCapTerm,
   ObjectCavesThinAcrossGenerations}.

### Unique-temp COMPOSITES — sm_def_temp_group (2nd pass, same day)
`sm_def_temp_group <name> {{dx dy subTemplate} …}` (CampaignInit.tcl:100-140) defines the unique
temps' CONTENT: hand-authored clusters (e.g. `urw_der_einsiedler` = einsiedler + raetsel_001 +
4 gng connectors). The native registers one FORCED record per entry (FUN_00650f90:281-503 → the
+0x194 list FUN_005896a0 walks) — the whole composite places together or the generation fails
(story failcount retry). `sm_set_temp` marks the registry record used when ALL its children have
placed (FUN_005fa320's child recursion) — the level list carries only sub-template names.

### lg_tp_setfilter = REMOVE (not restrict!)
FUN_0064f830: pool entries matching a member of the named group are swap-REMOVED. story.tcl:188's
`lg_tp_setfilter " $zone.Std.Gsg "` on unique-temp generations therefore grows the composite's
surroundings from the normal Gng/Hol pool WITHOUT the Gsg ring pieces. (The old "restrict to
Gsg" reading left a pool with no 1-door caps ⇒ unique-temp generations could never close ⇒ the
einsiedler depth band livelocked ALL deep growth — the manual test.)

### The exact.ent door law — FUN_005826d0:219-346
Door point = 4-aligned base `((px+sign*7)>>3)&~3` PLUS a half-cell +2 offset from the mod-32
remainders (rx=px%32, ry=py%32): `ry<rx` → `+2` on X (top/bottom edge; `rx+ry≥32` → right edge:
`+4` X, `+2` Y); `ry≥rx` → `+2` on Y (left/right edge; `rx+ry≥32` → bottom edge: `+2` X, `+4` Y).
The offset encodes the edge orientation into the COORDINATE (horizontal-edge doors have x≡2 mod 4,
vertical-edge doors y≡2 mod 4) so mating cannot cross orientations. Dir is then assigned strictly
from the boundary (Lx==0 / Ly==0 / Lx==W / Ly==H; native labels 0=top 1=right 2=bottom 3=left);
non-boundary doors are DROPPED. A plain corner quantization mislabels every 4-wide vertical
corridor's doors as LEFT doors — vertical docking dies.

### Ported-approximation notes (flagged, conservative)
- FUN_0058a510:42-61's map-node occupancy probe is live through the port's stamped-template
  `HasTpl` callback once world arrays exist. During initial campaign registration `map create`
  has established only the dimensions and the map is known clean, so the probe is intentionally
  empty. Like the native probe, it does not reserve one queued preset against another;
  `lg_start`'s ordered `ForcePlace` pass re-probes the map and rejects grid overlap before emission.
- FUN_00587480 corridor router un-RE'd (only `-connect/-digconnect` uniques need it).
- def+0x1c in the cost capTerm approximated by the piece's tracked-object total (≤5 cap bounds
  the error at ≤1000 cost units pre-multiplier).
- FUN_00585390 finalize (dead-end prune) not ported — never fails a generation natively.
- Per-piece object metadata (def+0x34) parsed from the template.tcl `new <Class>` lines against
  the lg_addfilterclass list (native builds the same table at load).

## SCOPING
SCOPING (Finding 5, verified against Campaign.tcl): the generator is NOT needed for the INITIAL Urwald play. The starting area is FIXED — Campaign.tcl directly `call`s urw_unq_start.tcl (a 12-chunk split_template) + urw_unq_hoehlenmalerei.tcl + the hand-written first_level proc (irandom-shuffled cavelist/comblist/tunnlist/vodolist), with ZERO lg_* calls. That already renders today via the working stamping pipeline. The generator is exercised only LATER, in the StoryMgr idle state, once the colony has dug out its start area and sm_scan finds an un-grown colonized frontier cell. HOWEVER, the world cannot GROW (and crucially cannot reach the Metall transition) without it: the zone-border template urw_uebergang_swf is placed by the grower (sm_add_temp... -zoneborder), so progressing past Urwald requires either the grower OR a scripted stamp of those specific uebergang/transition templates.\n\nWHAT BLOCKS GROWTH TODAY is not primarily lg_* — it's the sm_* gating stubs: sm_scan returns \"0\" so generate_next is NEVER called (nothing crashes, nothing grows); and if it were called, sm_get_resolution=\"0\" → divide-by-zero in generate_next. So the cheapest path to ANY growth is: implement the sm_* layer + a generator that returns valid placements.\n\nRECOMMENDATION — two tiers:\n(A) FAITHFUL-FULL port of lg_start (the algorithm above): correct, connected, ore-spread caves that match the original's feel; larger effort (the cost heuristic constants, the finalize/route BFS, and exact edge-quantization are the deep parts). Worth it for a faithful port and required if we want the original's procedural variety + guaranteed connectivity.\n(B) PRAGMATIC-MINIMAL grower (flag as the fast path to a playable growing world): implement the sm_* layer faithfully (resolution=32, zone bands, sm_scan over gnome frontier, sm_get_level_info free-rect) + a SIMPLIFIED lg_start that (1) seeds the chosen start template, (2) greedily places opposite-edge-matching pool pieces into the free rect honoring levelcount caps and a coarse fill target, (3) SKIPS the cost-heuristic banded ratio constants (use a simple weighted-random by group value + tunnelWeight) and SKIPS the corridor-routing finalize (instead just dead-end-cap any leftover open doors with a hol/cap piece). This produces plausible connected expansion. RISK of the minimal version: skipping strict door-edge matching would leave unreachable pockets — so opposite-edge matching ((d-t)&3==2) MUST be kept even in the minimal grower (it's what makes diggable tunnels line up); only the cost-distribution fidelity is sacrificed.\n\nMINIMAL-BUT-WORKING grower = sm_* layer (faithful) + start-seed + opposite-edge greedy fill with caps + dead-end caps + {name x y} output + distance sort. That is enough to grow the world and, with the uebergang templates in the pool + sm_set_zone hooks, to reach Metall. Recommend shipping (B) first (gated by the headless lg_start-returns-valid-placements test, step 7), then upgrading toward (A) — the data model,.ent metadata, TryPlace/MergeEdges, and integration are SHARED, so (B)→(A) is incremental, not a rewrite.

## OVERVIEW
The Diggles level generator is the StoryMgr's world-growth engine, driven SOLELY by classes/story/story.tcl (no other caller — verified). It is NOT WFC or a true constraint solver: it is a randomized greedy door-to-door tiler with rollback-on-validation and a fill-ratio-gated retry loop, followed by a finalize pass that caps/routes every still-open doorway so the cave network is always fully connected and closed. The Tcl half (story.tcl generate_next/fill_temp_buffer/load_next/the idle think-loop, lginit.tcl, lgtools.tcl, CampaignInit zone/temp data, and every per-template MapTemplateSet body) runs UNCHANGED on our interpreter. We only reimplement, in C#, the NATIVE primitives those scripts call: the lg_* generator family (27 commands) and the sm_* StoryMgr zone/scan/bookkeeping layer. The output of the generator is a Tcl list of 3-tuples {template_tcl_name x y}. That list flows into temp_buffer (lg_sort_level reorders nearest-first by the gnome position act_sort_x/y), and load_next pops one per tick, `call`s data/templates/tcl/$template (defining MapTemplateSet), and invokes MapTemplateSet x y — which calls our ALREADY-WORKING map_template + map_setlayer2 + `new` stamping chain (CmdMapTemplate@DigglesTclEngine.cs:3708, CmdMapSetLayer2@3725, CmdSplitTemplate@3747 → RuntimeMapBuilder → BdbPmpWorldBuilder). So once the generator emits real triples and the sm_* gating returns real data, the entire downstream pipeline already works.\n\nCONTRADICTION RESOLUTION across the 5 findings: (1) Native function attribution — Finding 1 labels FUN_00585390 as the connectivity/validation pass and FUN_00589790 as the lg_start grow-core; Finding 3 agrees and adds detail (FUN_005842c0=TryPlace, FUN_00583f60=MergeEdges, FUN_00586710=cost, FUN_00585390=finalize/cap+route). Finding 4 is consistent. I adopt the Finding-3 decomposition (most granular and self-consistent) as authoritative. (2) Output tuple arity — Finding 1/4/5 say 3-tuple {name x y} for the Tcl-visible list (lg_get_level emits only name/x/y per FUN_00652d10); the native record is 16 bytes {templateId,x,y,connected/orient-flag}. Resolved: the C# placement record carries {templateName,x,y,dir} internally for the finalize pass, but lg_get_level serializes ONLY {name x y} — matching what story.tcl reads. (3) Edge-direction encoding — Finding 2 says edgeCode 0=bottom/1=right/2=top/3=left (from FUN_005826d0 vs w/h comparisons); Finding 3/5 say direction low-2-bits 0=left/1=up/2=right/3=down with opposite test (a-b)&3==2. These are two different labelings of the same 4-way opposite-matching; the load-bearing invariant is that two doors mate when their directions are OPPOSITE ((dir-otherDir)&3==2), regardless of the exact label assignment — so the C# port picks ONE internal convention and applies it uniformly. (4) Resolution — Finding 1 says \"resolution = digcount = 32\" while also musing \"≈8\"; Finding 5 confirms sm_set_digcount 32 and CLevelGenerator default area 0x20=32. Resolved: sm_get_resolution MUST return 32. (midx=8 is a separate story-grid mid-index constant, story.tcl:943, unrelated to resolution.) (5) Scope — Finding 5 proves (and I verified Campaign.tcl) the INITIAL Urwald area is FIXED (first_level + unique templates, zero lg_*); the generator is only exercised LATER when sm_scan finds an un-grown colonized cell. So the generator is NOT on the critical path for first play, but IS required to reach the zone-transition (urw_uebergang_swf is placed by the grower via sm_add_temp -zoneborder).\n\nHOW IT GROWS THE WORLD end-to-end: per StoryMgr idle tick — if temp_buffer non-empty, load_next stamps ONE piece (then returns, one-piece-per-tick paced); else sm_scan walks the colony's gnomes, maps each gnome world-pos to a coarse cell (pos/resolution), and returns the first cell whose sm_map_get!=1 (un-grown frontier near the colony); generate_next(x,y) then configures the pool (zone's Std group, objfilter Troll/Zwerg, Hol weight, object-count deficits), picks a start template + free rect (lg_set_starttemplatepro), runs lg_start (seed→greedy grow→fill target), lg_get_level returns the triples, they're sorted into temp_buffer, and the cell is marked grown (sm_map_set x y 1 1). Terrain emission, currently ONE-SHOT (RunWorldScript→EmitTerrain once, DigglesWorldRuntime.cs:701), must become incremental: after each runtime MapTemplateSet stamps a new region, re-run a bbox-growing EmitTerrain/RemeshFromArrays (reuse DiggableTerrain's existing debounced partial re-mesh + nav re-bake path).

## ALGORITHM
C# reimplementation of lg_start (faithful to FUN_00589790 + FUN_005842c0 + FUN_00583f60 + FUN_00586710 + FUN_00585390). All coords are in SUB-GRID units (cell = world/32; lg_set_area / story pass cell*resolution = world units; internal grid uses cell-aligned 4-step coords — TryPlace requires (x|y)&3==0).\n\n=== STATE (LevelGen object) ===\n- AreaRect (x0,y0,x1,y1) // from lg_set_starttemplatepro's chosen rect (or lg_set_area)\n- int[] Grid width=(x1-x0) // occupancy: 0=empty, 2=piece-interior, |0x80=reserved doorway margin\n- List<OpenConn> Frontier // {worldX,worldY,dir} unmated doorways — the work-list\n- List<int> CandidatePool // template indices eligible this run (from lg_tp_* filters)\n- List<Placement> Placed // {tplIdx,x,y,dir} — the OUTPUT\n- PieceDef[] Pieces // {W,H, Edge[]{lx,ly,dir}, openSideFlags, placedCount, gamecountCap, levelcountCap, tunnelWeight, baseScore, groupId, objClasses[], isObjectBearing}\n- group caps/weights (per group: levelcount, gamecount, value/ratio)\n- ObjCount constraints {class, min, max, stride} + placed-object positions (for spacing)\n- float FillTargetMin/Opt/Max (lg_set_templateratio a min max → opt,max; story passes 0,0.10,1.0)\n- leveltype (default=0/base=1/other=2)\n- RNG (see Risks: seed)\n\n=== TEMPLATE METADATA (port of FUN_005826d0) — precompute once at config time ===\nFor each template name in the active groups:\n 1. base = template name with zone prefix stripped to the PMP/ENT base (e.g. urw_gng_050_a → gng_050). [Map via the MapTemplateSet body or a name convention; safest is to read the.pmp basename from the template's first map_template call, but at config time use the documented stem rule: strip the leading `<zone>_` and trailing `_<variant>`. CONFIRM per-zone — flag in Risks.]\n 2. W,H = PmpData.Width/Height of base.pmp (already exposed; lg_get_temp_size returns {W H}).\n 3. Parse data/templates/ent/<base>.ent: each line `map_entry <type> <Xpx> <Ypx>`. Keep type==1 (door). Convert px→subcell: `sub = (px + sign(px)*7) >> 3` then 4-align `& ~3` (FUN_005826d0:218-224). Assign dir from boundary test: if subX==0 → one edge, subX==W → opposite, subY==0 → another, subY==H → opposite (the &0x8000001f fold @225-231). Use ONE fixed convention (e.g. 0=left@x0,1=top@y0,2=right@xW,3=bottom@yH) consistently; OR `1<<dir` into the piece's openSideFlags.\n Result: PieceDef{W,H, Edge[]{lx,ly,dir}}.\n\n=== lg_start (port of FUN_00589790) ===\n1. Reset Grid over AreaRect (all 0); paint any pre-reserved/forced regions = 1 (lg_mark_area).\n2. PRECONDITION: place each forced/start template (the lg_set_starttemplatepro result, honoring the nodig map flag) via TryPlace; if any fails → return false (empty level).\n3. GROW loop (do/while):\n a. SeedOrAdvance (FUN_005862c0): if a START piece exists in pool (a fully-open fill piece, all 4 edges open) and nothing placed yet, set it as anchor; else if Frontier empty, RANDOMLY try up to 16 positions in AreaRect to drop a seed via TryPlace. If neither → break (growth ends).\n b. Pop an open Frontier point P {wx,wy,dir}.\n c. Build candidate cost array: for each pool template compute Cost(tpl,P) (FUN_00586710), QUICKSORT ascending by cost.\n d. For each sorted candidate whose mating edge direction is OPPOSITE P.dir (`(edgeDir - P.dir)&3==2`): compute docked top-left = (P.wx - edge.lx, P.wy - edge.ly); TryPlace. First success → placed=true, break.\n e. Continue while currentFill < FillTargetMax AND last step succeeded.\n4. If nothing ever placed → return false.\n5. FINALIZE (FUN_00585390): repeat until no open doors remain — for each Placed piece still owning an open door: try to mate it to a neighbour (FUN_00584f80 find owner) or ROUTE a corridor between two doors via grid BFS/Dijkstra (FUN_00587480) and seal with a dead-end cap piece (TryPlace skipPaint). If a door is unsealable → backtrack (remove the offending placement, FUN_00584d10) and retry; if still unsatisfiable → return false (empty level).\n6. VALIDATE object counts: every ObjCount constraint min must be satisfiable within Placed; if not → return false.\n7. Success: Placed is the result.\n\n=== TryPlace(tplIdx, lx, ly, margin, dir, skipPaint) — port of FUN_005842c0 ===\n- Require (lx|ly)&3==0; W=def.W,H=def.H; bounds: lx+W<=areaW && ly+H<=areaH.\n- OVERLAP: scan W×H window of Grid; any cell!=0 → fail.\n- If !skipPaint: MergeEdges(tpl,lx,ly) (tentatively).\n- Apply margin: set |0x80 reserved border of `margin` cells on the piece's OPEN sides (per openSideFlags).\n- VALIDATE corner-connectivity (FUN_00584120): for each frontier point, if all 4 diagonal-corner grid cells are occupied → doorway sealed → reject.\n- On pass: paint interior cells=2; add object contributions to group counters; def.placedCount++; append Placement{tpl,x0+lx,y0+ly,dir}; if object-bearing, record world pos for spacing. Return true.\n- On fail: undo margin (&0x7f), MergeEdges again to ERASE the tentative frontier merge. Return false.\n\n=== MergeEdges(tpl,dx,dy) — port of FUN_00583f60 ===\nFor each edge: worldPt=(edge.lx+dx,edge.ly+dy). If exact point already in Frontier → it's a MATING pair, REMOVE it (swap-with-last). Else APPEND {worldPt,edge.dir}. This is the connectivity bookkeeping that builds the connected piece graph.\n\n=== Cost(tpl,P) — port of FUN_00586710 (lower=better; INT_MAX=reject) ===\n- Reject if placing the piece's group objects would exceed any group levelcount/gamecount cap.\n- Object-bearing: reject if remaining-count<=0, or any prior same-class object within min-spacing radius (stride*spacing const) — enforces ore/mushroom spread.\n- Fill bias: weight = tunnelWeight (empty/connector pieces lighter, rooms heavier); reject if it overshoots the global fill cap.\n- START piece (all edges open): return a very-low score (~INT_MIN+edgecount) so it's always tried first.\n- Add ratio-pressure terms toward FillTargetOpt + a RANDOM jitter `rand*k*(1-ratio)` (this stochasticity is why retries can succeed). Scale by def.baseScore.\n\n=== lg_get_level ===\nWalk Placed; for each emit Tcl 3-tuple `{templateName x y}` (x=x worldoffset, y=y). Return as a space-joined Tcl list of brace-wrapped triples. EMPTY string/list on generator failure (story reads llength==0 as failure).\n\n=== lg_sort_level sx sy levellist ===\nParse the list of triples; for each compute piece-center (x+W/2, y+H/2) from PieceDef; stable-sort ascending by squared distance to (sx,sy); rebuild the list nearest-first. (Order only affects load streaming, not geometry — any stable order is acceptable per Finding 3 openQ.)

## DATA MODEL
=== Tcl-side data (built UNCHANGED by lginit/lgtools, consumed by our C# lg_* impls) ===\n- lg_addfilterclass {Zwerg Wuker Troll Pilz Kohle Kristallerz Golderz Eisenerz Schatztonne} (lginit.tcl:5) — the classes the obj-count/objfilter system tracks.\n- LG_set_templategroup (lgtools.tcl:1-73) recursively glob-expands the `<Urwald>{Std{Gng{'tcl/urw_gng_*'} Gsg{'tcl/urw_gsg_*'} Hol{'tcl/urw_hol_*'}}}` tree into FLAT dotted groups (Urwald.Std, Urwald.Std.Gng/.Gsg/.Hol) of bare.tcl filenames, then calls native lg_set_templategroup <name> {files} per group;.hol/.gsg get lg_set_templategroupprops -levelcount 1 (cap one per level). Zones: Urwald(urw_*), Metall(swf_*), Kristall(kris_*), Lava(lava_*) — display-name vs file-prefix mismatch is in the glob patterns (lginit.tcl:7-41).\n- LG_set_templateprops {{urw_gng_050_a 1 1 -} …} (lginit.tcl:45-50) → lg_set_templateprops name -gamecount G -levelcount L -tunnel T.\n- sm_add_zone <name> + sm_add_temp <name> [Y0 Y1] [-opts] (CampaignInit.tcl:675-722): per-zone Y-bands + the SPECIAL/unique templates the StoryMgr force-places (e.g. urw_uebergang_swf 132 164 -exclusive 1 -zoneborder Urwald -stoneidx 0). Opts: -connect/-mindist <tpl> f/-exclusive/-digconnect/-nonrelevant/-zoneborder/-stoneidx/-middist/-checkalign.\n- sm_create_map 512 640 (CampaignInit.tcl:34) + sm_set_digcount 32 (line 36) → resolution=32.\n- Per-zone object targets: $Urwald_oc_Pilz=12 etc (story.tcl:965-988); oc_list={Kohle Kristallerz Golderz Eisenerz Pilz}; mingen_list={Wuker}.\n\n=== C# structures to hold it ===\nclass LgTemplateRegistry {\n Dictionary<string,LgGroup> Groups; // dotted name → group\n Dictionary<string,LgPieceDef> Pieces; // template.tcl name → def\n HashSet<string> FilterClasses;\n}\nclass LgGroup { string Name; List<string> Templates; int LevelCount=int.Max, GameCount=int.Max; float Value=0; }\nclass LgPieceDef { string TclName, PmpBase; int W,H; List<LgEdge> Edges; int OpenSideFlags; int GameCountCap,LevelCountCap; float TunnelWeight,BaseScore; string GroupName; List<string> ObjClasses; bool IsObjectBearing; int PlacedThisLevel, PlacedThisGame; }\nstruct LgEdge { int Lx,Ly; int Dir; } // dir 0..3, one fixed convention\nclass LgActivePool { List<int> PieceIdx; } // built by lg_tp_addtemplatesets, narrowed by lg_tp_setfilter / lg_tp_objfilter / lg_tp_mapfilter\nstruct LgObjCount { string Class; int Min,Max,Stride; }\nstruct Placement { int TplIdx; int X,Y,Dir; }\nstruct OpenConn { int X,Y,Dir; }\nclass LevelGen { /* state from algorithm section: AreaRect, Grid, Frontier, CandidatePool, Placed, FillTarget{Min,Opt,Max}, LevelType, group/objcount tables, RNG */ }\n\n=== sm_* StoryMgr layer ===\nReuse the EXISTING ctx.StoryMap: Dictionary<(int,int),int> (DigglesTclContext.cs:150) for the grown-cell grid (sm_map_get/set already real, DigglesTclEngine.cs:1819-1820). Add:\nclass StoryZones { List<(string name,int y0,int y1)> Bands; List<SmTemp> Specials; int Resolution=32; }\nstruct SmTemp { string Name; int Y0,Y1; /* parsed -opts */ }\nsm_get_resolution → 32. sm_get_zone(worldY) → band lookup (default Urwald). sm_get_level_info(x,y) → {x y w h startName} free-rect from StoryMap/world bounds. sm_scan → first un-grown cell near a gnome (port of get_next_areas). sm_set_temp/sm_get_temppos → dedup table of placed specials. sm_draw_stone -border/-funnel/-exlimit/-utmat → paint diggable/solid borders into DiggableTerrain (the -border/-funnel path is already used by the initial Campaign.tcl so it must at least no-op gracefully; -exlimit/-utmat are post-growth stone/material painting — flag fidelity in Risks).\n\n=== lg_get_level output format (consumed by story.tcl, VERIFIED) ===\nTcl list of brace-wrapped 3-tuples: e.g. `{urw_hol_027_a.tcl 33 40} {urw_gng_050_a.tcl 12 8}`. Each = {template_tcl_name x y}; name =.tcl filename under data/templates/tcl/; x,y = WORLD-UNIT integer offsets (already cell*resolution). Read by story.tcl via [lindex $item 0/1/2]. Empty list ⇒ generation failure.

## INTEGRATION
The generator produces ONLY the {tcl x y} placement list; it stamps NOTHING. Integration is therefore: lg_get_level → temp_buffer (Tcl) → load_next (Tcl) → `call data/templates/tcl/$template` (sources the file, defining proc MapTemplateSet) → `MapTemplateSet x y` → the per-template body calls `map_template <base>.pmp x y` + `map_setlayer2 x y <name>.l2m` + `new <Class>...`. All of THAT already works in our port:\n- CmdMapTemplate (DigglesTclEngine.cs:3708, handles both arg orders) → ctx.MapBuilder.LoadPmp(pmp,x,y).\n- CmdMapSetLayer2 (3725) → LoadL2m.\n- CmdSplitTemplate (3747) → walks chunks, sources templates/tcl/split/<sub>, LoadPmp + MapTemplateSet per chunk.\n- RuntimeMapBuilder (World/RuntimeMapBuilder.cs) accumulates (absPath,x,y) into _pmps/_l2ms; resolves bare filenames against Templates/pmp, /l2m, /split.\n- EmitTerrain (RuntimeMapBuilder.cs:87) → BdbPmpWorldBuilder.BuildFromTclDriven → BlitPmpIntoWorld/BlitL2mIntoWorld → RenderWorldArrays (BDB-block mesh) + NavGraphBuilder + DiggableTerrain.Init.\n- `new <Class>` → RuntimeWorldSpawner (entities).\n\nTWO integration changes needed beyond de-stubbing:\n1. INCREMENTAL terrain. Today RunWorldScript (DigglesWorldRuntime.cs:697-701) evals the campaign then calls EmitTerrain ONCE. Runtime growth stamps MapTemplateSet repeatedly over time, so after each load_next-driven stamp the new region must be meshed+nav'd. RuntimeMapBuilder already ACCUMULATES all placements; the straightforward hook is a second EmitTerrain-style pass over the now-larger _pmps/_l2ms set (BuildFromTclDriven recomputes the render bbox from min/max placements at BdbPmpWorldBuilder.cs:258, so a larger set just produces a larger world). For perf, prefer the existing DiggableTerrain debounced path (RemeshFromArrays @275 + nav re-bake) generalized to GROW the world arrays + expand the bbox before re-render (currently RemeshFromArrays re-renders the SAME bbox from already-sized arrays). Debounce so a burst of stamps coalesces into one re-mesh.\n2. DRIVE the StoryMgr idle tick. The StoryMgr `callback` think loop (sm_register this \"callback\", story.tcl:1020) must be ticked from the sim clock (EngineTickService/DigglesGameSim) so load_next runs one piece per pass and sm_scan refills when the buffer drains. Coordinate convention stays (X, palette.z, -tile_Y) — confirm large x/y offsets (far-from-origin growth) stay consistent (the initial campaign only stamps near origin; verify a far template lands correctly — Risk).\n\nDe-stub mechanics: the lg_*/sm_* names are in GetBulkStubNames (DigglesTclEngine.cs:1894-1957) and registered LAST via RegisterStubs, which SKIPS names already registered (line 1889). So register the real lg_*/sm_* commands BEFORE the RegisterStubs call (alongside the existing sm_map_set/get at 1819-1821) and they win automatically — no need to edit the stub list.

## lg_* COMMANDS

## IMPLEMENTATION PLAN
1. **1. Port the.ent door-marker loader + PieceDef metadata (FUN_005826d0)** — New LgTemplateMeta: given a template.tcl name, derive PMP base, read PmpData.Width/Height (already exposed, PmpData.cs:10-11) and parse data/templates/ent/<base>.ent lines `map_entry 1 Xpx Ypx`. Convert px→subcell ((px+sign*7)>>3 &~3) and assign a 0..3 edge dir from boundary vs W/H. Pick the PMP base by reading the template's actual map_template arg (most robust) OR the documented stem rule. _(test: Headless: assert LgTemplateMeta.Load("gng_050") yields W,H matching PmpData and 2 doors at subcells (0,10) and (20,10) [from gng_050.ent: 0/80 and 160/80, /8] on OPPOSITE edges. Assert gng_001.ent → doors at (0,2),(4,2).)_
2. **2. Port the config setters as real C# data structures** — Implement lg_addfilterclass, lg_set_templategroup, lg_set_templategroupprops, lg_set_templateprops, lg_set_templategroupvalue, lg_set_templatevalue, lg_get_temp_size → populate LgTemplateRegistry. Register these BEFORE RegisterStubs so they win. lg_get_temp_size returns {W H}. _(test: Headless: after evaluating lginit.tcl+lgtools.tcl on the interp against real data, assert Registry.Groups contains "Urwald.Std.Gng" with the globbed urw_gng_* files, that.hol/.gsg groups have LevelCount==1, and lg_get_temp_size urw_gng_050_a.tcl returns the PMP W/H.)_
3. **3. Port the active-pool filters (lg_tp_*)** — lg_tp_clear, lg_tp_addtemplatesets (resolve "Zone.Std" → all child group templates into pool), lg_tp_objfilter (drop pieces whose ObjClasses ∩ list), lg_tp_setfilter (restrict pool to one group), lg_tp_log (debug dump). Pool = List<int> piece indices. _(test: Headless: lg_tp_clear; lg_tp_addtemplatesets "Urwald.Std"; assert pool size = count of urw_gng_*+gsg_*+hol_* templates; lg_tp_objfilter {Troll Zwerg} drops any troll-bearing pieces; lg_tp_setfilter "Urwald.Std.Gsg" → pool == only gsg templates.)_
4. **4. Port the per-run config (area/objcount/ratio/leveltype/starttemplate)** — lg_set_objcount (record {class,min,max,stride}), lg_set_templateratio (a min max → FillTarget Opt/Max), lg_set_leveltype (default/base/other→0/1/2), lg_set_area, lg_mark_area, lg_set_starttemplatepro <mode> <relaxOK> → choose a start piece + rect, return {x y w h utemp} (utemp=unique name or "" or "nodigmark"). _(test: Headless: lg_set_templateratio 0 0.10 1.0 → FillTargetOpt≈0.10, Max≈1.0; lg_set_leveltype base → LevelType==1; lg_set_starttemplatepro 1 1 returns a 5-element list with a non-empty start template name and w>0,h>0 for a normal Urwald cell.)_
5. **5. Port TryPlace + MergeEdges + corner-validator (FUN_005842c0 / 00583f60 / 00584120)** — The core placement primitive over the int[] occupancy grid: bounds+4-align, overlap scan, tentative MergeEdges, margin paint, corner-connectivity reject, on-pass interior paint + record append, on-fail rollback. MergeEdges adds/removes Frontier points (mating pair removal). _(test: Headless unit test on a synthetic 16x16 area: place a start piece at (0,0); assert Grid interior==2, Frontier has its open doors; TryPlace an overlapping piece → fails, Grid unchanged; TryPlace a mating piece docked to a door → succeeds and the mated door is REMOVED from Frontier (count decreases).)_
6. **6. Port the cost heuristic (FUN_00586710) incl. rand jitter** — Cost(tpl,P): group-cap reject, object-spacing reject, fill-overshoot reject, START-piece very-low score, ratio-pressure + rand jitter scaled by baseScore. Seed the RNG (see Risks). _(test: Headless: with one group at levelcount=1 already placed once, Cost of a second same-group piece == INT_MAX (rejected). A fully-open start piece scores lower than any room piece. Determinism: same seed → same cost ordering.)_
7. **7. Port lg_start grow loop + lg_get_level + lg_sort_level** — Assemble SeedOrAdvance + the candidate-sort + opposite-edge docking grow loop gated on fill target; FINALIZE (cap/route open doors via grid BFS FUN_00587480 + dead-end caps); object-count validate; on any unsatisfiable → return empty. lg_get_level serializes {name x y} triples; lg_sort_level distance-sorts. _(test: Headless (THE key gate): configure Urwald.Std pool + a 32x32-cell free rect + ratio 0,0.10,1.0, run lg_start, assert lg_get_level returns a NON-EMPTY Tcl list of well-formed {name x y} triples where every name is a real urw_*.tcl, x/y are multiples of 4*resolution within the rect, and no two pieces' footprints overlap. lg_sort_level 0 0 <list> returns the same set nearest-first.)_
8. **8. Port the sm_* StoryMgr zone/scan/bookkeeping layer** — sm_get_resolution→32, sm_get_zone(worldY)→band name (from sm_add_zone bands), sm_add_zone/sm_add_temp (record bands+specials), sm_get_level_info(x,y)→free-rect {x y w h name}, sm_scan→first un-grown cell near a gnome (port get_next_areas: walk Zwerg positions, pos/resolution, skip sm_map_get==1), sm_set_temp/sm_get_temppos (dedup), sm_draw_stone (-border/-funnel already exercised; -exlimit/-utmat post-growth painting), sm_set_zone/sm_force_zone (override bands at runtime). Reuse ctx.StoryMap. _(test: Headless: sm_get_resolution==32; sm_get_zone of a low Y → "Urwald"; after placing 2 gnomes (or stub positions) in un-grown cells, sm_scan returns the nearest cell {x y}; sm_get_level_info returns w*res>=16,h*res>=16 for an open region. Run generate_next x y end-to-end on the interp (no divide-by-zero) and assert temp_buffer becomes non-empty with valid triples.)_
9. **9. Wire de-stub: register real lg_*/sm_* before RegisterStubs** — Move/add the real command registrations ahead of the RegisterStubs(GetBulkStubNames) call (RegisterStubs skips already-registered names, DigglesTclEngine.cs:1889) so no stub-list edit is needed. Keep map_template/map_setlayer2/split_template as-is (already real). _(test: Headless: after engine init, CommandExists for every lg_*/sm_* is true and each dispatches to the real impl (not "0"). Evaluating story.tcl's generate_next no longer short-circuits.)_
10. **10. Incremental terrain emission + StoryMgr tick** — Make EmitTerrain re-runnable to GROW the world arrays + expand the render bbox (generalize RemeshFromArrays @BdbPmpWorldBuilder.cs:275 + nav re-bake), debounced after a burst of MapTemplateSet stamps. Drive the StoryMgr `callback` idle think-loop from EngineTickService so load_next runs one piece/tick and sm_scan refills. _(test: Play-only: start Urwald, dig toward the colony frontier so sm_scan fires, observe a NEW cave area appear over several ticks (one piece per tick), with terrain mesh + nav graph extended and walkable; eventually urw_uebergang_swf places at the zone border (reaching Metall).)_

## RISKS
- DEEPEST UNKNOWN — per-template connection points. Confirmed source = data/templates/ent/<base>.ent (`map_entry 1 Xpx Ypx`, type 1=door, px=8*subcell; verified: gng_050.ent=0/80,160/80 → subcells 0,10 & 20,10). 1229.ent files exist. The exact edge-dir LABEL (0=left/1=top/2=right/3=bottom vs the Finding-2 bottom/right/top/left) is ambiguous across findings — but the load-bearing invariant (doors mate when OPPOSITE, (d-t)&3==2) is convention-independent, so pick ONE labeling and apply it uniformly. Quantization px→subcell ((px+sign*7)>>3 &~3) needs a targeted FUN_005826d0 re-read for bit-exactness.
- PMP-base derivation: lg_* operates on template.tcl names (urw_gng_050_a.tcl) but.ent/.pmp use the PMP base (gng_050). The mapping is the map_template arg inside each MapTemplateSet body, NOT a pure name rule (zone prefix + variant suffix stripping is a heuristic). Safest: parse the base from the template's map_template call; flag if any template deviates.
- RNG seeding/determinism: native uses rand (FUN_0049c670) with unknown seeding. Save-game/replay parity would require matching the seed; unknown whether the original even seeds deterministically. For the port, per-run randomness is acceptable for normal play but breaks exact save reproduction — flag and use a stored seed if save-parity is later required.
- FILL-RATIO/cost constants: FUN_00586710's banded ratio-pressure thresholds + tunnel weights (0.5/2.0) and the global fill cap live in.data segment floats not fully recovered. The minimal grower can use a coarse fill target; the faithful version needs those exact floats for matching placement distribution.
- sm_scan source ambiguity: the Tcl get_next_areas (story.tcl:24-49, scans Zwerg positions vs sm_map_get) is a documented reference, but native sm_scan may be FUN_0058b130 (a separate beautifier-grid scan). Port the Tcl get_next_areas semantics first (it's the documented frontier-near-colony behavior); verify against StoryMgr.cpp if growth targets look wrong.
- sm_draw_stone -exlimit/-utmat (post-growth stone-border + unique-material painting) semantics not fully RE'd; affects which tiles become diggable cave vs solid stone and material/scape at seams. The -border/-funnel variants are already exercised by the initial Campaign.tcl so must at least no-op gracefully. Fidelity of -exlimit/-utmat is a separate channel that may interact with the existing L2M/cellId scape port — flag for a follow-up RE pass.
- INCREMENTAL re-mesh perf + bbox growth: RemeshFromArrays currently re-renders the SAME bbox from already-sized arrays; runtime growth needs to GROW the world arrays + expand the render bbox, then re-mesh + re-bake nav. Doing a full RenderWorldArrays over an ever-expanding bbox each stamp may be expensive — prefer chunked re-mesh of only the newly-stamped sub-region (debounced), reusing DiggableTerrain's partial path.
- Coordinate consistency far from origin: the initial campaign only stamps near origin; verify a template stamped at a large x/y offset lands correctly in the (X, palette.z, -tile_Y) frame before relying on growth.
- FUN_00587480 (8251 bytes, the corridor-route/object-count placement) is the single largest unread function. The faithful finalize (route open doors) and faithful object-count spread depend on it; needs a dedicated RE pass for a full port. The minimal grower sidesteps it with dead-end caps + simple spacing.

## `lg_add_preset` / `lg_tp_clear` lifecycle

`lg_add_preset <name.tcl> <x> <y>` is the authored-layout lane, not an MP-only cosmetic.
`FUN_00651c80` requires exactly four Tcl arguments including the command name and otherwise
returns no result. Its worker `FUN_0058a510` first requires a configured generator area, then
finds or registers the template through `FUN_00583080 → FUN_005826d0`; an invalid metadata load
is unregistered and skipped silently. The supplied coordinates remain area-relative in the
forced record, while validation uses `(x + areaX0, y + areaY0)`: the absolute origin must be
4-aligned, at least eight tiles inside the map, and satisfy the strict upper bounds
`absX + W < map.x1 - 8` / `absY + H < map.y1 - 8`. `FUN_0058a510:42-61` also rejects any
footprint node whose templated bit is already set. The port uses `HasTpl` when stamped world
arrays are reachable; between a fresh `map create` and first emission those arrays do not exist
and the footprint is known clean, with `lg_start` retaining the ordered placement re-probe.
Success appends to the same ordered `+0x194`
list modeled by `_forcedRecords`; `lg_start` places those records first in call order and
`lg_get_level` emits absolute coordinates with the `.tcl` name intact.

`lg_tp_clear` owns that list's lifecycle. `FUN_0064e4f0 → FUN_00583650` clears not only the
template pool, but also the `+0x198` forced-record count, `+0xa8` connection count, and `+0x180`
mark-area/reserved-rect count, while resetting the area and seed state. Consequently every
Campaign/free-game setup starts from one authored preset set, while story growth remains safe:
`story.tcl` clears before configuring its start template and invoking `lg_start`.
