# Production-slot override reverse engineering and port contract

> Implemented from the shipped tutorial/tournament scripts, the original
> `tclhelp` contract, and the decompiled native command and production-window handlers.

## Native state and command

`prodslot_override` is registered by `CTclGlobalMethods_Register` at `0x00618010`; its
handler is `FUN_0061ea80`. The command operates on 32 signed integers stored at
`CGame+0xbc`. `FUN_0055a7c0` resets the whole table and `FUN_00524de0` addresses one
four-byte entry. Valid indices are `0..31`.

The command is case-insensitive and exposes five subcommands:

| Command | Stored value | Result |
|---|---:|---|
| `prodslot_override reset` | `0` in all 32 entries | empty string |
| `prodslot_override normal <index>` | `0` | empty string |
| `prodslot_override disable <index>` | `1` | empty string |
| `prodslot_override hide <index>` | `2` | empty string |
| `prodslot_override query <index>` | unchanged | decimal state value |

The original processes `reset` before checking the three-argument form, so trailing reset
arguments are harmlessly ignored. A missing subcommand, wrong arity, unknown subcommand, or
index outside `0..31` is an error.

## Production-window consumers

Product overrides are positional, not keyed by class name. The zero-based index is the item's
ordinal in the selected building's native production-slot list. Both production catalog passes
use the same table:

- `FUN_00520620` builds invented product cells;
- `FUN_00521310` builds not-yet-invented product cells;
- state `2` skips the position completely;
- state `1` keeps the cell, resolves the virtual `Crossout<icon>` composite, and changes the
  widget to its disabled input mode;
- state `0` leaves the normal buildability/invention behavior unchanged.

Index `31` is reserved for the shared pack cell. `FUN_00521c80` reads `CGame+0x138`, which is
exactly `CGame+0xbc + 31*4`. State `2` omits the cell, state `1` renders disabled
`Crossoutpacktobox`, and state `0` exposes whichever action applies to the current object:
pack, unpack, or cancel packing.

## Shipped story use

`Trigger_Tutorial.tcl` and `Trigger_Tournament.tcl` initially disable positions `0..5` and
`31`. The scripts later expose only the position required by the current lesson. For example,
the tutorial enables position `31` immediately before teaching pack/unpack, resets the table
before the normal catalog lesson, disables every product except position `2`, and later enables
position `0`. Failure or level-transition paths reset the table.

This state is therefore part of story progression, not a cosmetic window preference.

## Port implementation

`ProductionSlotOverrides` is owned by `DigglesTclContext`, matching the original per-game
lifetime. The real Tcl handler preserves the numeric state and query contract. The bottom-menu
controller applies overrides by production-list ordinal and independently applies index `31`
to pack/unpack/cancel. Hidden or disabled entries are also rejected in the controller, so a
stale or synthetic UI click cannot bypass the story gate.

The runtime icon loader composes disabled icons from the original base TGA and
`GUI/icons/xoverlay_Crossout.tga`, preserving the original virtual `Crossout<icon>` convention
without baking replacement art into the Unity project. The view sets the cell's `Button` to
non-interactable while keeping the crossout visible.

The runtime capture/restore contract exposes all 32 entries to the native `.sav` mapper. This is
required to resume a tutorial checkpoint between a `disable` and the later `normal`/`reset` command.

## Verification

`ProductionSlotOverrideTests` pins:

- all five subcommands, numeric values, case-insensitivity, reset behavior, and validation;
- product-list ordinal mapping, disabled input rejection, and hidden-cell removal;
- index-31 pack visibility and input gating;
- composition from the shipped `xoverlay_Crossout.tga` asset.

The focused production-slot tests additionally pin the 32-entry table across capture/apply.
