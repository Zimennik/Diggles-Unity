> 📜 RE SOURCE REPORT (1 of 6,) — one agent's raw deliverable from the max-effort worldgen
> RE workflow. Disasm facts are ground truth; port-status notes here PRE-DATE the 1:1 rebuild (43805a0).
> Consolidated + current status: **Docs/WORLDGEN_ORIGINAL_RE.md**.

I have everything needed with certainty. Let me write the final deliverable.

Here is my complete, cited spec and diagnosis.

---

# CAVE-GROWTH CORE — RE spec + variety diagnosis (100%-certainty pass)

Working files: probe at a scratch folder (not in the repository) (standalone build of the real `LevelGenerator.cs` against real the original data folder, dotnet 8). Port under diff: `Assets\Diggles\Runtime\World\LevelGenerator.cs`.

## 1. THE GROW LOOP — `lg_start` = FUN_00589790 (exact, cited)

The binding is **FUN_00652bc0** (`CTclGlobalMethods::LGStart`, trace-string @0x730...:28): it calls **FUN_0058a100** (reset) *then* **FUN_00589790** (grow) *then* frees the grid (:48-53).

- **FUN_0058a100 = per-generation reset** (this is where "per-level" state is cleared): zeroes the objPlaced table +0x94 (:46-56); **zeroes every template's placedThisLevel +0x24 in a 0x54-stride loop (:57-71)**; zeroes filled +0xa0 (:72); reallocs+memsets the grid +0x84, sets area +0x88 (:77-82); counts free cells into +0x9c (:87-104). It **does NOT touch template +0x50** (EmittedThisSession → session-persistent). ⇒ the port's `foreach p.PlacedThisLevel=0` in `Start` and its session-persistent `EmittedThisSession` are **faithful**.
- **FUN_00589790 body**: forced placement **FUN_005896a0** (:64) → connect-routing loop **FUN_00587480** (:69-115, non-empty only for `-connect` uniques) → mark-area paint =1 (:117-171) → **the grow `do…while`** (:172-259):
  - `FUN_005862c0` seed/advance (:173); fail ⇒ rollback.
  - inner `while(frontier>0)` (:182-257): head door = `frontier[0]` (`+0x8c`[0..2] = x,y,dir,:186-191); `FUN_00586f40` cost-sorts the whole pool ascending (:196); scan candidates, for each try only edges with `((edge.dir − doorDir)&3)==2` (:223), place at `(doorX−edge.lx, doorY−edge.ly)` via `FUN_005842c0` (:234); first success sets ok, breaks to reload the frontier (:236-256); head undockable ⇒ `ok=0`, break (:252).
  - `FUN_005866a0(0)` (:258) is a **verified no-op** (compares +0x74 to a const, returns either way).
  - outer condition `fill/total < ratioA(+0x70) && ok` (:259). **story.tcl passes ratioA=0** (`generate_next`:206 `lg_set_templateratio 0 0.10 1.0`; arg→field FUN_00652830:140-154) ⇒ **one seed cluster per generation**.
- **Closure/termination law** (:262): head-door-undockable ⇒ `ok=0` ⇒ **the WHOLE generation fails & rolls back** (`lg_get_level` empty ⇒ story failcount retry). Success ⇒ every door mated/capped. `FUN_00585390` finalize = dead-end prune, failure-tolerant (cosmetic; unported).

The port's `Start` / grow loop / `SeedOrAdvance` / `Fail` are a **line-faithful** port of the above (diffed each branch; the only extra is a `guard<8192` safety net that never trips). **No grow-loop divergence.**

## 2. RNG draw order/count — FUN_0049c670 (shared MT19937, const-seed 0x1105)

Per generation, with a **forced start** (the dig case): `FUN_005862c0` draws **0** (forced-record path returns at LAB_00586523 without seeding,:24). Then per frontier door, `FUN_00586710` draws **exactly one** jitter (`:264`, at LAB_00586ea2) **per VIABLE candidate** — pieces that early-return INT_MAX (over-max / object-once-gate / overshoot / levelcount / tunnel) and the 4×4 seed short-circuit (:98-100) draw **nothing**. No-forced seeding draws **2 per placement attempt** (`:89-90`, ≤16). The port matches this draw discipline exactly.

## 3. ⭐ THE VARIETY MECHANISM — empirically pinned (this is the core answer)

I ran the real Urwald.Std pool (185 pieces after `lg_tp_objfilter {Troll Zwerg}`, Hol value 0.287, Hol/Gsg levelcount 1, opt 0.10) through the **actual port code** many times:

| experiment | distinct clusters |
|---|---|
| **F** forced start, fixed pose, 300 **independent** seeds | **2 / 300** |
| A/B one session vs 30 indep seeds, fixed pose | 2 / 30 |
| D area sweep 2k→33k tiles, indep seeds | 1–2 / 30 |
| E opt sweep 0.05→0.80 | 1–2 / 30 |
| **I** forced start, **varied pose**, 60 seeds | **23 / 60** |
| **H** SEED mode (random position), 60 seeds | **50 / 60** |

**Conclusion the numbers force:**
1. **The cost jitter is a near-powerless tiebreaker.** With the pose fixed, even *fully independent* RNG yields only **2** clusters. Reason (all faithful to FUN_00586710): the size term `2·|20−(W+H)|·(2−progress)·value·100` separates distinct pieces by hundreds of cost units, while the jitter `(rand&0x7fff)/32768·(1−progress)·value·100` contributes ≤~26 and dies to 0 once `progress≥1` (which happens after ~2 caves, since a single Hol cave ≈128–288 tiles vs the opt target of 0.10·2048≈205). It only ever flips one exact tie (`urw_hol_005_a↔_b`). Area/opt don't change this (Exp D/E).
2. **The dominant variety driver is the SEED/START-PIECE POSE relative to the growth area** (Exp F 2/300 → I 23/60 → H 50/60). Different relative pose ⇒ different frontier geometry ⇒ a different greedy chain unfolds.

**Why plain L/R digs are deterministic (and this is FAITHFUL):** the dig path uses a *forced* start (no seed randomness — FUN_005862c0:24), the start piece is a **1-door** chamber (`gng_021`/`022`; all 22 variants share one pmp/ent, door @32,16 — identical geometry, differ only in object dressing), and its pose is `breakthrough+1cell`. The area is the `sm_get_level_info` rect **CENTERED on the breakthrough**: FUN_005fac90 passes **mode 1** to FUN_005fa9a0 at both call sites (:781,:828), case 1 = centered (FUN_005fa9a0:case1); it only diverts to case-0 **random** position when a **utemp is matched** (`+0x54 != -1`), and FUN_005fc0a0 **early-returns for non-utemp** (:27-29). So on virgin terrain the breakthrough sits at the rect center ⇒ **constant area-relative start pose ⇒ deterministic cluster in BOTH the original and the port.** The port centers exactly here too (`FreeRectFit` mode 1, `hasUtemp?random:centered`) — **faithful.**

**Therefore the original's per-breakthrough variety for plain caves does NOT come from the grow core.** It comes from *pose variation*, whose only real sources are:
- **(a) Utemp/story caves** — matched probabilistically by depth/zone; they take the **case-0 RANDOM position** (FUN_005fa9a0), so each is placed at a varied pose ⇒ visibly different large composites. (Exp H's 50/60 = the same case-0 random-position effect.)
- **(b) Accumulated prior-cave geometry** — `sm_get_level_info`'s raw free-rect expansion stops at grown story cells (FUN_005fac90 strip probes / port `StripFree==1`); once the world fills, the raw rect is asymmetric so the *centered* fit puts the breakthrough **off-center** ⇒ pose shifts ⇒ cluster varies. Straight digs into *virgin* rock ahead of the tunnel keep it symmetric ⇒ same pose ⇒ same cluster.
- (c) Start-chamber object dressing (021_a vs 021_k…) — cosmetic.

## 4. PORT DIVERGENCES + fixes

1. **SPLIT-TEMPLATE DOOR DROP (real bug — ALREADY FIXED in the working tree).** `LoadMeta` used to set `split=true` and return before loading doors, so all **22 of 40** Urwald Hol caves that are `split_template` (hol_011/013/014/017/018/019 families — the big through-caves) loaded with **0 doors ⇒ undockable ⇒ silently excluded from growth**. Their doors live in the *parent* pmp's `.ent` (e.g. `hol_011.ent` = left+right through-cave), which the loader skipped. Objectively wrong. The current file (LevelGenerator.cs:1114-1171, an uncommitted parallel-agent edit) sets `p.PmpBase` from the split header's pmp arg and guards only the pmp-dims re-read with `!split` — **this is exactly the correct fix; I independently verified it** (probe Exp C: the 22 caves now load 1-/2-door signatures matching their `.ent` files). Native justification: the native resolves every template through FUN_00583080→FUN_005826d0 whose `.ent` read runs *outside* the split guard. **Keep it.** ⚠️ But my probe proves it is **necessary-but-not-sufficient** for the monotony: Exp F stays **2/300** after the fix, because the size cost excludes the big split caves from plain-dig docks anyway. Its real payoff is pool completeness for the SEED/utemp/composite paths and filled-world docking.

2. **The plain-dig monotony itself is NOT a grow-core bug.** The grow loop, cost, seed, reset, and rect-centering are all faithful (cited above). Chasing more jitter/tie-breaking in the cost would *diverge* from the decompile. The highest-leverage fix for player-visible variety is **outside my dimension**, in the utemp/anchor/rect path: **(i) make story/utemp caves actually generate** (they take the case-0 random position ⇒ the big visible variety; the docs repeatedly flag "story caves never generate" as an open bug), and **(ii) verify accumulated geometry shifts the pose** (that `sm_get_level_info`'s asymmetric raw rect + centered fit is actually moving the breakthrough off-center in a filled world). If either is broken, the port shows only the deterministic plain caves while the original interleaves varied story caves — precisely "a monotonous long set of similar caves."

Minor flagged deltas (negligible, not the cause): FUN_00586710 objPress computes the `a2` term even when `progress==0` (native skips it,:168) — but `progress>0` always after a forced start; and FreeRectFit consumes a rand when `targetW==w` in the native but the port guards it (rect dimension, plain-growth-irrelevant).

## Bottom line
The grower is faithful. Digging L/R yields deterministic clusters because, by faithful design, the forced 1-door start sits at a **constant pose in a breakthrough-centered rect**, and the cost jitter is too weak to vary a size-narrowed, door-constrained greedy chain (empirically 2 distinct per 300 independent RNG seeds). Real per-breakthrough variety is **pose-driven** and lives in the **utemp story-cave path (case-0 random placement) and accumulated-geometry rect-shifting**, not in FUN_00589790/FUN_00586710. The split-template door fix (already applied) is correct and restores 22 undockable caves to the pool but does not by itself change plain-dig output.