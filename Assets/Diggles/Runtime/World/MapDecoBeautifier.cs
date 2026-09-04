#nullable enable

namespace Diggles.Runtime.World
{
    using System;
    using System.Collections.Generic;
    using UnityEngine;

    // CMapBeautifier — the DURING-DIG template-deco subsystem (the sibling of the cave-skin
    // completion swap, which lives in DigglesTclEngine.CaveSkinDigCheck). Ground truth
    // (decompile, full law in
    // Docs/DIGGING_MECHANIC.md §9.B):
    //
    // • ScanAtPos (FUN_0058b130) runs from DigApply (FUN_006347a0:112-118) BEFORE each carve,
    // local view only, with the digger-owner's dig layer. It matches the local field of
    // "actively dug" nibbles — (n & 8) == 0 && (n & 0xF) != 0, i.e. the MODE-1 DUMMY marks
    // (value 3) that both the STANDARD tunnel order (painter FUN_005672d0) and the StoryMgr
    // worldgen growth paint — against the LEVEL-GEN TEMPLATE registry (beautifier+0x1a4,
    // stride 0x54 = the same registry the lg_* commands populate; port = LevelGenerator's
    // pieces). A matching template registers a 4-aligned beam rect (beautifier+0x10, stride
    // 0x18) and queues the template's authored deco list — the `new <Class>` spawns parsed
    // from data/Templates/<name>.tcl (Balken/Holzstuetzen supports, Schatztonne treasure,
    // moss/crystal props, critters) — as pending entries (beautifier+0x8, stride 0x8c) via
    // FUN_0058c920, positioned at rect origin + the authored offsets.
    // • The template SHAPE grid (FUN_00581b40 → FUN_00599fc0, cache beautifier+0x18) is
    // rasterized from the template's PMP: cell = 1 ⇔ the authored block's aux 8×8-heightmap
    // byte 0x1b (sub-cell 3,3) has nibble 11 — the TUNNEL-FLOOR height (verified on real
    // BDB2 + gng_001.pmp: rows 1-2 of the 4×4 = the standard 2-wide corridor).
    // • Eligible templates (FUN_0058a8b0): not "_hol_" (+0x41), not a split/multi header
    // (+0x40), a non-empty deco set (+0x48 ≥ 0), zone (+0x4c, from the name prefix
    // urw_/swf_/kris_/lava_ = 0..3, FUN_00583080 + exe.rdata 0x72a898/0x72a8a0) equal to
    // the dig row's band (FUN_005fcdf0, < 0 → 0), and W*H ≤ 16 (the 4×4 gng tunnels).
    // Identical-shape templates share one placement scan (the shape-pointer dedupe);
    // candidates recently chosen are dropped while >1 remain (the ≤4-entry recent ring);
    // the winner is rand % candidates.
    // • The pending tick (FUN_0058d370, called once per SrvDoThink — port: the 10 Hz think)
    // examines ONE pending entry per call (frame % count): once no dig nibble remains in the
    // entry's bbox ±2 area (bbox-min/max corners rounded,:65-69 — the local dig is FINISHED;
    // each completed carve clears its nibbles) it evaluates the 8 bbox-corner floor probes
    // (block aux hmap at the probe sub-cell, ×8.0 = DAT_0073752c) and spawns iff
    // 3 ≤ (#probes with hmap ≤ round(z)) ≤ 7 AND every probe's hmap > 10 (the tunnel band:
    // floor 11+; a deeper cave carve kills the spawn). The entry is removed after ONE
    // evaluation either way; a dirty area keeps it pending. Beams appear progressively
    // BEHIND the diggers.
    // • A random existing beam rect is retired per ScanAtPos call once its ±4 area holds no
    // dig nibbles (FUN_0058b130:300-388) — the rect GC that re-opens the area for matching.
    // • REMOVAL (corrected; the earlier "native never removes tunnel deco" note was
    // WRONG): inside the BEAUTIFIER the only remover is the cave-skin completion pass
    // (FUN_0058ba00, +0xcc2 classes only) — but the ENGINE removes dummy deco per-carve via
    // the MapValidator DUMMYDES phase (FUN_005a46d0 label 0x10 → FUN_005eb7b0): every carve
    // dirty rect is tested against live objects with (obj+0x3d0 & 0x12) == 0x10 (physic
    // flag on, not dying) and obj+0x278 == −1 (unlinked); an objtype-dummy class
    // (class+0x54 == 0 → verdict class+0x68 = 3) whose ANIM BBOX (obj pos ± frame bbox,
    // FUN_004059a0+0x44..0x54) overlaps the rect is destroyed through FUN_005eae80 —
    // release render/light handles, then the deferred-destroy list guarded by obj+0x404.
    // Enrolment: the authored template init runs `set_physic $objhandle true` on every
    // deco piece (Templates/urw_gng_001_a.tcl:8-31) → FUN_0067fba0 sets the 0x10 flag +
    // creates the dummy record (FUN_005eb470); cave-skin classes are class_physic 0 and
    // never enrolled. So in the ORIGINAL, tunnel boards/beams/moss DO vanish one by one as
    // a cave carve front reaches them. Port equivalents: CaveSkinDigCheck purges
    // MapDecoSpawned entities inside a completed cave rect, and the per-carve sweep
    // (DigglesTclEngine.MapDecoCarveSweep, wired to DiggableTerrain.OnCarveNode) applies
    // the literal bbox law — piece pos ± FowExt/DecoExt vs the carve window — plus a
    // port-only cave-band guard (floating beams; back-wall deco;
    // boards surviving a front-block carve).
    //
    // Port deviations (documented, cosmetic):
    // • The native probe box = the 8 corners of the class's 3DB anim-frame bounding box,
    // rotated by the entry's rotation (FUN_0058c920:195-310). The port has no per-class
    // 3DB bbox index, so it splits the two consumers of that box:
    // – the AREA-CLEAR gate (FUN_0058d370:62-101 — bbox-min/max ±2, "is the local dig
    // finished") uses pos ± DecoExt as the box (piece-local, spawn tracks the DIG
    // FRONT). History: v1's ±0.5 box read "clear" mid-dig → supports popped in beside
    // the digger; v2's whole-BEAM-RECT box waited for the entire
    // section →;
    // – the 8 FLOOR PROBES keep the fixed half-extent box around the deco position
    // (±ProbeHalfXY in x/y, [−ProbeDepthLow, +ProbeDepthHigh] in z) — same acceptance
    // behavior over tunnel floors (top corners pass, bottom corners fail ⇒ count 4) and
    // same rejection over cave-band floors (hmap ≤ 10).
    // • Model/anim variation picks (class+0x10/+0x18 lists) ride the port's normal `new`
    // spawn path instead of the native's direct object construction.
    // • The native gates deco-list entries at PARSE time on class-exists + class+0xca0 == 0
    // (a class-level {4 ints + Tcl obj} block at +0xc90 whose setter is not in the decompile
    // dump; a no-op for every class the shipped tunnel templates spawn). The port gates on
    // class-exists at SPAWN time — same observable outcome once classinit has run.
    // • detail-culled entries (entry detail vs the object-detail setting) natively defer into
    // the per-block deferred list (FUN_005d7450); the port runs at max detail — all spawn.
    public sealed class MapDecoBeautifier
    {
        // beautifier+0x10 rect entry {x0,y0,x1,y1,templateIdx,layer} (stride 0x18).
        public struct BeamRect
        {
            public int X0, Y0, X1, Y1;
            public string Template;
            public int Layer;
        }

        // beautifier+0x8 pending entry (stride 0x8c): class + world pos/rot + detail + layer +
        // the 8 probe corners (port: an axis-aligned box, see the deviation note above).
        public sealed class PendingDeco
        {
            public string ClassName = "";
            public string Template = "";
            public int Detail;                    // entry+0x1c (loading_objdetail bracket)
            public int Layer;                     // entry+0x88 (digger-owner dig layer)
            public float Px, Py, Pz;              // entry+0x04.. (rect origin + authored offset)
            public float Rx, Ry, Rz;              // entry+0x10..
            public float Bx0, By0, Bx1, By1;      // probe box (entry+0x20.. corners, approximated)
            public float BzLow, BzHigh;
        }

        // Port probe-box half extents (see the deviation note).
        public const float ProbeHalfXY = 0.5f;
        public const float ProbeDepthLow = 5f;
        public const float ProbeDepthHigh = 0.5f;

        // Default piece half-extent (native = the class's 3DB anim-frame bbox, which the port
        // has no index for): stands in for the bbox in the AREA-CLEAR gate (Tick) and the
        // per-carve kill law (DigglesTclEngine.MapDecoCarveSweep); live entities prefer their
        // renderer-union extents (DigglesTclEntity.FowExtX/Y) once computed. 1.5 tiles covers
        // boards/supports that span a 2-wide corridor and protrude from their wall face.
        public const float DecoExt = 1.5f;

        public LevelGenerator? Templates;         // the +0x1a4 registry (shared with lg_*)
        public DiggableTerrain? Terrain;
        public BdbModel? Bdb;                     // shape rasterization (aux 8×8 hmap byte 0x1b)
        public System.Random Rng = new();         // the shared game RNG (ctx.StoryRng)
        public Func<int, int>? ZoneOfY;           // dig y → zone band (FUN_005fcdf0, < 0 → 0)
        public Action<PendingDeco>? SpawnEntity;  // engine spawn callback (CmdNew path)
        // Test seam: floor probe override. Default = the faithful get_hmap chain
        // (NavWorld sub-cell hmap, falling back to the tile height on headless fixtures).
        public Func<float, float, int>? HmapSampler;

        readonly List<BeamRect> _rects = new();
        readonly List<PendingDeco> _pending = new();
        readonly List<string> _recent = new();    // the ≤4-entry recently-matched ring
        // Shape cache by PMP base (beautifier+0x18 analogue; zone variants share one PMP).
        readonly Dictionary<string, int[]?> _shapes = new(StringComparer.OrdinalIgnoreCase);
        int _frame;                                // DAT_00772400+0x6c analogue (advanced by Tick)

        public IReadOnlyList<BeamRect> Rects => _rects;
        public IReadOnlyList<PendingDeco> Pending => _pending;

        // Test seam: inject a synthetic shape grid for a PMP base (row-major w*h of 0/1).
        public void InjectShape(string pmpBase, int[] cells) => _shapes[pmpBase] = cells;

        // ---------------- ScanAtPos (FUN_0058b130) ----------------
        // Runs BEFORE each carve (dig_apply) at the rounded dig node, with the digger's owner
        // as the dig layer.
        public void ScanAtPos(int x, int y, int layer)
        {
            var t = Terrain;
            var lg = Templates;
            if (t == null || lg == null) return;

            // Inside an existing beam rect → full exit (native:15, skips the GC too).
            foreach (var r in _rects)
                if (r.X0 <= x && x <= r.X1 && r.Y0 <= y && y <= r.Y1)
                    return;

            // Eligibility: the node's own nibble must be "actively dug" (dummy band 1..7,
            // native:97-102 — (n & 8) == 0 && (n & 0xF) != 0).
            int nib = t.RawDigNibble(x, y, layer);
            if ((nib & 8) == 0 && (nib & 0xF) != 0)
            {
                // ±16 window clamped 4 tiles inside the map (native:81-96, exclusive upper).
                int x0 = Math.Max(4, x - 16), y0 = Math.Max(4, y - 16);
                int x1 = Math.Min(t.Width - 4, x + 16), y1 = Math.Min(t.Height - 4, y + 16);
                int gw = x1 - x0, gh = y1 - y0;
                if (gw > 0 && gh > 0)
                {
                    var grid = new int[gw * gh];
                    for (int gy = y0; gy < y1; gy++)
                        for (int gx = x0; gx < x1; gx++)
                        {
                            int n = t.RawDigNibble(gx, gy, layer);
                            grid[(gy - y0) * gw + (gx - x0)] = ((n & 8) == 0 && (n & 0xF) != 0) ? 1 : 0;
                        }
                    int zone = ZoneOfY != null ? ZoneOfY(y) : 0;
                    var m = MatchTemplates(grid, gw, gh, x - x0, y - y0, zone);
                    if (m != null)
                    {
                        var (piece, ox, oy) = m.Value;
                        // 4-aligned rect: (origin + 2) & ~3 per edge (native:268-273).
                        var rect = new BeamRect
                        {
                            X0 = (x0 + ox + 2) & ~3,
                            Y0 = (y0 + oy + 2) & ~3,
                            X1 = (x0 + ox + piece.W + 2) & ~3,
                            Y1 = (y0 + oy + piece.H + 2) & ~3,
                            Template = piece.TclName,
                            Layer = layer,
                        };
                        _rects.Add(rect);
                        QueuePending(piece, rect);   // FUN_0058c920
                    }
                }
            }

            // Rect GC (native:300-388): one pseudo-random rect per call; retire it once its
            // ±4 neighborhood holds no dig nibble at the rect's layer.
            if (_rects.Count > 0)
            {
                int ri = ((_frame % _rects.Count) + _rects.Count) % _rects.Count;
                var r = _rects[ri];
                int sx0 = Math.Max(4, r.X0 - 4), sy0 = Math.Max(4, r.Y0 - 4);
                int sx1 = Math.Min(t.Width - 4, r.X1 + 4), sy1 = Math.Min(t.Height - 4, r.Y1 + 4);
                for (int gy = sy0; gy < sy1; gy++)
                    for (int gx = sx0; gx < sx1; gx++)
                        if (t.RawDigNibble(gx, gy, r.Layer) != 0)
                            return;                   // still active → keep (native goto exit)
                int last = _rects.Count - 1;
                if (ri < last) _rects[ri] = _rects[last];   // swap-last removal (native:373-388)
                _rects.RemoveAt(last);
            }
        }

        // ---------------- the matcher (FUN_0058a8b0) ----------------
        (LevelGenerator.Piece p, int ox, int oy)? MatchTemplates(int[] grid, int gw, int gh, int relX, int relY, int zone)
        {
            var lg = Templates!;
            var cands = new List<(LevelGenerator.Piece p, int ox, int oy)>();
            // Identical-shape scan dedupe (native:19): zone variants share one PMP shape;
            // scan once per shape, replay the hit/miss for the rest.
            var seen = new Dictionary<int[], (int ox, int oy)?>(ReferenceEqualityComparer<int[]>.Instance);

            foreach (var p in lg.Registered)
            {
                if (p.Hol) continue;                    // +0x41 ("_hol_" caves never beam-match)
                if (p.Zone != zone) continue;           // +0x4c (hoisted before meta — name-derived)
                lg.EnsureMeta(p);
                if (p.Split) continue;                  // +0x40 (split/multi headers carry no deco)
                if (p.Deco.Count == 0) continue;        // +0x48 < 0 (no authored deco set)
                if (p.W <= 0 || p.H <= 0 || p.W * p.H > 16) continue;   // small tunnels only
                var shape = ShapeFor(p);
                if (shape == null || shape.Length != p.W * p.H) continue;

                if (seen.TryGetValue(shape, out var prior))
                {
                    if (prior != null) cands.Add((p, prior.Value.ox, prior.Value.oy));
                    continue;
                }

                // Placement window pre-check (native:143-148): every covering placement must
                // fit inside the grid, else the template is skipped near the window edge.
                int oxLo = relX - p.W + 1, oyLo = relY - p.H + 1;
                int oxPeak = relX + p.W - 1, oyPeak = relY + p.H - 1;
                if (oxLo < 0 || oyLo < 0 || oxPeak >= gw || oyPeak >= gh)
                {
                    seen[shape] = null;
                    continue;
                }
                int oxHi = Math.Min(gw - p.W, oxPeak);  // exclusive (native `<`)
                int oyHi = Math.Min(gh - p.H, oyPeak);
                (int ox, int oy)? hit = null;
                for (int ox = oxLo; ox < oxHi && hit == null; ox++)
                    for (int oy = oyLo; oy < oyHi; oy++)
                    {
                        bool all = true;
                        for (int c = 0; c < p.W && all; c++)
                            for (int rw = 0; rw < p.H; rw++)
                                if (shape[rw * p.W + c] != grid[(oy + rw) * gw + ox + c]) { all = false; break; }
                        if (all) { hit = (ox, oy); break; }   // first placement wins (native break)
                    }
                seen[shape] = hit;
                if (hit != null) cands.Add((p, hit.Value.ox, hit.Value.oy));
            }

            if (cands.Count == 0) return null;
            // Recent-ring dedupe (native:257-296): drop recently-chosen templates newest→oldest
            // while more than one candidate remains.
            if (cands.Count > 1)
                for (int ri = _recent.Count - 1; ri >= 0 && cands.Count > 1; ri--)
                    for (int c = 0; c < cands.Count; c++)
                        if (cands[c].p.TclName == _recent[ri])
                        {
                            cands[c] = cands[cands.Count - 1];
                            cands.RemoveAt(cands.Count - 1);
                            break;
                        }
            var pick = cands[Rng.Next(cands.Count)];
            if (_recent.Count > 3) _recent.RemoveAt(0);   // ring cap 4 (native:299-309)
            _recent.Add(pick.p.TclName);
            return pick;
        }

        // ---------------- the pending queue (FUN_0058c920) ----------------
        void QueuePending(LevelGenerator.Piece p, BeamRect rect)
        {
            foreach (var d in p.Deco)
            {
                float px = d.X + rect.X0, py = d.Y + rect.Y0, pz = d.Z;   // native:140-144
                _pending.Add(new PendingDeco
                {
                    ClassName = d.ClassName,
                    Template = p.TclName,
                    Detail = d.Detail,
                    Layer = rect.Layer,
                    Px = px, Py = py, Pz = pz,
                    Rx = d.Rx, Ry = d.Ry, Rz = d.Rz,
                    Bx0 = px - ProbeHalfXY, Bx1 = px + ProbeHalfXY,
                    By0 = py - ProbeHalfXY, By1 = py + ProbeHalfXY,
                    BzLow = pz - ProbeDepthLow, BzHigh = pz + ProbeDepthHigh,
                });
            }
        }

        // ---------------- the pending tick (FUN_0058d370) ----------------
        // ONE pending entry per call, chosen frame % count. Returns true when an entry was
        // evaluated (spawned or dropped); false = nothing to do / entry kept pending.
        public bool Tick()
        {
            _frame++;
            var t = Terrain;
            if (t == null || _pending.Count == 0) return false;
            int pi = ((_frame % _pending.Count) + _pending.Count) % _pending.Count;
            var p = _pending[pi];

            // Map-bounds gate (native:56-61): an out-of-window entry stays parked forever.
            int px = Mathf.RoundToInt(p.Px), py = Mathf.RoundToInt(p.Py);
            if (px < 16 || px >= t.Width - 16 || py < 16 || py >= t.Height - 16) return false;

            // Area-clear gate (native:62-101): ANY dig nibble in the deco's OWN bbox ±2 keeps
            // the entry pending — the local dig isn't finished (completed carves clear their
            // nibbles), and pieces mature as the DIG FRONT passes them, not when the whole
            // order ends. The port's bbox stand-in = pos ± DecoExt (no 3DB bbox index).
            // History: v1 used the ±0.5 probe box → supports popped in mid-dig beside the
            // digger; v2 over-corrected to the whole BEAM RECT ±2 → every
            // piece waited for the entire section,
            //). The piece-local ±(DecoExt+2) window is the native geometry.
            int ax0 = Mathf.RoundToInt(p.Px - DecoExt) - 2, ax1 = Mathf.RoundToInt(p.Px + DecoExt) + 2;
            int ay0 = Mathf.RoundToInt(p.Py - DecoExt) - 2, ay1 = Mathf.RoundToInt(p.Py + DecoExt) + 2;
            for (int gy = ay0; gy <= ay1; gy++)
                for (int gx = ax0; gx <= ax1; gx++)
                    if (t.RawDigNibble(Math.Max(0, Math.Min(t.Width - 1, gx)),
                                       Math.Max(0, Math.Min(t.Height - 1, gy)), p.Layer) != 0)
                        return false;

            // Fit test + spawn; the entry is removed after ONE evaluation either way (:422-446).
            // Both outcomes log under [map_deco] — the Play probe for
            //: the resolve + real-carve fit legs are pinned green headless
            // (MapDecoBeamTests), so a missing support in Play must show here either as a drop
            // (with the probe depths that rejected it) or not at all (ScanAtPos never matched).
            if (ProbeFit(p)) SpawnEntity?.Invoke(p);
            else Debug.Log($"[map_deco] drop {p.ClassName} @({p.Px:0.##},{p.Py:0.##},{p.Pz:0.##}) "
                + $"probe h={SampleHmap(p.Bx0, p.By0)}/{SampleHmap(p.Bx1, p.By0)}/"
                + $"{SampleHmap(p.Bx0, p.By1)}/{SampleHmap(p.Bx1, p.By1)} (need: all >10, cnt 3..7)");

            int last = _pending.Count - 1;
            if (pi < last) _pending[pi] = _pending[last];
            _pending.RemoveAt(last);
            return true;
        }

        // The 8-probe floor-fit law (native:102-314): sample the block aux hmap under each
        // probe corner; spawn iff 3 ≤ #(hmap ≤ round(z)) ≤ 7 (partially embedded — not floating,
        // not buried) AND every probe's hmap > 10 (the TUNNEL floor band 11+; a deeper cave
        // carve rejects).
        bool ProbeFit(PendingDeco p)
        {
            var t = Terrain!;
            // Sub-grid inset gate (native:18): silently dropped when outside.
            int sx = Mathf.RoundToInt(p.Px * 8f), sy = Mathf.RoundToInt(p.Py * 8f);
            if (sx < 16 || sx >= t.Width * 8 - 16 || sy < 16 || sy >= t.Height * 8 - 16) return false;

            int cnt = 0;
            bool band = true;
            Span<float> xs = stackalloc float[2] { p.Bx0, p.Bx1 };
            Span<float> ys = stackalloc float[2] { p.By0, p.By1 };
            Span<float> zs = stackalloc float[2] { p.BzLow, p.BzHigh };
            for (int ix = 0; ix < 2; ix++)
                for (int iy = 0; iy < 2; iy++)
                    for (int iz = 0; iz < 2; iz++)
                    {
                        int h = SampleHmap(xs[ix], ys[iy]);
                        if (h <= Mathf.RoundToInt(zs[iz])) cnt++;
                        if (h <= 10) band = false;
                    }
            return cnt >= 3 && cnt != 8 && band;
        }

        // ---------------- per-carve stranded-deco test (BUG A) ----------------
        // After a carve, a spawned deco whose floor is no longer in the TUNNEL band (hmap ≤ 10 =
        // cave-band or fully open) has lost the footing the spawn law required — the SAME band
        // gate that rejects a spawn over a deeper cave carve (ProbeFit: `if (h <= 10) band =
        // false`). The engine sweep (DigglesTclEngine.MapDecoCarveSweep, wired to
        // DiggableTerrain.OnCarveNode) calls this per beautifier/cave-skin deco standing on a
        // carved node's tile footprint and deletes the ones that answer true. Symmetric with the
        // spawn/reject law, so a beam over intact tunnel floor (hmap 11+) is kept and only a
        // cave-re-carved one is removed.
        public bool FloorStranded(float px, float py) => SampleHmap(px, py) <= 10;

        // ( wall-anchor bookkeeping — WallAnchorsFor/AnchorLost, recorded anchor tiles
        // per piece — was SUPERSEDED by the literal native kill law: the DUMMYDES pass
        // tests the piece's ANIM BBOX against the carve dirty rect (FUN_005eb7b0:83), which a
        // wall board's protruding model satisfies for a carve of the block IN FRONT of its
        // wall face — a case anchor tiles missed. See MapDecoCarveSweep.)

        // The faithful floor sample = get_hmap (FUN_004093f0): the tile block's aux 8×8 hmap at
        // the ×8 sub-cell; headless fixtures (no nav) fall back to the tile height.
        int SampleHmap(float wx, float wy)
        {
            if (HmapSampler != null) return HmapSampler(wx, wy);
            var t = Terrain!;
            var nav = t.Nav;
            if (nav != null)
                return nav.GetHmapSub(Mathf.RoundToInt(wx * 8f), Mathf.RoundToInt(wy * 8f));
            return t.GetHmap(Mathf.RoundToInt(wx), Mathf.RoundToInt(wy));
        }

        // ---------------- the shape cache (FUN_00581b40 → FUN_00599fc0) ----------------
        // Rasterize the template PMP into 0/1 cells: 1 ⇔ the authored block's aux hmap byte
        // 0x1b (sub-cell 3,3) nibble == 11 (the tunnel-floor height). Cached by PMP base —
        // zone variants (urw_/swf_/kris_/lava_ of one gng) share the grid, which is what makes
        // the native same-shape candidate dedupe meaningful. Public: real-data shape tests.
        public int[]? ShapeFor(LevelGenerator.Piece p)
        {
            string key = p.PmpBase;
            if (string.IsNullOrEmpty(key)) return null;
            if (_shapes.TryGetValue(key, out var cached)) return cached;
            int[]? cells = null;
            try
            {
                var path = Templates?.FindTemplatePmp(key);
                if (path != null && Bdb != null)
                {
                    var map = PmpParser.Parse(path);
                    int w = (int)map.Width, h = (int)map.Height;
                    cells = new int[w * h];
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                        {
                            uint blk = map.BlockIdxAt(x, y);
                            int hm = blk < Bdb.Header.NBlocks ? Bdb.AuxHeightAt((int)blk, 0x1b) : 15;
                            cells[y * w + x] = (hm & 0xF) == 0xB ? 1 : 0;
                        }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[map_deco] shape load failed for '{key}': {e.Message}");
                cells = null;
            }
            _shapes[key] = cells;
            return cells;
        }

        sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T> where T : class
        {
            public static readonly ReferenceEqualityComparer<T> Instance = new();
            public bool Equals(T? a, T? b) => ReferenceEquals(a, b);
            public int GetHashCode(T obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }

    public static class MapDecoPresentationCull
    {
        public const int ActiveDetail = 2;
        public const float SeedRenderDistance = 64f;

        public static bool ShouldMaterialize(
            Vector3 position,
            byte lodTier,
            int activeDetail,
            Vector2 focus,
            bool culled)
        {
            if (culled || lodTier > activeDetail) return false;
            float dx = position.x - focus.x;
            float dy = position.y - focus.y;
            return dx * dx + dy * dy <= SeedRenderDistance * SeedRenderDistance;
        }

        public static void FillCullSet(
            IReadOnlyList<Diggles.Runtime.Tcl.Engine.DigglesTclEntity> live,
            IReadOnlyList<Diggles.Runtime.Tcl.Engine.DigglesTclEntity> saveSeeds,
            List<Diggles.Runtime.Tcl.Engine.DigglesTclEntity> target)
        {
            target.Clear();
            for (int i = 0; i < live.Count; i++) target.Add(live[i]);
            for (int i = 0; i < saveSeeds.Count; i++) target.Add(saveSeeds[i]);
        }
    }
}
