# Rim of authored cave cutouts: what the original renders

Verified line by line against the `Diggles.exe` decompile, the `.rdata`/`.data` constants of the
executable (image base 0x400000) and an unpacked `save_tutorial.sav`. The schematic
`rim-cutout-schema.png` next to this file is a software render of the reproduction cave window.

## Summary

1. Terrain is never alpha-blended. The material of rim cells is 0x12 `scape_solid`:
   ALPHABLENDENABLE=0, **ALPHATEST GREATER ref=126**, SRC/DST=ONE/ZERO (FUN_00457b90:737-749).
2. Pixel alpha is purely per-vertex (stage `scape_st`: ALPHAOP=SELECTARG1, ARG1=DIFFUSE,
   FUN_004564c0:140-143); vertex alpha is the bilinear of the four corner bytes `[0x4d2..0x4d5]`
   (FUN_004cc040:55-57, packed into the diffuse A at :218). A corner byte is 0 for a node in
   state 3 (`tile+0x18 & 3 == 3`), 0xff otherwise.
3. Consequence: the cut is a smooth geometric iso-line at bilinear = 126.5 running through the
   middle of the ring cells. Everything closer to the cutout (including the whole back-wall strip
   z=7 in the half tile at the rim, alpha 0..127) is clipped by the test.
4. The port's "rim band" is exactly that back-wall strip, which the native clips away (or, in
   blend experiments, a translucent film that does not exist in any native terrain state).
5. The window (cells whose 4 nodes are all state 3 are not emitted, FUN_004ca830:291-292) is
   filled by a Clear with the colour of the nearest horizon region; in the reproduction cave
   (Info_Fog_Urw) that is RGB(78,96,109), region (374.5,43.5), depth 32.0, size 70.0.
6. There is no soft fade of interior terrain. The fade seen on the menu diorama is light (a radial
   falloff to ~23-35/255 at the rim) on a dark background. Path 0x16 (SRCALPHA/ZERO = fade to
   black) requires fog-negative corners and never occurs in the interior.
7. The port's black gradient in the menu is a port invention: the native corner byte goes into
   alpha (clip), while the port multiplied RGB by it (`DigglesGround.shader`).

## Evidence

### Blend vs. test (confirmed)

- Shader table FUN_00457b90: 17 `scape_fow` (:701-707), **18 = 0x12 `scape_solid` :737-749**
  ([4]=1 ATEST, [5]=5 GREATER, [6]=0x7e, [7]=2/[8]=1 ONE/ZERO, [3] untouched → 0),
  19 `scape_blend` :779-788 ([3]=1 but ONE/ZERO), 20 lava, 21 lava fow.
- Indices proven by the copy-source arithmetic (0x798/0x6c=18, 0x870→20, 0x8dc→21) and the
  bounds checks (:775 `<0x13`, :856 `<0x15`, :894 `<0x16`).
- Descriptor → D3D render states: FUN_0045e240:104-176 ([3]→RS 0x1b ABLEND, [4]→0xf ATEST,
  [5]→0x19 AFUNC, [6]→0x18 AREF, [7]→0x13 SRC, [8]→0x14 DST, [9]→0x1d SPEC, [10]→0x16 CULL,
  [0xb]→0x89 LIGHTING, [0x10]→0x2f ZBIAS).
- Defaults FUN_00455760:42-59 (ABLEND=0, ATEST=0, AFUNC=8, SRC=2/DST=1, CULL=3).
- Texture stages FUN_004564c0: 2 model (:104-110, alpha = tex.a × TFACTOR), **3 `scape_st`
  (:140-143: copy of model + [5]=2 SELECTARG1, [6]=0 DIFFUSE, [8]=0)**, 4/5 `urw_mt`
  (:174-176, :22: ALPHAOP=DISABLE; `urw_mt1` COLOROP=5 MODULATE2X).
- Sort/classification FUN_00450850:67-89: transparent ⇔ shader[3]≠0 and (alpha<0xfa or
  DESTBLEND≠1) → 0x12/0x14/0x15 and 0x16 are the opaque class; key
  (layer·2|transp)<<16 | z<<8 | avg, qsort ascending (FUN_00450a50).
- Token dictionary FUN_00456070: "greater"=5 (:64), "srcalpha"=5 / "invsrcalpha"=6 (:72-73).
- Threshold = 126.5/255 ≈ 49.6%. The writer of the +0x200 material table is the ctor
  FUN_004c4420:93-100 (0x15 only for scape 3/7).

### Block 71 and the ring cells (confirmed)

- BDB re-parsed independently: block 71 aux NW=0, NE=0, SW=15, SE=15, centre=15, gates
  0,0,15,15, +0x54=0xF0; heightmap 4 north rows 0 / 4 south rows 15. Geometry: sub-mesh 0 is a
  quad at z=7 on y∈[0,0.5] (the back wall in the rim strip), sub-mesh 1 the shelf.
- Route of a ring cell (3,3,0,0): P branch FUN_004ca830:405-420 → `goto LAB_004cb110` (:420),
  FUN_004ca080 is not called; Q-plain (:436-489): [0x4d2]=0 (A, :462-463), [0x4d3]=0 (B,
  :441-442), [0x4d4]/[0x4d5]=0xff.
- The diagonal route (3,3,0,0) → GetBlockHole exists (:449-455 → LAB_004cb1b1:471-473, forced
  type 0x15) but is locked behind fog-negative and type==0x15; promotion 0x15→0x16 at :491-494
  (fog-negative and no zeroed corner).
- FUN_004c4c50: classes = bits 8-15 of cellId (:22), mismatch mask (:34-38), level permutations
  (:39-58), **uVar5==0 → type 0x12** (:87-88), else the type from the +0x200 scape table (:60-62).
- The ring shelf (alpha 127) and the front are opaque rock; the back-wall strip (alpha < 127) is
  clipped. A translucent shelf does not exist natively.
- Fog corner: FUN_004ca830:146-162 applies ×1.3 (DAT_007372e8) capped at 0x80, so for fog=0xEF
  the corner = min(int(111·1.3),128) = 128 and the grey re-fog is 0 in the reproduction.

### Window fill and backdrops (confirmed; panorama medium confidence)

- FUN_006ad9a0: unconditional Clear(0) :76-78; 4-flag gate :82-83; path B: Clear with colour
  (+0x2854..285c) :128-137; debug colour 0xffb8b2aa :149-155 (toggle +0x19f12).
- FUN_004750b0: reset :35-39; nearest region by 2D distance < size :104; colour/depth copy :18;
  **+0x2850=1 only when +0x38f0≠0** :141-142; off-map regions are dropped :54-91.
- The backdrop flag +0x1040: reset FUN_004c4de0:81; set FUN_004c8480:226-244 for a visible cell
  that has a state-3 node with a non-zero fog byte in that node's 3×3 neighbourhood.
- Fifth clip plane: FUN_00473300:21-25 / :73-77 — bit 0x10 enters the mask when
  bboxMaxZ ≤ +0x38e8 and is never cleared, so an object deeper than the marker is culled
  unconditionally; a marker of −FLT_MAX opens the backdrops.
- Model fade: FUN_004856e0 — z≥0: grey re-fog with spill 0xAA/0xAA/0x9B (:163-181);
  **z<0: blend towards the horizon colour ∝ |z|/depth** (:183-199).
- Save: the horizon record (374.5, 43.5, z=15.01), RGB as ints (78,96,109), depth float 32.0,
  size float 70.0, found bit-exact at offset 6426045 of the unpacked stream;
  (78,96,109) = Info_Fog_Urw {0.305,0.375,0.429}×255.
- Material registry order FUN_0045aa00 (index = count−1); "backdrop materials" 21/22 do not
  exist — those are the lava multitexture and its fow variant.
- Texture alpha plays no part in terrain (scape_st selects DIFFUSE alpha). `cave0..3.tga` are
  32×32 (TGA headers), not 64×64.

### Menu light (confirmed)

- Constants verified in the executable: DAT_00737468=3.6, DAT_0073777c=−2.546,
  DAT_007373e8=+2.546, DAT_00737034=0.1, DAT_00736fc4=1/256, DAT_0073708c=0.5,
  DAT_00737520=7.0, DAT_0073703c=0.125, DAT_0073713c=1.02.
- Vertex formula FUN_004cc040: dir = 3.6 − 2.546(nx+ny) (:62-63), base = 16·bilin(node+7)+64
  (:58-61), S<0: dimming (1+S/256) (:69) / alpha S+256 with byte5 (:74), dynamic lights with
  an N·L floor of 0.1 and exp(−2d²/R²) (:96-117).
- Under the alpha test the menu rim is a hard clip at the 126.5 iso-line; the visible fade into
  shadow is light falloff, and the edge itself is sharp.

### Software render of the window

Window x364-382, y38-47 from the save: cellId=0 everywhere (all cells 0x12), fog=0xEF everywhere
(S=128 saturated, grey re-fog 0; 0x15/0x16/black covers unreachable). The blob of state-3 nodes
spans rows 41-46 (bottom x367-381); the ring below is block 71.

## Port plan

1. Terrain: alpha test instead of blend / RGB multiplier. Opaque material; fragment
   `clip(vertAlpha − 127/255)` (equivalent to GREATER 126). Vertex alpha = bilinear of the corner
   bytes: 0 at a state-3 node, 255 otherwise.
2. Skip law unchanged (all 4 nodes state 3 → the cell is not drawn); ring cells must be drawn
   with the clip, neither skipped nor blended.
3. Window fill: Clear/background with the colour of the nearest horizon region (registry already in
   `OriginalSaveHorizonDocument`; pick the 2D-nearest to the camera target with dist < size);
   active when a state-3 node with fog≠0 is in view.
4. Backdrop models: draw behind z<7 only while the window is active (the fifth-plane analogue) and
   fade them towards the region colour ∝ |z|/depth (the `_HorizonFog` shader twin exists; make
   sure it fades to the region colour and only for z<0).
5. Menu gradient: after step 1 the black ramp disappears; the menu rim fade must come from
   lighting — base = (16·bilin(node+7)+64)·(3.6−2.546(nx+ny))·0.1 (≈23/255 on the front without
   lamps) plus radial lamps × S.
6. Exposure / fog-of-war path (0x16): only with fog-negative corners — rgb×α, opaque, Z-write,
   no clip (fade to black, not transparency). Keep it a separate flag from the authored-cutout
   logic.
7. Optional cosmetics: the GetBlockHole shape for cells with 3 open corners and
   |nib(solid)−nib(centre)|>3 — quarter clip of the geometry (FUN_004c9d50). Three cells in the
   reproduction; step 1 covers most of the visual without it.

## Open questions

1. A screenshot of the original inside an interior cave would close the model: it predicts a
   window of colour (78,96,109), the bottom rim half a tile below the node line, and a sharp edge.
2. ALPHAOP=DISABLE on the urw/lav stages (materials 0x14/0x15) is formally undefined in DX7;
   pass-through diffuse alpha is assumed (otherwise the enabled alpha test is meaningless).
3. `exp` for FUN_006f5ca0 is a structural libm identification (high, not certain).
4. The writer of the panorama threshold +0x1560 and its reader +0x2864 were not found (they do not
   affect interiors).
5. Which backdrop dummies sit at (372,43) needs the dummy stream of the save parsed.
6. Shelf texture: FUN_004c4ae0 combo = 0 → solid rock; the level-1 texture choice (:19-27) was not
   re-checked (no effect on geometry or states).
7. The exact rasterization of alpha at the iso-line (127 vs 126.5) may flicker by half a pixel;
   irrelevant for the port (`clip` reproduces it).
