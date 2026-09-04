# Diggles World-Rendering Pipeline — Complete Reverse-Engineering Reference

> 
> **The RE ground truth (§1–§9, §11–§13) is still authoritative.** The Unity **port-status**
> sections (§10, §14, §15) describe the first Editor-importer prototype and are
> **SUPERSEDED** — the live runtime pipeline is now `Assets/Diggles/Runtime/World/BdbPmpWorldBuilder.cs`
> with the custom URP shaders (`Runtime/Shaders/DigglesGround.shader`, `DigglesModel.shader`).
> Textures, per-scape material variety, corner blending, per-vertex point-lights AND native
> warm-grey fog are all IMPLEMENTED now. For current rendering/lighting architecture see
> `Docs/RENDERING_LIGHTING_RESEARCH.md`.

**Status:** geometry pipeline fully reversed and ported to Unity.
**Visual confirmation:** Unity render matches original game shape (3-sub-mesh
stacks, cliff walls, bowl floor, tunnels). Textures/materials, multi-texturing,
per-vertex shading and the offline `block_idx` resolver remain open.

This document is the single source of truth for the rendering **RE**. If anything
in `re/notes/` or memory contradicts it, this doc wins. (For PORT status it does
NOT — see the banner above.)

---

## 1. Overview — how Diggles renders a tile

Diggles is a 2001 "ant-farm" RTS where the visible world is built from a fixed
catalogue of pre-authored 3D **blocks** living in `BlockDB{0,1,2}.BDB`. Each
map tile picks ONE block (by index) and stacks THREE sub-meshes on top of each
other (top face, mid, bottom face). The map files (`.PMP` heightmap + `.L2M`
cell IDs) do NOT directly contain geometry — they contain inputs (terrain
type, corner mode, height) that the engine turns into a `block_idx` at
world-load time.

```
┌────────────────────┐    ┌────────────────────┐
│.PMP heightmap │ ┐ │ Campaign/Tcl │
│.L2M cellId │ │ │ urw_unq_start + │
│.PMP terrain_type│ │ │ first_level procgen│
└────────────────────┘ │  └─────────┬──────────┘
                       │            │
        ┌──────────────▼────────────▼────────────────┐
        │  Engine world-load (CMapBeautifier::       │
        │  ScanAtPos → FUN_0058a8b0)                 │ ← NOT yet reversed
        │  picks block_idx per tile.                 │
        └──────────────┬────────────────────────────┘
                       │
        ┌──────────────▼──────────────────────────┐
        │  Runtime: tile_struct[ty][tx]            │
        │   +0x00  uint32 block_idx                │
        │   +0x07  byte   zone                     │
        │   +0x14  uint16 cellId  (material/anim)  │
        │   +0x18  uint32 digg_flags (lo 2 bits)   │
        │   ... 124 bytes total ...                │
        └──────────────┬───────────────────────────┘
                       │
        ┌──────────────▼──────────────────────────┐
        │  FUN_004ca830 per-tile renderer (4610 B) │
        │   for sub = 0..2:                        │
        │     • get CBlockTri from BDB cells[]     │
        │     • read triangle range from           │
        │       descriptor[CBlockTri.f5]           │
        │     • build vbuf (vert_count vertices)   │
        │     • upload ibuf range                  │
        │   if aux corner delta > 4 → cliff path   │
        │     (FUN_004ca080, CBlockHole)           │
        └──────────────────────────────────────────┘
```

---

## 2. File formats (binary, on-disk)

### 2.1 BlockDBn.BDB — block catalogue

Three files: `BlockDB0.BDB`, `BlockDB1.BDB`, `BlockDB2.BDB`. The runtime uses
**BDB2** for the campaign-start scene (verified via the runtime dump: BDB2 has
`n_palette = 3323` which matches `DAT_00b836a8` in the dump).

```
+0x00 uint32 version (always 0x0B)
+0x04 uint32 _reserved
+0x08 uint32 n_blocks (BDB2: 118688)
+0x0C uint32 _reserved
+0x10 uint32 n_index (u16 triangle-index pool size)
+0x14 uint32 n_descriptors (triangle descriptor count)
+0x18 uint32 n_palette (global vertex palette entries)
+0x1C → cells[n_blocks][3] 6 bytes each
            uint16  vert_count          per-cell vertex pool size
            uint32  descriptor_idx      index into descriptors[]
+(.. + n_blocks*18)
       → per_block_aux[n_blocks]  89 bytes each   ← see §2.1.1
+(.. + n_blocks*89)
       → index_pool[n_index]      uint16 each     triangle vertex indices
+(.. + n_index*2)
       → descriptors[n_descriptors]  8 bytes each
            uint32  offset_in_index_pool
            uint32  count                          always multiple of 3
+(.. + n_descriptors*8)
       → palette[n_palette]    20 bytes each      5 floats: x, y, z, u, v
+(.. + n_palette*20)
       → bulk[]                                    per-cell vertex/normal pools
```

**Per-cell bulk layout** (this was the bug that broke the first Unity port):
the data for one cell (sequential in file) is **NOT interleaved**.

```
[cell.vert_count × uint16 palette_idx]
[cell.vert_count × uint8 normal_idx ]
```

`palette_idx` indexes the global palette. `normal_idx` indexes `sphere.dat`
(82-entry normalised direction table).

Runtime confirmation: `CBlockDB::LoadDB` (0x00565ab0) lines 215–220 set
`CBlockTri[1] = palette_ptr` and advances by `vert_count * 2` bytes, then
`CBlockTri[3] = normal_ptr` advances by `vert_count * 1` bytes.

#### 2.1.1 Per-block aux blob (89 bytes)

```
+0x00..0x3F 8×8 byte heightmap (values 0..15)
              row r, col c = aux[r*8 + c]
              corners NW=+0x00 NE=+0x07 SW=+0x38 SE=+0x3F  center=+0x24
              Cliff trigger: |aux[corner] - aux[+0x24]| > 4

+0x40..0x43 neighbour_NW: uint32 (BDB block index for stitching,
+0x44..0x47 neighbour_NE: uint32 0xFFFFFFFF = "no stitching")
+0x48..0x4B neighbour_SW: uint32
+0x4C..0x4F neighbour_SE: uint32

+0x50..0x53 corner heights NW/NE/SW/SE (copies of heightmap corner samples,
              cached for fast variant lookup)

+0x54 variant/flag byte
                low nibble (& 0x0F) = shader variant idx (0..15+)
                read by FUN_004ca830 line 339, special value 0x15

+0x55..0x58 extra flags / refcount tail
```

#### 2.1.2 Sample aux values

| block | corners NW/NE/SW/SE | center | u32×4 neighbours | meaning |
|-------|---------------------|--------|-----------------------|----------------------|
| 0 | 0F/0F/0F/0F | 0F | 54, 44, 32, 22 | solid earth default |
| 59 | 00/00/00/00 | 00 | 59, 59, 59, 59 | uniform open air |
| 13739 | 04/07/05/0B | 07 | -1, -1, -1, -1 | cliff/cave terminator|
| 27222 | 0F/0D/0F/0F | 0F | -1, -1, -1, -1 | bowl-rim cliff |
| 27323 | 0B/0D/0D/0E | 0D | -1, -1, -1, -1 | bowl-rim cliff |

### 2.2 PMP — heightmap + terrain type

Per-tile metadata for ground geometry. NOT the visible mesh — that's BDB.
- Width × Height × 3 RLE-encoded byte streams.
- Stream 1: `terrain_type` (0x80 bit = "carved", 0x85=grass, 0x84=stone,
  0x02=dirt low, 0x04=dirt high)
- Stream 2: `corner_pattern` (3-byte? — see `reference_diggles_pmp_*.md`)
- Stream 3: 8×8 per-sub-cell height (the depth/ant-farm indicator)

The PMP heightmap and L2M cellIds feed into the engine's `block_idx` resolver
that we have NOT yet reversed.

### 2.3 L2M — per-tile material/animation

- `width * height * uint16` cellIds, plus per-tile animation frame info.
- cellId encoding: hi-byte = 4-corner height-corner pattern (pair-encoded
  0/1/2 per corner); lo-byte = texture/material/animation frame.
- The cellId is **not** a BDB index. It's an INPUT to the block_idx resolver.

### 2.4 sphere.dat — normal table

82 entries × 3 floats (NX, NY, NZ). Indexed by the `normal_idx` byte in
each per-cell vertex pool.

> **Correction:** the table is
> **256 entries**, not 82 — the loader does `fread(...,0xc,0x100,"data\sphere.dat")` (0x100 = 256,
> 0xc = 3 floats). It is the global `DAT_00b9bed0` normal table (earlier misread as an "82-entry
> palette"). The `normal_idx` byte (0..255) is a direct index. The normals are global/render-space — they must
NOT be transformed when changing axes (see
`reference_diggles_3db_canonical_import_settings`).

### 2.5 Ground/detail/urw_XXXX.tga — corner-mode atlas

`urw_1111.tga` (all corners covered), `urw_0001.tga` (only one corner), etc.
16 corner patterns × 5+ random variants. These are the **multi-texturing
blend masks** for the ground. The actual textures are 32-bit TGA (verified:
64×64 bpp32). Variants: `urw_*` (forest), `urw2_*`, `kris2_*` (crystal),
plus characters/objects.

---

## 3. Runtime tile struct (124 bytes)

```
+0x00 uint32 block_idx ← BDB block index (THE key field)
+0x04 uint32 ???
+0x07 uint8 zone (4 = default earth, 20 = template area)
+0x08 uint32 per-direction state (FUN_004ca830 reads (>> dir*4) & 0xF)
+0x0C..0x13 ???
+0x14 uint16 cellId (material/animation, NOT BDB index)
+0x16 uint16 ???
+0x18 uint32 digg_flags (low 2 bits = state: 0=solid, 3=open air)
+0x1C..0x3B ???
+0x3C..0x7B 8×8 sub-grid? (possibly cached PMP heightmap)
+0x7C END
```

Total stride: `0x7C = 124` bytes.

Layout verified against `FUN_00592f10` (tile lookup) and `FUN_004ca830`
(renderer). The runtime dump `dumps/extracted/tile_array.bin` is exactly
`640 × 512 × 124 = 40,632,320` bytes — confirms shape.

---

## 4. Per-tile rendering — FUN_004ca830 (4610 bytes)

```c
void FUN_004ca830(this, float gx, float gy)
{
    // 1. Sample 4 corners (gx,gy), (gx+1,gy), (gx,gy+1), (gx+1,gy+1)
    //    via FUN_00592f10(x, y) → tile_struct*
    //    Pull cellId, diggability, lighting per corner.

    // 2. Early-out checks
    if (all 4 corners diggability == -128) {
        FUN_004c6b20(...);   // cheap quad column for solid block
        return;
    }
    if (all 4 corners state == 3 && some flag) return;   // pure air

    // 3. Pre-build vertex buffers for this tile (3 sub-cells, local_3c = 2,1,0)
    for (sub = 2; sub >= 0; sub--) {
        FUN_004c4ae0(...);   // some bilinear prep
        FUN_004c4c50(...);   // if DAT_00768369 != 0, additional shading?
    }

    // 4. Cliff trigger?
    if (any of {pbVar16[0x00], pbVar16[0x07], pbVar16[0x38], pbVar16[0x3F]}
        differs from pbVar16[0x24] by > 4)
    {
        FUN_004ca080(gx, gy, ...);   // cliff path — see §5
        return;
    }

    // 5. Normal mesh emit — 3 sub-meshes
    for (sub = 0; sub < 3; sub++) {
        block_idx = *(uint32*)tile;          // line 330: tile[+0x00]
        per_cell = BDB.cell_ptr_table[block_idx*3 + sub];   // BDB+0x18
        vert_count = per_cell[0];
        desc_idx   = per_cell[5];
        descriptor = BDB.descriptors[desc_idx];   // BDB+0x34
        idx_range  = BDB.index_pool[descriptor.offset .. +descriptor.count];

        // Allocate runtime CBlockTri (0x528 bytes)
        // Fill vbuf: for i in 0..vert_count
        //     palette_idx = per_cell.palette_idx_array[i]
        //     normal_idx  = per_cell.normal_idx_array[i]
        //     (x,y,z,u,v) = DAT_00b836a4[palette_idx]
        //     FUN_004cc040(vert, normal, gx, gy, ...) // per-vertex shader
        //     vbuf[i] = world_pos + diffuse + specular + uv0 + uv1
        // Set ibuf = idx_range (triangle list — count multiple of 3)
        // Upload to DX7
    }
}
```

**Key constants:**
- `0x2c2 = D3DFVF_XYZ | D3DFVF_DIFFUSE | D3DFVF_SPECULAR | D3DFVF_TEX2` —
  vertex format = `vec3 + u32 + u32 + vec2 + vec2 = 36 bytes/vertex`.
- The TWO UV sets are written from the SAME palette (u, v) by FUN_00462b40
  → multi-texture blending (base + corner-mode mask).

---

## 5. Cliff path — FUN_004ca080 (1967 bytes)

When aux corner deltas exceed 4, the renderer switches to the cliff path,
which emits **clipped side-face geometry** as `CBlockHole` drawables.

```c
for (layer = 0; layer < 3; layer++) {
    per_cell = BDB.cell_ptr_table[block_idx*3 + layer];   // same as normal path
    vert_count = per_cell[0];
    desc_idx   = per_cell[5];
    descriptor = BDB.descriptors[desc_idx];   // same descriptor system

    // ... fill workspace at this+0x1378 with vertices ...
    // ... copy index range to this+0xa378 ...

    // CLIP against 4 neighbour planes:
    if (corner_NW + corner_NE) FUN_004c9010(1,1, ...);
    if (corner_SW + corner_SE) FUN_004c9010(1,0, ...);
    if (corner_NW + corner_SW) FUN_004c9010(0,1, ...);
    if (corner_NE + corner_SE) FUN_004c9010(0,0, ...);

    // Translate to world XY, upload as DX index buffer
}
```

**Important insight:** the cliff geometry itself is **pre-authored** per block
(stored in the same BDB structure). The cliff path only **clips and
translates** it; nothing is generated procedurally. The previous
attempt with "procedural cliff generation" was a wrong-trail.

The bowl walls / cave entries visible in `tounel.png` are simply blocks whose
authored mesh forms a cliff face, placed at the bowl rim by the `block_idx`
resolver.

---

## 6. Per-vertex shader — FUN_004cc040

For each vertex emitted in §4 step 5:

1. **Bilinear corner luminance** — interpolate 4 corner brightness values
   (sampled from tile diggability bytes) at the vertex's local (x, y).
2. **Directional light** — dot product of `sphere.dat[normal_idx]` with the
   global sun direction.
3. **Point lights** — iterate `this+0x12e4` table; each entry is 7 floats
   `(pos.xyz + radius² + RGB)`; add attenuated colour.
4. **Pack RGBA → diffuse u32** (`vert[3]`).
5. **HDR overspill** — anything over 1.0 → specular u32 (`vert[4]`), capped
   at 64/channel.
6. **Water wave** — if water tile, displace position by triple-sin (small
   waves on top of base mesh).

For the Unity port this is replaced by Unity's standard URP lighting —
identical visual quality isn't required for geometry validation.

---

## 7. Coordinate systems

### 7.1 Diggles (right-handed, Z-up)

- **X** = horizontal (map columns)
- **Y** = depth/into-screen (map rows)
- **Z** = vertical (up)
- Palette entries: `x, y ∈ [0, 1]` (tile-local), `z ∈ [7, 14.5]` (block-local
  elevation, baked per-vertex).
- World position of a tile's vertex: `palette.xy + (tile_x, tile_y, 0)`,
  z = `palette.z`.

### 7.2 Unity (left-handed, Y-up)

The Unity port emits vertices using the simple mapping
`pos = (p.x + tx, p.z, -(p.y + ty))` — Diggles X → Unity X, Diggles Z → Unity
Y (up), Diggles Y → Unity -Z (back into screen). Triangle winding is kept as
`(a, b, c)` from the BDB (no swap needed; verified by visual lighting on
the rendered mesh).

To get the player-requested "map flat on X/Y, Z = elevation" view, the **root
GameObject** is rotated `-90°` around X. This preserves all vertex data and
normals — only the parent transform is rotated.

---

## 8. Engine constants & globals

| Address | Symbol | Meaning |
|---------------|----------------------|----------------------------------------|
| `DAT_00b836a4`| palette base | global vertex palette pointer |
| `DAT_00b836a8`| palette count | global vertex palette entry count |
| `DAT_0077241c`| world manager | CMapMgr/CWorld global |
| `DAT_0077241c[5]` | tile array base | runtime tile_struct[] base, 124 B each |
| `DAT_0077241c[9]` | BDB ptr | active CBlockDB instance |
| `DAT_0077241c[0x11]`| world stride X | for `(y*stride+x) % mod` indexing |
| `DAT_0077241c[0x13]`| world mod | wrap-around modulus |
| `DAT_0077241c[0x21..24]` | clip rect | xmin, ymin, xmax, ymax (world bounds) |
| `BDB+0x18` | cell ptr table | 12 B/block (3 LOD pointers each 4 B) |
| `BDB+0x28` | index pool ptr | u16 array shared across all cells |
| `BDB+0x2C` | index pool count | |
| `BDB+0x34` | descriptor ptr | (u32 offset, u32 count) pairs |

---

## 9. Decompiled Ghidra functions used

| Address | Name | Bytes | Role |
|-------------|------------------------|-------|-----------------------------|
| 0x00565ab0 | `CBlockDB::LoadDB` | 1278 | parses BDB, sets up runtime |
| 0x004ca830 | per-tile renderer | 4610 | the main loop (§4) |
| 0x004ca080 | cliff path | 1967 | side-face clipping (§5) |
| 0x004cc040 | per-vertex shader | (n/a) | bilinear lighting (§6) |
| 0x00462b40 | palette vertex fetch | 355 | reads (x,y,z,u,v) → out |
| 0x004c6b20 | solid quad fast path | (n/a) | called when all corners=-128|
| 0x0058a8b0 | (unnamed) | (n/a) | likely block_idx selector — **open RE gap** |
| 0x0058b130 | `CMapBeautifier::ScanAtPos` | 2242 | the resolver entry — **partly reversed** |
| 0x005fc9d0 | tile modifier | (n/a) | sm_draw_stone writes |
| 0x0065e090 | `SMDrawStone` Tcl handler | (n/a) | funnel/border/utmat |
| 0x00592f10 | tile lookup | (n/a) | (x, y) → tile_struct* |
| 0x0045fd00 | configure draw call | 273 | sets FVF=0x2C2 etc |

Source: `the decompile*.c`.

---

## 10. Unity port — what's implemented

> ⚠️ **SUPERSEDED (describes the first Editor-importer prototype).** The live runtime pipeline is
> `Assets/Diggles/Runtime/World/BdbPmpWorldBuilder.cs` + the custom URP shaders. The "NOT yet
> implemented" list in §10.4 (textures, corner-mode blending, material variety, per-vertex shading,
> fog) is now **DONE** in that runtime path. Kept here for the RE trail. Current architecture:
> `Docs/RENDERING_LIGHTING_RESEARCH.md`.

### 10.1 Files (paths)

| File | Role |
|------|------|
| `Assets/Diggles/Runtime/World/BdbData.cs` | runtime model + cell triangle/palette accessors (moved from Editor/Importers) |
| `Assets/Diggles/Runtime/World/BdbParser.cs` | binary parser, layout § 2.1 (moved from Editor/Importers) |
| `Assets/Diggles/Runtime/World/BdbPmpWorldBuilder.cs` | **live** per-tile mesh build (BDB+PMP) + textured/lit/fogged shaders |
| `Assets/Diggles/Runtime/World/GroundTextureBlender.cs` | per-scape ground texture-array blending |
| `Assets/Diggles/Editor/Importers/BdbWorldBuilder.cs` | legacy per-tile mesh build from BDB + runtime dump (removed from the repository) |
| `Assets/Diggles/Editor/Importers/BdbWorldMenu.cs` | the legacy dump menu was removed; `Build From Real PMP` remains |

### 10.2 Build flow

`BdbWorldMenu.BuildFromDump`:
1. Loads the BDB.
2. Loads the tile dump (640×512×124).
3. Calls `BdbWorldBuilder.BuildWorld` with bbox X 195..315, Y 0..80.
4. Spawns `DigglesBdbWorld` GameObject, rotates `-90°` around X (map upright
   on X/Y plane).

`BdbWorldBuilder.BuildWorld`:
- For each tile in bbox:
  - Skip if `block_idx >= NBlocks` or `block_idx == 59` (open-air sentinel).
  - Pick material: `zone == 20 && cellId != 0` → stone submesh, else → ground.
  - For each of 3 sub-cells:
    - `(triStart, triCount) = bdb.CellTriangleRange(block_idx, sub)`
    - `bdb.CopyCellPaletteIndices(...)` fills `palIdxBuf[0..vert_count]` and
      `normIdxBuf[0..vert_count]`.
    - For each triangle: emit 3 vertices (dedupe by `(palette_idx, tile)`),
      add `(a, b, c)` to triangle list.
- Build one Mesh per material with `URP/Lit` material (solid colour).

### 10.3 Verified to match game

- Bowl shape, rim cliffs, central plateau, tunnel mouths south of the bowl.
- 3-sub-mesh stacks (top/mid/bottom) render correctly with z-buffer.
- All ~9.4k tiles in the X 195..315 × Y 0..80 area render without crashes.

### 10.4 NOT yet implemented

| Item | What we know | What's needed |
|------|--------------|---------------|
| **Ground textures** | atlas in `Ground/detail/urw_*.tga` (32-bit TGA, 64×64). Palette UVs `(u, v) ∈ [0, 7.5]` index into an atlas. | Build a packed atlas, sample `palette.uv` directly (no scaling — already in atlas units). |
| **Corner-mode blending** | TEX1 = blend mask `urw_XXXX.tga` (16 patterns) selected per tile from corner heights. | Implement blend in Unity shader (TEX0 = base material, TEX1 = corner mask). |
| **Material variety** | terrain types: 0x85 grass, 0x84 stone, 0x02 dirt-low, 0x04 dirt-high. | Switch material per tile by terrain_type from PMP, or by `cellId` lo-byte. |
| **Per-vertex shading** | bilinear corner luminance + sphere.dat normal + point lights. | Leave to Unity URP lighting; sufficient for visual parity. |
| **Cliff path** | pre-authored geometry from same BDB, clipped by neighbours. | NOT needed in Unity — depth buffer handles the clipping naturally. |
| **block_idx resolver** | filled by `CMapBeautifier::ScanAtPos` → `FUN_0058a8b0` at world load. | Decompile FUN_0058a8b0; trace the (cellId + PMP corner + neighbour) → block_idx lookup table. Without this, can ONLY render runtime tile dumps. |
| **Animation** | cellId lo-byte may carry animation frame. | Inspect engine animation code (out of scope so far). |
| **Fog of war / minimap brightness** | `terrain_type & 0x80` = "carved" flag drives minimap dimming. | Cosmetic, can be deferred. |
| **Water tiles** | water mesh + sinusoidal displacement in FUN_004cc040. | Detect via terrain type, animate per-vertex Z. |

---

## 11. Python validation tooling

All in `the decompile`:

| Script | Role |
|--------|------|
| `bdb_correct_renderer.py` | painter softrast, validates triangle topology |
| `zbuf_renderer.py` | z-buffered softrast — used to confirm geometry before Unity port |
| `export_world_obj.py` | exports BDB+dump → Wavefront OBJ + MTL + decoded grass texture |
| `dump_renderer.py` | early/buggy version (kept for reference) |
| `bdb_real_renderer.py` | early/buggy version (kept for reference) |

Outputs in `re/minimap_validation/out/`:
- `zbuf_render.png` — known-good geometry render (matches game)
- `world.obj` + `world.mtl` — Wavefront export
- `world_atlas.png` — decoded `urw_1111.tga`

---

## 12. Memory entries

Single source of truth for future a contributor sessions — keep these synced with the
code, update when wrong.

| Name | What |
|------|------|
| `reference_diggles_render_pipeline_complete` | pipeline overview (this doc supersedes) |
| `reference_diggles_block_idx_in_tile` | block_idx is `*(u32*)tile`, not cellId |
| `reference_diggles_bdb_aux_complete` | full 89B aux layout + DX7 FVF 0x2C2 |
| `reference_diggles_bdb_format` | BDB file layout |
| `reference_diggles_bdb_aux_corrected` | older — superseded by `_complete` |
| `reference_diggles_bdb_mesh_decode` | older — partially correct |
| `reference_diggles_block_idx_resolver_gap` | the open RE gap (§13) |
| `reference_diggles_pmp_*`, `_l2m_*` | map file formats |
| `reference_diggles_callstat_oracle` | how to auto-rename FUN_* via profiler strings |
| `reference_diggles_ghidra_setup` | how to use the Ghidra project |

---

## 13. Open RE gaps (ranked by impact)

### 13.1 block_idx resolver (HIGH — blocks new-map support)

`tile_struct[+0x00]` is filled at world-load time by chain
`CMapBeautifier::ScanAtPos` → `FUN_0058a8b0`. Without this resolver:
- Unity port works ONLY with a runtime dump (`tile_array.bin`).
- Mod-installed PMP/L2M files don't render correctly.

Suggested next steps:
1. Decompile `FUN_0058a8b0` (no CallStat name — need manual analysis).
2. Find data globals it reads — probably a `[corner_pattern][terrain_type] →
   block_idx` lookup table. The variant table notes mention "corner-mode hash
   → 0..80 entries".
3. Look at the WRITE site for `*(uint32*)tile`. Use Ghidra "find writes to
   address" feature on offset 0 of any known tile pointer.

### 13.2 Texture binding & multi-tex (HIGH — visual parity)

§10.4 row 1 + 2. Sample atlas + corner-blend shader needed. Atlas packing
needs to enumerate all `Ground/detail/urw_*.tga` (and `urw2_`, `kris2_`,
plus interior terrain sets) and figure out the packing the runtime uses.

Hint: the constant `_DAT_00737124` (UV scale in FUN_00462b40) likely encodes
"divide by atlas dimensions". Inspect what value the engine stores there.

### 13.3 Cliff path validation (LOW — current Unity render works without it)

The Unity port renders ALL 3 sub-meshes per tile. The original switches to
cliff path for big aux deltas. Visually equivalent in Unity because depth
buffer occludes the wrong faces. May matter for performance on large worlds
(naive emit is 3× more triangles than cliff-optimised).

### 13.4 Animation, water, FoW (LOW — campaign-start doesn't need)

Not blocking. Pickup once core port is done.

---

## 14. What the player sees (first prototype — SUPERSEDED)

> The description below is the first dump-driven prototype. The live game now renders textured,
> lit, fogged terrain via `BdbPmpWorldBuilder` in Play mode. §14/§15 kept for the RE trail only.

Run `Diggles → Build BDB World From Dump`:
- ~9.4k tiles processed in the X 195..315 × Y 0..80 area
- 2 sub-meshes spawned: `Ground (BDB)` (dirt brown), `Stone (BDB)` (grey)
- Root rotated upright (-90° around X) — map flat on X/Y, Z = elevation
- Geometry matches `tounel.png` (bowl, cliff walls, tunnels south of bowl)

Visually the only differences from the original game right now are:
- Solid-colour materials instead of textured grass/stone/dirt
- No corner-mode blending (sharp colour boundaries)
- No vertex lighting overlay (Unity URP handles it differently)

---

## 15. Quick recipe to add the next layer (textures)

Concrete next-step plan when ready to add textures:

1. **Decode the urw_*.tga atlas.** Use `re/minimap_validation/export_world_obj.py`
   `decode_tga` as the reference. There are ~16 corner patterns × ~5
   variants. Save them as a single PNG sprite-sheet (e.g. 16 × 5 grid of 64×64).
2. **Determine UV scale.** Find `_DAT_00737124` in the runtime dump (offset
   from `Diggles.exe + 0x00737124`). It's the divisor that brings palette UV
   ∈ [0, 7.5] into atlas units.
3. **Per-tile texture choice.** Need to know which corner pattern this tile
   uses. Comes from `cellId` hi-byte (the 4-corner pair encoding). Map this
   to one of the 16 `urw_XXXX` patterns.
4. **In Unity shader:** sample atlas with `(palette.uv - 0.5) * scale + 0.5`
   plus the per-tile corner-pattern offset. Use vertex colour to drive
   per-vertex shading if pure standard lighting doesn't match.

This is non-trivial (probably another half-day of RE) — but unblocked by
everything in §1–§12 being already verified.
