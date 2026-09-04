# Diggles Unity Port — Coordinate System & Spawn Pipeline

> Coordinate/axis facts (root `Q_x(-90)`, outer+inner split, marker-height
> constants, terrain vertex map) are current and load-bearing. File paths
> refreshed: `BdbPmpWorldBuilder` moved Editor→`Runtime/World`; the old
> `WalkAction.cs` is gone — actions now live in `EntityActions.cs`, and the
> live movement executor is `MetaWalkAction` (§4).

How positions, rotations and walking work end-to-end in this port.
Read this BEFORE touching axis conversion, marker spawn or movement facing.

---

## TL;DR

- Diggles native frame is **Z-up** (X horizontal, Y depth, Z height).
- Unity is **Y-up**.
- The campaign root has `rotation = (270, 0, 0)` (= `Q_x(-90)`). All terrain
  + markers live under it. That single root rotation is the only "axes
  hack" — everything else stays in Diggles native data form.
- Marker meshes are NOT axis-converted at build time. Vertex data is raw
  Diggles native (Y-DOWN model authoring: head at mesh -Y, face at mesh +Z).
- Each marker is **outer GameObject** (controller) + **inner "Model"
  GameObject** (mesh + animator).
- `inner.localRotation = Q_x(-90) * markerRot` — OLD-style spawn rotation
  proven visually correct.
- `WalkAction` rotates the OUTER's world rotation to face target direction,
  derived from per-character spawn face direction.

---

## 1. Axes per layer

| Layer | X axis | Y axis | Z axis | Source |
|---|---|---|---|---|
| Diggles world | horizontal | depth | up | engine convention |
| Diggles model (`.3db` verts) | side | up (Y-DOWN: head at -Y) | forward/back | per-character authoring |
| Unity world | horizontal | up | depth | engine convention |
| Campaign root in scene | (X) | (Y) | (Z) | with rotation (270, 0, 0) applied |

Diggles palette/terrain vertex data is in Diggles WORLD frame (Z up).
Diggles model vertex data is in MODEL local frame which is **Y-DOWN** by
visual inspection — the head verts cluster around mesh `-Y`. This is
verified empirically via `DigglesSpawnDiagnostics` raw-mesh test.

---

## 2. Vertex transforms

### 2.1 Terrain (`Runtime/World/BdbPmpWorldBuilder.EmitVertexW`)

```
unity_pos = Vector3(p.Position.x + tx, p.Position.z, -(p.Position.y + ty))
```

Maps Diggles `(X, Y_depth, Z_height)` → Unity `(X, Y_height, -Z_depth)`.
After the root's `Q_x(-90)` applies, this lands in the visual world frame
the project was originally tuned for (mountains extend in world `-Z`,
ground plane is world `XY`).

### 2.2 Marker meshes (`Editor/Importers/MarkerModelResolver.ApplyAxisConvention`)

```
ApplyAxisConvention(v) = v // identity — no conversion
```

Mesh stays in raw Diggles model frame. The compensating rotation lives on
the inner `Model` GameObject (see §3.2), not in the vertex data.

---

## 3. Spawn pipeline (`SpawnTclMarkers`)

There are TWO spawners that build the SAME outer+inner structure below:
- **Editor import**: `Runtime/World/BdbPmpWorldBuilder.SpawnTclMarkers` (+
  `Editor/Importers/CampaignBrowserBuilder`) — bakes markers into a scene.
- **Play-mode runtime**: `Runtime/Engine/RuntimeWorldSpawner` — spawns the
  same entities live from Tcl (its own copies of `MarkerHeightScale=0.5`,
  `MarkerYOffset=7.0` and the `(x, z*scale+off, -y)` map at
  `RuntimeWorldSpawner.cs:341`).

### 3.1 Two-level structure

```
Diggles__Campaign (root, rotation = (270, 0, 0) = Q_x(-90))
└── Markers (.tcl) (bucket, identity)
    └── Zwerg_L123  (OUTER GameObject)
        ├── DigglesObject    (entity / controller)
        ├── BoxCollider      (NO — collider is on Model)
        ├── transform.localPosition = marker pos (axis-converted)
        ├── transform.localRotation = identity
        └── Model            (INNER GameObject)
            ├── MeshFilter / MeshRenderer / DigglesAnimator
            ├── BoxCollider  (sized to frame0 mesh bounds — follows mesh rotation)
            └── transform.localRotation = Q_x(-90) * markerRot
```

### 3.2 Why outer + inner

- The **inner** rotation (`Q_x(-90) * markerRot`) bakes in the per-marker
  TCL rotation AND the axis-convention pitch that puts the Y-DOWN mesh
  upright relative to the rotated world.
- The **outer** stays free for `WalkAction` to set its world rotation
  without disturbing the model's visual orientation.
- `BoxCollider` lives on the inner so it rotates with the mesh (otherwise
  it ends up axis-aligned in outer space while the visible model is
  rotated by inner — collider "lying down" while model stands).

### 3.3 Marker position

```
go.transform.localPosition = new Vector3(
    m.Position.x + tpl.WorldX + xOffsetAdjust,
    m.Position.z * MarkerHeightScale + MarkerYOffset,    // height
    -(m.Position.y + tpl.WorldY + yOffsetAdjust) + tieBreaker);
```

`MarkerHeightScale = 0.5` and `MarkerYOffset = 7.0` come from
`FUN_004c6b20` constants in `Diggles.exe` (`DAT_0073708c`, `DAT_00737520`).

### 3.4 TCL rotation interpretation

```
markerRot = Quaternion.Euler(-rX * Rad2Deg, -rY * Rad2Deg, -rZ * Rad2Deg)
```

**ALL THREE angles are negated** (the old form negated only `rY`). This is
not an empirical convention but the decompiled native law:
the engine composes the object rotation fields (`+0x40/44/48` = rx/ry/rz)
as `M = Ry(ry)·Rx(rx)·Rz(rz)` — Z innermost, then X, then Y — with every
per-axis matrix the **transpose** of Unity's numeric same-axis matrix
(native rotation directions are mirrored vs Unity's left-hand convention).
Verified at two independent decompile sites:

- `FUN_004d2420` (link point → world → screen): rz block `x'=x·c+y·s`,
  `y'=y·c−x·s`; rx block `y''=y'·c+z·s`, `z''=z·c−y'·s`; ry block
  `x=x'·c−z''·s`, `z=z''·c+x'·s`.
- `FUN_005cbf70` (object offset vector → world): identical shape and order.

Unity's `Quaternion.Euler(x,y,z)` composes `Qy·Qx·Qz` — the same order —
so the exact mapping is `Euler(−rx,−ry,−rz)`. It is also the SAME law
`DigglesAnimator.LinkEulerToQuat` already used for link eulers (one
engine, one euler convention).

Yaw-only rotations (every character, most props) are unaffected by the
fix (`x=z=0`), which is why dwarves spawning right at rY = 4.71 = 270°
validated the old half-law for years. Props with authored pitch/roll spun
the wrong way: the anchor case is the herbalist-camp handsaw
(`Fuchsschwanz`, `urw_unq_vodo_001_a.tcl:407`, rot `5.01099 2.69998
1.11144`) which rendered 165° off with its blade swung ~1 m screen-right
out of the support beam (`Dummy_Multi_Holzstuetze_f`, x=29.163) — while
loading our.sav into the original showed it correctly (archived bits
true; presentation-only bug). Law pinned by
`Tests/Editor/AuthoredRotationLawTests.cs`.

---

## 4. Movement facing (per-character spawn face)

> The old single `WalkAction.cs` file is gone. Actions now live in
> `Runtime/Tcl/Engine/EntityActions.cs`: `WalkAction` (root-motion distance
> walker for cutscene `do_action walk`) and `MetaWalkAction` (the live
> gameplay nav executor — faithful `CAct_MetaWalk`, walks world-space
> waypoints). Neither re-derives the yaw formula below anymore — the
> per-character spawn-face math (§4.2/§4.3) now lives on **`DigglesObject`**
> (`FaceWorldDirection` / `FaceRotationForNativeAngle`) and is shared by the
> cutscene facing path. This section documents that facing basis.

### 4.1 The challenge

Different `.3db` characters author "forward" along different mesh axes:

| Character | Loop anim | `motionOffset` (mesh frame) | Forward axis |
|---|---|---|---|
| `mann` (Zwerg) | `gehen_loop` | `(0, -16.22, 0)` | -Y mesh |
| `hamster` | `laufen_loop` | `(0, -18.73, 0)` | -Y mesh |
| `hamster` (gallop) | `galopp_loop` | `(40.04, 0, 0)` | +X mesh |
| `troll` | `gehen_loop` | `(0, ?, 0)` | -Y mesh |
| `raupe01` (worm) | `kriechen` | `(-6.02, 0, 0)` | -X mesh |

A fixed walk-yaw formula doesn't work universally. Each character spawns
facing a DIFFERENT world direction depending on its mesh authoring + TCL
rotation.

### 4.2 Per-character spawn face

`DigglesObject.Awake` captures `SpawnFaceWorld` — the direction the
visible model "faces" in world coords at spawn time.

Algorithm:
1. Iterate `animator.Clips`, pick the first one with non-zero
   `MotionOffset` (regardless of name suffix — `kriechen` doesn't end
   with `_loop`).
2. Treat that motion offset as a "forward direction in mesh frame".
3. Transform to world via `Model.rotation * motionOffset_normalized`.
4. Project to horizontal XZ plane.
5. **If the projection is non-zero**: that's the spawn face. Used for
   worm (motion `-X` mesh → `-X` world after spawn rotation, horizontal).
6. **If the projection is ~zero** (motion direction goes vertical in
   world): fall back to `Model.rotation * Vector3.forward` (mesh `+Z`).
   Used for mann/hamster/troll (motion `-Y` mesh → vertical `+Y` world
   after the `Q_x(-180)` chain, so use mesh `+Z` as visual face axis).

```csharp
public Vector3 SpawnFaceWorld;
public Quaternion SpawnRotation;

void Awake {
    var modelChild = transform.Find("Model");
    Quaternion modelRot = modelChild != null
        ? modelChild.rotation
        : Quaternion.identity;

    Vector3 faceMesh = Vector3.zero;
    foreach (var clip in animator.Clips) {
        if (clip.MotionOffset.sqrMagnitude < 0.01f) continue;
        faceMesh = clip.MotionOffset.normalized;
        break;
    }

    Vector3 face = Vector3.zero;
    if (faceMesh.sqrMagnitude > 0.001f) {
        face = modelRot * faceMesh;
        face.y = 0f;
    }
    if (face.sqrMagnitude < 0.01f) {
        face = modelRot * Vector3.forward;
        face.y = 0f;
    }
    SpawnFaceWorld = face.magnitude > 0.001f ? face.normalized : Vector3.forward;
    SpawnRotation = transform.rotation;
}
```

### 4.3 Walk yaw formula (`DigglesObject.FaceWorldDirection`)

```csharp
float targetYaw = Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg;
float spawnYaw = Mathf.Atan2(SpawnFaceWorld.x, SpawnFaceWorld.z) * Mathf.Rad2Deg;
transform.rotation = Quaternion.AngleAxis(targetYaw - spawnYaw, Vector3.up) * SpawnRotation;
```

It's a **delta** between target direction and spawn face. Universal across
characters — the per-character offset is encoded in `SpawnFaceWorld`,
captured once at Awake. The absolute-native-yaw variant
(`FaceRotationForNativeAngle`, used by cutscene `rotate` and dig facing)
runs the same delta after mapping native yaw → world heading
(`wx = -sin a`, `wz = -0.5·cos a`; do NOT "simplify" to `-cos`).

### 4.4 Velocity / integration (SUPERSEDED)

> The old model — `obj.Velocity` set in Unity world coords and integrated by
> a per-frame `DigglesObject.Update` — is **dead in Play**. `DigglesObject`
> has no `Update` (see the note at `DigglesObject.cs:97`); the `Velocity`
> MonoBehaviour field and `CurrentAction`/`SetAction` path are never driven
> in gameplay. Live velocity lives on the **Tcl entity** (`ent.Velocity`)
> and is integrated by the **fixed-step sim** (`DigglesGameSim.Step`,
> phase 2b2), which advances `MetaWalkAction`. Movement translates along the
> world horizontal plane toward each waypoint (no axis swap in the mover).

---

## 5. Inspector caveat (rotation gizmos)

Editing a marker's `transform.localEulerAngles.y` in the Inspector does
NOT yaw the model cleanly. The root's `Q_x(-90)` means the marker's
local Y axis is the world `-Z` direction — Inspector Y rotation rotates
around world `-Z`, not around vertical.

**For inspector-driven rotation in scene view:** flip the rotation gizmo
to **Global** mode (top-left toolbar). The green handle then aligns with
world Y axis — drag it to yaw around vertical.

**For runtime code:** always use `transform.rotation =
Quaternion.AngleAxis(yaw, Vector3.up) * baseRotation` (world-frame yaw).
This is what `WalkAction` does.

---

## 6. Camera setup

The OLD-style world rendering (with root rotation -90X) means terrain
extends in world XY plane, with mountains visible in world `-Z` direction.

Camera reasonable positions:
- **Front view** (mountains visible): `(X_center, +5, +20)` looking toward
  the world center.
- **Top-down**: `(X_center, +30, Y_center_diggles)` with rotation
  `Q.LookRotation(Vector3.down, Vector3.up)`.

The `DigglesSpawnDiagnostics` editor window (`Diggles → Spawn Diagnostics`)
is the canonical way to inspect individual entities in isolation when
debugging.

---

## 7. Key files

| File | Purpose |
|---|---|
| `Assets/Diggles/Runtime/World/BdbPmpWorldBuilder.cs` | Terrain + marker spawn (editor import); `EmitVertexW`, marker constants |
| `Assets/Diggles/Runtime/Engine/RuntimeWorldSpawner.cs` | Play-mode live spawner (same outer+inner structure) |
| `Assets/Diggles/Editor/Importers/CampaignBrowserBuilder.cs` | Editor `SpawnTclMarkers`; sets inner `Q_x(-90)*markerRot` |
| `Assets/Diggles/Editor/Importers/MarkerModelResolver.cs` | TCL class → 3db mesh resolution; `ApplyAxisConvention` (identity) |
| `Assets/Diggles/Editor/Importers/DigglesCampaignBrowser.cs` | Manual campaign-load UI; sets root rotation |
| `Assets/Diggles/Editor/Importers/DigglesSpawnDiagnostics.cs` | Spawn / orientation debug tool |
| `Assets/Diggles/Runtime/Engine/DigglesObject.cs` | Per-marker entity; captures spawn face, owns facing math (§4) |
| `Assets/Diggles/Runtime/Tcl/Engine/EntityActions.cs` | `WalkAction` + `MetaWalkAction` (movement executors) |
| `Assets/Diggles/Runtime/Engine/Sim/DigglesGameSim.cs` | Fixed-step sim; integrates entity velocity + advances actions |
| `Assets/Diggles/Runtime/Engine/DigglesAnimator.cs` | Vertex-morph anim playback |

---

## 8. History / why this convention

The OLD-style world rendering (root `Q_x(-90)`) was chosen because users
familiar with Diggles' isometric look perceived a pure Y-up Unity scene
as "sideways". Rotating the root to map Diggles `Z` (height) to world `-Z`
recreates the familiar Diggles camera-angle look.

The cost of this hack: Inspector `Y` rotation doesn't directly yaw around
vertical (see §5). The win: terrain + markers visually align with the
expected Diggles game look without further per-asset config.

A "clean Y-up" alternative (root identity, Y-flip in vertex axis
conversion) was attempted and abandoned — see commit history if you
need to revisit. The OLD-style approach is the one all production code
is currently calibrated for.
