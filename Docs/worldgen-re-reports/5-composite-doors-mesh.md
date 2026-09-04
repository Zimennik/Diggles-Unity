> 📜 RE SOURCE REPORT (5 of 6,). Disasm facts are ground truth; the split-template door fix
> (B0) described here landed and is retained in the 1:1 rebuild (43805a0). Consolidated + current status:
> **Docs/WORLDGEN_ORIGINAL_RE.md**.

All changes are in place and compile cleanly. The `split ||` guard is gone (grep confirms), the split branch now captures PmpBase, and the door read runs for split parents.

---

# COMPOSITE TEMPLATE PLACEMENT — COMPLETE RE + PORT BUG + FIX

## 1. The composite placement pipeline (cited, verified)

A composite (`sm_def_temp_group`, e.g. `urw_unq_voodoo_dorf`) is placed **exactly through the same per-sub-piece path as any forced start piece** — there is no separate composite placement path:

- **`lg_set_starttemplatepro` = FUN_00650f90** (`.rdata` name string @ FUN_00650f90:70 `"...LGSetStartTemplatePro"`). It walks the dig direction, positions the composite in a rect (≤4 random tries, FUN_00650f90:304-502), and when the named template is a **group** (template record `+0x3c` != −1, tested at FUN_00650f90:364-365) calls **FUN_005f7d10** (FUN_00650f90:384). A single (non-group) template instead takes FUN_0058a510 directly (FUN_00650f90:371).
- **FUN_005f7d10 = group registration.** It iterates the group's sub-piece list (`param_1+4` base, `param_1+8` count, **stride 0x18**, FUN_005f7d10:73) and for each calls **FUN_0058a510**(pos, base_x + sub_dx, base_y + sub_dy) (FUN_005f7d10:58-59). It is **atomic**: `local_1c` counts successes (FUN_005f7d10:70); if not all succeed (`cVar5==0`, the running AND at:64-66/:77) it **rolls back** the appended records (`*(+0x198) -= local_1c`, FUN_005f7d10:92) and returns 0.
- **FUN_0058a510 = per-sub-piece pose-probe + register.** Resolves the sub-piece name → template index (FUN_00583080, FUN_0058a510:22), checks 4-alignment + map-inset ≥8 (FUN_0058a510:38-41) + no covered tile already templated (`tile+0x13 bit0`, FUN_0058a510:54-58), then **appends a forced record** `{tplIdx, x, y}` to the `+0x194` list (FUN_0058a510:45-51). **It does NOT run the frontier validator.**
- **`lg_start` → FUN_005896a0** walks the `+0x194` forced records **in group-def order** (stride 0x10, FUN_005896a0:19-45) and places each via **FUN_005848b0** (FUN_005896a0:38). **Any single failure fails the whole generation** (`return 0`, FUN_005896a0:40-42) — the story `failcount` retry channel.
- **FUN_005848b0** paints the sub-piece body into the occupancy grid (`+0x84`), toggles its doors into the frontier via **FUN_00583f60 (MergeEdges)** (FUN_005848b0:103/143/197), and **runs the frontier validator FUN_00584120** (FUN_005848b0:145). This is where a buried seam door is rejected.

**Answer to "does the native place composites via a path OTHER than per-piece FUN_005848b0?" → NO.** It is per-piece FUN_005848b0, identical to the port's `ForcePlace`.

## 2. The DOOR model (FUN_005826d0) — faithful, not the bug

`.ent` `map_entry 1 Xpx Ypx` (type gate `local_38==1`, FUN_005826d0:210). 1 tile = 8 px. Per door:
- **Quantize**: `q = ((v + (v>>31 & 7)) >> 3) & ~3` — signed /8 truncated-toward-zero, aligned to 4 (FUN_005826d0:218-224).
- **Half-cell +2 edge encode** from in-cell remainders `rx=x mod 32, ry=y mod 32` (FUN_005826d0:225-274): `ry<rx` → (`rx+ry<32`: qx+=2 | else qx+=4,qy+=2); else (`rx+ry<32`: qy+=2 | else qx+=2,qy+=4).
- **dir from boundary, last-match-wins** (FUN_005826d0:283-329): qx==0→left, qy==0→top, qx==W→right, qy==H→bottom; default **−1 → the door is DROPPED** (`+0x198` decremented, FUN_005826d0:334-346). W/H are the template dims (`local_18[2]/[3]`).

The port's port of this (LevelGenerator.cs door block, now ~1172-1204) is **faithful and produces the correct doors** — I verified byte-for-byte against the real `.ent` files. So **"is the port's door extraction/quantization wrong?" → No, the extraction math is correct; the sub-pieces just never reach it (see §5).**

Two flagged nits (both **inert on shipped data**, not this bug): (a) `Quant` adds `+7` for positive v where native adds `0` — but every real door px is a multiple of 16, so `>>3` absorbs it (0 difference on all templates). (b) The dir tie-break is first-match vs native last-match — differs only at an exact bottom-right corner; the +2 offset keeps every real door mid-edge, so never triggers.

## 3. Door-MATING law — the mechanism that avoids burial

**FUN_00583f60 (MergeEdges) is a pure coordinate toggle** (LevelGenerator.cs:887-902, faithful): a door whose (x,y) already sits in the frontier is **removed** (mated); else appended. **FUN_00584120** rejects a frontier door iff it is <2 from the area border **or all 4 of its diagonal grid cells are non-zero** (buried) (FUN_00584120:35-83).

So a composite avoids burial because **its sub-pieces' `.ent` doors are hand-authored to COINCIDE** — when the second of two tiling neighbors is placed, MergeEdges cancels the shared seam door before its cells are fully surrounded.

I extracted the real `.ent`/pmp bytes for all 7 `urw_unq_voodoo_dorf` sub-pieces and computed every door → world (group offsets from CampaignInit.tcl:179-188). **All 6 internal seams mate exactly; exactly one door stays open (the village entrance):**

| seam (world) | door A | door B | result |
|---|---|---|---|
| (108,46) | vodo_003 left | vodo_002 **right** | MATE |
| (72,42) | vodo_002 left | vodo_008 **right** | MATE |
| (68,46) | vodo_008 left | vodo_004 **right** | MATE |
| (68,58) | vodo_008 left | vodo_007 **right** | MATE |
| (72,66) | vodo_008 **right** | vodo_005 left | MATE |
| (92,66) | vodo_005 **right** | vodo_006 left | MATE |
| (116,46) | vodo_003 **right** | — | OPEN (entrance) |

(vodo_008, the 4×40 `unq_vodo_gng` column, is the spine: 4 doors, all mating.) **Answer to "do the sub-pieces actually MATE?" → YES, definitively.** The task's hypothesis that the Wachhaus neighbor (vodo_003) "has no reciprocal boundary door" is **REFUTED**: vodo_003's `unq_vodo_003.ent` left door `map_entry 1 0 48` → local (0,6) → world (108,46) is precisely the reciprocal that mates vodo_002's right door. `urw_der_einsiedler` mates identically (all 6 pieces place; open doors (66,24) + (132,34)).

## 4. The `mat` material-paint spec (cited)

`{mat {0 x y w h} …}` rows (CampaignInit.tcl:187, 9 rects for voodoo_dorf). Parsed to a per-group table (stride 0x14 = 5 ints) in corner form `{x1, y1, x1+w, y1+h, type}`. At composite placement, for each **type-0** rect (FUN_00650f90:424) it offsets both corners by the composite origin (x-coords += local_44, y-coords += local_48; FUN_00650f90:425-434) and appends to **CLevelGenerator `+0x17c`** (count `+0x180`, FUN_00650f90:430-434). **These rects are the connective courtyard/path ground between buildings; they are NOT written to the occupancy grid (`+0x84`)** and therefore **play no part in door validation** (confirmed: the placement simulation succeeds with mat ignored). They are stamped into terrain during the level-realization pass. The port currently **drops `mat` entirely** (`ParseTempGroupEntries` fails `int.TryParse(f[0])` on "mat", DigglesTclEngine.cs:3847-3849) — a separate cosmetic gap (buildings place, connective ground unpainted), not required for the placement fix.

## 5. THE PRECISE PORT BUG

**5 of the 7 voodoo_dorf sub-pieces are SPLIT templates** — `urw_unq_vodo_002_a/004_a/005_a/006_a/008_a` begin `split_template … <mergedPmp> W H { children }`. The port's `LoadMeta` detected `split_template`, set W/H, and **early-returned before the door read** (old `if (split || string.IsNullOrEmpty(p.PmpBase)) return;`). So those 5 pieces loaded **zero doors** (`p.Edges` empty). The split CHILDREN's `.ent`s are in fact empty; the doors live only in the **merged** `unq_vodo_002.ent` etc. — which the native reads because **FUN_00583080 resolves every template (split included) through FUN_005826d0** (FUN_00583080:83), whose `.ent` read (FUN_005826d0:146+) runs **outside** the split guard (FUN_005826d0:86 only skips the pmp-dims re-read for the `+0x40` split flag set at FUN_00581d30:348).

Consequence, reproduced by a bit-exact simulation of the port's placement over the real `.ent` bytes:
```
VOODOO_DORF (PORT: split=doorless):
  place vodo_003 doors=2 -> ok
  place vodo_002 doors=0 -> REJECT: door(108,46) buried >>> GENERATION FAILS <<<
EINSIEDLER (PORT: split=doorless):
... place raetsel_001 doors=0 -> ok...
  place gng_003_a doors=2 -> REJECT: door(92,34) buried >>> GENERATION FAILS <<<
```
vodo_003's left door (108,46) enters the frontier; doorless vodo_002 can't mate it, its body fills the left cells while vodo_003's fills the right → all 4 corners set → FUN_00584120 buries it → FUN_005896a0 fails the whole generation. (With all doors loaded, the same sim places all 7, leaving only (116,46) open.) This is exactly why composite/unique story caves never generate in the port.

## 6. THE FIX (applied + verified)

`Assets/Diggles/Runtime/World/LevelGenerator.cs`, `LoadMeta`:
1. In the `split_template` branch, capture the merged pmp base from arg 2: `p.PmpBase = Path.GetFileNameWithoutExtension(st[2])` (LevelGenerator.cs:1125-1127).
2. Drop `split ||` from the early-return guard → `if (string.IsNullOrEmpty(p.PmpBase)) return;` (LevelGenerator.cs:1159).
3. Wrap only the **pmp-dims re-read** in `if (!split)` so a split parent keeps its header W/H (faithful to FUN_005826d0:86) (LevelGenerator.cs:1162-1171).

The existing door-read block (unchanged) then runs for split parents off `p.PmpBase` and loads the merged `.ent`. All required `.ent`s resolve in `templates/ent/` with the expected door counts.

Added test seam `DoorCount`/`GetDoors` (LevelGenerator.cs:1258-1266) and two regression tests in `Assets/Diggles/Tests/Editor/LevelGenTests.cs`: `SplitTemplate_LoadsMergedEntDoors` (unit: vodo_002→2, vodo_008→4, vodo_004→1, vodo_005→2 doors; the (36,14)↔(0,6) seam) and `Composite_VoodooDorf_PlacesWithoutBuriedSeam` (integration: registers the 7 forced records + an entrance cap, asserts `lg.Start` succeeds and all 7 sub-pieces emit — this failed before the fix, passes after per the simulation).

**Verification:** `dotnet build Diggles.sln` → Build succeeded, 0 errors. Bit-exact placement simulation over real `.ent` bytes confirms before(fail)/after(all-place) behavior for both voodoo_dorf and einsiedler. (Unity 6000.0.62f1 isn't installed here — only 2021-2023 — so the EditMode tests are authored/compiled but not run in-session; they run under the project's editor. Per your commit policy, hold for Play-verification before committing, on a branch.)

## 7. Follow-ups (out of scope of this bug, flagged)
- **`mat` terrain paint** is still ignored by the port (§4) — buildings place but the connective courtyards/paths (`+0x17c` type-0 rects, offset by origin) aren't stamped. Spec above is ready to implement in the terrain pass.
- **Split parents don't aggregate child object-spawn counts** (`new <Class>` lives in the split children's tcls, not the parent) — harmless for forced composites (not cost-gated), but the `ObjCounts`/object-bearing metadata is empty for split uniques.
- Two faithful-quant nits (§2), both provably inert on shipped data.

Key files: `Assets\Diggles\Runtime\World\LevelGenerator.cs` (fix + helpers), `Assets\Diggles\Tests\Editor\LevelGenTests.cs` (tests). Verification scripts: `…\scratchpad\verify_composite.py`, `…\scratchpad\sim_placement.py`.