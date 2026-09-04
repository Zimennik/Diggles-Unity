# Item placement / find_free_place / putdown / product-drop — native law RE

Scope: the native free-place search + seat law used by putdown and production product-drop.
Every claim cites the original decompile (`FUN_xxxxxxxx` under `re/decompiled/Diggles/_global/`)
or the original Tcl. Float constants read directly from `original/Diggles.exe` (`.rdata`,
image base 0x00400000).

Manual test bugs this answers (13-14.07):
1. Put-down items sometimes end up UNDERGROUND (sunk into the ground relief, invisible).
2. Production output CRATES land off to the side (native crates hug the building).
3. Dropped items/food spread too far apart (native packs them tight).

Port fix: DigglesTclEngine.cs `get_place` — free-depth scan + standing-law seat
floor + native materials footprint + jitter ×3 + `-placelock` auto-lock.
Tests: Assets/Diggles/Tests/Editor/ItemPlacementTests.cs (all three failed pre-fix for the
derived reasons; fail-first numbers quoted in §5/§6).

---

## 0. Coordinate frame (the load-bearing facts — ant-farm side view)

- **native.x = east** (screen horizontal).
- **native.y = screen-VERTICAL** (the 640-tall tile axis). Game-up = −y; falling = +y
  (GravitySettle walks rows down, RuntimeWorldSpawner.cs:454-465). **Floor lines are y rows**
  (the start meadow line is y = 30.5).
- **native.z = DEPTH into the scene**: z = 0 deepest (back), z = 15 the front glass
  (PlacementMeadowTests:138 "z=15 = the ant-farm front glass must not validate").
  `get_hmap(x,y)` = how far the rock RELIEF extends toward the viewer at that cell
  (per-sub-cell height nibble, FUN_004093f0). Open pocket = z ≥ h; inside the relief = z < h
  (SettleDropPosition contract, RuntimeWorldSpawner.cs:409-411).

**"Buried / invisible item" = the item's z is below the relief around it** — it renders behind
the terrain surface. The walkable GROUND at a floor line is 2-D: (east x × depth z); putdown's
"in front of the gnome" offset `(-sin(roty)·abst, 0, cos(roty)·abst)` (z_procs.tcl:385) steps in
exactly this plane.

**The standing/seat depth**: `z = hmap(x, y−1) + 1` — one nibble in FRONT of the back-wall
relief at the row above the floor line — with cap 14.5:
`vector_fix` FUN_00595770:256-262: after the spiral finds the nearest walkable sub-cell,
`z = min(max(pos.z, hmap(fx, fy−1)+1), DAT_00737564=14.5)`. On an already-walkable input cell
vector_fix KEEPS the caller's z (:270-275, passthrough).

---

## 1. Which Tcl commands implement placement, and their native handlers

| Tcl command | Native handler | Role |
|---|---|---|
| `find_free_place …` / `find_free_place_self` | (misc) | used ONCE in the whole game (wuker.tcl:206, worm spawn). NOT the putdown/product law; stubbed in the port, harmless. |
| `get_place <opts>` | `TCL:CTclGlobalMethods::GetPlace` FUN_00656560 → `CPlaceFinder::FindPlace` FUN_005a9700 (scorer FUN_005a9270, reset FUN_005a9190, lock FUN_005aa900) | THE free-place search used by product drops, farm laydown, sparetime spots, wander, beam_back. |
| `set_posbottom <obj> <vec>` | (object) | raw anchor setter (= bottom for a free entity; identity — entity-anchor.md §2). No settle/snap. |
| `vector_fix <vec>` | FUN_00595770 | landscape snap; see §0. |
| `from_wall <item>` | Tcl proc z_procs.tcl:424-438 | wall-drop un-bury: only when the GNOME is on a wall; raises item z against get_hmap. |

Putdown / drop CHAINS (all Tcl, live in the port):
- **beamto_world** (z_procs.tcl:358-419): `abst = get_boxed ? 0.8: 0.5`; `npos = gnome pos +
  (−sin·abst, 0, cos·abst)`; `set_posbottom item (vector_fix npos)`; `from_wall`.
- **beam_from_inv_to_pos** (z_procs.tcl:292-342): `set_posbottom item pos` RAW (no vector_fix);
  pos comes from get_place.
- **putdown / putdown_tasklist** (z_procs.tcl:675-733): pos==0 → beamto_world, else walk +
  beam_from_inv_to_pos.
- **prod_createproduct_box / _rndrot / _box_with_dummybox** (z_work_prod.tcl:200-286, 829-855):
  `get_place -center {workplace} -rect -10 [hmin 1 (11−cz)] 10 (13−cz) -mindist 1.5 -random 2
  -nearpos {gnome}` (fallback `-materials false`) → walk_near_item / rotate / beam_from_inv_to_pos.
  `hmin` = MIN → the DEPTH band is `[cz + min(1, 11−cz), 13]` — the crate seats 1..3 nibbles IN
  FRONT (toward the viewer) of the building.
- **prod_laydown_infrontof_farm** (z_work_prod.tcl:279-292): center = farm + {0 0 4},
  `-rect -3 -0.5 3 2 -mindist 1.2 -random 0.5`.

Registrar gotcha honored: the command-name string push precedes the handler push in
FUN_00656560; behavior (option arity/results) was verified, not labels.

---

## 2. get_place search geometry & cost (FUN_005a9700 + FUN_005a9270)

Constants (read from Diggles.exe.rdata):
`DAT_00737428 = 3.0` (jitter scale AND the no-nearpos dc weight), `DAT_00737058 = 0.25`
(depth² weight), `DAT_0073708c = 0.5` (depth weight), `DAT_00736f78 = 0.0`,
`DAT_00736f94 = 1/32768` (rand normalizer; RNG FUN_006d46ea = MSVC rand ∈ [0,32767]),
`DAT_0073752c = 8`, `DAT_0073703c = 0.125`, `DAT_00737564 = 14.5`.

- **The scan is 2-D over the GROUND plane (x-column × depth z)**; the row (y) per column comes
  from the prebuilt grid (FUN_0041f7a0 + the per-column position array read at:278-299).
  - columns: rect x0..x1 / circle ±R around the centre column;
  - depth: rect `center.z+z0.. center.z+z1` (:163-164), circle `center.z ∓ 2R` (:149-152),
    stepped **0.5** (:249-313 — `local_44 += 4`, z = local_44·0.125), range bounded by the
    64-cell grid (the `0x40` checks :253/:272 ⇒ ≤ 32 units).
- **Occupancy**: `FUN_0041c4b0` + `FUN_0041cac0(grid, 0, materialsFlag)` (:225-226) stamp
  object bodies (and, when materialsFlag, lying items) into the (x,z) grid; candidate reject =
  `(cell & 0xff000000) != 0` (:264). Grid cell ≈ 0.31 units (64 cells over a ±10 rect) — an
  item's stamp is its bbox over 1-2 cells, NOT a whole column.
- **-walldist/-rimdist**: extra grid-bit probes (:266-274, bits 0x10000000/0x8000000).
- **Ideal point** (:188-207): `-nearpos` re-projected onto the MINDIST ring around the centre
  along the depth-weighted (×0.5) direction — "stand on MY side at the standoff". No nearpos →
  ideal = centre.
- **Cost** (FUN_005a9270): `dc² = dx² + dy² + 0.25·dz²` (depth-weighted distance to centre).
  - reject unless `mindist ≤ 0 || mindist² ≤ dc²` (**mindist floor UNCONDITIONAL**,:29) and
    `rectFlag || dc² ≤ circle²` (**circle cap rect-bypassed**, :30);
  - `cost = ‖cand−ideal‖w + 2·dc + randW·rand01·3.0` (:65-66,:57); with ideal==centre it
    degenerates to `3·dc + jitter` (:34-43) — algebraically the same formula;
  - placelock veto (:69-102): candidates within a lock's own radius (rec+0x14) rejected,
    skipping locks whose id == the `-placelockidexcept` id (+0x54; reset default −2);
    the whole check is gated on finder+0x58 (**default ON** — FUN_005a9190).
- **Result auto-lock** (:322-331): the winner is passed to `FUN_005aa900(pos, id −1, radius
  0.8f)` with duration = the `-placelock` value (finder +0x2c, default 0) — LockPlace is a
  no-op unless duration > 0 (the DAT_00736f78 guard), and id −1 always APPENDS a new record
  {pos, expiry=now+dur, id, radius}. Script users: only `-placelock 5` in walk_out_of
  (z_dignwalk.tcl:1064) and baby.tcl:318 — products/food do NOT auto-lock.
- **Failure** = "-1 0 0" (:402-405); `-checkfree` flips the result to a boolean (:333-398).

### Port deltas closed by the fix (DigglesTclEngine.cs get_place)
1. The port pinned ONE analytic z per (column,row) (`zOpt` clamp) instead of scanning depth —
   now a faithful 0.5-stepped free-z loop over the band **for -rect shapes** (see §7 for the
   r13 circle rollback).
2. The candidate z floor was `min(hA,hO)+0.5` — half a nibble UNDER the standing law; now
   `hmap(sx,sy−1)+1` (FUN_00595770:256) with the 14.5 cap.
3. The materials footprint was a ±0.4/±0.4/±0.9 box (killed whole columns); now
   ±0.3/±0.3/±0.4 ≈ the native 1-2 grid cells stand-in.
4. The jitter was `randW·U(0,1)` — missing the ×3.0 (DAT_00737428); now ×3, drawn once per
   (column,row) cell (bounded Random-stream use; native draws per candidate).
5. `-placelock T` was parsed-and-dropped; now auto-locks the winner for T seconds (unique
   negative ids emulate the native id-−1 append; the port lock check radius stays the callers'
   0.7 vs the native per-lock 0.8 — stand-in noted).

## 7. r13 manual test fallout — the circle-shape depth rollback (⚠ PORT DEVIATIONS)

Manual test r13 confirmed the -rect product law GOOD (crates hug the building, tight packing)
but exposed that the free-depth scan on the -circle WANDER/APPROACH shapes regressed gnome
behaviour:
- **(A) crowd stacking**: act_when_idle dispersal = `walk_random [irandom 2 4]`
  (z_procs.tcl:1298/:1305) → `get_place -circle L -mindist 0.7 -random L -walldist 1`
  (z_dignwalk.tcl:725). With depth scanned over the whole standing pocket the jittered winners
  scattered along z — gnomes dispersed INTO the screen and looked stacked at one point.
- **(B) barrels won't open**: open_box → walk_near_item 0.6 → `get_place -center {item}
  -nearpos {gnome} -mindist 0.6 -circle 2.6 -except this` (z_dignwalk.tcl:764) → arrival gate
  `check_switcher_dist` (3-D dist > 1 ⇒ tasklist_clear, z_procs.tcl:161-166/:953-956). A
  depth-offset approach put the weighted-ring spot |dz| ≈ 2·radius from the item — gate death.

The native scans z = center.z∓2R for circle too (005a9700:149-152), but each (cell,z) is
gated by flood-grid stamps (FUN_0041f7a0 build + FUN_0041bff0) whose z-legality is NOT yet
RE'd — that is what keeps native approach/wander depths sane. **Until those stamps are RE'd,
the port PINS one z per (column,row) for -circle shapes** (the pre-r13 analytic optimum
`(ideal.z + 2·center.z)/3` clamped into the standing pocket) and keeps the free scan for
-rect (explicit scripted depth bands). Flagged in code at the bandLo/bandHi split.

- **(D) inventory drops buried**: putdown-in-place seats items at gnome pos ± abst via
  beamto_world (z_procs.tcl:379-395) through `vector_fix`, whose native walkable branch
  passes z through untouched (FUN_00595770:270-275). A pocket-floor-standing gnome next to a
  relief step hands it a below-relief z → the ring/pannier renders inside the ground. Port
  deviation: the walkable branch now applies the spiral branch's own seat law
  (z = min(max(z, hmap(y−1)+1), 14.5),:256-262) — it only alters depths that are illegal to
  stand at. The ghost-click drop path was already safe (the FUN_0046b190 settle guarantees
  z ≥ hA by construction).

RE debt for full fidelity: decode FUN_0041bff0 / the flood-grid (cell,z) stamp semantics,
then restore the free-depth scan for circle shapes under the native legality.

---

## 3. What makes a candidate "free"

- The column's row must be a FLOOR sub-cell (port: `nav.SubIsFloor`; native: the grid build).
- Depth legality: within the standing pocket `[hmap(x,y−1)+1, 14.5]` (§0) ∩ the scan band.
- Not stamped occupied: other object BODIES always (unless `-except <obj>`), LYING ITEMS only
  when `-materials true` (the default; every producer retries `-materials false` — items may
  stack when the yard is full).
- Not within an unexpired placelock's radius (unless the asker's own / `-placelockidexcept`).
- `-walldist W`: no climb-wall within W. `-clip`: inside the exclusion rect never wins.

## 4. The SPACING law (why the original packs tight)

There is NO minimum distance BETWEEN dropped items anywhere in the law. Tight packing emerges
from: (a) `-mindist` is measured from the CENTRE (the workplace), so every drop competes for
the same ring; (b) the `2·dc` cost term pulls every drop as close to the centre as legality
allows; (c) an existing item only vetoes its own 1-2 grid cells (~0.3-0.6 units) — the next
drop lands in the ADJACENT cell, including in FRONT/BEHIND at the same east-column (the depth
dimension packs too); (d) when the ring truly fills, the scripts' `-materials false` fallback
lets items overlap. The " drop spot mindist" change (f423aed) is NOT the over-spacing
culprit — it is the faithful unconditional mindist floor (FUN_005a9270:29-30); the over-spread
was the column-killing footprint + pinned depth (§2 deltas 1/3). Fail-first: ten sequential
drops pre-fix smeared into a 1-D east line, pile radius 2.75 (all z identical); post-fix they
pack the (x,z) plane — radius 1.15, median neighbour 0.28.

## 5. The SEAT law and the "underground" case

- Product/get_place path: the returned z is a scanned depth within the standing pocket; the
  fix floors it at `hmap(x,y−1)+1` — a low/sunk workplace centre can no longer drag the seat
  into the relief. Fail-first: centre z = standing−0.7 pre-fix returned z = standing−0.5
  (`min(hA,hO)+0.5`); post-fix returns exactly the standing depth.
- Manual putdown (beamto_world): the item lands at the GNOME's z ± 0.5 through vector_fix
  passthrough (:270-275 — native-faithful). It is safe exactly when the gnome's own z obeys
  the standing law; a gnome whose z has drifted low (the separate climb/dig drift bugs) will
  put down "buried" items in native code too — that root lives in the movement executors, not
  in placement.
- InvAdd (FUN_0069bd30:145-151) parks contained items at {0,0,0}; every drop chain derives the
  drop position from the HOLDER/get_place — never from the item's stored position (verified in
  beamto_world/beam_from_inv_to_pos/prod_createproduct_*).

## 6. The crate/product anchor (why native crates hug the building)

`prod_createproduct_box*`: centre = the building, depth band `[cz + min(1, 11−cz), 13]`,
`-mindist 1.5`, `-nearpos gnome`, `-random 2`. The band starts 1 IN FRONT of the building, so a
candidate at dx≈0 can satisfy the whole mindist through depth (dz 3 ⇒ weighted 1.5) — the
native picks either the gnome-side ring spot (clean yard; ~1.4 east/west, dz +1), a slot
BETWEEN lying items' depth rows, or the frontal spot (walled yard) — all ON the tight ring at
the building. The pre-fix port could only pay mindist horizontally (pinned z) and its item
footprint walled entire columns, so a littered yard shoved the crate off the ring
("crate to the side", bug 2). Fail-first: littered-yard crate pre-fix at horiz 2.63 / weighted dc
2.67; post-fix on the ring (dc ≈ 1.6).
