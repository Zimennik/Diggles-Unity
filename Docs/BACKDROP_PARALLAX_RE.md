# Backdrop / Parallax Panorama RE

Reverse engineering of the original background presentation: the moving parallax landscape
behind the surface meadow, the authored cave backdrops, and why the original shows **no earth
rim** around rear cutouts. Decompile paths are relative to
the per-function decompile of `Diggles.exe`; constants were extracted from the `.rdata`
float pool of `Diggles.exe` (PE section mapping → raw values).

Port implementation: `Assets/Diggles/Runtime/World/BackdropLaws.cs` (pure laws + tests in
`Assets/Diggles/Tests/Editor/BackdropLawsTests.cs`), `BackdropPresenter.cs` (scene),
`DigglesBackdrop.shader`, horizon-fog additions in `DigglesGround.shader` /
`DigglesModel.shader`, wiring in `DigglesWorldRuntime.cs`.

## 1. Subsystem map (three features, one seam)

| Feature | Native code | Data |
|---|---|---|
| Surface panorama (6 parallax layers) | ctor `FUN_0047edc0`, frame dispatcher `FUN_00480ee0`, per-layer UV grid `FUN_004802c0`, mesh emit `FUN_0047f440` (single-tex) / `FUN_0047fad0` (`RenderM` dual-tex caps path), draw from `CDisplay_Draw` (`004781b0_CDisplay_Draw.c:203-207`) | `data/Texture/Misc/Panorama_layer_0..5.tga` (32-bit TGA with alpha) |
| Meadow ground carpet (`GroundRenderer2`) | `FUN_00480720` | `Panorama_layer_6.tga` |
| Horizon zones (cave/meadow backdrop colour + rear fade) | Tcl `horizon_set` (registry `CGame+0x2868/+0x286C`, save doc `ORIGINAL_SAVE_FORMAT_RE.md` §late-tail item 3), per-frame scan `FUN_004750b0`, frame clear `FUN_006ad9a0:128-140` (`CGameThread::Draw() … Fill`), terrain vertex fade `FUN_00484cd0:82-104`, model twin `FUN_004856e0:162-199` | `Scripts/classes/items/infoobjs.tcl` `Info_Fog_*` classes; placed by ~237 map templates (`data/Templates/*.tcl`), including the meadow scene `obw_szene.tcl` (`Info_Fog_Urw_a/b`) |

The backdrop-model databases `hinterlegung.3db` / `flusslandschaft.3db` (loaded in
`Scripts/init/animinit.tcl:35-37`) are template-placed deco models — **but they are the
probable LIVE TARGETS of the §4 rear fade** (models standing behind the slab at render z < 0;
terrain provably never reaches there — see the §4 measurement).

## 2. The panorama pass

### 2.1 Construction — `FUN_0047edc0`

Loads the 7 textures (`s_Panorama_layer_N_tga_...` string pool refs,:36-225) and fills the
per-layer tables (:226-288). The loop first writes defaults (depth `100+25*i`), then the tail
overwrites everything from the float pool. Extracted values:

| layer | texture | depth D (0x1528) | scale S (0x1498) | offU (0x14c8) | offV (0x14f8) |
|---|---|---|---|---|---|
| 0 (farthest) | layer_0 | 21100 | 0.53 | 0 | 0.75 |
| 1 | layer_1 | 20500 | 0.04 | 0 | 1.0 |
| 2 | layer_2 | 11300 | 0.11 | 0 | 0.5 |
| 3 | layer_3 | 1000 | 1.12 | 0 | −4.9 |
| 4 | layer_4 | 200 | 6.68 | 0.7 | −33.0 |
| 5 (nearest) | layer_5 | 100 | 18.0 | 1.0 | −90.25 |

Global constants: `Ku = 0.00075` (`_DAT_007370c8`), `Kv = 0.0015` (`_DAT_007370f0`),
`UvBias = 5.0` (`DAT_007373a8`), `DepthBias = 1.0` (`DAT_007372a8`).

### 2.2 Per-frame dispatch — `FUN_00480ee0`

* **Surface gate** (:38-39): draw only when `cameraFocusRow − 20 ≤ groundRow`
  (`DAT_00737578 = 20`; camera focus = `camState+0x154/+0x158`, ground line =
  `panorama+0x1560`, an int in tile-row units — its writer was not located; the port derives
  the line as the deepest load-time block-59 open-air row, see §5).
* Unprojects a **9×9 screen grid** (81 verts, `0x51`) through the screen→world matrix at
  `DAT_0076a338+0x2794` at screen z = 0 and z = 1, storing per-ray tables (:131-183):
  `[0x50] = Δx/Δz`, `[0x2d8] = Δy/Δz`, `[0x560] = x_near`, `[0x7e8] = y_near`.
* Also computes a per-column horizon clip line (:52-130): the carpet plane `y = groundRow − 2.5`
  is projected twice through the world→screen matrix (+0x2754) at probe points `fx ± 10`
  (`DAT_00737534 = 10`) — once at render z = **−37.5** (`_DAT_007377c8`), once at z = **+14.5**
  (`DAT_00737564` — the undug FRONT PLANE; direct evidence of the metres frame, §2.3) — and per
  column the **min** screen-y (higher line) wins. `FUN_0047f440:176-255` then emits only cell
  triangles with ≥1 corner above the line — an **overdraw optimization** for the z-test-less
  native pre-pass (everything below is over-painted by carpet/terrain); the port's draw order
  achieves the same final image, so the clip is not ported.
* Draws layers 0→5 (`FUN_004802c0` + `FUN_0047f440` each; `FUN_004804f0`+`FUN_0047fad0` pairs
  on multitexture hardware — flag `+0x155c`), then the carpet `FUN_00480720`.

### 2.3 The parallax law — `FUN_004802c0` + `FUN_0047f440:139/166`

For each grid ray and layer with depth `D`, with `d' = D − 1`:

```
uCore = Ku * (slopeX * d' − x_near) + 5.0 (grid 0xa70; FUN_004802c0:32-40)
vCore = −Kv * (slopeY * d' − y_near) + 5.0 (grid 0xcf8; sign flip = XOR DAT_00737960)
u = S_layer * uCore + offU_layer (FUN_0047f440:139)
v = S_layer * vCore + offV_layer (FUN_0047f440:166)
```

Coordinates are the **native render frame** — the frame `RV_VIEW`/`RV_PROJ`
(CDisplay+0x26d4/+0x2714) consume and the screen→world inverse (+0x2794) produces: x = tileX
(east), y = tileY (the ant-farm vertical, growing downward), z = the height axis **in METRES**:
deepest dug floor z = **7** (nibble 0), undug front plane z = **14.5** (nibble 15), camera
look-at height = the const **14** (`DigglesCameraController.AtHeight`). That is exactly the
port's world-root LOCAL frame up to the row sign, so `BackdropLaws.RenderFromLocal` is the
pure axis swap `(lx, −lz, ly)` — the same mapping the RV_VIEW-verified camera controller uses
(`NativeToWorldPoint`). Evidence this pass shares the frame: the clip line's front-plane probe
z = +14.5 (`DAT_00737564`, §2.2), the carpet's near row z = +7.5 hugging the deepest plane 7
(§2.4), the vertex-brightness knee 7.0 (`DAT_00737520`, §4), and `FUN_00488980:131-148`
feeding the fade the very z it renders through the view matrix.
⚠️⚠️ correction: the note here claimed the frame was the RAW nibble
axis (0..15) and imposed `render z = 2*ly − 14`. That was WRONG — it doubled every ray Δz
(halved slopeX/slopeY → the backdrop counter-panned at HALF the native rate, reading as
"detached from the camera") and shifted every layer's V by whole strip heights (the painted
meadow band mis-placed vertically). It survived the camera cross-check because 14 is the
fixed point of `ly = z*0.5+7`: the at-height matches under both readings, the eye (z≈25)
does not. The ray's near point is ≈ the eye (native zn = 0.5 = the port camera's
`NearPlane`), so `−x_near` gives the counter-pan: **near layers (S=18, D=100) scroll
strongly against camera pans while far layers (S=0.53, D=21100) barely move — the parallax**.
`du/dpan = −S·Ku` per world unit; near(5)/far(0) ratio = 18/0.53 ≈ 34×, and since u
*decreases* toward the east side of the screen (Δz < 0 for the down-tilted camera), features
slide **opposite** to the pan.

Vertices are screen-space TLVERTs (x = col·w/8, y = row·h/8, z = 0.02, rhw = 50, diffuse
0xffffff — `FUN_0047f440:114-171`), alpha-blended, drawn as `"GroundRenderer"` render nodes
with sort key `layer+1` (:54-56) **before the terrain, without depth** — the terrain then
paints over them. The port now mirrors this literally: the layer meshes hold STATIC
viewport-space vertices and the `Diggles/Backdrop` shader's `_ScreenAnchored` path emits them
straight to clip space (`ZTest Always`, `ZWrite Off`, queues 1000+layer, before Geometry) —
the quads are glued to the screen by construction and only the UVs stream per frame. (The
first-round "grid pushed to 0.9·far world quads" recomputed vertices from the camera pose in
`LateUpdate` with undefined order against `DigglesCameraController`'s own LateUpdate — a
one-frame-stale pose made the whole backdrop swim against the camera.)

### 2.4 The meadow ground carpet — `FUN_00480720`

`"GroundRenderer2"`, sort key 7 (after the layers), texture `Panorama_layer_6.tga`. World-space
20-unit chunks spanning `snap(focusX) ± 140` (`DAT_00737578 = 20`, `DAT_00737624 = 140`),
each a 4×4 vertex grid (3×3 quads,:265-281):

* `x = chunkX + col·20/3` (:162)
* `y = groundRow − 2.5` (`DAT_007373d8`), far rows lifted `+0.75` (row 0, `DAT_007370c0`) and
  `+0.25` (row 1, `DAT_00737058`) (:163-170)
* `z = (row·7.5 − (3−row)·60)/3 → {−60, −37.5, −15, +7.5}` (:158) — the meadow floor ribbon
  stretching from just under the slab out to the horizon
* `uv = (col·0.33, row·0.33)` (`_DAT_00737068`,:240/:255-261); chunks culled when >16 of the
  16 projected verts fall off-screen (:183-187, 294).

## 3. Horizon zones — the authored cave/meadow backdrop

* `horizon_set <pos> r g b depth size` (Tcl) appends `{x,y,z, r·255,g·255,b·255, depth, size}`
  to the `CGame+0x2868` registry (already ported for save parity —
  `DigglesTclEngine.cs:4577-4607`, `HorizonRegionState`). All `Info_Fog_*` classes pass
  `depth = 32`; sizes 15..100 (`infoobjs.tcl`). The meadow scene template `obw_szene.tcl`
  places `Info_Fog_Urw_a/b` (0.305/0.375/0.429 — the hazy blue-gray), so **the starting meadow
  has zones too** — same subsystem as the caves, as the report suspected.
* Per frame `FUN_004750b0` picks the **nearest region whose 2D distance to the camera focus is
  < its size** (:118-127; best-distance init `DAT_00737700 = FLT_MAX`) into
  `CDisplay+0x2850..0x2864` (active flag, rgb, depth, FoW fade). Out-of-map records are dropped
  (:56-90).
* **Frame clear**: when a zone is active, `CGameThread::Draw`'s end-of-build Fill uses
  `(r<<16 | g<<8 | b)` as the clear colour (`FUN_006ad9a0:128-140`); otherwise the fill is
  plain (black). This colour IS the authored cave backdrop you see through rear cutouts.
* The FoW fade byte (+0x2864,:19) had no locatable consumer in the decompile and is not
  ported (documented deviation).

## 4. The earth rim — root cause and fix

`FUN_00484cd0` (terrain vertex colour; called from the `"CModelRenderer Vertexprocessing"`
loop `FUN_00488980:148` / `FUN_004891a0:173`). `FUN_00488980:131-143` transforms the packed
ushort vertex through the model→world matrix and calls the fade with `in_XMM3` = the resulting
**world z — the render frame of §2.3 (metres, terrain 7..14.5), no rescale**. The exact law
:

```
if (z < 0 && zoneDepth != 0) { //:82-83 — z<0 = SEVEN metres behind the deepest
    f = clamp01(−z / zoneDepth);            // :84-92 —        dug floor plane (z=7)
    base(rgb)  *= (1−f);                    // :94-96 — the diffuse (texture-modulate) part
    spill(rgb)  = spill·(1−f) + zoneRGB·f;  // :97-102 — zone colour enters the SPILL/specular
    z = 0;                                  // :103    — (kills the depth-brightness term)
}
```

The final compose (:121-129) is `out = tex·base + spill` (D3D specular add), so at f→1 the
vertex becomes pure zone colour — a straight lerp for practical purposes (spill ≈ 0 without
oversaturated lights), which is what the port's shader lerp implements. Note:10: the
"depth brightness" term `(max(z,7)−7)·0.125·0.5·0.75 + 0.9921875 + 0.3` (knee `DAT_00737520 =
7.0` = the deepest floor) is ≥ 1 for every reachable z and clamps to 1 — a dead law, nothing
to port. `FUN_004856e0:162-199` is the identical model-path twin.

**⚠️⚠️ MEASURED — the earlier "skirt band native −32..0" story was FICTION.** A
direct dump of `BlockDB2.BDB` (all 118688 blocks, global vertex palette) gives **z ∈ [7.0,
14.5] EXACTLY — zero vertices below the deepest floor plane 7**, let alone below the fade
threshold 0 (scratchpad `bdb_zrange.py`; block 59 min 7.0, 71 min 7.0, bowl-rim cliffs
27222/27323 min 9.5/12.5). Consequences:

* **Terrain never enters this fade** — natively or in the port. Both the shader
  threshold (`ly < 7`) and the faithful one (`ly < 0`) fire on zero terrain vertices; the
  "rim fix" was a no-op that could not have changed the picture.
* The law's **live native targets are authored backdrop MODELS** placed behind the slab —
  the `hinterlegung.3db` ("backing/backdrop") / `flusslandschaft.3db` model sources §1
  dismissed are exactly the class of deco that can stand at render z < 0 behind rear
  cutouts, and the model twin `FUN_004856e0` melts them into the zone colour with depth.
  Whether the port spawns those template decos (and at what height) is an OPEN follow-up.
* What play sessions showed as the "earth frame" around rear cutouts is therefore NOT
  un-faded sub-floor skirt geometry (none exists) — candidates: the cutout blocks' ordinary
  side faces (z 7..14.5, never faded natively either), a missing backdrop model, or the
  world-edge/FoW handling (cf. the 6ec579f world-edge black pin). Needs a fresh Play look
  with `horizon_tint`/`horizon_debug` now that the zone chain is live and the frame correct.

**The port keeps the faithful shader law** (fidelity + instrumentation): `_HorizonFog`
(rgb + depth) + `_HorizonWorldToNative` globals uploaded by `BackdropPresenter`, applied in
`DigglesGround.shader` / `DigglesModel.shader` before the FoW veil, with `hz = local y`
directly (the `hz = ly*2 − 14` was the wrong frame — threshold at the floor plane,
double ramp rate; see §2.3). No terrain geometry, walkability or nav data is touched.

### 4b. The rim mechanism — SETTLED native cutout ALPHA TEST
### (full research: Docs/RIM_CUTOUT_RENDER_RE.md + Docs/rim-cutout-schema.png;
### implemented r11, user-verified on captures)

The..28 attempts (alpha dissolve/blend, luminance multiplier, quarter clip,
interior-air masks, darkness floors) all failed the player's Play verdicts because the
base assumption was wrong: **the native never blends or darkens terrain at cutouts**.
The researched truth:

* Terrain material `scape_solid` draws with **ALPHABLEND OFF + ALPHATEST GREATER
  ref=126** (`FUN_00457b90:737-749`); the tested alpha is the VERTEX diffuse alpha
  alone (`ALPHAOP=SELECTARG1(DIFFUSE)` — `FUN_004564c0:140-143`; texture alpha never
  participates).
* The vertex alpha = the bilinear of the per-corner cutout bytes `[0x4d2..0x4d5]`
  (0 at a `tile+0x18` STATE-3 node, 255 otherwise; `FUN_004ca830:388-491`,
  `FUN_004cc040:55-57` → packed at `:218`).
* Result: a hard STENCIL cut along the smooth bilinear=126.5 isoline through the
  cutout ring's mid-tile — the half-tile back-wall strip toward the hole is CUT AWAY,
  the ring blocks' mid-tile shelf (bilinear exactly 127 — integer math
  `(255·256·128)>>16 = 127 > 126`) and the front plane stay opaque stone. The window
  visually deepens half a tile to the shelf ledge; the edge is SHARP. The rim band
  every port round showed was exactly the strip the native cuts (drawn lit), or a
  translucent/darkened film that exists in no native terrain state.
* All-4-state-3 cells emit nothing (`:291`); the window shows the horizon-zone clear
  (§3) + backdrop models behind z<7 melting into the zone colour (`FUN_004856e0`).
  The menu edges' fade-out is LIGHT falloff (base ≈(16·bilin(node+7)+64)·0.1 with
  radial lamps), not transparency.

Port state (r11, `f30b815`): `DigglesGround` clips at `a*255 − 126.5`; the mesher
bakes corner alphas from the STATE predicate `(NativeTerrainFlags & 3) == 3` (the
tile+0x18 word — save-loaded verbatim, template worlds synthesized; NEVER
blockIdx==59 — the ring lies on solid blocks) and skips all-4-state-3 cells.

Remaining follow-ups from the research port-plan (unimplemented, in order):
window-fill activation by the native `+0x1040` flag (a visible state-3 node with
fog≠0; the port activates zones by proximity alone), backdrop models behind z<7
gated on the active window, the native menu LIGHT model (after the alpha test the
menu edge is bright stone under Unity ambient — native is ~23-35/255 dark), the
fog-negative 0x16 path (rgb×α opaque fade-to-black), and the optional GetBlockHole
quarter-clip for 3-open steep cells (3 cells in the tutorial repro).

With the skirt story dead, the actual native law behind "no earth frame" is in the per-tile
renderer `FUN_004ca830` + vertex shader `FUN_004cc040`:

* The drawn cell (gx,gy) spans the four map NODES (gx,gy)…(gx+1,gy+1); a node is "open"
  when its **tile+0x18 word carries state 3 in the low 2 bits** (see the writer law below).
  A cell whose FOUR corner nodes are all state-3 is skipped entirely (`:291` — the backdrop
  shows), gated on the display byte +0x40a which the ctor sets to constant 1
  (`FUN_004c4420:91`). The cell's geometry comes from the NW node's block.
* **The writer law.** Phase A recodes the
  template FILE terrain bytes to internal classes: `2→3, 3→2, 4→1, 5→4`; file byte 1 =
  "inherit the existing node class + 1" (`:162-173`; zero uses in the shipped 220-PMP
  corpus); file byte 0 = transparent. Phase B walks the blit rect and, per node, borrows
  the **first nonzero of [self, SOUTH, EAST, SE]** (`:293-304`, bit7 from self): borrowed
  class 4 (⟺ **file byte 5**) writes `tile+0x18 = (self&0x80) | 3` — **state 3, no variant
  byte** (`:317-344`); any other class writes `(rand%6)<<8 | (class−1 | bit7)`. Map create
  defaults every node to `hash<<8 | 2` (`FUN_00598f10:154`); the world-growth mutator
  `FUN_005fc9d0:117-121` re-rolls low2 but never touches states 0/3; **no dig path writes
  the word**. So state-3 ⟺ the authored "backdrop hole" paint class, nothing else.
  Empirics: `unq_menue.pmp` carries exactly 32 byte-0x85 tiles — the aperture blob is the
  hole cells' NODE-CLOSURE around a 21-cell block-59 core (adversarial review
  the all-4-open skip set in the menu EQUALS the 59 set; the visible aperture widening
  comes from the ring cells' corner DISSOLVE + quarter clip, not extra skips);
  `obw_tutorial.pmp`'s whole sky is byte-5; the tutorial.sav stores the word verbatim
  (500×200 terrain records: every cutout's 59-core all state-3 PLUS a state-3 ring on
  solid blocks — the dissolve fringe, 427 solid-block state-3 tiles world-wide; the
  `_cut`/`ende`-family templates carry hundreds of all-open cells on SOLID blocks that
  the native skips whole).
  The session's "material-3 / exposure" readings confused FILE bytes with
  internal classes (file 4 → internal 1, NOT class 4) — that dead end is closed.
* **⚠️ OPEN Play verdict — the sky silhouette.** Because the blob is a node-closure,
  state-3 nodes enclose the hole/sky on EVERY side: the meadow cliff staircase samples
  open corners too (312 solid cells in the tutorial save get ≥1 open corner from the sky,
  30 quarter-clip, 7 skip). The faithful law therefore re-introduces the half-tile
  dissolve at sky edges that the etalon reading (`original_start1.png` —
  "crisp") argued against and answered with the interior-air sky mask. The DATA law is
  proven (writer + save word); whether the native's ramp reads "crisp" at gameplay zoom
  is for the player's Play A/B against the etalon — do not re-add a sky mask without it.
* Port deviation (order): the native checks the all-corner-FOG-BLACK cheap-quad cover
  BEFORE the all-state-3 skip (`FUN_004ca830:284-289` → `FUN_004c6b20`, exit before
:291). The port skips unconditionally and lets the FoW overlay close unexplored cells
  (`DiggableTerrain.RendersTerrain` mirrors the skip → gradient patches / covers).
* For every open corner node the renderer zeroes that corner's byte
  (`[0x4d2..0x4d5] = 0`, default 0xff — `FUN_004ca830:LAB_004cb110` block);
  `FUN_004cc040:55-57` blends the four bytes **bilinearly** per vertex (weights from the
  vertex's tile-local x,y — the `>> 0x10` fixed-point quad) and **:218 packs the result
  into the DIFFUSE ALPHA byte**: `((r | a<<8)<<8 | g)<<8 | b` — the lighting rgb is NOT
  touched. The alpha-blended terrain node therefore **DISSOLVES INTO THE BACKDROP** over
  its last half-tile toward every rear-cutout window — no lit band and no black band.
  (Round 1 of this fix mis-read the quad as a luminance multiplier and faded the edges to
  BLACK — in places the port fades to black; the ARGB pack line settles it.)
* The port meshed those boundary cells fully opaque → the flat earth strip, half a block
  around every rear cutout (S03-002). Fix: `BdbPmpWorldBuilder.BuildTileBuckets` bakes the
  bilinear of the four corner-node factors into vertex COLOR.a — `OpenNode:=
  (NativeTerrainFlags & 3) == 3`, i.e. the loaded/synthesized tile+0x18 word: the.sav
  loader reads it verbatim (`OriginalSaveTerrainTile.NativeTerrainFlags`), template worlds
  synthesize it from the terrain bytes (`MaterialTypeFromTerrainByte` {4→0, 3→1, 5→3, _→2}
  + the borrow probe in native order self→S→E→SE). Player-carved 59s are not open by
  construction (digging never writes the word). All-4-open cells are skipped whatever
  their block. `DigglesGround.shader` alpha-blends (`Blend SrcAlpha OneMinusSrcAlpha`,
  ZWrite on, alpha 1 everywhere else — opaque terrain renders unchanged). Adjacent cells
  share corner nodes → identical alphas at shared corners → seamless; the chunk dirty-rect
  already carries the ±1 margin these corner effects need.
* PLUS the ported `GetBlockHole` quarter clip: a 3-open-corner cell with a steep block
  (|solid-corner aux height − center| > 3) keeps only the solid corner's QUARTER, clipped
  at the tile-local 0.5 midlines (`FUN_004ca080` → `FUN_004c9010`; `DAT_0073708c = 0.5`) —
  `BuildTileBuckets.EmitClippedTri` (Sutherland–Hodgman per triangle, midline verts exact).
* Documented deviations (follow-ups if Play still shows artefacts):
  1. Native renders NW=59 cells that have ≥1 solid corner (their z=7 floor quad, corners
     dissolved) — the port keeps skipping ALL load-time-59 cells; that native fringe floor
     sits 7.5 m behind the front plane and is essentially invisible from gameplay poses.
  2. The unexplored/water two-air `GetBlockHole` patterns (type 0x15, FoW-negative corners)
     are not ported — FoW covers those cells in the port anyway.
  3. `FUN_004856e0` (models) has no corner-alpha input — deco near holes keeps its own
     lighting, as in the native.
  4. Never-template-stamped filler is NOT treated as open: the native renders undiggable
     virgin columns through the `FUN_004c6b20` cheap-quad path unconditionally (an
     experimental `!HasTpl` predicate gutted 88% of the start world's triangles — reverted;
     the render-bbox+4 cut of that filler is a separate, mostly invisible port artifact).

## 5. Port notes / deviations (all deliberate, cited in code)

1. **Ground line source**: the native `panorama+0x1560` writer was not located (nor is the
   value in the.sav — the display save chunk carries only the zone registry). The port
   derives the surface line as **the deepest load-time block-59 (open-air) row + 4**
   (`BackdropLaws.ComputeGroundRow` / `SurfaceLineFromDeepestAir`): the raw air row put the
   carpet plane (groundRow − 2.5) ~4.5 rows ABOVE the deepest meadow-floor face — above the
   EYE row, so the carpet rendered from below and smeared across the sky (,
   "the horizontal backdrop sits ~5 units too high"); +4 lands it half a row under the floor face.
   `BackdropPresenter.GroundRowBias` stays the fine-tuning lever. Cave-only worlds have no
   air row → the gate never opens (matches native underground behaviour).
2. **Pre-pass ported literally**: screen-anchored layer quads, `ZTest Always`,
   `ZWrite Off`, Background queue — the opaque terrain draws after and covers them exactly like
   the native over-paint. The native per-column horizon clip is dropped (overdraw optimization
   only).
3. **Dual-texture pair path** (`FUN_0047fad0`) is a caps optimization — visually identical to
   the sequential path we port.
4. **V flip**: native texture v grows down; `TgaLoader` normalizes rows to Unity's bottom-up →
   the presenter samples `1 − v` (toggle `FlipLayerV`).
5. **Texture addressing = U REPEAT everywhere; V REPEAT only on layer 0, CLAMP on strips
   1..5 + carpet** (second pass; supersedes both the e472bce all-CLAMP
   band-aid and the first all-REPEAT revert). U must wrap: the strips tile horizontally by
   design (`Panorama_layer_0`'s left/right edge columns are bit-identical; all-clamp froze
   the U counter-pan the moment u left [0,1] — the "background detached from the camera"
   smear). V CANNOT wrap on the strips: the law divides by the ray's render Δz, and camera
   ORBITS (dy → the east-west axis) shrink Δz toward zero — slopeY grows without bound and
   v sweeps tens of tiles across the screen, turning every strip into a stack of diagonal
   stripes. The strips are authored transparent-top /
   opaque-bottom (TGA probe), so V-clamp degrades cleanly at any pose: above the band the
   stretched edge row is fully transparent sky, below it solid ground colour that
   carpet/terrain over-paint. Layer 0 is fully opaque and seamless both ways (the base
   cloud sky) — its few vertical repeats read as clouds, so it keeps the native default
   full WRAP. (Caveat: the exact native ADDRESSV state per render-node type 0x17 was not
   recovered from the decompile; V-clamp on strips is the artifact-free reading consistent
   with the strip artwork.)
6. **Zone FoW fade** not ported (§3). Zones activate regardless of exploration.
7. **Session gate — PANORAMA ONLY** (⚠️⚠️ round-3 correction; the reading
   below it was WRONG in scope). The gate structure in `004781b0_CDisplay_Draw.c`:
   * `:203-206` — the PANORAMA dispatcher `FUN_00480ee0` runs only under
     `*(char*)(display+0x38f0) != 0 && *(int*)(StoryMgr+0x84) == 0`.
   * `:210` — the horizon-zone scan `FUN_004750b0` is called **UNCONDITIONALLY**, every
     frame, for whatever world the display draws; the scan re-checks the byte only for the
     final active-flag write (`FUN_004750b0:141-143` — `if (*(+0x38f0)) *(+0x2850) = 1`).
   `StoryMgr+0x84` is the forced zone index (`FUN_005f8080` singleton; written by
   `sm_force_zone` = `FUN_0065fa10:87`; zone 0 = "Urwald", `CampaignInit.tcl:675`; ctor
   default 0) — the deeper campaign zones (Metall/Kristall/Lava) force nonzero and get no
   panorama. The `+0x38f0` byte's writer is not in the decompile by literal (bulk/ctor init);
   it is the display-live/world-attached flag, NOT a "campaign" flag: the menu diorama is a
   live drawn world that authors Info_Fog markers (`obw_szene.tcl:1650-1669` natively; the
   port's menu boot stamps `unq_menue.tcl:445/450` = `Info_Fog_Urw_d` at world (32.98, 39.87),
   ~1.2 units from the menu camera focus 32.368/40.858 — well inside its size 15), and the
   native menu shows **no raw earth rim** — so zone activation IS live over the menu. The
   port gated the WHOLE presenter (layers + zone clear + fog globals) on
   `!_atMenu`, which made the menu rim unfixable by construction. Round 3 rescopes it:
   `gameplayActive` gates only the panorama/carpet (`show`), while the zone scan + clear
   colour + `_HorizonFog` fade run in ANY world with registered zones — a world attached to
   the presenter is by definition "display-live". (The "stripes over the menu"
   came from the round-1 presentation bugs — stale-pose world quads, later the wrong 2*ly−14
   frame doubling the on-screen V span — not evidence that the menu draws no backdrop.)

## 6. Verification

* EditMode: `BackdropLawsTests` pins the constant tables, the UV law (formula + parallax
  ordering), the surface gate boundary, the carpet grid/snap, the zone pick, the rear-fade ramp
  and the ground-row scan; the counter-pan sign + the 34× near/far ratio; and the
  corrected frame — `RenderFromLocal` is a pure axis swap, tile nibbles COMPRESS into it via
  `NativeToLocal` (render z = nibble·0.5+7), the fade threshold sits at render z 0 = seven
  metres behind the deepest floor, with 14 as the affine's fixed point documented as the reason
  the wrong frame survived the camera cross-check.
* Play: meadow panorama — layers counter-pan at full rate glued to the camera (no lag,
  no freeze at pan extremes), the painted meadow/horizon band sits at the right height and the
  carpet ribbon reaches the horizon; cave backdrop colour through rear cutouts (`horizon_debug`
  / `horizon_tint` in the console if anything looks off — §4: the rim can no longer be blamed
  on sub-floor skirt geometry, none exists).
