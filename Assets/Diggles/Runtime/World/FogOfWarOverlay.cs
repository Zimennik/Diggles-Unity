#nullable enable

namespace Diggles.Runtime.World
{
    using System;
    using System.Collections.Generic;
    using UnityEngine;
    using UnityEngine.Rendering;

    // Fog-of-war BLACK VOLUME — the opaque geometry half of the native fog render
    // (Docs/FOG_OF_WAR_RE.md §4):
    // • FRONT COVERS (FUN_004cca20, queue 9, material 17): every all-black tile — its 4
    // fog lattice corners byte==0, the FUN_004ca830:284-289 sentinel — is closed by an
    // opaque black quad on the front plane z = 14.5 (0x41680000); horizontal runs of
    // covered tiles merge into one strip per row (004c4de0:580-610), each rect expanded
    // ±0.01 (DAT_00736FE0);
    // • SIDE WALLS (FUN_004c6b20, queue 10, material 17): for each edge of an all-black
    // tile whose beyond-edge fog column is EXPLORED (byte ≠ 0 — e.g. W edge probes
    // (x−1,y),(x−1,y+1), 004c6b20:41-67), a vertical black quad spans the tile edge from
    // z = 14.5 down to the slab back-depth (nibble·0.5+7 — DAT_0073708C/DAT_00737520);
    // tiles above the fow-begin sky line never get boxes (004c6b20:30-38). The walls are
    // what make black fog read as a VOLUME: a tunnel into the dark ends at a black wall
    // closing its cross-section instead of a see-through hole.
    //
    // PORT NOTES (documented deviations):
    // • the native SKIPS the all-black tile's terrain mesh and the covers close the hole;
    // the port still renders that terrain (the DigglesGround fog term multiplies it to
    // pure black), so the covers' remaining job is occluding entities/particles/backdrop
    // behind the front plane — hence the depth-writing opaque material (Diggles/FowBlack,
    // queue Geometry), unlike the alpha-blended DigMark plane;
    // • COVERS SIT AT THE TILE'S OWN FILL SURFACE, not the native front plane (manual test
    // "hard straight black edges across deep caves"). MECHANISM (proven vs the
    // camera law eye=at+R(0,sin dx,cos dx)): with black rows ≤ t−1 over a deep open cave
    // and revealed rows ≥ t, covers exist over rows ≤ t−2 (AllBlack needs a 2×2 black
    // corner block), so the cover silhouette is lattice t−1 at y=14.53 while the ONLY
    // gradient carrier is row t−1's surface at its fill depth y≈7 — a view ray grazing
    // the cover edge lands on the y≈7 plane (14.53−7)·tan(pitch) ≈ 1.9 (default tilt
    // 0.25 rad) … 5.3 (35°) tiles PAST the seam: the whole 1-tile gradient band lies in
    // the cover's parallax shadow and the screen shows cover-black → hard edge → fully
    // lit wall. The gradient only ever showed on near-front surfaces (solid tops at
    // 14.5, shadow 0.03·tan ≈ 0.01 tile). The UV law is NOT at fault: a lattice-line
    // fragment samples the black tile's texel center exactly (= native per-corner
    // bilerp, FUN_004cc040). FIX: the cover is the stand-in for the tile mesh the
    // native skipped, so it sits where that mesh's surface would be — the tile's own
    // fill depth (nibble·0.5+7+CoverLift). Solid rock (nibble 15) keeps the front plane
    // 14.53 bit-for-bit; open/partial tiles drop to the cave back, coplanar with the
    // patches/terrain carrying the fade (the native's own carrier position for open
    // tiles: FUN_004ca080 emits the mat-21/22 backdrop as per-tile template geometry at
    // the tile's own depths, +tileX/+tileY, fog-shaded per vertex — NOT a front plane).
    // Runs merge only across equal fill; depth steps between adjacent covered/patched
    // tiles are closed by SKIRTS (vertical quads in the gradient submesh, so their
    // alpha law matches the quads they connect: opaque in the black interior, graded
    // on the boundary ring). Known cost: an alive particle drifting through a black
    // open tile's airspace is no longer occluded (entities/particles are already
    // visibility-gated at spawn — native front covers hid this edge case);
    // • GRADIENT PATCHES over MESHLESS tiles ("hard rectangular fog edges
    // in caves"): the black→revealed gradient is drawn by the terrain shader (fow.g
    // bilinear), so it needs a terrain surface. Load-time block-59 cave air / oob blocks /
    // beyond-bbox tiles render NOTHING in the port — when such a tile hits the black
    // boundary (mixed corners: cover retreats, tile not yet fully lit) the covers/walls of
    // the all-black tiles beyond used to show through as razor-edged rectangles. The
    // NATIVE closes open tiles with its mat-21 "cave backdrop" plane, alpha-faded 21→22 at
    // fog borders (per-vertex alpha = bias+0x100 ≡ the decoded black indicator —
    // 02-rendering.md §3 byte+5, §4c); the port has no backdrop, so the overlay emits a
    // black quad at the tile's fill depth with shader alpha = 1−fow.g (Diggles/FowGradient
    // samples the same fog texture ⇒ the gradient is bit-matched to the neighbouring
    // terrain's darkening and reaches exactly 1 on the black lattice lines the covers/
    // walls sit on). Host passes rendersAt = DiggableTerrain.RendersTerrain (the exact
    // BuildTileBuckets skip law); null (tests/legacy) = everything renders = no patches;
    // • PATCHES FOLLOW THE DISPLAY FIELD (— the
    // per-tile stair-step dissolve at cave fog edges). ROOT CAUSE: the presenter smooths
    // the DISPLAYED fog over SmoothSeconds while the sim bytes flip instantly, and the
    // patch set used to follow the RAW bytes — a meshless cave tile lost its patch the
    // frame its last corner byte flipped, while the display (= the patch's shader alpha,
    // 1−fow.g of the SAME texture) was still mid-chase: a near-opaque black quad vanished
    // in one frame and the void/lit content behind popped in at full contrast, tile by
    // tile, at the raw reveal front (2.5 Hz) — the stair-step. Covers/walls have NO such
    // window: AllBlack fails on the FIRST corner flip, and a byte-0 corner's display is
    // exactly 0, so a dropping cover/vanishing wall always exposes an all-black surface
    // (display starts chasing the same frame from 0) — raw-driven is already seamless
    // there, and stays faithful to the native cover law. Fix: with a presenter wired,
    // a meshless tile keeps its patch until ALL 4 corner DISPLAY values reach 255 — the
    // bilinear shader alpha is then identically 0 over the quad, so removal is invisible
    // BY CONSTRUCTION; geometry and shading follow the same smoothed field. The presenter
    // raises DisplayChanged rects while texels chase, re-dirtying the affected chunks
    // (bounded: the chase set is the reveal neighbourhood, the window is SmoothSeconds).
    // Headless / legacy (no presenter): raw AnyBlackCorner ring, the old law.
    // REJECTED: a front-plane gradient "skirt" on the
    // ring — alpha quads at 14.5 parallax-float over the lit cave floor at 7 under the
    // orbit tilt and read as detached dark rectangles; the fade carrier must live at the
    // tile fill depth, where it darkens content at its own screen position;
    // • wall bottom: the native drops a −14 curtain (DAT_007377B8) behind fully-dug tiles;
    // the port's dug floor renders at render-z 7 (DiggableTerrain.GetHmap nibble 0 →
    // 0·0.5+7), so 7.0 is the deepest wall bottom here;
    // • walls are inset 0.005 into the black tile: the port still renders the black tile's
    // own cliff faces (shader-blackened) exactly on the edge plane — the native did not,
    // so it could emit at the exact edge without z-fighting;
    // • the native also skips walls whose explored neighbor is the default-solid-earth cell
    // (004c6b20:41-67); the port instead skips zero-height walls (bottom reaching the
    // front plane — a full-height solid neighborhood hides the wall entirely), which is
    // the same visual result without a cellId model;
    // • WORLD-EDGE PIN (— the raw void past the last tiles showed
    // when a reveal reached the map edge): tiles the renderer can never draw read as
    // PERMANENT black in the render lane — covers/walls/patches here plus the fog texture
    // via FogOfWarPresenter.SetPinnedBlack — regardless of their sim fog bytes. See
    // PinnedBlack below for the two-part law (native RenderScape inset clip
    // 004c4de0:569-578 + the default-solid-earth stand-in) and the fow-begin sky gate.
    //
    // Structure mirrors DigMarkOverlay (persistent meshes, dirty rebuild, Pump so EditMode
    // tests can drive it), chunked 32×32 like TerrainChunkGrid so a reveal/fade only rebuilds
    // its own chunks. Subscribes to FogOfWarState.RegionDirty (the port of the native
    // dirty-rect masks 0x50/0x10). Hidden entirely while fogActive == false (show_fogofwar 0
    // / -nofow) — the underlying fog bytes keep evolving. Cutscenes do NOT hide the volume:
    // the native runs fog through sequences (confirmed the reveal-in-cutscene
    // extension was wrong); story scripts open their stages via remove_black_fog/FogRemover.
    [DisallowMultipleComponent]
    public sealed class FogOfWarOverlay : MonoBehaviour
    {
        public const float FrontPlaneY = 14.5f;   // native front plane z (0x41680000)
        // full-height solid tiles' TOP faces sit exactly at 14.5 too
        // (nibble 15 → 15·0.5+7) — the native never meshed fully-black tiles so its covers
        // had nothing to fight; the port does mesh them → coplanar z-fight (, lit
        // terrain winning pixels). Lift the black volume just above the terrain plane.
        public const float CoverLift = 0.03f;
        public const int ChunkTiles = 32;         // TerrainChunkGrid parity
        const float RectGrow = 0.01f;             // cover rect expansion (DAT_00736FE0)
        const float WallInset = 0.005f;           // port-only anti-z-fight inset (see header)
        const float SkirtMinStep = 0.1f;          // > CoverLift so cover(+0.03)↔patch pairs at
                                                  // the same fill never spawn a sliver skirt

        FogOfWarState? _fog;
        Func<int, int, int>? _heightAt;           // tile fill nibble 0..15 (DiggableTerrain.GetHmap; OOB = 15)
        Func<int, int, bool>? _rendersAt;         // tile emits terrain geometry (DiggableTerrain.RendersTerrain);
                                                  // null = everything renders (no gradient patches needed)
        FogOfWarPresenter? _presenter;            // smoothed DISPLAY field driving patch persistence
                                                  // (see PORT NOTES; null headless = raw ring law)

        GameObject? _holder;
        Material? _mat;
        Material? _patchMat;
        int _cw, _ch;
        Mesh?[] _meshes = Array.Empty<Mesh?>();
        GameObject?[] _chunkGos = Array.Empty<GameObject?>();
        bool[] _dirty = Array.Empty<bool>();
        bool _anyDirty;

        readonly List<Vector3> _vtx = new();
        readonly List<int> _tris = new();         // submesh 0: opaque black (covers + walls)
        readonly List<int> _trisPatch = new();    // submesh 1: alpha gradient patches (meshless tiles)

        public int ChunksX => _cw;                // test/debug access
        public int ChunksY => _ch;
        public Mesh? ChunkMesh(int cx, int cy) =>
            (uint)cx < (uint)_cw && (uint)cy < (uint)_ch ? _meshes[cy * _cw + cx] : null;

        public void Init(FogOfWarState fog, Func<int, int, int> heightAt,
                         Func<int, int, bool>? rendersAt = null,
                         FogOfWarPresenter? presenter = null)
        {
            if (_fog != null) _fog.RegionDirty -= OnRegionDirty;
            if (_presenter != null) _presenter.DisplayChanged -= OnDisplayChanged;
            if (_presenter != null && !ReferenceEquals(_presenter, presenter))
                _presenter.SetPinnedBlack(null);   // old presenter must not call into stale state
            _fog = fog;
            _heightAt = heightAt;
            _rendersAt = rendersAt;
            _presenter = presenter;
            fog.RegionDirty += OnRegionDirty;
            if (presenter != null)
            {
                presenter.DisplayChanged += OnDisplayChanged;
                // World-edge law: the fog texture must agree with the pinned covers, so the
                // terrain shader/patch alpha grade INTO the pinned seam instead of lighting
                // terrain beside a cover that will never drop.
                presenter.SetPinnedBlack(PinnedBlack);
            }
            _cw = (fog.Width + ChunkTiles - 1) / ChunkTiles;
            _ch = (fog.Height + ChunkTiles - 1) / ChunkTiles;
            DestroyChunks();                       // re-Init over an old world drops old meshes
            _meshes = new Mesh?[_cw * _ch];
            _chunkGos = new GameObject?[_cw * _ch];
            _dirty = new bool[_cw * _ch];
            EnsureHolder();
            _holder!.SetActive(true);
            for (int i = 0; i < _dirty.Length; i++) _dirty[i] = true;
            _anyDirty = true;
            Pump();   // build now (don't wait a frame) — DigMarkOverlay.Init parity
        }

        // Un-wire (menu / fogless world on the same root): hide the volume, keep the data.
        public void Clear()
        {
            if (_fog != null) _fog.RegionDirty -= OnRegionDirty;
            if (_presenter != null)
            {
                _presenter.DisplayChanged -= OnDisplayChanged;
                _presenter.SetPinnedBlack(null);   // predicate closes over _fog — un-pin first
            }
            _fog = null;
            _presenter = null;
            if (_holder != null) _holder.SetActive(false);
        }

        // RegionDirty comes in tile coords (local lane). A byte at (bx,by) influences the
        // covers of tiles (bx−1..bx, by−1..by) and the walls of black tiles up to 2 lattice
        // steps away (the beyond-edge probes) — grow by 2 before chunk mapping. Also called
        // by the host when terrain heights change under existing walls (dig carves).
        public void MarkRectDirty(int l, int t, int r, int b)
        {
            if (_dirty.Length == 0) return;
            int cl = Math.Max(0, (l - 2) / ChunkTiles), ct = Math.Max(0, (t - 2) / ChunkTiles);
            int cr = Math.Min(_cw - 1, Math.Max(0, (r + 2) / ChunkTiles));
            int cb = Math.Min(_ch - 1, Math.Max(0, (b + 2) / ChunkTiles));
            for (int cy = ct; cy <= cb; cy++)
                for (int cx = cl; cx <= cr; cx++)
                    _dirty[cy * _cw + cx] = true;
            _anyDirty = true;
        }

        void OnRegionDirty(int l, int t, int r, int b) => MarkRectDirty(l, t, r, b);

        // Presenter display texels moved (the SmoothSeconds chase) — the patch set follows the
        // display field (see PORT NOTES), so the affected chunks must re-emit this frame.
        void OnDisplayChanged(int l, int t, int r, int b) => MarkRectDirty(l, t, r, b);

        void OnDestroy()
        {
            if (_fog != null) _fog.RegionDirty -= OnRegionDirty;
            if (_presenter != null)
            {
                _presenter.DisplayChanged -= OnDisplayChanged;
                _presenter.SetPinnedBlack(null);
            }
            DestroyChunks();
            if (_mat != null) DestroySafe(_mat);
            if (_patchMat != null) DestroySafe(_patchMat);
        }

        void Update() => Pump();

        // The per-frame pump, public so EditMode tests (no player loop) can drive it.
        // While hidden the dirty set accumulates and is rebuilt lazily on re-show.
        public void Pump()
        {
            if (_fog == null || _holder == null) return;
            bool active = _fog.Enabled && _fog.ShowFog;
            if (_holder.activeSelf != active) _holder.SetActive(active);
            if (!active || !_anyDirty) return;
            _anyDirty = false;
            for (int i = 0; i < _dirty.Length; i++)
                if (_dirty[i]) { _dirty[i] = false; RebuildChunk(i % _cw, i / _cw); }
        }

        void EnsureHolder()
        {
            if (_holder != null) return;
            _holder = new GameObject("FowBlack");
            _holder.transform.SetParent(transform, false);   // terrain-local = the native render frame
            var shader = Shader.Find("Diggles/FowBlack");
            // Headless EditMode may lack the pipeline; mesh data still builds (DigMarkOverlay parity).
            if (shader != null) _mat = new Material(shader) { name = "FowBlackMat" };
            var patchShader = Shader.Find("Diggles/FowGradient");
            if (patchShader != null) _patchMat = new Material(patchShader) { name = "FowGradientMat" };
        }

        // ---- per-chunk rebuild -------------------------------------------------------------

        void RebuildChunk(int cx, int cy)
        {
            var fog = _fog!;
            int x0 = cx * ChunkTiles, y0 = cy * ChunkTiles;
            int x1 = Math.Min(x0 + ChunkTiles, fog.Width), y1 = Math.Min(y0 + ChunkTiles, fog.Height);
            int p = fog.LocalPlayer;
            int wallMinY = fog.FowBegin(p) - 1;   // native sky-line early-out (004c6b20:30-38)
            _vtx.Clear(); _tris.Clear(); _trisPatch.Clear();

            for (int ty = y0; ty < y1; ty++)
            {
                int run = -1, runNib = 0;
                for (int tx = x0; tx <= x1; tx++)
                {
                    bool black = tx < x1 && AllBlack(tx, ty, p);
                    int nib = black ? HeightAt(tx, ty) : -1;
                    // Runs merge only across EQUAL fill: each run's cover sits at its own
                    // fill surface (PORT NOTES — the parallax-shadow fix).
                    if (run >= 0 && (!black || nib != runNib))
                    {
                        EmitCover(run, tx - 1, ty, runNib);   // merged horizontal run (004c4de0:580-610)
                        run = -1;
                    }
                    if (black)
                    {
                        if (run < 0) { run = tx; runNib = nib; }
                        if (ty >= wallMinY) EmitWalls(tx, ty, p);
                    }
                    else if (tx < x1 && _rendersAt != null && !_rendersAt(tx, ty)
                             && NeedsPatch(tx, ty, p))
                    {
                        // Meshless tile on the black boundary OR still fading in: give the
                        // shader gradient a surface until the smoothed display field catches
                        // up — the patch may leave only when its alpha is identically 0 (see
                        // the PORT NOTES header — the native mat-21→22 backdrop fade).
                        EmitPatch(tx, ty);
                    }
                    // Close depth steps between adjacent covered/patched tiles (each shared
                    // edge owned by its W/N tile — including across chunk borders).
                    if (tx < x1) EmitStepSkirts(tx, ty, p);
                }
            }
            UploadChunk(cx, cy);
        }

        // ---- world-edge render pin (permanent black in the RENDER lane) --------------------
        // A pinned tile reads byte 0 for every render consumer (covers, walls, patches, and —
        // via FogOfWarPresenter.SetPinnedBlack — the fog texture) regardless of its sim byte:
        // (a) the native render border band: RenderScape clips its per-tile pass to the world
        // rect INSET (minX+2 < tx < maxX−2, same for y — 004c4de0:569-578, record-less
        // tiles skipped identically:579), so the outer band never draws terrain NOR
        // covers and reads as permanent blackness even after reveals touch it (the reveal
        // side has no clamp, 0057e120:137-140 — FogOfWarState.IsRenderedTile);
        // (b) PORT stand-in: a SOLID tile the port renderer cannot draw (RendersTerrain
        // false — beyond the render bbox / invalid block — with fill ≥ 14, the Solid
        // law). The native draws such tiles as the default-solid-earth cell (unpopulated
        // world tiles all share it), so a reveal that touches them shows lit rock; the
        // port has no mesh there, and without the pin the reveal dropped their covers and
        // exposed the raw void past the last meshed tiles (world-edge fog
        //. Black is the closure until digging/growth actually stamps terrain
        // (the bbox then extends, RendersTerrain flips, and the pin lifts by itself).
        // Open cave air (fill < 14) is NOT pinned — it keeps the r5 gradient-patch law.
        // Above the fow-begin sky line nothing is pinned: the native's undrawn band shows the
        // sky BACKDROP there, not blackness — the port equivalent is simply drawing nothing.
        bool PinnedBlack(int tx, int ty)
        {
            var fog = _fog;
            if (fog == null) return false;
            if (ty < fog.FowBegin(fog.LocalPlayer)) return false;      // sky: backdrop, not black
            if (!fog.IsRenderedTile(tx, ty)) return true;              // (a) native inset band
            return _rendersAt != null && !_rendersAt(tx, ty) && HeightAt(tx, ty) >= 14;   // (b)
        }

        // The RENDER-lane fog byte (pin-aware). Sim bytes/queries stay untouched.
        byte RenderFogAt(int tx, int ty, int p)
            => PinnedBlack(tx, ty) ? (byte)0 : _fog!.FogAt(tx, ty, p);

        // All-black sentinel: the tile's 4 fog lattice corners — the bytes of tiles
        // (x,y),(x+1,y),(x,y+1),(x+1,y+1) (FUN_004ca830 corner sampling; FogAt OOB → 0,
        // matching the native's solid unexplored world border). Pin-aware (world-edge law).
        bool AllBlack(int tx, int ty, int p) =>
            RenderFogAt(tx, ty, p) == 0 && RenderFogAt(tx + 1, ty, p) == 0 &&
            RenderFogAt(tx, ty + 1, p) == 0 && RenderFogAt(tx + 1, ty + 1, p) == 0;

        bool AnyBlackCorner(int tx, int ty, int p) =>
            RenderFogAt(tx, ty, p) == 0 || RenderFogAt(tx + 1, ty, p) == 0 ||
            RenderFogAt(tx, ty + 1, p) == 0 || RenderFogAt(tx + 1, ty + 1, p) == 0;

        // Patch persistence law (PORT NOTES): the permanent black-boundary ring (raw byte-0
        // corner — its display is pinned at 0) PLUS, with a presenter wired, any tile whose
        // corner DISPLAY has not yet reached 255. The patch shader's alpha = 1−fow.g of the
        // display texture, bilinear over these same 4 corners — so a patch is dropped exactly
        // when (and only when) its alpha is 0 everywhere on the quad: removal is invisible.
        bool NeedsPatch(int tx, int ty, int p)
        {
            if (AnyBlackCorner(tx, ty, p)) return true;   // raw ring (headless/legacy law)
            var pr = _presenter;
            if (pr == null) return false;
            return pr.DisplayAt(tx, ty).g < byte.MaxValue
                || pr.DisplayAt(tx + 1, ty).g < byte.MaxValue
                || pr.DisplayAt(tx, ty + 1).g < byte.MaxValue
                || pr.DisplayAt(tx + 1, ty + 1).g < byte.MaxValue;
        }

        // Front cover for the equal-fill tile run [xa..xb] × row ty (FUN_004cca20 run-merge).
        // Native frame → terrain-local: tile (tx,ty) spans local x∈[tx,tx+1], z∈[−(ty+1),−ty]
        // (BdbPmpWorldBuilder.EmitVertexW:1088). PORT: the cover sits at the run's OWN fill
        // surface (nibble·0.5+7+CoverLift; nibble 15 ≡ the native front plane 14.5) — see
        // PORT NOTES, the parallax-shadow fix: the cover stands in for the tile mesh the
        // native skipped, so it must sit where that mesh's surface would be.
        void EmitCover(int xa, int xb, int ty, int nib)
        {
            float y = nib * 0.5f + 7f + CoverLift;
            float lx0 = xa - RectGrow, lx1 = xb + 1 + RectGrow;
            float lz0 = -(ty + 1) - RectGrow, lz1 = -ty + RectGrow;
            AddQuad(_tris,
                    new Vector3(lx0, y, lz0), new Vector3(lx1, y, lz0),
                    new Vector3(lx1, y, lz1), new Vector3(lx0, y, lz1));
        }

        // Gradient patch for a meshless boundary tile: a single quad at the tile's fill depth
        // (nibble·0.5+7 — where the native's mat-21 cave-backdrop plane sits for a fully-open
        // cell, z≈7; a beyond-bbox solid tile lands at 14.5 = the missing rock top). The
        // Diggles/FowGradient material paints it black with alpha = 1−fow.g, so the fade is
        // the same bilinear corner interpolation the terrain shader applies — opaque exactly
        // on the black lattice lines shared with the covers/walls, fully transparent once the
        // tile's corners are lit.
        void EmitPatch(int tx, int ty)
        {
            float y = HeightAt(tx, ty) * 0.5f + 7f;
            float lz0 = -(ty + 1), lz1 = -ty;
            AddQuad(_trisPatch,
                    new Vector3(tx, y, lz0), new Vector3(tx + 1, y, lz0),
                    new Vector3(tx + 1, y, lz1), new Vector3(tx, y, lz1));
        }

        // Side walls of an all-black tile (FUN_004c6b20): each of the 4 edges probes the two
        // fog lattice cells BEYOND it (W edge at lattice x=tx probes column tx−1 — the RE's
        // "(x−1,y) and (x−1,y+1)"; the E edge at lattice tx+1 probes tx+2, symmetric for N/S)
        // and emits only toward explored (byte ≠ 0) space.
        void EmitWalls(int tx, int ty, int p)
        {
            TrySide(tx, ty, tx - 1, ty, tx - 1, ty + 1, p, vertical: true, edge: tx, insetSign: +1);
            TrySide(tx, ty, tx + 2, ty, tx + 2, ty + 1, p, vertical: true, edge: tx + 1, insetSign: -1);
            TrySide(tx, ty, tx, ty - 1, tx + 1, ty - 1, p, vertical: false, edge: ty, insetSign: +1);
            TrySide(tx, ty, tx, ty + 2, tx + 1, ty + 2, p, vertical: false, edge: ty + 1, insetSign: -1);
        }

        void TrySide(int tx, int ty, int nx0, int ny0, int nx1, int ny1, int p,
                     bool vertical, int edge, int insetSign)
        {
            if (RenderFogAt(nx0, ny0, p) == 0 && RenderFogAt(nx1, ny1, p) == 0) return;   // faces black: hidden
            // Wall top = the black tile's own cover plane (fill surface + lift); wall bottom =
            // the deepest open slab top along the edge: min fill nibble over the black tile +
            // the two beyond probes, → render z = nibble·0.5+7 (native 004c6b20:177; a
            // fully-dug nibble 0 → 7.0 = the port's dug-floor plane, cf. the native −14 case).
            // A wall therefore exists only where the black tile stands PROUD of the deepest
            // neighbouring surface (black rock beside a revealed pit); flush open-to-open
            // seams (deep-cave interiors) emit nothing — their black closure is the depth
            // cover itself, coplanar with the gradient carrier (PORT NOTES).
            float topBase = HeightAt(tx, ty) * 0.5f + 7f;
            int nib = Math.Min(HeightAt(tx, ty), Math.Min(HeightAt(nx0, ny0), HeightAt(nx1, ny1)));
            float bottom = nib * 0.5f + 7f;
            if (bottom >= topBase - 0.01f) return;   // no exposed cross-section: zero-area wall
            float top = topBase + CoverLift;

            if (vertical)
            {
                // W/E edge at lattice x = edge, inset into the black tile; spans the tile row.
                float lx = edge + insetSign * WallInset;
                float lz0 = -(ty + 1), lz1 = -ty;
                AddQuad(_tris,
                        new Vector3(lx, top, lz0), new Vector3(lx, top, lz1),
                        new Vector3(lx, bottom, lz1), new Vector3(lx, bottom, lz0));
            }
            else
            {
                // N/S edge at native y = edge → local z = −edge, inset into the tile.
                float lz = -(edge + insetSign * WallInset);
                float lx0 = tx, lx1 = tx + 1;
                AddQuad(_tris,
                        new Vector3(lx0, top, lz), new Vector3(lx1, top, lz),
                        new Vector3(lx1, bottom, lz), new Vector3(lx0, bottom, lz));
            }
        }

        // ---- depth-step skirts ---------------------------------------------------------------

        // The depth quad the overlay lays over a tile, if any: an opaque cover at the tile's
        // own fill surface (AllBlack) or an alpha gradient patch at fill depth (meshless
        // boundary ring). OOB tiles read as AllBlack at nibble 15 (FogAt OOB → 0, HeightAt
        // OOB → 15), which seals the map border with a skirt up to the front plane.
        bool DepthQuadAt(int tx, int ty, int p, out float y, out bool meshless)
        {
            meshless = _rendersAt != null && !_rendersAt(tx, ty);
            if (AllBlack(tx, ty, p)) { y = HeightAt(tx, ty) * 0.5f + 7f + CoverLift; return true; }
            if (meshless && NeedsPatch(tx, ty, p)) { y = HeightAt(tx, ty) * 0.5f + 7f; return true; }
            y = 0f;
            return false;
        }

        // Vertical connector quads where two adjacent depth quads sit at different fills and
        // no terrain closes the step (at least one side meshless — between two MESHED tiles
        // the terrain's own cliff faces render, shader-blackened, exactly there). Emitted into
        // the GRADIENT submesh: its alpha (1−fow.g) is identically 1 between two AllBlack
        // tiles (matching the opaque covers it connects) and graded on the boundary ring
        // (matching the patches) — the skirt can never out-darken or out-live its quads.
        void EmitStepSkirts(int tx, int ty, int p)
        {
            if (!DepthQuadAt(tx, ty, p, out float y0, out bool m0)) return;
            if (DepthQuadAt(tx + 1, ty, p, out float yE, out bool mE) && (m0 || mE)
                && Math.Abs(yE - y0) > SkirtMinStep)
            {
                float lo = Math.Min(y0, yE), hi = Math.Max(y0, yE);
                float lx = tx + 1, lz0 = -(ty + 1), lz1 = -ty;
                AddQuad(_trisPatch,
                        new Vector3(lx, hi, lz0), new Vector3(lx, hi, lz1),
                        new Vector3(lx, lo, lz1), new Vector3(lx, lo, lz0));
            }
            if (DepthQuadAt(tx, ty + 1, p, out float yS, out bool mS) && (m0 || mS)
                && Math.Abs(yS - y0) > SkirtMinStep)
            {
                float lo = Math.Min(y0, yS), hi = Math.Max(y0, yS);
                float lz = -(ty + 1), lx0 = tx, lx1 = tx + 1;
                AddQuad(_trisPatch,
                        new Vector3(lx0, hi, lz), new Vector3(lx1, hi, lz),
                        new Vector3(lx1, lo, lz), new Vector3(lx0, lo, lz));
            }
        }

        int HeightAt(int x, int y) => _heightAt?.Invoke(x, y) ?? 15;

        void AddQuad(List<int> tris, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int i0 = _vtx.Count;
            _vtx.Add(a); _vtx.Add(b); _vtx.Add(c); _vtx.Add(d);
            tris.Add(i0); tris.Add(i0 + 1); tris.Add(i0 + 2);
            tris.Add(i0); tris.Add(i0 + 2); tris.Add(i0 + 3);
        }

        void UploadChunk(int cx, int cy)
        {
            int i = cy * _cw + cx;
            if (_vtx.Count == 0)
            {
                _meshes[i]?.Clear();
                return;
            }
            var mesh = _meshes[i];
            if (mesh == null)
            {
                mesh = new Mesh { name = $"FowChunk_{cx}_{cy}" };
                mesh.MarkDynamic();
                _meshes[i] = mesh;
                var go = new GameObject($"FowChunk_{cx}_{cy}");
                go.transform.SetParent(_holder!.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.shadowCastingMode = ShadowCastingMode.Off;   // the black volume casts no shadows
                mr.receiveShadows = false;
                mr.lightProbeUsage = LightProbeUsage.Off;
                mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
                if (_mat != null)
                    mr.sharedMaterials = new[] { _mat, _patchMat != null ? _patchMat : _mat };
                _chunkGos[i] = go;
            }
            mesh.Clear();
            mesh.indexFormat = _vtx.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(_vtx);
            mesh.subMeshCount = 2;                       // 0 = opaque covers/walls, 1 = gradient patches
            mesh.SetTriangles(_tris, 0, false);
            mesh.SetTriangles(_trisPatch, 1, false);
            mesh.RecalculateBounds();
        }

        void DestroyChunks()
        {
            for (int i = 0; i < _meshes.Length; i++)
            {
                if (_meshes[i] != null) DestroySafe(_meshes[i]!);
                if (_chunkGos[i] != null) DestroySafe(_chunkGos[i]!);
            }
            _meshes = Array.Empty<Mesh?>();
            _chunkGos = Array.Empty<GameObject?>();
            _dirty = Array.Empty<bool>();
        }

        static void DestroySafe(UnityEngine.Object o)
        {
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }
    }
}
