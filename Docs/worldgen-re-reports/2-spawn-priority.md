> 📜 RE SOURCE REPORT (2 of 6,). Disasm facts are ground truth; port-status notes PRE-DATE
> the 1:1 rebuild (43805a0 — B1 fit test + B2 cutoff-removal now landed). Consolidated + current status:
> **Docs/WORLDGEN_ORIGINAL_RE.md**.

I now have a complete, binary-verified picture. All weight-law constants confirmed from `.rdata`, the field map is internally consistent between the writer (FUN_0065cba0) and reader (FUN_005fac90), and the architecture is confirmed (selection in FUN_005fac90 → `+0x54`; placement in FUN_00650f90). Here is the deliverable.

---

# SPAWN PRIORITIZATION — `sm_get_level_info` = FUN_005fac90 (100% RE)

## A. THE EXACT NATIVE SELECTION / PRIORITY LAW

`sm_get_level_info(cellX, cellY)` (worker FUN_005fac90; Tcl binding FUN_00661340) runs in three stages and writes its result to the StoryMgr object: rect → `+0x38/+0x3c/+0x40/+0x44` (W,H,X,Y), **selected utemp index → `+0x54`** (`param_1[0x15]`), scan cell → `+0x48/+0x4c`, `+0x68=0`.

### Stage 1 — free-rect expansion (FUN_005fac90:67-117)
4-directional grow of the un-grown region around the scan cell via `FUN_005fa8e0` (which returns 0 if any probed story-grid cell == 1 "grown", FUN_005fa8e0:39-40). Probe order **left(:78), up(:89), down(:98), right(:106)**. Result stored to `+0x58`(left)/`+0x5c`(top)/`+0x60`(right)/`+0x64`(bottom) (:114-117). **The port replicates this faithfully** (CmdSmGetLevelInfo:4054-4072).

### Stage 2 — candidate scan (:128-330), per registry entry (stride 100 = 0x64), in registration order
A temp becomes a weighted candidate only if it passes ALL of:
- **G1 used** (:138): `entry+0x14 != 0` → skip. (Set by `sm_set_temp` when placed → each story cave is ONE-TIME.)
- **G2 min-depth** (:143): `depth < entry+0x20(min) + zoneBase` → skip. `depth = cellY*4`; `zoneBase = FUN_005fcdf0(depth)`'s matched zone `+4` (FUN_005fcdf0:29-42 + FUN_0040c060).
- **G3 events** (:223-254 via `local_14`): every `-events` entry must be active (`def[ev]+0xc != 0`). **No-op in shipped data** (no temp uses `-events`).
- **G4 registration-zone** (:254): `entry+0x50 != FUN_005fcdf0(depth)` → skip. `entry+0x50` = zone index at `sm_add_temp` time (the port's `rec.Zone`).
- **G5 mindist/middist** (:220): skip if `distMetric < entry+0x28(mindist)`.
  - `distMetric` init 0; = `‖refPos − candPos‖` only if the `-mindist` **ref temp** (`entry+0x2c`) is already placed AND `mindist>0` (:152-197). Because the candidate's own pos slot `+0xc/+0x10` is never written pre-placement, this degenerates to: **`-mindist` = "the referenced temp must be placed first"** (a prerequisite chain: trollburg after einsiedler, uebergang after trollburg, …).
  - `-middist` (`entry+0x60`,:203-215): if `middist>0` AND `abs(cellX − mapW/2)*4 < middist` → `distMetric = -1.0` → G5 skips. **= "must be ≥ middist/4 cells from the map's horizontal centre"** (center-exclusion).

**WEIGHT** (:261-314), verified against `.rdata` (all constants extracted from Diggles.exe):
```
weight = 0.8 if max == min (@0x7370d4; dead in shipped data)
       = entry+0x44 (-nonrelevant)                  if nonrelevant != 0 (:284-289)
       = (depth − min − zoneBase + 1) / (max − min) otherwise           (:309-313; RAMP)
```
`min = entry+0x20`, `max = entry+0x24` = `min(lo, hi+1)`/`max(lo, hi+1)` (FUN_0065cba0:238-249 — the `hi+1`). **The ramp exceeds 1.0 once `depth ≥ max+zoneBase−1`.** There is **NO high-depth skip** — the only use of `max` is this ramp. `{index, weight}` is appended to the candidate array.

### Stage 3 — SELECTION (:634-820), the priority draw
Iterate collected candidates **in registration order**; per candidate:
1. `r = (rand&0x7fff)/32768 ∈ [0,1)` (FUN_0049c670; `1/32768 @0x736f94`) (:641-642).
2. If the name == **`"urw_kif_4"`** (`DAT_0072c634`, verified from binary) → `r = 0` (force-pass,:669-671). *Dead in shipped data — kif_4 is only ever a `sm_def_temp_group` child, never an `sm_add_temp` registry entry, so it is never scanned.*
3. **Dice: if `weight < r` → decline, next candidate** (:675). (⇒ P(accept) = min(weight,1); a past-band temp with weight ≥ 1 is never declined.)
4. Placement checks + **THE FIT TEST** `FUN_005fa9a0(mode 1)` (:781): reject (→ next candidate) if the scan cell is grown (FUN_005fa9a0:21), **or the utemp's cell size exceeds the free rect**: `seedW > freeRectW` (:80) or `seedH > freeRectH` (:76), where `seedW=(utW+8)/4+1`, `seedH=(utH+8)/4+1`, `freeRectW=+0x60−+0x58`, `freeRectH=+0x64−+0x5c`. (-exclusive temps take an extra map-edge probe,:725-779.)
5. First candidate that passes **dice AND fit** → selected (`+0x54 = index`). **On fit-failure the loop FALLS THROUGH to the next candidate.**
6. If none → `+0x54 = -1`.

## B. NORMAL vs STORY generation
- **STORY**: Stage-3 selects a utemp (`+0x54 = idx`). `lg_set_starttemplatepro` (FUN_00650f90:245 branches on `+0x54 != -1`, reads it at:281/:457) then places it; `lg_start` emits it; `sm_set_temp` sets its used flag.
- **NORMAL**: `+0x54 = -1` ⇒ FUN_00650f90 takes the plain-growth path (walk start-piece + Std-pool growth only).
- So **story-cave frequency = P(some candidate passes the dice roll AND fits the free rect)**. The **fit test is the native's primary throttle**: as the colony grows and free rects shrink, big story caves stop fitting → the native returns `-1` → normal caves. Past-band one-time caves get `weight ≥ 1` (guaranteed dice-pass) but are still gated by fit + fall-through + the used flag, and are tried in **registration = depth-band order** (einsiedler 56-60 → troll/voodoo 60-88/110 → trollburg 88-128 → uebergang 132-164), with the wide-band `-nonrelevant` caves (feen 60-160) sprinkled in probabilistically behind them.

## C. WHY THE PORT OVER-PRIORITIZES STORY CAVES

The port's ramp/nonrelevant weight and the dice test are **faithful**. The over-picking comes from three deviations in `CmdSmGetLevelInfo`:

1. **⭐ MISSING FIT TEST (the headline bug).** The port picks the first dice-passing temp and `break`s (DigglesTclEngine.cs:4127-4137) with **no comparison of `seedW/seedH` against the free rect `rw/rh`**. The native rejects a utemp that doesn't fit (FUN_005fa9a0:76/:80) and falls through to the next candidate or to `-1` (normal). ⇒ **The port converts native-NORMAL generations into port-STORY generations** whenever a story cave wins the dice but wouldn't fit — the exact "story caves spawn when they should not." The port also has no fall-through: a dice-pass ends the search even if that temp is later unplaceable.

2. **⭐ INVENTED HIGH-DEPTH CUTOFF.** `if (wy > hi + zoneBase && rec.ZoneBorder.Length == 0) continue;` (DigglesTclEngine.cs:4106). **The native has no such gate.** It was added to stop einsiedler (band 56-60, big composite, ramp ≫ 1 past band) from being perpetually picked-and-failing — a symptom that only exists *because* deviation #1 removed the fit test. The cutoff strips every narrow-band one-time cave from eligibility once you dig past it, so (a) einsiedler/troll_012/troll_014 are **under-spawned** (lost if you pass their band un-placed — the native makes them `weight ≥ 1` = certain), and (b) the wide-band `-nonrelevant` cave **feen (60-160, weight 0.474 across the whole zone)** loses its higher-priority competitors and wins disproportionately early ⇒ "feen spawns with priority."

3. **Dropped `-middist` center-exclusion.** The port only evaluates `-middist` as a *rescue inside the `-mindist` block* (DigglesTclEngine.cs:4112-4119). No shipped temp has both, so for `feen`/`kris_wasser_raetzel`/`kris_unq_raetsel_002`/the lava temps (middist-only) the block is skipped and **`-middist` is never applied** — they spawn in the central column the native forbids (FUN_005fac90:203-215). Minor, but widens their spawn footprint.

## D. THE EXACT FIX (in `CmdSmGetLevelInfo`, DigglesTclEngine.cs:4080-4137)

1. **Delete the high-depth cutoff** (line 4106) — not native.
2. **Add the free-rect fit test** with **fall-through** (mirrors FUN_005fa9a0:76/:80). Compute the seed size *before* committing and `continue` (don't `break`) when it doesn't fit:
```csharp
if (weight < ctx.StoryRng.NextDouble) continue;
var (tw, th) = StoryTempSize(ctx, rec.Name);
int sW = tw > 0 ? StoryCell(tw + 8) + 1: 1; // = (tw+8)/4+1, native local_1c
int sH = th > 0 ? StoryCell(th + 8) + 1: 1; // native local_34
if (sW > rw || sH > rh) continue; // FUN_005fa9a0:80 /:76 reject + FALL THROUGH
ctx.StoryUtemp = rec.Name; seedW = sW; seedH = sH;
break;
```
`rw/rh` are the Stage-1 expanded free rect already in scope (res-4 cells; units match `StoryCell`/`StoryTempSize` and the native — `sm_get_resolution == 4`). These two changes together restore the native behavior: the fit test alone prevents the einsiedler monopoly (it falls through when it doesn't fit) so the cutoff is no longer needed, and it stops feen from being selected in rects too small for it.
3. **Apply `-middist` independently** of `-mindist` (center-exclusion for feen et al.): skip the candidate when `rec.MidDist > 0 && abs(cx − StoryMapW/2)*4 < rec.MidDist` (FUN_005fac90:203-215/:220).

## E. VERIFICATION & FLAGGED ITEMS
- **Verified from binary**: weight constants `0.0 / 1.0 / 0.8 / −1.0 / 1/32768` (`.rdata` @0x736f78/0x737138/0x7370d4/0x73773c/0x736f94); forced-name `DAT_0072c634 = "urw_kif_4"`; the `hi+1` band normalization (FUN_0065cba0:238-249). Registry field map cross-checked writer↔reader: `+0x14` used, `+0x20/+0x24` band, `+0x28/+0x2c` mindist val/ref, `+0x40` exclusive, `+0x44` nonrelevant, `+0x50` reg-zone, `+0x54` zoneborder-zone, `+0x58` checkalign, `+0x5c` stoneidx, `+0x60` middist.
- **Not modeled (lower priority, do not affect feen)**: the `-exclusive` map-edge probe (FUN_005fac90:725-779; only uebergang/titanic/sirene), `-digconnect`/`-connect` corridor routing (lg_start FUN_00587480, not selection), and the `zoneBase` `+4` semantics (a decompiler index artifact per the port's own note; for zone-0 Urwald `zoneBase = 0`, so it does not affect the feen case).
- **Not a dump dive**: the StoryMgr registry offsets were proven by writer/reader agreement plus the `.rdata` constant extraction rather than locating the object in the raw minidump (no symbols); flagged for completeness, but the selection law is fully determined.

**Bottom line:** the ramp weight is faithful; the port over-prioritizes because it **omits the free-rect fit test (FUN_005fa9a0) that gates every native story pick** and papers over the resulting einsiedler monopoly with a **non-native high-depth cutoff** that in turn over-exposes the wide-band `-nonrelevant` caves like feen. Restore the fit test + fall-through, delete the cutoff, and apply `-middist` independently.