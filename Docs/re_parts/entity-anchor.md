> **RE working note — port status is consolidated & kept live in `Docs/GNOME_BEHAVIOR_COMPLETE_RE.md`.** The decompile addresses/offsets/constants below are ground-truth LAW; the *port-gap / "our state" snapshots are the PRE-FIX analysis* — all such gaps were addressed in Phases 0-7 (DONE, see the parent doc §11). Note: `WalkClimbAction.cs` and `DigglesInputController.cs`, cited in older gap notes, were DELETED in Phase 7.

# entity-anchor — CObj position/anchor semantics, per-tick pos/velocity, facing & gravity

**Scope:** Where the CObj anchor lives in the struct, what world point it represents relative to
feet/body, how pos & velocity update per tick during walk vs climb (who writes pos), the Tcl
`get_pos`/`get_posy`/`set_pos`/`set_posbottom`/`get_rot*`/`set_rot*` contract, facing (`roty`) math
incl. `rotate_to*`/`get_anglexz`, and any gravity/snap applied outside the actions.

All citations are `Diggles.exe` decompile under
the per-function decompile of `Diggles.exe`. Float constants were read directly from the PE
(`Diggles.exe`, image base 0x400000) — values are authoritative, not inferred.
Live-state numbers are from the meadow memory dump via `tools\find_gnomes.py`.

---

## 0. World frame recap (ground truth)

The 512×640 tile grid is the SIDE VIEW (ant-farm). In the CObj struct (all floats):
- **+0x0c = X** — horizontal EAST, the walk axis.
- **+0x10 = Y** — the GAMEPLAY VERTICAL (tile rows; +Y = down/deeper; gravity & climb act on this).
- **+0x14 = Z** — DEPTH INTO THE SCREEN (the dig axis; `world z = nibble*0.5 + 7`, range 7.0..14.5).

`find_gnomes.py` and `CTclObjMethods::GetPos` (`006842c0:69-71`) both read the anchor at
`+0x0c/+0x10/+0x14`. The port's `RuntimeWorldSpawner.NativeToLocal(n) = (n.x, n.z*0.5+7, -n.y)`
(`RuntimeWorldSpawner.cs:28`, `MarkerHeightScale=0.5`, `MarkerYOffset=7.0`) is the exact image of
this: native X→localX, native Z→localY (height), native Y→local −Z.

---

## 1. The anchor IS the feet line — live-dump reconciliation

`find_gnomes.py` (meadow dump) returns two clusters near the y≈30.5 floor line:

| group | owner | example pos | anchor.Y | stands on +0x3c | rot.Y (+0x44) | carrier (+0x39c/+0x3a0) |
|---|---|---|---|---|---|---|
| **A (alive player gnomes)** | 0 / 1 | (246.84, **30.50**, 6.42) | on the floor line | byte 0x03 (walk\|floor) | 4.712 = 3π/2 | 0 / 10 (slot empty) |
| **B (carried/neutral)** | −1 (0xFFFFFFFF) | (246.13, **29.00**, 11.19) | ~1.0 above line | off the +0x3c grid | 3.985 + Z-tilt −0.099 | 25 / 369 (real link slot) |

Both clusters share the **same vftable 0x00d9bcb8** (probed) — same class. The decisive facts:

1. **For a free-standing, owned (alive) gnome the anchor sits EXACTLY on the floor sub-cell line.**
   A gnome at y=30.50 maps to sub-grid row `round(30.50*8)=244` → tile-row 30, **in-tile subY=4**,
   which is precisely the +0x3c floor row (floor bits live on subY 3-5). Mid-walk gnomes show y=30.12
   (subY=1) and y=14.00 (subY=0): the anchor's Y is **continuous**, tracking the sampled floor height
   between sub-cells, never an integer snap.
   → **The old note "body anchor sits ~1.0 above the feet" is WRONG for alive ground gnomes.** Anchor
   = feet on the ground line. There is NO +1.0 centre offset baked into the stored anchor.

2. **Group B (y≈29) is NOT "the same gnome 1.0 higher".** Every B entity has `owner == −1` (no player),
   a real carrier slot (`+0x39c`=25, `+0x3a0`=369 → an entry in the link table), and a 3-axis tilt.
   These are attached/neutral objects (carried, mounted, or wildlife), not the alive player gnomes.
   The ~1.0 Y delta is their support being a different structure, **not** an anchor↔feet offset.

**Conclusion for the port:** place the gnome anchor (its `Position`) ON the floor line; do not add a
body-centre lift to the stored anchor. (A *visual* mesh offset is fine, but it is not the anchor.)

---

## 2. The anchor / feet relationship — `get_pos` vs `set_posbottom` (the carrier offset)

There IS a feet↔anchor adjustment, but it is the **carrier-height term**, applied only when the entity
is *attached to a carrier* (link/mount/elevator). For a free-standing gnome it is zero.

### 2.1 `get_pos` — `CTclObjMethods::GetPos` (FUN_006842c0)
```
e = entity
carrier = e[+0x39c] // u32 carrier-slot id (0xFFFFFFFF = none)
carrierIx = e[+0x3a0] // i32 carrier sub-index (-1 = none)
if (e[+0x3d0] & 0x4000): // "use global carrier" flag
    carrier   = G[+0xa3d4];  carrierIx = G[+0xa3dc]        // G = DAT_00772400
if (carrier == 0xFFFFFFFF || carrierIx == -1): // ── NO CARRIER (gnome on ground)
    out = (e[+0x0c], e[+0x10], e[+0x14])                   // raw anchor
else: // ── CARRIED
    slot = (&G[+0x5c8])[carrier]      // carrier<0x100 else RaiseException(0xC000008C)
    h = 0.0
    if slot != 0:
        rec = carrierIx*0xE8 + slot[0]     // FUN_004059a0: 0xE8(232)-byte records
        h   = rec[+0x54] * DAT_00736fcc    // = rec[+0x54] * 0.005   (model→world scale)
    out = (e[+0x0c], e[+0x10] + h, e[+0x14])               // Y lifted by carrier height
```
Citations: `006842c0:62-89`; `FUN_004059a0` = `004059a0:7-15` (`param_2*0xE8 + *param_1`);
`DAT_00736fcc = 0.005` (PE-read).

So **only Y (+0x10) gets the carrier-height correction**; X and Z are always raw. `get_posy`
(`CTclObjMethods::GetPosY`, FUN_00683de0) returns the same Y component (mangled wrapper, calls the
shared result helper `FUN_0069eb00`).

### 2.2 `set_posbottom` — `CTclObjMethods::SetPosBottom` (FUN_00683050 → FUN_005cb600)
The exact inverse: it **subtracts** the same carrier height from the input Y before storing.
```
// FUN_005cb600(e, x, y, z)
... resolve carrier exactly as get_pos...
if (carrier == 0xFFFFFFFF || carrierIx == -1):
    // no change   (store x,y,z as-is)
else:
    h = (slot != 0) ? rec[+0x54]*0.005 : 0.0
    y = y - h                                  // 005cb600:41
if (changed) FUN_005cd8e0(e, {x,y,z}) // core writer (§3.1)
if (e[+0x238] == 0) { FUN_005ebc90(e); FUN_005eb470(e); } // re-register + dirty
```
Citations: `005cb600:17-54`.

**Contract:** `set_posbottom` takes a FEET/ground-line world Y and stores anchor = feet − carrierHeight;
`get_pos`/`get_posbottom` return anchor + carrierHeight = feet again. For a free gnome (no carrier) both
are identity, so `set_pos` ≡ `set_posbottom` and `get_pos` ≡ `get_posbottom` ≡ raw anchor. The two only
diverge when the entity is mounted/linked.

### 2.3 `set_pos` — `CTclObjMethods::SetPos` (FUN_00682c30)
Plain anchor write, NO carrier adjustment:
```
parse x,y,z (3-arg list form OR 4/5-arg components; 5th arg = z when verb is component form)
if (e[+0x0c]!=x || e[+0x10]!=y || e[+0x14]!=z):
    FUN_005cd8e0(e, {x,y,z})                              // 00682c30:179-181
    if (e[+0x238]==0) { FUN_005ebc90(e); FUN_005eb470(e); }
```
Citations: `00682c30:178-191`. A list-form `set_pos {x y z}` from the CAction dispatcher
`CTclGlobalMethods::Elf` (FUN_00649640) writes its CAction descriptor's own slots
(`piVar4[0x14..0x16]` = +0x50 AND `[0x1a..0x1c]` = +0x68, `00649640:990-995`) — that is the *action
parameter block*, NOT the live entity; the live write still goes through `FUN_005cd8e0`.

---

## 3. WHO writes the anchor — there is no separate physics step; the ACTION integrates pos directly

### 3.1 Core anchor writer `FUN_005cd8e0(e, float* xyz)` — the single mutation point
Every position change (set_pos, set_posbottom, the walk mover, the gravity step) funnels here.
```
if (xyz != current):
    e[+0x3dc] = 0
    e[+0x0c]=xyz[0]; e[+0x10]=xyz[1]; e[+0x14]=xyz[2]      // store anchor
    // re-bucket in the CMap spatial hash using ONLY X (+0x0c) and Y (+0x10):
    s   = DAT_0073703c (= 0.125)         // world→hash scale
    M   = DAT_0077244c                   // CMap
    oldCell = M[+0x849c]*round(oldY*s) + round(oldX*s)     // row stride M[+0x849c]
    newCell = M[+0x849c]*round(newY*s) + round(newX*s)     // (mod M[+0x84ac])
    if (oldCell != newCell):   unlink from oldCell bucket; link into newCell bucket
    FUN_005cb210()                       // notify spatial observers
    e[+0x3d8] |= 2                        // "moved" dirty bit
    e[+0x402] = 1                         // render-transform dirty
```
Citations: `005cd8e0:18-55`; `DAT_0073703c=0.125`, `DAT_0077244c`=CMap singleton.
**Z (+0x14, depth) does NOT participate in the spatial hash** — only the X/Y ground plane does, which is
why the side-view grid is indexed by (X,Y) and depth is a per-cell attribute.

Thin wrapper `FUN_005cd7e0(e, xyz)` = "set + re-register if changed", used by the vector-form set_pos.

### 3.2 The per-tick walk mover (CAct_WalkAnim-style) `FUN_004103b0(act)` — the heart
`act+0xc` = the entity. Two modes selected by `act[+0x54]` (root-motion flag):

```
e = act[+0xc]
act[+0x50] += 1 // step counter (caps walk at < 0x33 = 51)
start = (e[+0x0c], e[+0x10], e[+0x14]) // saved into act[+0x34..+0x3c]
toTarget = act[+0x1c..+0x24] - start // vector to target (act+0x1c = target XYZ)
d2 = |toTarget|^2

── MODE A: act[+0x54] != 0 (ROOT-MOTION / animation-driven) ───────────────── (004103b0:75-97)
   rm = root-motion translation for this frame (from the anim; computed via FUN_00410940/0040fa20)
   head = heading-blend term
   newPos = start
          + rm   * DAT_00737054 (= 0.2)          // root-motion contribution
          + head * DAT_007370d4 (= 0.8)          // heading-align contribution
   if (newPos != current): FUN_005cd8e0(e, newPos); if(e[+0x238]==0){FUN_005ebc90;FUN_005eb470;}
   // facing:
   yaw = act[+0x40] - DAT_00737440 (= π) // 004103b0:93
   FUN_005cd840(e, {yaw, 0, 0}) // writes rot (§5)
   return act[+0x50] < 0x33 // keep ticking until step cap

── MODE B: act[+0x54] == 0 (VELOCITY / non-root-motion) ───────────────────── (004103b0:99-162)
   // build a unit step along toTarget (or use stored dir act[+0x28..]), scaled by DAT_00737010(=0.05)
   vel = act[+0x48]*DAT_007370b0(=0.7) + step*DAT_00737060(=0.3) // damped velocity (act+0x48)
   act[+0x48] = vel
   newPos = start + step // 004103b0:135-137
   if (newPos != current): FUN_005cd8e0(e, newPos); re-register
   // facing via FUN_005cd840 from the velocity heading (004103b0:148-153)
   moved2 = |current - target|^2
   return (moved2 >= DAT_00736fa4(=1e-4)) && !arrived && act[+0x50] < 0x33
```
Citations: `004103b0:30-162`; constants PE-read: `DAT_00737054=0.2`, `DAT_007370d4=0.8`,
`DAT_00737440=π`, `DAT_00737010=0.05`, `DAT_007370b0=0.7`, `DAT_00737060=0.3`, `DAT_00736fa4=1e-4`.

**Key takeaway:** the action itself calls `FUN_005cd8e0` every tick. **There is no separate physics
integrator that moves the gnome behind the action's back.** "Velocity" (`act+0x48`) is the action's own
damped step accumulator, not an engine-wide rigidbody. Walk speed is FRAME-COUPLED to animation root
motion (Mode A) — see ORIGINAL_PATHFINDING_RE.md §4.2 (`disp = (rm[9..12]) * 0.25`, `DAT_00737058=0.25`).

### 3.3 Floor-height following — Y is sampled per node, not lerped start→goal
The walk Y per intermediate point is the sampled terrain nibble (`FUN_00424470`):
```
FUN_00424470(subX, subY):
    tile = FUN_00592f10(subX>>3, (subY+2)>>3)            // +2: support row is 2 sub-cells "below"
    aux  = tile.block_idx*0x59 + CMap[+0x24][+0x10]       // BDB aux, stride 0x59 (89)
    return aux[ ((subY+2)&7)<<3 | (subX&7) ]              // height NIBBLE byte for that sub-cell
```
Citation: `00424470:16-29`. The integrator interpolates Y **between adjacent on-terrain points**; it
never straight-lerps start→goal. (This is the port's "straight-Y-lerp" bug — §6.)

### 3.4 During CLIMB the same machinery runs
Climb (`CAct_Climb`) dispatches per-waypoint `CAct_WalkAnim` movers (the same `FUN_004103b0`-family),
so pos is written identically through `FUN_005cd8e0`. Climb-end Z offsets: up→+14.5 (`DAT_00737564`),
down→+12.5 (`DAT_00737548`); arrival eps 0.30 (√0.09). See ORIGINAL_PATHFINDING_RE.md §5.

---

## 4. Gravity / snap applied OUTSIDE the walk action

### 4.1 The fall/settle step `FUN_0040f440(act)` — writes Y ONLY
A dedicated action (not the walk mover) handles dropping/settling:
```
e = act[+0xc]
supported = FUN_004097b0(e[+0x0c], e[+0x10], e[+0x14]) // ground test (§4.2)
if (!supported): act[+0x5c] = 1 // start/continue falling
else if (act[+0x5c]==0): // landed → settle exactly once
    newPos = (e[+0x0c],  e[+0x10] + act[+0x58],  e[+0x14])  // add fall delta to Y ONLY
    if (newPos != current): FUN_005cd8e0(e,newPos); re-register
... timed wait on G[+0xa088]...
```
Citations: `0040f440:18-40`. **Only +0x10 (Y, the gameplay vertical) is touched** — X and Z are added
0.0. This is the proof that gravity acts on tile-Y, exactly matching `+y = down/deeper`.

### 4.2 Ground/support test `FUN_004097b0(x, y, z)`
```
sx = (int)(x * DAT_0073752c) // DAT_0073752c = 8.0 → world→sub-cell
sy = (int)(y * 8.0)
// must be inside CMap sub-cell bounds with a 0x21(=33) margin:
if CMap[+0x94]+0x21 <= sx < CMap[+0x9c]-0x21
 && CMap[+0x98]+0x21 <= sy < CMap[+0xa0]-0x21:
    h = FUN_004093f0(sx, sy) & 0xFF        // terrain height byte at that sub-cell
    if ((int)h <= (int)z):  return 1       // z at/above terrain → SUPPORTED
return 0 // unsupported (fall) / OOB
```
Citations: `004097b0:15-27`; `DAT_0073752c=8.0` (PE-read). Note support compares the **Z/depth byte**
`h` against the entity's Z — the "stands on ground" test is along the depth axis vs the per-cell height
table, consistent with depth being the dug axis.

There is **no continuous gravity integrator on every tick** — falling is an explicit action state; a
standing gnome is simply held by its action (or idle). Position is never mutated except through one of
the action steps above, all of which call `FUN_005cd8e0`.

---

## 5. Facing — `roty` (+0x44), the rotation setter, snap, and `get_anglexz`

### 5.1 Rotation storage & setter `FUN_005cd840(e, float* rot)`
Rotation is a 3-float triple: **rotX@+0x40, rotY@+0x44 (= the heading / "roty"), rotZ@+0x48.**
```
e[+0x40]=rot[0]; e[+0x44]=rot[1]; e[+0x48]=rot[2]
// per-component garbage/NaN guard: if outside (−10000, 10000) snap to 0
if (rot[i] >= DAT_007376c4(=10000) || rot[i] <= DAT_007377e8(=-10000)): e[+0x40+4i] = 0
e[+0x3d8] |= 2; e[+0x402] = 1 // moved + render-dirty
if (e[+0x238]==0){ FUN_005ebc90(e); FUN_005eb470(e); }
```
Citations: `005cd840:14-35`; `DAT_007376c4=10000`, `DAT_007377e8=-10000` (PE-read). The guard is a
sanitiser (drops ±inf/garbage to 0); it is NOT a near-zero deadzone.

### 5.2 Tcl rot accessors
- `CTclObjMethods::SetRotY` (FUN_00685140): reads existing `e[+0x40]` & `e[+0x48]`, overwrites only
  the middle (Y) with the arg, then `FUN_005cd840` → preserves rotX/rotZ, sets the heading
  (`00685140:93-95`).
- `SetRot`/`SetRotX`/`SetRotZ` analogous; `GetRotY` (FUN_006858a0) returns `e[+0x44]` via the result
  helper.

### 5.3 The `rotate` action and `rotate_to*` procs (Tcl ↔ native contract)
`rotate_to*` are **per-class Tcl procs**, not engine builtins. They wrap the engine verb
`action this rotate <angle>` (→ constructs a **CAct_Rotate**, factory `FUN_00405e10:240-258`,
`CAct_Rotate::vftable`). Fixed named angles (radians, CCW):

| proc | angle | meaning |
|---|---|---|
| `rotate_tofront` | 0 | face front |
| `rotate_toleft` | 1.57 (π/2) | face left |
| `rotate_toback` | 3.14 (π) | face back |
| `rotate_toright` | 4.71 (3π/2)| face right |
| `rotate_toangle <a>` | a | arbitrary |

Source: e.g. `original/data/Scripts/classes/characters/brut.tcl:490-495`,
`fenris.tcl:829-833`. The CAct_Rotate tick rotates `e[+0x44]` toward the target each frame (turn anim);
`set_roty this <a>` sets it instantly. Live dump corroborates: the standing alive gnome had
rotY=4.712 = 3π/2 ("right").

### 5.4 `get_anglexz <p1> <p2>` — engine builtin (X-Z-plane heading)
`get_anglexz` is an engine command (string at VA 0x72e330, referenced from the big Tcl-math dispatcher
`FUN_00617c90`-region; Ghidra split the body — its exact disassembly is NOT cleanly recovered here).
Its behaviour is VERIFIED empirically (project facing harness, ANGERR≈0 for E/W/depth/oblique and named
dirs):
```
get_anglexz(p1, p2) = atan2(p1.x - p2.x, p2.z - p1.z) // native X-Z plane, dx NEGATED
```
Used as `set_roty this [expr {[get_anglexz $rpos $place] - 1.57}]` (`raupe.tcl:81,112`) and in combat
facing (`brut.tcl:301,331`). The `-1.57` offsets the model's default facing. The port mirror is
`DigglesObject.FaceRotationForNativeAngle`: `wx=-sin(a); wz=-0.5*cos(a); yaw=atan2(wx,wz)` then
`AngleAxis(yaw - spawnYaw) * SpawnRotation` — the `-0.5*cos` is the exact consequence of the `z*0.5`
height scale in NativeToLocal (see `DigglesObject.cs:145-153`). **Do not "simplify" `-0.5*cos` to
`-cos`** (twice-regressed historically). See ORIGINAL_PATHFINDING note & reference-cutscene-facing.

---

## 6. PORT GAP analysis vs current code

Port files: `Assets/Diggles/Runtime/Tcl/Engine/DigglesTclEngine.cs`,
`Assets/Diggles/Runtime/Engine/DigglesObject.cs`,
`Assets/Diggles/Runtime/Engine/RuntimeWorldSpawner.cs`,
`Assets/Diggles/Runtime/Engine/Actions/WalkClimbAction.cs`.

1. **Anchor semantics — CORRECT in spirit.** Port stores `entity.Position` as native (x,y,z) and renders
   via `NativeToLocal` (`RuntimeWorldSpawner.cs:28`), matching native +0x0c/+0x10/+0x14 → render.
   The floor-line placement (anchor on the ground line, no +1.0 centre lift) is the correct target and
   matches alive-gnome dump data — keep it; do NOT re-introduce a +1.0 body-centre offset on the anchor.

2. **Carrier-height offset is NOT modelled.** Port `get_pos`/`get_posy`/`get_posbottom` return raw
   `e.Position` and `set_pos`/`set_posbottom` store raw (`DigglesTclEngine.cs:330-334, 935-945`). The
   native pair adds/subtracts `carrierRecord[+0x54]*0.005` to/from Y when the entity is mounted/linked
   (§2). For free-standing gnomes this is identity (no gap). **Gap only bites for carried/mounted
   entities** (elevators, riding, work-dummies). Low priority unless mount/link Y looks off; if so,
   implement `set_posbottom = anchor.Y = feetY − carrierHeight` and `get_pos = anchor.Y + carrierHeight`.

3. **`set_posbottom` == `set_pos` and `get_posbottom` == `get_pos` in the port** — they should be the
   inverse-paired forms (§2.1/2.2). Same root cause as (2); same low priority for ground gnomes.

4. **No spatial-hash re-bucket / world re-register on position change.** Native `FUN_005cd8e0` re-links
   the entity in the CMap (X,Y)-plane hash and raises observers (`005cd8e0:28-52`,
   `FUN_005ebc90`/`FUN_005eb470`). Port `set_*` just sets `Position`/transform. If the port's nav or
   neighbour queries ever read a live spatial index keyed off entity position, they will be stale.
   Assess against the port's nav design (custom NavWorld) — may be intentionally absent.

5. **Rotation NaN/garbage guard missing.** Native `FUN_005cd840` snaps any rot component with |x|≥10000
   to 0 (`005cd840:20-28`). Port `set_rot*` stores raw (`DigglesTclEngine.cs:340-351`). Add a clamp to
   match (defensive only).

6. **Per-tick pos write architecture — partial.** Native: the action calls the single writer every tick
   (Mode A root-motion = `start + rm*0.2 + head*0.8`; Mode B velocity = damped step). Port
   `WalkClimbAction` writes `obj.Velocity`/position with a synthesised mount/dismount choreography and
   `arriveEps=0.12` (`WalkClimbAction.cs:73-74`). Divergences vs native: arrival eps should be **0.30**
   (√`DAT_0073702c`=0.09) with the **Z axis weighted ×0.5** (`DAT_0073708c`); speed should be root-motion
   driven, not a fixed `spd`; the mount/crawl/jump synthesis is invented (native climb flag is uniformly
   2). These are the ORIGINAL_PATHFINDING_RE §4-§5 movement gaps, surfaced again from the anchor side.

7. **Floor-Y following.** Port lerps Y toward the leg target; native samples per-node terrain height
   (`FUN_00424470`, the `(subY+2)` support-row nibble) and interpolates between on-terrain points (§3.3).
   Port should sample each waypoint's floor Y and interpolate neighbour→neighbour.

8. **Facing math — CORRECT and VERIFIED** (`DigglesObject.FaceRotationForNativeAngle`, `-0.5*cos`,
   matched-pair dx/wx negation). Keep as-is. Ensure `rotate_to*`/named angles use front=0, left=π/2,
   back=π, right=3π/2 (§5.3) and `get_anglexz` = `atan2(p1.x−p2.x, p2.z−p1.z)`.

---

## 7. Constants table (all PE-read from Diggles.exe @ base 0x400000)

| symbol | VA | value | role |
|---|---|---|---|
| DAT_00736fcc | 0x736fcc | **0.005** (1/200) | model→world scale; carrier-height term `rec[+0x54]*0.005` (get_pos/set_posbottom) |
| DAT_0073703c | 0x73703c | **0.125** (1/8) | world→spatial-hash scale in `FUN_005cd8e0` (X,Y bucketing) |
| DAT_0073752c | 0x73752c | **8.0** | world→sub-cell scale (support test `FUN_004097b0`) |
| DAT_00737054 | 0x737054 | **0.2** | root-motion contribution weight (walk mover Mode A) |
| DAT_007370d4 | 0x7370d4 | **0.8** | heading-align contribution weight (walk mover Mode A) |
| DAT_00737058 | 0x737058 | **0.25** | root-motion per-frame displacement scale (WalkFloor §4.2) |
| DAT_00737010 | 0x737010 | **0.05** | unit-step normalize scale (walk mover Mode B) |
| DAT_007370b0 | 0x7370b0 | **0.7** | velocity damping (retain) |
| DAT_00737060 | 0x737060 | **0.3** | velocity damping (new-step) |
| DAT_00736fa4 | 0x736fa4 | **1e-4** | min-progress² to keep ticking (Mode B) |
| DAT_00737440 | 0x737440 | **π** (3.14159) | facing offset (`yaw = act[+0x40] − π`) |
| DAT_00737520 | 0x737520 | **7.0** | Z height bias (`z = nibble*0.5 + 7`) |
| DAT_0073708c | 0x73708c | **0.5** | Z/depth weight in dist metrics; `z = nibble*0.5+7` |
| DAT_0073702c | 0x73702c | **0.09** | arrival eps² (→ 0.30 world) |
| DAT_007370d0 | 0x7370d0 | **0.7854** (π/4) | turn-clip threshold |
| DAT_007371e0 | 0x7371e0 | **1.5708** (π/2) | quarter-turn angle |
| DAT_00737564 | 0x737564 | **14.5** | climb-end Z (up) |
| DAT_00737548 | 0x737548 | **12.5** | climb-end Z (down) |
| DAT_007377e8 | 0x7377e8 | **−10000** | rotation garbage-snap low bound (`FUN_005cd840`) |
| DAT_007376c4 | 0x7376c4 | **10000** | rotation garbage-snap high bound |

## 8. CObj struct offsets

| offset | field | source |
|---|---|---|
| +0x00 | vftable (gnome = 0x00d9bcb8 in meadow dump) | dump probe |
| +0x0c / +0x10 / +0x14 | **anchor pos X / Y / Z** (float) | `006842c0:69-71`, dump |
| +0x40 / +0x44 / +0x48 | **rot X / Y(heading) / Z** (float) | `005cd840:17-19`, `00685140:93` |
| +0x58 | fall delta (added to Y by gravity step) | `0040f440:28` |
| +0x238 | "no re-register" guard (==0 ⇒ re-register on move) | `005cd8e0`/`00682c30:181` |
| +0x248 | walkresult (Tcl `get_walkresult`) | find_gnomes / pathfinding doc §3.4 |
| +0x36c | owner (player id; 0xFFFFFFFF = none) | find_gnomes / dump |
| +0x39c / +0x3a0 | carrier-slot id / carrier sub-index (−1/0xFFFFFFFF = none) | `006842c0:62-63`, `005cb600:17-18` |
| +0x3d0 | flags; **bit 0x4000 = use global carrier** | `006842c0:64`, `005cb600:19` |
| +0x3d8 | dirty flags; **bit 2 = moved/rotated** | `005cd8e0:53`, `005cd840:29` |
| +0x402 | render-transform dirty (u8) | `005cd8e0:54`, `005cd840:30` |
| (carrier rec) +0x54 | carrier height (×0.005 → world Y offset) | `006842c0:84`, `005cb600:39` |

---

## 9. Unresolved / low-confidence

- **`get_anglexz` exact disassembly NOT recovered** — its body lives in a Ghidra-split region of the
  big Tcl-math dispatcher (`FUN_00617c90`-area; the 131-line decompile at that addr is an unrelated OBJ
  exporter). The formula `atan2(p1.x−p2.x, p2.z−p1.z)` is empirically VERIFIED (ANGERR≈0) but not
  line-cited from native. INFERENCE on the exact internal sign source; behaviourally proven.
- **CAct_Rotate tick rate** (how fast `e[+0x44]` slews per frame toward the `rotate` target) not traced;
  only the factory and the named-angle contract are confirmed. The turn-clip threshold (π/4) and turn
  anims are from the pathfinding doc, not re-derived here.
- **Mode A root-motion extraction** (`FUN_00410940`/`FUN_0040fa20`/`FUN_004234a0` producing `rm`/`head`)
  is summarised from ORIGINAL_PATHFINDING_RE §4.2 (`disp = rm[9..12]*0.25`), not fully re-walked in this
  pass; cited there.
- **Group-B (owner=−1, y≈29) identity** — confirmed carried (real carrier slot) + same vftable, but
  whether they are wildlife, decoration, or a posture variant of gnomes is not pinned. Does not affect
  the anchor=feet conclusion for alive player gnomes (group A).
- **Whether the port needs the spatial-hash re-register** depends on the custom NavWorld design (the
  native CMap hash may have no port analogue). Flagged, not resolved.
