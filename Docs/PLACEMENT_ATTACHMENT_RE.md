# Placement attachment laws — native RE and port mapping

Status: implemented on. This document covers object-to-object snap placement and
the two non-default `set_placesnapmode` terrain laws. Ordinary building/item placement remains
documented in `ITEM_PLACEMENT_RE.md`; doors, ladders, and elevators remain in
`LADDER_ELEVATOR_DOOR_RE.md`.

## 1. Command records

| Command | Native handler / field | Port state |
|---|---|---|
| `class_objsnapclass <class> <range>` | `FUN_00617250`; class `+0xce4/+0xce8` | `DigglesClass.ObjectSnapClass/ObjectSnapRange` |
| `set_placesnapmode <obj> <mode>` | `FUN_0067d8b0`; object `+0x224` | `DigglesTclEntity.PlaceSnapMode` |
| `get_placesnapmode <obj>` | `FUN_0067dac0` | Real round-trip getter |
| `link_obj` / `get_linked_to` | `FUN_0069b7b0` and the child link record | Existing live attachment system |

`FUN_00530e80` selects placement tick mode 7 when the placed class has a non-null
`class+0xce4`. Otherwise object snap mode 1 maps to tick mode 8, mode 2 maps to tick mode 9,
and mode 0 maps to ordinary tick mode 2.

## 2. Object-to-object snap — tick mode 7

Shipped callers:

- `Kristall` -> `Reflektor`, range 1.5 (`Lorelei.tcl`).
- `Ring_Der_Magie` -> `Lasercollector`, range 4.0 (`Lorelei.tcl`).
- `Bild1`..`Bild8` -> `Bild_Positionsmarke`, range 1.5 (`Kristallwelt.tcl`).

The mode-7 branch in `FUN_0046b450:1531-1651` constructs an exact-class query around the
cursor. Candidate distance is

`dx^2 + dy^2 + (0.5 * dz)^2 < range^2`.

The strict comparison excludes a candidate exactly on the range boundary. The closest target
wins. The preview position is the target's current link point 0 transformed into world/native
coordinates; the target handle is stored at placement singleton `+0xec`. When no target wins,
the branch falls through to ordinary free putdown instead of invalidating the cursor.

`FUN_00531310:229-265` includes `-snapobj <handle>` in the normal placement eventgen call.
The unchanged `Scripts/init/eventgen.tcl:116-124` emits `evt_task_snapitem`, with the placed item
as `-subject1` and the host as `-subject2`. The unchanged gnome handler verifies that the host is
still empty, invokes the host's `snapping_action`, removes the item from inventory, and finally
calls the host's `snap_obj`. Story classes implement `snap_obj` with `link_obj child host 0` and
`set_pos child (host position + get_linkpos host 0)`.

Port mapping:

- `PlacementController.SnapTargetHandle` mirrors `+0xec`.
- Candidate selection uses the exact weighted metric and range law.
- A rendered host supplies its live link point 0; headless mode falls back to its origin.
- Confirm passes `-snapobj` through the real event generator. It does not bypass the original
  host-empty check or story-specific `snapping_action`.
- The existing `DigglesLinkAttachment` drives the final linked model and `get_linked_to` exposes
  the entity-side record used by the puzzle update timers.

## 3. Wall placement — object snap mode 1 / tick mode 8

The shipped user is `Steinfalle_Medusa` (`fallen.tcl:245`). The mode-8 branch
`FUN_0046b450:152-604` performs:

1. Native cursor lattice snap.
2. The cursor tile is rejected when `tile+0x18 & 3 == 3`.
3. A sub-row scan from offsets 2 through 15. It advances while the current and next sub-cell
   both carry floor bit `0x02`, then seats Y at `cursorY + offset * 0.125 - 0.8`.
4. Hmap probes at the four X/Y corners of the model side silhouette. All four depths must be
   identical.
5. `abs(cursorZ - wallDepth) < 4.0` (`DAT_00737398`, double).
6. The object back face is seated at `z = wallDepth - bboxMinZ * 0.01`.
7. `FUN_005e8440` checks the four 1/1.4-inset back-rectangle points are in air against the flat
   wall face.
8. Body and order-ghost overlap checks run with the building model.

If the wall branch fails, mode 8 retries the ordinary settled box branch. This is a native
behavioral difference from normal mode 2, where a blocked building does not become a box.

## 4. Ceiling placement — object snap mode 2 / tick mode 9

The shipped user is `Plattmachfalle` (`fallen.tcl:60`); its source comment states that the upper
BBox corners must lie in the ceiling. `FUN_0046b450:605-794` first performs the ordinary vertical
support settle. A picked depth within one unit of 13 is normalized to 13
(`DAT_0073754c=13`, `DAT_007372a8=1`).

`FUN_005e8720` then applies the special terrain gate:

- X and depth extents are inset by `DAT_0073719c=1.4`.
- The upper row is `posY + bboxMinY*0.005 - 0.2`.
- All four upper X/depth corners must be behind the hmap relief, meaning they are embedded in
  the ceiling support.
- The four lower corners at `posY + bboxMaxY*0.005` must lie on the rock/air boundary. A corner
  at least one depth unit into agreeing air or rock is rejected; the neighbouring probes use
  +0.25 Y and +/-0.375 X for air, -0.375 Y and +/-0.375 X for rock.

Body/order-ghost overlap tests follow. A failed ceiling branch retries the box branch, matching
mode 8.

## 5. Verification and manual acceptance

Automated coverage pins:

- command round trips and object record values;
- exact wall scan, flat-four-corner law, back-face seat, and strict depth tolerance;
- ceiling upper-corner embed and lower boundary rejection;
- real `eventgen.tcl` mode-7 confirmation into `evt_task_snapitem`;
- weighted nearest-host selection and ordinary-putdown fallback outside the range.

The next manual gameplay run must visually verify a Medusa trap on a wall, a crushing trap under
a ceiling, a Lorelei crystal/ring snap, and a Kristallwelt picture snap. The automation covers
the placement and event contracts; the manual pass confirms authored model pivots and puzzle
presentation in the original campaign maps.
