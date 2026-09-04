> 📜 RE SOURCE REPORT (6 of 6,) — live minidump cross-check. Disasm/dump facts are ground
> truth; port-status notes PRE-DATE the 1:1 rebuild (43805a0). Consolidated + current status:
> **Docs/WORLDGEN_ORIGINAL_RE.md**.

All the static ground-truth is now nailed down. The live registries exist (counts 59/27/23/82 at StoryMgr+0x00/08/10/20) but their per-record fields are nested CTemp sub-objects that don't resolve without a full class RE — the strings found are heap-adjacent game text/Tcl, not registry names. Per the task's anti-fabrication rule I'll report that honestly and fall back to the Tcl source as the registry's authoritative model. I have everything needed for the deliverable.

---

# GROUND-TRUTH (live dumps) + RANKED PORT-GAP AUDIT — worldgen / StoryMgr / LevelGenerator

## 0. Dump viability & binary identity (PROVEN)
- Tooling: `minidump` (pip) parses both 1 GB full-process dumps; VA→bytes works. Both are **ASLR-relocated**: `diggles_loaded.dmp` load 0x5d0000 (Δ**+0x1d0000**), `diggles_meadow.dmp` load 0xa70000 (Δ**+0x670000**).
- **Same binary as the decompile**: on-disk `Diggles.exe` ImageBase=0x400000, SizeOfImage=0x8ed000, EntryRVA=0x2cf39b — all three match both dumps' in-memory PE. Globals live in `.data` BSS (VA≥0x367000, materialized at runtime). Every VA below is read at `VA+Δ`.

## 1. GROUND-TRUTH RUNTIME STATE (concrete offsets/values)

**MT19937 RNG** — state `DAT_00767990` (624 words), index `DAT_00768350`; sole seeder `FUN_004454d0(0x1105)` (mult 0x10dcd=69069 = classic 1997 `sgenrand`), called only from `FUN_0049c670:33` + `FUN_00598f10:38`.
- loaded: index=**450**, all 624 words non-zero, state[0..7]=`679b9e94 3e6f7fa7 b8d5606f 7b2d34cc 20471f96 092b4070 c6777c9a 84ee2e44`.
- meadow: index=**14**, all 624 words non-zero (different stream position → different session consumption).
- ⇒ MT is live; the constant-0x1105 seed mechanism is verified from decompile; state evolves per session as every engine system draws from the one shared stream.

**CStoryMgr** = `*(DAT_00772474)` (loaded 0x1cb3bd44 / meadow 0x2a1a3ca4). Layout proven by ctor `FUN_005fa7c0`:
- **digcount `+0x9c` = 32 in BOTH dumps** (ctor default is 5 at FUN_005fa7c0:32) → confirms `sm_set_digcount 32` (the campaign pacing constant); port `ctx.StoryDigThreshold`, engine:3520. ✓
- latch dir/x/y `+0x88/+0x8c/+0x90` = 0/0/0; consumed byte `+0x94` = 0; weight `+0x98` = 0 → no breakthrough latched at capture (dumps at rest).
- rect `+0x38/+0x3c/+0x40/+0x44` = 0 (the sm_get_level_info fit result, FUN_005fac90:833-836) — idle.
- **Registry arrays LIVE & populated**: `+0x00/+0x04`={ptr,**59**}, `+0x08/+0x0c`={ptr,**27**}, `+0x10/+0x14`={ptr,**23**}, `+0x20/+0x24`={ptr,**82**}. (These are the registered temps/zones; see §3 flag.)

**CLevelGenerator** = `*(DAT_00772414)` (loaded 0x1bfd24a4 / meadow 0x1c866a0c). Layout proven by ctor `FUN_00581470` + SetArea `FUN_00583ac0`:
- **Both dumps: freshly-constructed, NEVER configured for a growth run.** ratio `+0x70/+0x74/+0x78` = **0.0 / 0.5 / 2.0** = ctor defaults (a live generation would show story.tcl's 0 / 0.1 / 1.0 via FUN_00652830); area `+0x50/+0x54/+0x58/+0x5c` = 0; ALL runtime lists NULL/count 0 — forced `+0x194/+0x198`, placed `+0x184/+0x188`, objPieces `+0x18c/+0x190`, connect `+0xa4/+0xa8`. ASCII bleed ("material"/"class_ans") sits at exactly the fields the ctor leaves un-zeroed (`+0x60/+0x64/+0x7c`) → confirms an idle post-ctor block.
- ⇒ **No in-flight/completed lg_* generation was captured in either dump.** The placed-level / forced-record / frontier CONTENTS the task hoped to read are simply not present. (Stated explicitly per the anti-fabrication rule — I did not invent them.)

**Map** `DAT_0077241c` (loaded 0x03348e2c / meadow 0x038d0e7c): bounds `+0x84/+0x88/+0x8c/+0x90` = **0,0,512,640** in BOTH → confirms the 512-wide × 640-tall map and the ≥8-tile inset gate (FUN_005848b0:37-46 / FUN_0058a510) the port models as MapW/MapH.

## 2. CROSS-CHECK of the spawn-priority / grow-variety claims

**Cost constants** — read live from dump `.data` (loaded, Δ0x1d0000); **all 17 match the port's hardcodes exactly**: 1/32768@0x736f94, 0.3@0x737060, 0.5@0x73708c, 0.6@0x73709c, 0.8@0x7370d4, 1.0@0x737138, 1.3@0x737180, 1.5@0x7371c0, 2.0@0x737330, 2.5@0x7373d8, 3.0@0x737428, 10@0x737534, 100@0x73760c, 200@0x737644, 250@0x737650, −1@0x73773c, fabs-mask 0x7fffffff@0x737940.

**Cost function field reads** (`FUN_00586710`) confirm both "same caves" fixes are faithful:
- once-gate `:58-60` — `if (*(char*)(piece+0x41)) if (0 < *(int*)(piece+0x50)) return INT_MAX` → the SESSION-emit gate. Port `EmittedThisSession` (+0x50), not PlacedThisLevel. ✓
- capTerm `:25` — `iVar3=INT_MAX; if (piece+0x41) iVar3=*(int*)(piece+0x1c); if(5<iVar3)iVar3=5` → +0x1c is **-gamecount remaining**, ×200 (const@0x737644); `placedThisLevel(+0x24)*250` (const@0x737650). Port `GameCap` (+0x1c). ✓
- spacing `:63-89` — `|dx|·|dy| < r*1.5` over objPieces `+0x18c/+0x190`. ✓; levelcount `:120-122` = piece +0x20 vs +0x24. ✓

**Engine wiring** (DigglesTclEngine.cs) faithful: `lg.Rng = ctx.StoryRng` shared (3411/4220); weight-reset `ctx.StoryDigWeight=0` ONLY on emit + `CommitSessionEmission` (3467/3470); ratio binding argv→A/opt/max (3401); digcount→threshold (3520, matches dump's 32).

**Verdict on the core engine:** seed→dock→cost-sort→closure→emit, the pacing/re-arm law, the two variety fixes, all field offsets, and every cost constant are **ground-truth-faithful**. Do NOT re-chase these.

## 3. RANKED PORT DIVERGENCES (most impactful first)

1. **[HIGH] RNG substitution changes the *texture* of variety (and forfeits reference reproducibility).** Native = ONE shared MT19937 const-seeded 0x1105, advanced by EVERY engine system (map-init FUN_00598f10 alone burns ~512×640 draws) so each generation samples a heavily-decorrelated stream position. Port = a session-seeded `System.Random` consumed ONLY by the sm_/lg_ layer → consecutive generations see a barely-advanced, strongly-correlated stream. Consequence: cannot reproduce any specific original playthrough; consecutive caves are more RNG-correlated than native (only partly masked by the verified session-thinning once-gate). *Acknowledged/likely-unavoidable, but it is the deepest structural divergence.* (port LevelGenerator.cs:121)

2. **[MED-HIGH] Unique-temp selection is an explicitly UNVERIFIED empirical guess.** The depth-band eligibility + selection probability that decide WHICH story cave (einsiedler/voodoo/troll/feen/uebergang) spawns at which depth (FUN_005fac90:634-820 /:284-313) is approximated; the port's own comment says so: DigglesTclEngine.cs:4104-4105 "the exact native selection order was to be confirmed by the RE pass that hit the Fable spend limit — flagged for follow-up." Also the high-band gate + zoneborder exemption (4098-4106) are hand-tuned, not decompile-derived. Directly governs campaign story content.

3. **[MED] Corridor router is a non-faithful approximation — AND its live-path status is self-contradicted in the project docs.** FUN_00587480's wavefront flood (FUN_0058ee60 + per-relaxation rand jitter) is realized as a plain BFS; the variation-gate constants DAT_00737058 (rate), DAT_007376dc (K), persistent accumulator DAT_00c3c9b8 are un-extracted → guessed `_varRate=1/_varK=250` (LevelGenerator.cs:728-732). The port header (253-276) says the router runs live for utemp connections (FUN_005fc3b0→FUN_00587480); the audit note says it "NEVER runs" in shipped data. **Either way the port diverges** — it either approximates a live router or runs one the native doesn't. Affects story-cave corridor shape + side-branch rate.

4. **[MED] FUN_005fc0a0 utemp rect-recompute not ported.** For utemp generations the port keeps the centered sm_get_level_info rect instead of re-deriving the cursor-anchored rect sized past the template (DigglesTclEngine.cs:4146-4150, 4270-4340); the watermark clamps (+0x58..+0x64) and the FUN_005fa9a0 claim-modes are "remaining unknowns" per the port. Alters utemp generation rect shape/position.

5. **[LOW-MED] `-mindist` reinterpreted from a possibly-degenerate native path** (DigglesTclEngine.cs:4107-4120: "the native's distance term is degenerate…"). Affects unique-temp gating near the map center column.

6. **[LOW] finalize / dead-end prune (FUN_00585390) not ported** (LevelGenerator.cs:44-46, cosmetic) → occasional stubby 1-door dead-end pieces the original would delete/cap.

7. **[LOW] Composite utemp registered at the walked pose vs native away+routed** (LevelGenerator.cs header item 8) → composites positioned differently than native.

8. **[LOW / UNCERTAIN] Cost spacing reference point** — port uses the frontier door P.X/P.Y (Cost step 2b); native `param_3/param_4` identity (door vs candidate placement origin) could not be confirmed from the caller. Flagged, not asserted.

## 4. Explicitly could NOT prove (no fabrication)
- **Live grow-state contents** (forced +0x194 / placed +0x184 / frontier / area) — the CLevelGenerator was idle (post-ctor defaults) in BOTH dumps; nothing to read.
- **Unique-temp registry per-record fields** — the StoryMgr registries are LIVE (counts 59/27/23/82 confirmed) but records are nested CTemp sub-objects (each = 4 heap pointers); resolving them needs a full CTemp/CTempGroup class RE. Not extracted → the port's `StoryTemps` is correctly modeled from the **Tcl source** (CampaignInit.tcl `sm_add_temp`), the authoritative registry definition.
- **Corridor-variation floats** DAT_00737058 / DAT_007376dc / DAT_00c3c9b8 — `.data` BSS; would need a live in-corridor capture; the port's guesses stand unverified.
- **Cost spacing `param_3/param_4`** identity (item 8 above).

**Scratch artifacts** (reusable dump tooling): a scratch folder (not in the repository) (auto-detect ASLR delta from the diggles.exe module, then read any global at VA+Δ).