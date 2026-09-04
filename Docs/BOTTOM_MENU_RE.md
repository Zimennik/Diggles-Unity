# Bottom context menu — ground-truth RE

> Pure native RE (disasm addresses/laws) — left intact. Port implementation of these windows lives
> in `Assets/Diggles/Runtime/UI/BottomMenuController.cs`.

Companion to `PRODUCTION_PANEL_RE.md` (ViWinProduction — still authoritative for the built
building's row). Decompile cites = the Diggles.exe decompile; Tcl cites =
`original/data/Scripts/...`. Verified against the exe where the decompile was ambiguous.

## 1. Window selection law (dispatcher FUN_00529360, every frame from ViWinRoot::Tick:248)

| State | Bottom window |
|---|---|
| map-editor mode | none |
| selection count == 1, kind ∈ {5,6,7,10} (building; 6 = energy) | **Production** |
| selection count == 1, kind == 1 (ADULT gnome) | **Inventory** |
| anything else — nothing, multi-select, BABY (kind 0xb), items/crates | **DigButtons** |

Placement mode does NOT swap windows (only clears a secondary byte); the placement sub-mode
`+0x138` (1 = placement → `Crossout*` icon variants, 2 = move → crate cells suppressed) only
affects the production FILL. Cutscenes hide the HUD via the display layer id, not the dispatcher.
ViWinProperties follows its own tick (not in this law).

## 2. Dig menu (ViWinDigButtons — 6 buttons, 40 px pitch, 240×40 @ x=195, bottom−65)

| # | icon | tooltip (messages.txt) | enable attrib | unlock |
|---|---|---|---|---|
| 0 | `BuddelnG` | Tunnel | digenable0 | always |
| 1 | `BuddelnH0` | Tiny | digenable1 | always (post-tutorial) |
| 2 | `BuddelnH1` | Small | digenable2 | best exp_Stein ≥ **0.15** |
| 3 | `BuddelnH2` | Medium | digenable3 | ≥ **0.30** |
| 4 | `BuddelnH3` | Huge (Large) | digenable4 | ≥ **0.70** |
| 5 | `BuddelnUndo` | Delete | Σ digenable0..4 > 0.5 | any tool enabled |

Selected tool = window `+0x5c` (−1 none, 0..5); click toggles. Locked buttons are **HIDDEN,
not dimmed**: the DigButtons tick `FUN_0050e2a0:13` writes
`digenable{i} ≥ 0.5` straight into each button's `+0x1c` — the widget VISIBLE flag (the
hit-test refuses `+0x1c == 0`, `FUN_00505dd0:18`; the generic hide path is
`if (+0x1c) { invalidate; +0x1c = 0 }`, `FUN_00506230:104-107`).

**Unlock driver = Tcl `enable_digbrushes`** (z_dignwalk.tcl:185-203, run every gnome sparetime
tick, z_spare_main.tcl:752): `maxstones = gamestats attribmax $own exp_Stein` (the BEST gnome's
stone experience); thresholds `tttexp_digbrush{2,3,4}` = 0.15/0.30/0.70 (techtreetunes.tcl:1359-
1361); once one fails the rest are 0. The tutorial drives digenable* manually.

## 3. Tool → dig mode / template (world click FUN_005300f0; mark mgr ctor FUN_005661c0)

Mgr state `+0xe0`: 1 = tunnel marquee, 2 = erase paint, 3 = cave stamp (state 4 + right-drag
erase = DEAD code in the shipped exe — do not port).

- **Tunnel (tool 0, mode-1)**: drag rect; every cell passing the PILLAR LATTICE mask
  `((y−1)/2 & (x−1)/2 & 1)==0` (2×2 pillars on a 4-tile pitch) gets nibble 3 (+ the stroke-id
  nibble at tile+0x0c = mgr counter & 0xf). Per-cell validity FUN_00566870 (not cave-marked,
  terrain type ∈ {1,2}, below not cave-marked, 6-neighbour hardness > 1). Stroke id ++ per commit.
- **Caves (tools 1..4, mode-0 cnt|8)**: TEMPLATE STAMPS — `data/templates/cave{0..3}.tga`,
  32×32 32-bit; cell value = `(blue+32)>>6`: 0..3 = carve cnt, 4 (white) = skip. Stamp area
  cols [x−16, x+16), rows [y−32, y) — the MOUTH sits at the anchor row, the cave extends UP.
  Any invalid carve cell → whole stamp aborts (error sound; failure mask feeds the ghost
  preview). Carved shapes: Tiny 8w×5h, Small 12×5, Medium 16×9, Huge 12×12 dome. A bbox +
  templateIdx record is appended to the map-mgr list — consumed by CAVE-SKIN application when
  actually dug. (Template buffer orientation inferred — verify visually once.)
- **Undo (tool 5, state 2)**: STROKE-ERASE paint over plain tunnel nibbles (clears a 2×2 window
  per interpolated step, no bit-8 cells); a CLICK on a cave-marked cell removes the cave record
  and FLOOD-FILLS away every 4-connected cell with the same stroke-id nibble + cave bit
  (asm-verified 0x5674bb–0x567792). No marquee undo exists. `dig_resetid` is unrelated (gnome
  task reset).

## 4. Inventory window (ViWinInventory)

- **Slot law** (table built FUN_005c7410:49-60; InvGetSize FUN_005cde10): Zwerg base **6**;
  worn containers (kind 9) add: Holzkiepe **+4**, Grosse_Holzkiepe **+8**, Schubkarren **+6**,
  Hamsterkarren **+6** (⚠ binary spells `Hamsterkaren` — key on the real class name). 6
  category columns; 2 rows; 40×40 px cells; 50 px between category groups; bottom-centered.
- One icon per item (`icons/<Class>.tga`, `Red<Class>` unavailable variant), NO stacking/count.
- Clicks feed the generic pick/drag system (Hand.tga cursor; drop-to-world = place, drop-on-
  gnome = transfer via the target's `obj_use_callback`). Crate click → the shared boxed/buildup
  flow. No distinct right-click. Rebuild is event-driven (selection/contents/exp change).

## 5. Production window addenda (to PRODUCTION_PANEL_RE.md)

- Flags: 0x4000 = Boxed; 0x400000 = BuildUpState (`set_buildupstate 1` sets it AND clears
  Boxed). "Placed-unbuilt" and "interrupted half-built" are NOT distinguished natively — one
  `buildup` cell (aufbau / S1aufbau when queued); the progress distinction is Tcl-only
  (`buildup_step`, `info_progress_prod`, bau_a..bau_k).
- `atr_Hitpoints` is NORMALIZED 0..1 (def_attrib 0 1 1); repair cell appears at hp < 1.0 and
  heals +0.01/pass. **Correction**: the repair cell's click rides the BUILDUP case
  (FUN_005242b0:341-351 hard-codes `evt_btn_on -text1 buildup`; repair reuses buildup anims).
- Move-mode variants: `+0x138` = 1 → `Crossoutpacktobox` icon; 2 → pack/buildup suppressed.

## Flagged / unrecoverable
Cave-flood decompile mangled (asm-verified); DigButtons hide vtable slot inferred; cutscene
layer values partly inferred; inventory category-5 name unresolved; template orientation to be
verified visually; state-4 erase = dead code.
