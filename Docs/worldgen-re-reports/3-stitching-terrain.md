> 📜 RE SOURCE REPORT (3 of 6,). Disasm facts are ground truth; port-status notes PRE-DATE
> the 1:1 rebuild (43805a0 — B3 corridor buckets now sourced by BuildCorridorBuckets). Consolidated +
> current status: **Docs/WORLDGEN_ORIGINAL_RE.md**.

I now have a complete, decompile-verified picture. FUN_006c2340 confirms the post-stamp effect dispatcher only maintains a dirty-RECT job list (a "DSFastArray" of invalid rectangles) for the derived-data passes — it never rewrites block/height/geometry. The doc's core claim holds. Here is my deliverable.

---

# STITCHING dimension — RE spec + port divergences

## VERDICT (short version)
The original stitch — anchor pre-step, latch, the PMP→world blit, the solidity raster, and the post-stamp "re-mesh" — is **faithfully ported** in nearly every checkable detail (each verified against the decompile below). The mesh-holes-and-disconnection the player sees are **not produced by the stitch itself**; they are the *downstream consequence* of the one native law the port copies exactly — the **destination-blind, template-wins blit** — meeting a stamp that lands **on top of the player's carved tunnel or on a previous stamp**. That overlap is prevented *upstream* in the original (pre-step + grown-cell exclusion + pacing); where the port's upstream logic mis-fires, the faithful blit silently fills the tunnel in and manufactures an incoherent block seam (the see-through hole). The single stitch-side component that is itself a fragile reconstruction is the **utemp corridor router** — which is exactly "the tunnel that should connect to the cave."

---

## A. THE EXACT ORIGINAL STITCH (cited)

**1. Anchor / junction law — `lg_set_starttemplatepro` = FUN_00650f90**
- The DigNotify latch (`CStoryMgr::DigNotify` = **FUN_005fd060:89/95/113/118/143**, and the −y case **FUN_005fcfb0:28-31**) stores `{dir, x, y}` where **x,y = the ±4 NEIGHBOUR tile of the dug tile**, into the un-grown frontier (dir: 8=+x, 2=+y/down, 4=−x, 1=−y/up). The dug story-cell is marked grown (**FUN_005fd060:71-73** → FUN_005fa510).
- The latch is snapped to its 4-cell (**FUN_00650f90:179-180**, `&=~3`).
- **THE PRE-STEP** (**FUN_00650f90:182** → **FUN_005f7a90**, verified: `case1 y-=4; case2 y+=4; case4 x-=4; case8 x+=4`): the walk offset is advanced **one ±4 step along the dig dir before iteration 0**, so the start piece's top-left = `latchCell + preStep` = **one full cell of virgin rock beyond the latch** → the piece **never overlaps the tunnel**.
- Start-piece pick = **FUN_00585d80** (via FUN_005f7980): 4×4 pool pieces; down-dig mode 1 → the 2-door {left,right} crossing corridor (gng_001); other dirs → the single onward-door cap (gng_021/022/023/024).
- Pose probe = **FUN_0058a510:38-58**: 4-aligned, whole footprint ≥8 tiles inside the map (DAT_0077241c+0x84..0x90), no covered tile with HasTpl (tile+0x13 bit0); on success appends `{tplIdx, areaRelX, areaRelY}` to +0x194.
- **JUNCTION LATTICE**: marquee tunnels sit at world tiles **≡{1,2} mod 4** (DigMarquee: anchor 4k+1, 2-wide, cross-axis clamped); template bores sit at piece-local {1,2}; pieces land 4-aligned ⇒ bore ≡ tunnel by construction. The gnome connects by **digging the virgin gap + the piece's closed wall** — the connection is *made by digging*, never carved by the generator.

**2. The blit — `map_template` = FUN_005992b0**
- Reads block-idx array (`_DstBuf_00`), material/corner codes (`_DstBuf`), RLE solidity (`_DstBuf_02`), heightmap (`_DstBuf_01`).
- **Pass 1 (:124-225)** = material-class resolution only: file code `1 → (worldCorner&3)+1` "inherit", `2→3, 3→2, 4→1, 5→4`, `0` untouched — writes back into the *source* buffer, not geometry.
- **Transparency (:292-454)**: an all-zero 2×2 terrain-corner window skips only the per-tile block/material/height record writes; those destination fields survive.
- Non-transparent tiles get **verbatim** block-idx → tile+0 (:379-415), material/corner → tile+0x18 (:317-377), heightmap byte → tile+0x07 (:453). **Destination-blind** — the only dest read is Pass-1's inherit; dig-mark nibbles (tile+0x08) are never touched.
- HasTpl (tile+0x13 |= 1) over the complete clipped rect (:461-529), including transparent padding.
- Solidity sub-grid via the independent whole-plane call to **FUN_00596550** (:531-568;
  callee:31-141): source is a *raster over the whole region*
  `(subRow−y0)*regionSubW − x0 + subCol` → dst `tile+0x3c[subY*8+subX]`, including
  transparent seam tiles.

**3. The "re-mesh" — FUN_005a3dd0 / FUN_005a3fd0 (:570-607)**
- FUN_005a3dd0 = **just the effect-manager singleton allocator** (DAT_00772428).
- FUN_005a3fd0 = a **dispatcher** that clamps the rect and, per effect-flag, calls **FUN_006c2340** — which I verified is a **dirty-RECT list manager** (a "DSFastArray" of invalid rectangles, param_1+0/+4/+8 = array/count/granularity). The passes run over `rect` (flag 0x40000), `rect±1 tile` (0x400) and a band (8), and re-derive **DERIVED data only** (EFFPASSWALK / EFFBLOCK / RENDERJOBS / MINIMAP / FLUIDALIST). **Nothing rewrites block-idx, height, or corner geometry at the seam.**
- ⇒ **Watertightness in the original = non-overlap + authored coherence**, NOT reconciliation. Adjacent authored/virgin blocks meet because they were authored to; the carve keeps coherence via the aux-graph "gate==rim" accept (FUN_00596b80). There is **no seam-fixup** and none is needed *because nothing incoherent is ever adjacent*.

**4. The corridor — `FUN_00587480` (start↔utemp)**
- Only runs for `-connect` records (utemp/composite generations); plain growth has zero connections, so **no corridor is generated for the common horizontal/vertical dig**.
- Flood from the DST door step-off over LOW16==0 (empty) cells stepping ±4, gradient-ascend back to the SRC step-off, then emit a chain of **4×4 corridor pieces** via FUN_005842c0, picked from **dedicated corridor-piece tables at param_1+0xac / +0x154 (straight mask 5) / +0x130 (straight mask 10)** — *not* the growth pool. (Ghidra drops the 0x588ce1..0x5890c6 emission region as unreachable.)

---

## B. WHAT THE PORT DOES FAITHFULLY
- **Latch** (`StoryDigNotify`/`StoryDigProbe`, DigglesTclEngine.cs:4486-4513) = RAW ±4 neighbour, matches FUN_005fd060.
- **Pre-step + walk** (CmdLgSetStartTemplatePro:4237-4257): `stepX/stepY` switch is byte-identical to FUN_005f7a90; latch `&~3`; ProbeForcedPose = FUN_0058a510.
- **Blit** (`BlitPmpIntoWorld`): destination-blind; `PmpTileTransparent` gates only block/zone/terrain record writes (FUN_005992b0:292-454), while `HasTpl` covers the complete clipped rect (:461-529).
- **Solidity de-raster** (`BlitPmpIntoWorld`): `srcK=(pys+sy)*pmpW*8+(pxs+sx)` matches FUN_00596550:31-141 and copies the independent whole +0x3c plane, including transparent padding.
- **No seam reconciliation** (RefreshRect, DiggableTerrain.cs:722-753): correctly does *not* rewrite geometry — matches the FUN_005a3fd0 finding. (The old `ReconcileStampSeam`/`carve-wins skip` were the burial+holes and are correctly removed.)
- **Live-stamp pipeline** (`RuntimeMapBuilder.LiveBlitPmp`): blit → `SyncStampedTile` (block-59 pre-cut → dug-floor render) → material sync → `ExtendRenderBbox` → `RefreshStampedRect` → debounced chunk remesh + authored-plane-preserving tile-graph rebuild. Structurally sound; block-0 virgin renders as solid rock inside the growing bbox (apron).
- **Live material tail** (`sm_draw_stone -matlist`, `mp_tools.tcl:26-28`):
  `FUN_0065e090:190-206` translates each `{type x y w h}` by the supplied offset and
  passes `(x,y,x+w+2,y+h+2, flags=0, class=type, strength=2.5)` to `FUN_005fc9d0`.
  Its rect±1 update is EFFBLOCK/render-only (`FUN_005fc9d0:127-138`), so it refreshes the
  stamped tile graph without erasing the authored +0x3c plane.

---

## C. THE ROOT-CAUSE MECHANISM + DIVERGENCES (ranked, with fixes)

### The mechanism (this is the load-bearing insight)
Because the blit is **destination-blind template-wins** (faithful), the stitch has **zero tolerance** for a stamp that lands on a carved tunnel or a prior stamp: the blit overwrites the carved block-59 floor with the template's solid block (`SyncStampedTile` then sets `_dug=false`), which **fills the tunnel in → "not connected"**, and the template block now abuts the still-carved neighbour with disjoint edge-z intervals → **see-through hole**. The original avoids this *entirely upstream* (the piece never overlaps). So every hole/disconnection reduces to **"why did a stamp overlap a carve/another stamp?"** — and the answer is not in the blit.

### DIV-1 (my dimension, highest-confidence stitch bug): the corridor router picks corridor pieces from the **wrong table**
`BuildCorridorBuckets` (LevelGenerator.cs:761-772) builds buckets from the **growth pool's** 4×4 pieces. The native draws from **dedicated corridor tables +0xac/+0x154/+0x130** (FUN_00587480, the recovered emission region). Consequences:
- If the pool lacks an **L-shaped {perpendicular} 4×4** piece, every corridor *bend* falls through `MatchBucket` to a **superset** piece (⊇ mask) that carries an **extra open door** → an unmated frontier door the grow loop must cap → cluster bloat or an uncapped door = a wall gap / disconnection. (`PickCorridor`/`MatchBucket`, LevelGenerator.cs:741-759.)
- If no piece even satisfies the required mask, the **whole generation fails** (`RouteCorridor`→false→`Fail`, LevelGenerator.cs:401-404) → the utemp cave silently never appears.
- This is exactly "the tunnel that should connect to the cave is NOT connected."
**Fix**: model the native corridor tables (+0xac/+0x154/+0x130). They are the *original's* corridor-piece set (the straight-5/straight-10 + the corner table), populated separately from the growth pool. Recover the population site (search xrefs to +0xac in the CLevelGenerator ctor / an `lg_*` setter) and build `_corridorBuckets` from THAT, not `_pool`. Until then, restrict superset fallback to pieces whose extra doors can be capped, and make an unroutable corridor **cap-and-continue** rather than fail the whole generation.

### DIV-2 (cross-dimension, but the direct hole source for plain growth): overlapping / duplicate generations
Play sessions report "duplicates one under/behind another" and "several connected caves at once" **together with** the holes — these are the same event: two generations (or an un-paced burst) stamp overlapping world regions; the second blit overwrites the first + any carve between them → incoherent seams = holes. The stitch is the *victim*, not the cause. Root lives in:
- the free-rect (`sm_get_level_info`) not excluding **grown/dug** cells (the anti-overlap gate; **verify** `StoryMap` marking at DigNotify:4487 actually feeds the rect solver — RECT dimension), and
- the re-arm/pacing (`lg_get_level` weight reset, DigglesTclEngine.cs:3465-3467 — verify it truly gates re-latch to one-per-~32-digs in Play).
**Stitch-side note for the test runner**: there is *no* stitch fix for this — the native blit is intentionally destination-blind. The fix must prevent the overlap upstream (rect/pacing). Flag to those dimensions.

### DIV-3 (flag, lower severity): the carve→template seam step tolerance
The port's aux-graph carve into a stamped template wall can stop **one aux level (≤1.0u) short** of the bore floor; the junction test (`WorldGrowthPlayReproTests.SeamMismatches`, GapTol=1.1) *tolerates* this, so a ≤1.0u residual step ships and can read as a visible ledge/thin gap at the mouth. This is the **dig dimension's** aux-graph carve (`StepCornerGated`, DiggableTerrain.cs:581-595) — flag it there; it is not a blit issue.

---

## Confidence & flags
- Faithfulness of anchor/pre-step/latch/blit/solidity/effect-passes: **HIGH** — each byte-checked against the cited FUN_ lines.
- DIV-1 (corridor table): **HIGH** that the port sources corridor pieces from the growth pool while the native uses +0xac/+0x154/+0x130; **MEDIUM** on the exact downstream failure (needs the recovered emission region re-verified — Ghidra drops it, and I did not re-run a fresh recovery this pass — flagged).
- DIV-2 root (rect/pacing): **MEDIUM** — I proved the *mechanism* (template-wins fills carves) and that the port marks dug cells grown, but the free-rect exclusion + pacing are other dimensions; I did not trace `CmdSmGetLevelInfo`.
- I could **not** run the Explicit Play repro (`GrowthStamp_NextToCarvedTunnel_SeamIsWatertight`) headless (needs Unity batch mode) — so I could not empirically confirm which of DIV-1/DIV-2 fires in the player's exact session; both are consistent with "absolutely broken, holes, not connected."

Key files: `Assets/Diggles/Runtime/World/BdbPmpWorldBuilder.cs` (blit+render), `Assets/Diggles/Runtime/World/DiggableTerrain.cs` (SyncStampedTile:698, RefreshRect:722, carve:581), `Assets/Diggles/Runtime/World/RuntimeMapBuilder.cs` (LiveBlitPmp:114), `Assets/Diggles/Runtime/World/LevelGenerator.cs` (RouteCorridor:506, BuildCorridorBuckets:761), `Assets/Diggles/Runtime/Tcl/Engine/DigglesTclEngine.cs` (CmdLgSetStartTemplatePro:4215, lg_get_level:3427, DigNotify:4466).
