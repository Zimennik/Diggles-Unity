# ViWinProduction — reverse-engineering (building / production panel)

> 
> RE ground-truth (native addresses/laws) left intact. Port-status notes checked: the `IconLibrary`
> ScriptableObject + **Diggles ▸ UI ▸ Build Icon Library** menu (`Assets/Diggles/Editor/UI/IconLibraryBuilder.cs`)
> and the panel binding live in `Assets/Diggles/Runtime/UI/BottomMenuController.cs` — both current.

Ground-truth for the bottom building panel. Sources: decompile at
the per-function decompile of `Diggles.exe` (functions cited below) + the in-game book page
`original/data/Scripts/text/doc/en/info_prods.tcl`, which replicates the SAME logic in plain Tcl and
confirms the decompile line-for-line.

## TL;DR (the real, minimal UI)

The panel is just **two things**:
1. the **building name** (title), and
2. **ONE horizontal icon row** that mixes **product-order cells and action cells in the same list**
   (pack / unpack / switch on-off / repair / buildup are entries in the row, not a separate cluster).

No HP bar, no separate action grid, no page up/down, no pie separate from the count. Cells scroll
horizontally (`arrow_left` / `arrow_right`) when the row overflows.

## The list model

- One array at `this+0x54`: **32 slots × `0x1018` bytes** (ctor `FUN_00523100:31`,
  `_eh_vector_constructor_iterator_(…, 0x1018, 0x20, …)`).
- Live entry count = `this+0x20354`.
- Rebuilt every refresh by **`FUN_00521c80`**: clears the array (count→0), then appends entries.
  Products are appended by **`FUN_00520620`** (primary) and **`FUN_00521310`** (a second, tech/level-
  gated pass); actions are appended inline by `FUN_00521c80` itself.

### Per-entry fields (within a `0x1018`-byte slot)

| offset | meaning |
| --- | --- |
| `+0x00` | icon graphic object (from `FUN_00508830("data/gui/icons/<name>")`); its caption/tooltip lives at `icon+0x30` |
| `+0x0c` | order count `0..10` (the badge) — products only |
| `+0x10` | product index (drives `prodincrement` / `proddecrement`) |
| `+0x1010` | Tcl callback string for the entry (`"prodincrement"`, `"unpack"`, `"pack"`, `"switch"`, `"buildup"`, `"repair"`) |
| `+0x1014` | action-case tag: **0 = product, 1 = unpack, 2 = pack, 3 = switch on/off, 4 = buildup** |
| icon `+0x6c` | production progress `0..100` (only the item currently being produced) |
| icon `+0x68/0x69/0x70/0x74` | highlight flags + ARGB tints for the progress overlay |

## Fill logic (order + availability gates)

From `FUN_00521c80` and `info_prods.tcl:52-96`. The selected building object: `kind = obj+0xe8`
(panel only fills for kinds 5/6/7/10), flags at `obj+0x3d0` (`0x4000` = packed-in-crate,
`0x400000` = built/placed), production sub-object `CProd = obj+0x3e0`.

1. **Title** = building name — `get_objname` (`info_prods.tcl:48`).
2. The icon row (book pitch = 38 px from x=50; the live window scrolls in ~50 px steps —
   `FUN_005242b0` scroll is `±0x32`):
   - **Packed in a crate** (`get_boxed` / `obj+0x3d0 & 0x4000`) → **only `unpack`**, nothing else.
   - **Energy building** (`get_objtype == "energy"`) → **only the on/off toggle**:
     `energyon` if `get_prod_enabled`, else `energyoff` (`info_prods.tcl:61-71`; decompile
     `FUN_00521c80:123-237`, tag 3).
   - **Normal production building** → for each **invented** product (`get_prod_slot_invented`):
     - icon = `data/gui/icons/<class>.tga`; if **not buildable** (`get_prod_slot_buildable` = 0,
       i.e. missing ingredients) → `data/gui/icons/Red<class>.tga` (`info_prods.tcl:75-79`).
     - if `get_prod_slot_cnt > 0` draw the count badge (or `"oo"` at 10) (`info_prods.tcl:82-89`).
     - product fill: `FUN_00520620` / `FUN_00521310`, tag 0.
   - **Repair** appended at the end if `atr_Hitpoints < 1` (`info_prods.tcl:93-96`; decompile
     `FUN_00521c80:362-501`, callback `"repair"`).
   - The live window additionally offers **`buildup`** (enter crate placement, `aufbau`,
     `FUN_00521c80:503-639`, tag 4) and **`pack`** — but only in the relocate/"move" mode
     (`DAT_00772400+0x138`).

## Icon convention (the "Tcl request → icon" correlation)

- **Product icon = the class name**: `data/gui/icons/<ClassName>.tga` (that's why `icons/` is full of
  `Pilzhut`, `Farm`, `Stein`, `Schwert`, … — the file names ARE class names). Decompile: the product
  icon string comes straight from the product class object (`FUN_00520620:285-297`,
  `FUN_00508830(classIconString)`). Tcl: `set icon "data/gui/icons/$class.tga"` (`info_prods.tcl:75`).
- **Unaffordable variant**: `Red<ClassName>.tga` (`info_prods.tcl:78`). If no such file ships, red-tint
  the base icon at runtime.
- **Action icons**: fixed names — `unpack`, `energyon`, `energyoff`, `repair`, `aufbau`, `packtobox`
  (decompile also references `S1*` / `Crossout*` state variants).

Captured in the port as the `IconLibrary` ScriptableObject (built by **Diggles ▸ UI ▸ Build Icon
Library**, which bulk-imports `data/gui/icons/*` → `Assets/Diggles/Textures/UI/Icons` and maps
name → Sprite, with manual overrides).

## Click handling (`FUN_005242b0`)

- Left-click a product → `prodincrement` (cap 10); right-click → `proddecrement` (`:357-474`).
- Left-click an action cell → its case (`switch` on the entry tag at `+0x1014`):
  1 `unpack` (→ `FUN_00530e80` placement), 2 `pack`, 3 `switch` on/off, 4 `buildup`.
- Repair is dispatched by its own message at the top of the handler (`:45-68`, `FUN_005dc850` on
  `atr_Hitpoints`), not the case switch.
- Scroll arrows shift the row offset `this+0x20374` by `±0x32` (`:75,:89`).

## Engine seam (binding API for the hand-built layout + future controller)

Getters: `get_objname`, `get_objtype`, `get_prod_slot_list`, `get_prod_slot_invented`,
`get_prod_slot_inventable`, `get_prod_slot_buildable`, `get_prod_slot_cnt`, `get_prod_enabled`,
`get_boxed`, `get_attrib … atr_Hitpoints`.
Setters / actions: `set_prod_slot_cnt`, `set_prod_enabled`, + `PlacementController` for buildup/pack.

(Command list is documented inline at `info_prods.tcl:124-138`.)
