# Fog of War — full RE of the original + port contract

> The port is implemented: `FogOfWarState`/`FogOfWarPresenter`/`FogOfWarOverlay` (Runtime/World),
> Tcl handlers in `DigglesTclEngine`, FogOfWar* tests (Tests/Editor).

Status: **RE COMPLETE** — all 8 CFogOfWar functions verified, constants taken byte-exact from
.rdata. All addresses = Diggles.exe.

---

## 1. Data model

- Fog is stored in the world tile records (stride 0x7c): **`tile+0x34..0x3B` = u8[8], one byte per player**.
- Encoding: **0 = BLACK (never seen); 1 = GREY floor (explored); 2..0xFE = fading (−4 per update); 0xFF = visible**. There is no separate "currently visible" layer — a single persistent byte.
- Consumer thresholds: tile render: 0→black, 1..0x7F→full grey (fogFactor 1), 0x80..0xE2→`fogFactor=(255−2·min(128,⌊(b−0x80)·1.3⌋))/255`, ≥0xE3→clear; an object renders at maxFow ≥ 0x80; icons/particles ≥ 0x81; anim sounds > 0x78; picking > 0x7F; AI detection > 0x8C; obj_find* > 0; think-skip < 5.
- CFogOfWar — ONE per world, `*(World+0x20)`: +0x00 local player (caches `game+0x64`); +0x04 tick; +0x08 "reveal-all pending" (set when a world loads; fires when fog is disabled with `-nofow`); +0x0c int[8] **fowBegin[player]** (sky row); +0x2c 8×{ptr,count} **fade lists** ({tileX,tileY}); +0x6c int[8] counters.
- Per-player explored rect: `game+0xa328+p*0x10` (minX,minY,maxX,maxY in tiles), extended by every reveal, serialized.
- Per object: +0x364/+0x368 **fow size X/Y in fine units** (`set_fogofwar` multiplies its arguments by 8; exactly −1 = off); +0x36c owner (≥8 = does not participate); +0x3d0 bit 0x80 = viewinfog, bit 0x400000 = permission for a class of type 5; +0x3ec transport logic (0=elevator, 1=ladder, 2=door); +0x26c/0x270 last position; +0x6c int[16] GetMaxFow cache (−1 = dirty).
- Solidity: detail cell (0x59 bytes, the first 64 = an 8×8 grid of fine cells) indexed by `tile[0]`; **fill nibble byte0&0xF == 0xF = solid** (ray collision); a fine byte > 0xD blocks LOS.
- `tile+0x08` = 8 per-player dig-mark nibbles (in the port `DiggableTerrain.RawDigNibble`), reconciled by `NewVisibleBlock` (see §2.6).

### Constants (byte-exact from.rdata)
8.0 (tiles→fine, 0073752c), −8.0 (Y, 007377b0; the code writes `-(int)(y·−8)`), 0.125 (0073703c), 0.5 (0073708c), π double (00737378, numRays), π float (00737440, angle), 1.1 (00737150, ray step divisor), 16.0 (0073756c), 1.0 (00737138).

## 2. Algorithms

### 2.1 UpdateFoW (0057f5c0)
The only call site: `CGameThread_SrvDoThink` (006b4a30:421), **every 4th server think** (`(game+0x6c)&3==3`), while `world+0x29` (world ready). Gates: `game+0x590==0` (not -mapedit) and `game+0x8e!=0` (not -nofow; otherwise a one-shot full reveal via the +0x08 latch).
1. `FadeBlocks(p)` for p=0..7.
2. Slot `(s+1)%6`.
3. Over the objects of list `ObjMgr+0x84c2` (owner<8): reveal if `sizeX≠−1` AND (the object crossed a fine boundary of 8 relative to its last position OR `obj[0xdc]%6==slot`) AND (class+0x54≠5 OR flag 0x400000). Position: `xF=(int)(x·8)`, `yF=−(int)(y·−8)`, `yTop=yF−12`.
   - tl==NULL/2: sizeY≥1 → `RemoveFog(xF, round(yTop−0.5), sX, sY, owner, 0, 1)`; sizeY≤0<sizeX → `RemoveFog(xF, yF+4, sX, −sY,...)`; **both ≤0 → `RemoveFogBoxNoCollision(xF, yTop, −sX, −sY, owner)`** (FogRemover −50 −50 = a 50×50 tile box straight through rock);
   - tl==0 (elevator): `RemoveFogLine` downwards along the shaft (`tl[1]+0x230`·8+16); tl==1 (ladder): upwards (`tl[2]+0x2c`·8+0x10).
   - The last position is updated for ALL owned objects.
4. Tail: p=tick&7 → sort the fade list (y-major quicksort) + `FUN_0057fde0`: refresh minimap records + **invalidate the GetMaxFow cache on every object**.

### 2.2 RemoveFog (0057e120) — radial raycast, WITH collision
Everything in fine units (1 tile = 8). `numRays=(int)(max(sX>>3,sY>>3)·π·2)` (double π); ray indices `round(N·fFrom)..round(N·fTo)` (always 0..1 when called from UpdateFoW — sectors are possible). Angle `θ=i/N·2π`; dir=(cosθ, sinθ·(sY>>3)/(sX>>3)); start `x·0.125`, step `dir/1.1`; step count = sX>>3. Sampling `(int)(f+0.5)`.
**Collision**: a fill nibble of 0xF starts a counter; the ray travels up to 3 more tiles inside rock and then stops (tunnel walls get a "crust", there is no x-ray).
Tile reveal (shared by all three functions): `b==0xFF` — skip; `b<2`: b==0 → NewVisibleBlock; `tileY ≥ fowBegin[owner]` → push to the fade list; `owner==local && b<0xE3` → dirty rect (mask 0x50, ±8 fine); `b=0xFF`. Plus extension of the explored rect (except `game+0x8c`).

### 2.3 RemoveFogLine (0057ea10) — WITH collision
Rows from `(y0+4)&~7` to `(y1+4)&~7`: horizontal runs to the right up to +sX and to the left up to −sX, each with its own 3-tile penetration. Only for elevators/ladders.

### 2.4 RemoveFogBoxNoCollision (0057f170)
A rectangle `±sX,±sY` around the snapped centre, WITHOUT collision; same reveal semantics (fowBegin is compared ×8).

### 2.5 FadeBlocks(p) (0057e710)
Walking the list from the end: outside the rect → remove (swap-last); `v=b−4`; v<2 → **v=1**, remove from the list; v∈(0x7B..0xE2] and p==local and (`(v&~3)==0x7C` or tick%5==0) → dirty rect 0x50 (a repaint at the edge of the visible half and every 5th tick during the fade); write back. A full fade 0xFF→1 = 64 updates (256 server ticks); the visible ramp 0xE2→0x80 ≈ 25 updates.

### 2.6 NewVisibleBlock (0057dfc0) — on 0→visible
Reconciles the owner's **dig nibble** `tile+0x08`: a tile of type 0/3 (`tile+0x18&3` solid/dug out) → nibble=0; the threshold = n==0?15: !(n&8)?11: (n&3)·4−1 (= the dig target table, `DecodeMarkTarget` in the port); if the actual fill (the maximum of the 8×8 detail bytes around the centre, FUN_00597460, OOB=0xF) ≤ the threshold → nibble=0. So stale dig marks are erased on first sight. It raises no events.

### 2.7 Visible (0057ff40) / FindNextVisible (00580260)
`Visible` = a pure LOS DDA over fine cells (byte >0xD = blocked), it does NOT read fog. `FindNextVisible` = the nearest object of a class within a radius with LOS (list `+0x84b1`). The only consumer is the Tcl `obj_next_visible`.

### 2.8 GetMaxFow (005cb3a0) + Tcl get_max_fow (0067beb0)
player<0 → 0xFF. Follows the carrier chain `obj+0x278` (a carried object inherits the carrier's visibility). Cache `obj+0x6c[16]`. Sampler FUN_00580ea0: the object rect (position ± link box) → **7 tiles** (the centre, 4 corners, 2 midpoints of the vertical edges), result = max byte; early exit at 0xFF when a sample is ≥0xE3. Tcl: with an owner → that owner's value; without one → the max over 0..7.

### 2.9 logicfogskip (005cc600:77-98)
`obj+0x218`++ against the period `obj+0x210`; if `+0x210 < +0x214` and `GetMaxFow(local)<5` → the period becomes `+0x214` (thinks less often in fog). Both default to 0; **never scripted in the data** — a get→"0" stub is sufficient in the port.

## 3. Tcl commands (handlers)
| Command | Handler | Semantics |
|---|---|---|
| `set_fogofwar obj sx sy` | FUN_0068a980 | `obj+0x364/8 = arg·8`; −1 → off; negative → NoCollision box |
| `remove_black_fog p l t r b` | FUN_0066aec0 | tile rect: **only 0→1** (to grey!); dirty mask 0x10 if p==local |
| `set_fow_begin num [p]` | FUN_0064b380→FUN_00580610 | `fowBegin[p]=min(num,maxRow−2)`; rows [minY+5..num) → 0xFF (NewVisibleBlock when 0); prunes the fade list above the line; called on world load for all 8 |
| `show_fogofwar b` | FUN_0062af80 | `game+0x59c` + a full redraw; **nothing reads 0x59c — dead in the shipped exe** |
| `set/get_viewinfog` | FUN_00680620/850 | `obj+0x3d0` bit 0x80 |
| `class_viewinfog b` | FUN_00613b20 | class+0x2c bit 0 (the port honours the argument — implemented, `DigglesTclEngine:49`) |
| `get_max_fow obj [p]` | FUN_0067beb0 | see §2.8 (real port implementation at `DigglesTclEngine:3096`; the "50" stub is gone) |
| `obj_next_visible` | FUN_00673970 | §2.7 |

Usage in the data: gnomes/trolls/wukers 14×8; buildings from `techtreetunes.tcl` via `tttfow_x/y` (doors 2 … guard hut 30), set on construction and reset to −1 −1 when packed into a crate; elevators 8×8; creatures viewinfog=0, static props =1 (default); `set_fow_begin` 75–300 only in single maps (campaigns are fully covered); FogRemover (the −50 −50 box) and Info_Fog_Aufdecker (triggered by proximity) are editor triggers; `show_fogofwar`/`logicfogskip` are unused.

## 4. Rendering in the original
- The lane is the local player. Byte decoding — §1.
- **BLACK (volumetric)**: tiles with byte==0 emit no mesh; horizontal runs become black front quads at z=14.5 (colour = the set_brightness offset, pure black by default); black side walls along the edges towards explored neighbours, extending down the slab z 14.5→(slots·0.5+7) or −14 if dug out; partially explored tiles get a per-vertex RGB multiply ×(bias/256+1), bias=−128 at an unexplored corner (bilinear softening of the boundary).
- **GREY**: in the vertex shader `f=clamp01((1−(z−7)·0.125·1.02)·fogFactor)`; diffuse×=(1−f); **specular += f·(0xAA,0xAA,0x9B)** (an additive layer, not modulated by the texture). **Depth gradient**: ~4% on the front plane z=14.5 → 100% at z=7 — this is what gives the fog volume. Terrain under grey fog shows the LIVE state; point lights do not brighten the fog (gated on bias>0).
- **Objects**: a render job is issued only at GetMaxFow(local) ≥ 0x80; the same fog shader tints it; icons/particle emitters need ≥0x81 on any of the 4 tiles (an emitter's `is_vis_in_fog` = byte+0x64). `viewinfog` feeds **CDummyObjMgr** (the "ghosts" of static props in grey fog — the last seen state), not the renderer directly.
- Invalidation: dirty-rect masks 0x50 (reveal/fade), 0x10 (remove_black_fog); MapValidator → the terrain and minimap channels.

## 5. Port contract (Unity)
1. **`FogOfWarState`** POCO (Runtime/World): 8 byte planes of `Width×Height` (layout `(ty·W+tx)·8+p` as in the original), fowBegin[8], fade lists[8], explored rects[8], tick; terrain access through delegates (fill nibble = `DiggableTerrain.GetHmap`, OOB 15). Update: in `DigglesGameSim.Step` on **every 4th 10 Hz think tick** (= the native "every 4th server think").
2. Per-entity fields in `DigglesTclEntity`: FowSizeX/Y (fine, −1), last fog position, GetMaxFow cache; the owner already exists (`Owner`).
3. Tcl: real `set_fogofwar`, `remove_black_fog`, `set_fow_begin`, `get_max_fow` (instead of "50"), `show_fogofwar` (a render gate — kept LIVE in the port, unlike the dead original, for cutscene compatibility), and a fixed `class_viewinfog` (the argument is honoured). Registration happens BEFORE `RegisterStubs`.
4. **Rendering**: a global fog texture (RG16 with a CPU decode — see the deviations; the local player's lane) sampled in `DigglesGround`/`DigglesModel` (bilinear = corner softness): a black multiplier plus a grey additive layer with the depth gradient from the formulas in §4. Opaque black volume: `FogOfWarOverlay` in the style of DigMarkOverlay — covers at the **tile's own fill depth** (nibble·0.5+7+`CoverLift`=0.03; nibble 15 ≡ the front plane z=14.5 — see the parallax deviation) (`FowBlack`, flat black) + side walls towards explored neighbours where a black tile rises above their surface + skirts for the depth steps.
5. Entities: `ApplyRenderVisible` at threshold 0x80 (GetMaxFow with a cache, invalidated on every FoW update); particles ≥0x81; `viewinfog` statics render at byte≥1 (a simplification of the dummy ghosts). **Link children (`link_obj`: hats/tools) inherit the carrier's visibility**: their renderers belong to the carrier's sweep, so the fog pass neither flags them nor treats them as revealers (their logical position is stale — only root parents are 2c-synced; the carrier lights the same area anyway); `get_max_fow` on a linked object samples the live transform (the analogue of the obj+0x278 chain). This fixed an "invisible hats" regression.
6. Cutscenes: fog stays LIVE during cutscenes (as in the original — "reveal for the cutscene" was a port mistake); the story opens scenes itself (remove_black_fog/FogRemover); `show_fogofwar` remains a working lever.
7. The port has no minimap yet — the map is ready for one.
8. EditMode tests: fade laws (−4, floor 1), 3-tile penetration, the fow_begin band, remove_black_fog 0→1, get_max_fow 7 samples/cache, ×8 in set_fogofwar, the NoCollision box for negative sizes.

### Known port simplifications (documented deviations)
- The fog texture is RG16 with a CPU decode (R = fogFactor by the law of FUN_004ca830, G = the black indicator): both laws are non-linear in the byte, while the original bilinearly interpolates the already DECODED corner values (FUN_004cc040) — GPU bilinear filtering of decoded channels reproduces that exactly; a raw R8 byte could not.
- The particle gate checks the emitter's 1 tile (the original checks 4 neighbours).
- The black volume's side walls are inset by 0.005 (the port renders the cliff faces of a black tile, which the original never emitted — otherwise they z-fight through coplanarity).
- **The black volume is lifted by `CoverLift`=0.03** above its own plane and filled with **flat pure black** (shader `Diggles/FowBlack`). The original did not mesh solid black tiles, so its covers had nothing to fight; the port does mesh them, so nibble-15 tops (15·0.5+7=14.5) were coplanar with the covers = shimmer, and lit terrain with specular stole pixels ("not quite black, glossy"). A variant with a depth gradient on the covers themselves read as gloss and was **rejected** → covers stay flat black (the depth gradient belongs to GREY terrain only, §4).
- **Covers sit at the tile's OWN fill depth** (nibble·0.5+7+CoverLift; nibble 15 ≡ the native front plane 14.5 bit for bit). This fixed "hard straight black edges across the lit back wall of deep caves". The PROVEN MECHANISM is parallax occlusion, NOT a sampling error: the uv law is exact (a fragment on a grid line samples the CENTRE of the black tile's texel = the native per-corner bilerp FUN_004cc040); but with black rows ≤ t−1 above an open cave the only carrier of the gradient is the surface of row t−1 at depth ≈7, while the cover (rows ≤ t−2, front 14.53) parallax-overhung it by (14.53−7)·tan(tilt) ≈ 1.9 (at the default 0.25 rad) … 5.3 (at 35°) tiles — the whole 1-tile gradient band lay in its shadow and the screen jumped black→lit in one step (it was only soft on near-surface tops at 14.5, a 0.01-tile shadow). The fix: a cover, being a stand-in for the tile mesh the original SKIPPED, sits where that mesh's surface would be; in the original the carrier for open tiles is at tile depth anyway — FUN_004ca080 emits the mat-21/22 backdrop as PER-TILE geometry of the block template (+tileX/+tileY, z from the template), fogged per vertex. Runs merge only at equal nibbles; **walls** get top = the cover plane of their own tile (flush open-to-open seams emit nothing — these used to be "black rectangles spanning the whole cave depth"); **skirts** (vertical quads in the gradient submesh, alpha = 1−fow.g, so opaque inside the black and graduated on the ring) close the depth steps between neighbouring covers/patches wherever there is a meshless side (between two meshed ones the step is closed by the terrain's own cliff, blackened by the shader). A known cost: a live particle drifting through a black open tile is no longer occluded (spawning is gated by visibility anyway). Mechanism test: FogOfWarSeamParallaxTests (an analytic 35° ray fan over the real overlay geometry: the darkness profile across the seam is a monotone gradient, not a 1→0 cliff).
- **Gradient patches over meshless tiles** (symptom: "rectangular black fog edges in caves"): the port does not render fully open tiles (load-time block 59 / oob / outside the render bbox), and the black-to-grey gradient is drawn by the TERRAIN shader — so at a black boundary such tiles were a hole onto the covers/walls of the tiles behind. The original closes open tiles with the mat-21 backdrop, which alpha-fades 21→22 at the fog boundary (per-vertex alpha = bias+0x100 ≡ the decoded black indicator). Instead of the backdrop, the port lays a black quad at the tile's fill depth (nibble·0.5+7) with shader alpha = 1−fow.g (Diggles/FowGradient, the same fog texture, so the gradient matches the neighbouring terrain's darkening bit for bit and equals 1 exactly on the black grid lines where the covers/walls stand). Gate: FogOfWarOverlay.Init(rendersAt = DiggableTerrain.RendersTerrain — the exact skip law of BuildTileBuckets). Tests: FogOfWarCaveBoundaryTests (real caves_3, boot stamp) + FogOfWarOverlayTests.MeshlessBlackBoundary.
- **Patches follow the DISPLAY field, not the raw bytes** (symptom: "blocky staircase cutouts" at the fog boundary in caves). Cause: the presenter smooths the display over `SmoothSeconds`, while the patch set used to follow the raw bytes — a meshless tile lost its patch on the frame its last corner byte flipped, while its own alpha (= 1−fow.g of that same smoothed field) was still in flight: an almost-opaque quad vanished in one frame and the content behind it popped at full contrast, tile by tile along the reveal front (2.5 Hz) — the "staircase". Fix: with a presenter attached, a meshless tile keeps its patch until ALL 4 corner DISPLAY values reach 255 (at which point the bilinear alpha is identically 0, so removal is invisible BY CONSTRUCTION); the presenter raises `DisplayChanged` while texels are catching up, redirtying the affected chunks (`FogOfWarOverlay.OnDisplayChanged`/`NeedsPatch`). Covers/walls have no such window (AllBlack drops on the first corner, and the display of byte 0 is exactly 0), so they stay raw — faithful to the native law. Headless/legacy without a presenter keeps the old raw `AnyBlackCorner` ring. **REJECTED**: a front-plane gradient "skirt" on the ring — alpha quads at z=14.5 parallax-float above the lit cave floor at z≈7 under an orbit tilt and read as detached dark rectangles; the carrier of the falloff must live at the tile's fill depth.
- GetMaxFow samples 7 points of a rectangle **±1 tile** around the object's tile (the original extends the rect by the model's link box — equivalent for ordinary objects; very large buildings are slightly narrower). The single-point probe of the first version made wall/floor decoration lag behind the reveal front and pop in batches.
- The fade list is not sorted (the original quicksorts it for cache locality; it carries no semantics).
- CDummyObjMgr ghosts are not ported: viewinfog statics simply render in grey fog (equivalent for immobile objects).
- `show_fogofwar` works in the port (it is dead in the original) — an emergency lever for scripts.
- **Temporal render smoothing**: display texels chase the decoded targets over `FogOfWarPresenter.SmoothSeconds` (0.7 s) — black reveals as a smooth fade-in and the grey veil changes continuously (in the original the reveal is instantaneous and the fade is quantized every 5 updates). The sim bytes stay byte-exact; Init snaps (the world does not "fade in" on load).
- **Blockiness from a top-down angle is a KNOWN OPEN PROBLEM; the "view-projected sheets" attempt was REJECTED.** Symptom: from a steep orbit tilt the black fog boundary reads as hard tile staircases and the lit floor is bright right up to the black edge (from the side the same cave looks fine). The diagnosis remains valid: fog is a 2D field over (tileX,tileY); on a VERTICAL black surface (an overlay wall/skirt, a blackened cliff face) the bilinear sample is pinned to its grid line = a single flat value over all ~7.5 units of depth; at tilt p such a sheet covers ~tan(p)·7.5 screen rows (28 at 75°), hiding the gradient band and the open floor behind it. State dumps (FogOfWarTopDownTests) disproved the "stuck covers"/"stuck display" hypotheses. The attempted fix — (1) `clip(fow.g−0.002)` in DigglesGround (a per-fragment "no-mesh" law) plus (2) walls/skirts as view-ray-landing sheets in the gradient submesh — **failed and was reverted**: the clip opened HOLES through the mesh down to the clear colour, and the sheets produced many artifacts and angular joins between gradient and blackness. Lesson: analytic ray fans (75°) modelled an ideal composite and diverged from the real renderer (depth-write order, alpha sorting, the clear backdrop, coverage differences between meshed and meshless) — the 75° law test is marked `[Ignore]` in FogOfWarTopDownTests. **The next attempt must be verified against real screenshots, not analytic fans.** The current render state is the baseline: depth-positioned opaque covers/walls/skirts (fine from the side, blocky from above).
- Minimap/fluid gates — when the corresponding systems appear.
- logicfogskip: not implemented (unused in the data; the get stub "0" is correct).
