#nullable enable

namespace Diggles.Runtime.World
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;

    // C# port of the Diggles level generator (Map/LevelGen.cpp, CLevelGenerator) — the StoryMgr's
    // world-GROWTH engine (lg_* command family). The unchanged Tcl (story.tcl, lginit.tcl,
    // lgtools.tcl) drives it; we reimplement the native lg_* primitives FAITHFULLY from the
    // decompile:
    //
    // grow core FUN_00589790 — precondition → (route) → mark-areas → do{ seed; dock frontier[0]
    // until it fails }while(fill<ratioA && ok); ok==false ⇒ the WHOLE
    // generation FAILS and rolls back (full-closure law: the only
    // successful outcome is a fully-docked, door-closed cluster).
    // seeding FUN_005862c0 — first pool piece with W==4&&H==4&&edges==2, ≤16 random
    // 4-aligned spots ≥4 inside the area ((rand&0x7fff)%span&~3+4).
    // forced/start FUN_005848b0 — 4-aligned; whole template ≥8 tiles inside the MAP; live-map
    // tile-flag probe rejects; window CLAMPED to the area (partial
    // paint allowed); placement flag byte = 1.
    // TryPlace FUN_005842c0 — (x|y)&3==0; bounds; overlap(grid!=0); MergeEdges toggle;
    // margin(+0x80=4, FUN_00583650:59) extends the paint window on
    // DOORLESS sides only (openSideFlags bits 1=top 2=right 4=bottom
    // 8=left); tentative |0x80 paint; validator; success paints the
    // EXTENDED window = 2 (margins become permanent occupancy) and
    // bumps fill(+0xa0)+=W*H, objCounters, placedThisLevel(+0x24).
    // validator FUN_00584120 — EVERY frontier point must be ≥2 cells inside the area border
    // and must keep at least one of its 4 diagonal cells free.
    // MergeEdges FUN_00583f60 — coordinate-match toggle (COORD ONLY — no dir test): matching
    // point removed (swap-last), else appended {x,y,dir}.
    // cost FUN_00586710 — see Cost (all.rdata constants extracted from Diggles.exe).
    // ratio FUN_00652830 — lg_set_templateratio a opt max → +0x70/+0x74/+0x78; story.tcl
    // passes a=0 ⇒ the outer grow loop runs EXACTLY ONCE (one seed
    // cluster per generation); opt(0.10) only feeds the cost
    // progress; max(1.0) the overshoot reject.
    // output FUN_00652d10 — lg_get_level emits {name x y} per placement AND marks each
    // placement's story-cell rect grown (the engine binding does the
    // marking — the anti-duplicate law an earlier revision was missing) and
    // clears the objcount constraints.
    // finalize FUN_00585390 — dead-end-branch prune (deletes pointless flag-0 thin 1-door empty
    // stubs, capping the junction with a door-reduced variant or a 4×4
    // stub; chain-walks ≤2-door spurs). PORTED as
    // FinalizePrune, the last step of Start. ATOMIC/failure-tolerant:
    // the caller snapshots + restores on any non-clean exit
    // (FUN_00589790:280-327), so a valid closed cluster is never broken.
    // randomness FUN_0049c670 — one SHARED game-wide MT19937 lazily seeded with the CONSTANT
    // 0x1105 (FUN_004454d0); effective run-to-run variety comes from
    // the shared stream being consumed by every other system. The
    // port models that with one session-seeded Random shared via
    // ctx.StoryRng (a FIXED private seed here was the bug).
    //
    // Coordinates are TILE units; the StoryMgr passes cell*4 (story resolution = 4). A door
    // direction is 0=left(x=0) 1=top(y=0) 2=right(x=W) 3=bottom(y=H); docking candidates are
    // edges whose dir is OPPOSITE the open door's ((d-t)&3==2, FUN_00589790:223).
    public sealed class LevelGenerator
    {
        public struct Edge { public int Lx, Ly, Dir; }

        public sealed class Piece
        {
            public string TclName = "";       // e.g. urw_gng_050_a.tcl
            public string PmpBase = "";        // e.g. gng_050 (from the tcl's map_template arg)
            public int W, H;
            public readonly List<Edge> Edges = new();
            public string Group = "";
            public int LevelCap = int.MaxValue; // +0x20 (lg_set_templateprops -levelcount)
            public int GameCap = int.MaxValue;  // +0x1c (-gamecount REMAINING; ctor default INT_MAX,
                                                // the sole decrementer lg_dec_templatecount is never
                                                // called by shipped scripts ⇒ static = the -gamecount
                                                // arg). Feeds the cost capTerm (FUN_00586710:28).
            public int TunnelFlag;              // +0x30 (-tunnel: 1=vertical 2=horizontal)
            public float Value = 1f;            // +0x3c (lg_set_template(group)value; default 1)
            public bool MetaLoaded;
            public int PlacedThisLevel;         // +0x24 (reset per lg_start — the per-LEVEL count)
            public int EmittedThisSession;      // +0x50 — placed-this-SESSION; bumped once per emitted
                                                // object-bearing placement by lg_get_level
                                                // (FUN_00652d10:84-93) and NEVER reset. The object-piece
                                                // cost gate (FUN_00586710:58-61) rejects on >0 ⇒ an
                                                // object cave variant ships at most ONCE per session,
                                                // forcing later generations onto different object caves.
            // Per-filter-class object counts (def+0x34, parsed from the template's `new <Class>`
            // spawns) + the object-bearing flag (+0x41).
            public readonly Dictionary<string, int> ObjCounts = new(StringComparer.OrdinalIgnoreCase);
            public int ObjTotal;
            public bool ObjectBearing;
            // openSideFlags (+0x18): bit1=top door, bit2=right, bit4=bottom, bit8=left —
            // the TryPlace margin law (FUN_005842c0:86-106).
            public int OpenSideFlags;

            // ---- CMapBeautifier fields (the during-dig template-deco pass shares this
            // registry — the native beautifier+0x1a4 table IS the lg template table;
            // consumer: MapDecoBeautifier, Docs/DIGGING_MECHANIC.md §9.B) ----
            // +0x4c zone band from the NAME prefix (FUN_00583080;.rdata 0x72a898..:
            // urw_=0 swf_=1 kris_=2 lava_=3; unprefixed stays 0).
            public int Zone;
            // +0x41 — "_hol_" cave templates never participate in the beam match.
            public bool Hol;
            // +0x40 — split/multi header (split_template): dims from the header, NO deco (+0x48=-2).
            public bool Split;
            // The authored deco set (+0x48 → beautifier+0x20 set, entries stride 0x2c): the
            // `new <Class> "" "[expr {X+$xoffset}] [expr {Y+$yoffset}] Z" "rx ry rz"` spawns
            // parsed from the template's.tcl (FUN_00581d30:125-268) — offsets RELATIVE to the
            // template origin, with the surrounding loading_objdetail bracket as Detail.
            public readonly List<DecoEntry> Deco = new();
        }

        public struct DecoEntry
        {
            public string ClassName;
            public float X, Y, Z, Rx, Ry, Rz;
            public int Detail;   // loading_objdetail: h=2 m=1 l=0 o(ff)=0, digits verbatim
        }

        sealed class Group
        {
            public readonly List<string> Tcls = new();   // template.tcl names
            public int LevelCount = int.MaxValue;
            public float Value = 1f;
        }

        // lg_set_objcount <class> <min> <max> <stride> — per-filter-class constraint (+0x68,
        // stride 0xc = {min,max,stride}); stride −1 = excluded from the pressure term
        // (FUN_00586710:156 vs DAT_0073773c = −1.0f). Re-armed per generation by the story.tcl
        // define_obj_counts pass (generate_next re-issues lg_set_objcount before every lg_start).
        sealed class ObjCount { public float Min, Max = float.MaxValue, Stride = -1f; }

        readonly Dictionary<string, Piece> _pieces = new(StringComparer.OrdinalIgnoreCase);
        // Registration order (the native +0x1a4 vector order) — the beam matcher iterates this.
        readonly List<Piece> _order = new();
        readonly Dictionary<string, Group> _groups = new(StringComparer.OrdinalIgnoreCase);
        readonly List<string> _filterClasses = new();
        readonly Dictionary<string, ObjCount> _objCons = new(StringComparer.OrdinalIgnoreCase);
        public string DataRoot = "";

        // ---- per-run config (set by the lg_set_* commands before lg_start) ----
        readonly List<string> _pool = new();
        float _ratioA, _ratioOpt = 0.1f, _ratioMax = 1f;   // +0x70/+0x74/+0x78 (FUN_00652830:140-154)
        const int Margin = 4;                              // +0x80 (FUN_00583650:59)
        int _areaX0, _areaY0, _areaX1, _areaY1;

        // The SHARED random stream (native: one game-wide MT19937, FUN_0049c670, const-seeded
        // 0x1105 but consumed by every engine system → effective per-session variety). The engine
        // wires ctx.StoryRng here; the default is session-seeded. A fixed private seed here made
        // every playthrough generate the SAME caves.
        public System.Random Rng = new System.Random();

        // Live-map probe for forced placements (FUN_005848b0:54-55 — per-tile flag +0x13 bit0
        // rejects; the port probes the HasTpl layer): world tile → true = blocked.
        public Func<int, int, bool>? TileBlocked;
        // Map dims for the forced-template inset gate (FUN_005848b0:37-46: the whole template
        // must sit ≥8 tiles inside the map). 0 = unset (gate skipped; synthetic tests).
        public int MapW, MapH;

        // ---- run output ----
        readonly List<(string tcl, int x, int y)> _level = new();
        readonly List<(Piece p, int x, int y)> _placedOut = new();
        // Diagnostic: why the last Start returned false (logged by the story loop on failures).
        public string LastFailReason = "";

        // ===================== registry (shared with the map beautifier) =====================

        // The template registry in registration order — the native beautifier+0x1a4 vector the
        // during-dig deco matcher (MapDecoBeautifier) iterates.
        public IReadOnlyList<Piece> Registered => _order;

        // Ensure the piece's on-disk metadata (pmp dims, doors, deco set) is loaded — the
        // matcher resolves lazily; the native loads at registration (FUN_00583080 → FUN_005826d0).
        public void EnsureMeta(Piece p) { if (!p.MetaLoaded) LoadMeta(p); }

        // The template PMP path resolver (shape rasterization uses the same file LoadMeta does).
        public string? FindTemplatePmp(string baseName)
            => FindFile(Path.Combine("Templates", "pmp", baseName + ".pmp"))
            ?? FindFile(Path.Combine("templates", "pmp", baseName + ".pmp"));

        // EVERY piece is born here — FUN_00583080's registration side effects: the zone band
        // from the name prefix (contains-match, case-sensitive like the native strstr;.rdata
        // 0x72a898 "urw_", 0x72a8a0 "swf_", + "kris_"/"lava_") and the "_hol_" exclusion flag
        // (+0x41, which natively also forces +0x48 = -2 = no deco).
        Piece CreatePiece(string tcl, string group)
        {
            var p = new Piece { TclName = tcl, Group = group };
            if (tcl.IndexOf("_hol_", StringComparison.Ordinal) >= 0) p.Hol = true;
            if (tcl.IndexOf("urw_", StringComparison.Ordinal) >= 0) p.Zone = 0;
            else if (tcl.IndexOf("swf_", StringComparison.Ordinal) >= 0) p.Zone = 1;
            else if (tcl.IndexOf("kris_", StringComparison.Ordinal) >= 0) p.Zone = 2;
            else if (tcl.IndexOf("lava_", StringComparison.Ordinal) >= 0) p.Zone = 3;
            _order.Add(p);
            return p;
        }

        // ===================== config commands (lg_set_*/lg_tp_*) =====================

        public void AddFilterClass(string cls)
        {
            foreach (var c in _filterClasses)
                if (string.Equals(c, cls, StringComparison.OrdinalIgnoreCase)) return;
            _filterClasses.Add(cls);
        }

        // lg_set_templategroup <name> {tcl files}
        public void SetTemplateGroup(string name, IEnumerable<string> tcls)
        {
            if (!_groups.TryGetValue(name, out var g)) { g = new Group(); _groups[name] = g; }
            foreach (var t in tcls)
            {
                var tcl = TrimTcl(t);
                if (tcl.Length == 0) continue;
                if (!g.Tcls.Contains(tcl)) g.Tcls.Add(tcl);
                if (!_pieces.ContainsKey(tcl)) _pieces[tcl] = CreatePiece(tcl, name);
                else _pieces[tcl].Group = name;
            }
        }

        // lg_set_templategroupprops <name> -levelcount N...
        public void SetTemplateGroupProps(string name, int? levelCount)
        {
            if (!_groups.TryGetValue(name, out var g)) { g = new Group(); _groups[name] = g; }
            if (levelCount.HasValue) { g.LevelCount = levelCount.Value; foreach (var t in g.Tcls) if (_pieces.TryGetValue(t, out var p)) p.LevelCap = levelCount.Value; }
        }

        // lg_set_templateprops <name> -gamecount G -levelcount L -tunnel T
        public void SetTemplateProps(string tcl, int? levelCount, int? tunnel = null, int? gameCount = null)
        {
            tcl = TrimTcl(tcl);
            if (!_pieces.TryGetValue(tcl, out var p)) { p = CreatePiece(tcl, ""); _pieces[tcl] = p; }
            if (levelCount.HasValue) p.LevelCap = levelCount.Value;
            if (gameCount.HasValue) p.GameCap = gameCount.Value;   // +0x1c — the cost capTerm discount
            if (tunnel.HasValue) p.TunnelFlag = tunnel.Value;
        }

        public void SetTemplateGroupValue(string name, float v)
        {
            if (_groups.TryGetValue(name, out var g)) { g.Value = v; foreach (var t in g.Tcls) if (_pieces.TryGetValue(t, out var p)) p.Value = v; }
        }

        public void SetTemplateValue(string tcl, float v) { if (_pieces.TryGetValue(TrimTcl(tcl), out var p)) p.Value = v; }

        // lg_tp_clear / lg_tp_addtemplatesets "Zone.Std" / lg_tp_setfilter "Zone.Std.Gsg"
        public void PoolClear()
        {
            // lg_tp_clear FUN_0064e4f0 delegates to FUN_00583650, which clears the pool AND
            // the generator's +0x198 forced-record,
            // +0xa8 connection, and +0x180 mark-area counts. It also resets +0x50..+0x5c
            // (the active area) and the seed state; keeping only _pool.Clear made a
            // Campaign.tcl restart append a second copy of every authored preset.
            _pool.Clear();
            _forcedRecords.Clear();
            _connections.Clear();
            _reservedRects.Clear();
            _areaX0 = _areaY0 = _areaX1 = _areaY1 = 0;
            _seedState = -1;
        }

        public void PoolAddTemplateSets(string spec)
        {
            foreach (var setName in spec.Split(new[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                foreach (var kv in _groups)
                    if (kv.Key.Equals(setName, StringComparison.OrdinalIgnoreCase)
                        || kv.Key.StartsWith(setName + ".", StringComparison.OrdinalIgnoreCase))
                        foreach (var t in kv.Value.Tcls) if (!_pool.Contains(t)) _pool.Add(t);
        }

        // lg_tp_setfilter <group> — REMOVE the group's members from the pool (FUN_0064f830:
        // for each pool entry matching a member of the named group, swap-remove it). This is the
        // INVERSE of an earlier revision reading ("restrict to the group"): story.tcl:188's
        // `lg_tp_setfilter " $zone.Std.Gsg "` on unique-temp generations excludes the Gsg ring
        // pieces and grows the composite's surroundings from the NORMAL Gng/Hol pool — the
        // restrict reading left a pool with no 1-door caps, so unique-temp generations could
        // never close (the deep-growth livelock).
        public void PoolSetFilter(string groupName)
        {
            groupName = groupName.Trim();
            if (!_groups.TryGetValue(groupName, out var g)) return;
            var drop = new HashSet<string>(g.Tcls, StringComparer.OrdinalIgnoreCase);
            _pool.RemoveAll(t => drop.Contains(t));
        }

        // lg_tp_objfilter {Troll Zwerg} — drop pool pieces spawning any of these classes
        // (story.tcl:138 keeps monsters/dwarves out of Std growth; the old no-op here let
        // troll-bearing templates into grown caves — manual test "wrong objects/monsters").
        public void PoolObjFilter(IEnumerable<string> classes)
        {
            var drop = new HashSet<string>(classes, StringComparer.OrdinalIgnoreCase);
            if (drop.Count == 0) return;
            _pool.RemoveAll(t =>
            {
                var p = Resolve(t);
                if (p == null) return false;
                foreach (var kv in p.ObjCounts)
                    if (kv.Value > 0 && drop.Contains(kv.Key)) return true;
                return false;
            });
        }

        public void SetArea(int x0, int y0, int x1, int y1) { _areaX0 = x0; _areaY0 = y0; _areaX1 = x1; _areaY1 = y1; }

        // lg_set_templateratio a opt max (FUN_00652830: argv1→+0x70, argv2→+0x74, argv3→+0x78).
        public void SetTemplateRatio(float a, float opt, float max) { _ratioA = a; _ratioOpt = opt; _ratioMax = max; }

        public void SetObjCount(string cls, int min, int max, int stride)
            => _objCons[cls] = new ObjCount { Min = min, Max = max, Stride = stride };

        public void SetLevelType(string t) { /* +0x48 leveltype — no dependence found in the cost/grow decompile */ }

        // ---- FORCED START RECORDS — the native +0x194 list (stride 0x10 {tplIdx, x, y, flag}).
        // Registered by the lg_set_starttemplatepro binding (FUN_0058a510 appends the walked
        // mode-1 dig-direction piece; the unique-temp positioning appends its group pieces),
        // consumed IN ORDER by lg_start (FUN_005896a0 → FUN_005848b0 places each at its stored
        // AREA-RELATIVE x,y VERBATIM — no snapping, no adjustment; ANY failure aborts the whole
        // generation, FUN_005896a0:40-45 = the story.tcl failcount retry channel).
        readonly List<(string tcl, int gx, int gy)> _forcedRecords = new();
        public void ClearForcedRecords() => _forcedRecords.Clear();
        public void AddForcedRecord(string tcl, int gx, int gy)
        {
            tcl = TrimTcl(tcl);
            if (tcl.Length > 0) _forcedRecords.Add((tcl, gx, gy));
        }
        public int ForcedRecordCount => _forcedRecords.Count;
        public IReadOnlyList<(string tcl, int gx, int gy)> ForcedRecords => _forcedRecords;

        // lg_add_preset <name.tcl> <x> <y>. FUN_00651c80 requires exactly those three Tcl
        // arguments and forwards to FUN_0058a510. The latter gates on a configured area,
        // find-or-registers through FUN_00583080 → FUN_005826d0, probes the AREA-OFFSET
        // absolute footprint, then appends the original area-relative x/y to +0x194.
        // Every placement failure is deliberately silent at this layer.
        public void AddPreset(string tcl, int x, int y)
        {
            if ((_areaX0 | _areaY0 | _areaX1 | _areaY1) == 0) return;
            if (MapW <= 0 || MapH <= 0) return;

            tcl = TrimTcl(tcl);
            if (tcl.Length == 0) return;
            if (!tcl.EndsWith(".tcl", StringComparison.OrdinalIgnoreCase)) tcl += ".tcl";

            Piece? p = ResolveOrLoad(tcl);
            if (p == null || p.W <= 0 || p.H <= 0)
            {
                // FUN_00583080 unregisters the just-created registry row when metadata load
                // leaves +0x2c == 0. Port registrations are lazy, so remove any invalid row
                // here as well; a later valid registration remains possible.
                if (p != null) UnregisterInvalidPiece(p);
                return;
            }

            int wx = x + _areaX0;
            int wy = y + _areaY0;
            if (!ProbeForcedPose(p, wx, wy)) return;
            _forcedRecords.Add((p.TclName, x, y));
        }

        // ---- CONNECT records — the native +0xa4 list (stride 0x18 = 6 ints), registered by
        // FUN_005fc3b0 from the utemp positioning block (FUN_00650f90:503) and ROUTED one-by-one
        // by the corridor router FUN_00587480 in the grow core (FUN_00589790:66-116). Field map
        // (FUN_0058a6c0 store order, byte-verified): rec+0x00 SrcRec, +0x04 SrcDoor, +0x08 DstRec,
        // +0x0c DstDoor, +0x10 sizeHint (0x40 start↔utemp / 8 utemp-internal — UNUSED by the
        // router body), +0x14 Weight (0.4f — the outer priority, not routing geometry). SrcRec/
        // DstRec index the +0x194 forced-record/node list (== _forcedRecords, placed 1:1 into
        // _placed by the forced loop). SrcDoor/DstDoor == -1 ⇒ the router trivially succeeds
        // (FUN_00587480:310-312); the port also uses -1 = "auto-pick the routable door pair"
        // because the native per-template entry-door field (story-template +0x38) is unmodeled.
        public struct Connection { public int SrcRec, SrcDoor, DstRec, DstDoor; public float Weight; }
        readonly List<Connection> _connections = new();
        // The corridor router runs in the live chain: FUN_00587480's body was RECOVERED
        // from Ghidra (the flood + gradient-backtrace + the 0x588ce1..0x5890c6 direction/variation
        // pass) and RouteCorridor below is now a FAITHFUL port of it, not the earlier Lee-BFS
        // reconstruction that regressed Play (utemp routes over-rejected ⇒ story caves vanished). ON by
        // default; a route that genuinely cannot be carved still fails the whole generation, faithfully
        // (FUN_00587480 returns 0 ⇒ FUN_00589790:11 rollback → the story failcount retry).
        // the FUN_005fc0a0 rect re-placement now lands SINGLE-template utemps
        // correctly (headless diag: feen dir=1 places AWAY-OK, started=1 WITH the router on ⇒ the router
        // does not break placement). Turn the corridor router back on so placed utemps CONNECT. (Composite
        // utemps — voodoo_dorf/einsiedler — still fail earlier in ForcePlace on a buried composite-seam
        // door, a separate door-mating issue; the router never runs for them.)
        public bool RouterEnabled = true;
        public void ClearConnections() => _connections.Clear();
        public void AddConnection(int srcRec, int dstRec, int srcDoor = -1, int dstDoor = -1, float weight = 0.4f)
            => _connections.Add(new Connection { SrcRec = srcRec, SrcDoor = srcDoor, DstRec = dstRec, DstDoor = dstDoor, Weight = weight });
        public int ConnectionCount => _connections.Count;

        // ---- MARK-AREA / courtyard reservation records — the native +0x17c list (stride 0x10 =
        // 4 ints {x0,y0,x1,y1}, count +0x180). Registered by lg_set_starttemplatepro from a
        // composite's `mat` type-0 rects (sm_def_temp_group; FUN_0065b180 parses {mat {type x y w h}}
        // → group rect {x, y, x+w, y+h, type}; FUN_00650f90:424-434 forwards the TYPE-0 rects to
        // +0x17c, offset by the composite's grid origin). lg_start (FUN_00589790:117-171) then stamps
        // each rect's cells = 1 in the SAME placement grid TryPlace gates on (+0x84), BEFORE the seed —
        // an OCCUPANCY KEEP-CLEAR, NOT a terrain paint (the loop is an inline
        // grid[gridW*y+x]=1 with no terrain/material/floor call). Effect: the composite's inter-building
        // COURTYARDS stay open (the grow loop can't fill them). Coords are area-relative (grid-local)
        // CORNERS. Persist across lg_start retries like _forcedRecords (cleared per lg_set_starttemplatepro).
        readonly List<(int x0, int y0, int x1, int y1)> _reservedRects = new();
        public void ClearReservedRects() => _reservedRects.Clear();
        public void AddReservedRect(int x0, int y0, int x1, int y1) => _reservedRects.Add((x0, y0, x1, y1));
        public int ReservedRectCount => _reservedRects.Count;

        // FUN_00585d80 — the dig-anchored START-PIECE pick, called per walk iteration through
        // FUN_005f7980 (dig dir → door-side code): filters the ACTIVE POOL down to 4×4 pieces
        // (def W==4 && H==4,:49) whose door signature matches —
        // • down-dig mode 1 (:58-110): exactly TWO doors, dirs {left,right} in either order —
        // the east-west through-corridor pieces (gng_001_a/b/c in the Urwald pool): a shaft
        // pierces a horizontal stratum corridor;
        // • every other case (:113-125): exactly ONE door whose dir equals the side code — the
        // door faces ONWARD along the dig direction, AWAY from the player's tunnel (the
        // tunnel connects by piercing the piece's closed wall, not door-to-door): up→top
        // (gng_023), +x→right (gng_021), −x→left (gng_022), down mode 0→bottom (gng_024).
        // One match picked uniformly at random ((rand&0x7fff)*count>>15,:149-155); the name is
        // path-stripped after '/' (:170-196). Empty match set → null ("none" natively, which
        // fails to resolve downstream). `side` is in PORT door labels (0=left 1=top 2=right
        // 3=bottom; native 3/0/1/2 respectively).
        public string? PickStartPiece(int side, bool downThrough)
        {
            _startPickBuf.Clear();
            foreach (var t in _pool)
            {
                var p = Resolve(t);
                if (p == null || p.W != 4 || p.H != 4) continue;
                if (downThrough)
                {
                    if (p.Edges.Count != 2) continue;
                    int d0 = p.Edges[0].Dir, d1 = p.Edges[1].Dir;   // port 0=left 2=right
                    if (!((d0 == 0 && d1 == 2) || (d0 == 2 && d1 == 0))) continue;
                }
                else if (p.Edges.Count != 1 || p.Edges[0].Dir != side) continue;
                _startPickBuf.Add(t);
            }
            if (_startPickBuf.Count == 0) return null;
            return _startPickBuf[((Rng.Next() & 0x7fff) * _startPickBuf.Count) >> 15];
        }
        readonly List<string> _startPickBuf = new();

        // FUN_0058a510 — the walk-pose probe: piece 4-aligned at ABSOLUTE map tile (wx,wy),
        // whole footprint ≥8 tiles inside the MAP (:38-41 vs DAT_0077241c+0x84..0x90), and NO
        // covered tile already templated (tile+0x13 bit0 = HasTpl,:54-58). NOTE: this probe
        // does NOT run the frontier validator — a pose can pass here and still fail placement
        // inside lg_start (native: generation fails, story retries).
        public bool ProbeForcedPose(string tcl, int wx, int wy)
        {
            var p = ResolveOrLoad(TrimTcl(tcl));
            if (p == null || p.W <= 0 || p.H <= 0) return false;
            return ProbeForcedPose(p, wx, wy);
        }

        bool ProbeForcedPose(Piece p, int wx, int wy)
        {
            if (((wx | wy) & 3) != 0) return false;
            if (MapW > 0 && MapH > 0
                && (wx < 8 || wy < 8 || wx + p.W >= MapW - 8 || wy + p.H >= MapH - 8)) return false;
            if (TileBlocked != null)
                for (int y = wy; y < wy + p.H; y++)
                    for (int x = wx; x < wx + p.W; x++)
                        if (TileBlocked(x, y)) return false;
            return true;
        }

        // Template size in world tiles (transient resolve so sm_mark_temparea can size ANY
        // template — the native size table FUN_00583bd0 covers all of them).
        public (int w, int h) GetTempSize(string tcl)
        {
            tcl = TrimTcl(tcl);
            if (tcl.Length == 0) return (0, 0);
            if (!tcl.EndsWith(".tcl", StringComparison.OrdinalIgnoreCase)) tcl += ".tcl";
            if (!_pieces.TryGetValue(tcl, out var p))
            {
                p = CreatePiece(tcl, "transient");
                _pieces[tcl] = p;   // cached; NOT added to the pool
            }
            if (!p.MetaLoaded) LoadMeta(p);
            return (p.W, p.H);
        }

        // ===================== the generator (lg_start, FUN_00589790) =====================

        struct OpenDoor { public int X, Y, Dir; }   // GRID-LOCAL coords (area-relative)

        // run state
        int[] _grid = Array.Empty<int>();
        int _aw, _ah;
        int _filled;                                   // +0xa0
        readonly List<OpenDoor> _frontier = new();     // +0x8c/+0x90
        readonly List<(Piece p, int gx, int gy, byte flag)> _placed = new();   // +0x184 (grid-local)
        readonly Dictionary<string, float> _objPlaced = new(StringComparer.OrdinalIgnoreCase);  // +0x94
        readonly List<(int x, int y, float r)> _objPieces = new();             // +0x18c spacing records
        int _seedState;                                // +0x64: −1 initial, −2 seeded, else piece chosen

        public bool Start()
        {
            _level.Clear(); _placedOut.Clear(); _placed.Clear(); _frontier.Clear();
            _objPieces.Clear(); _objPlaced.Clear();
            foreach (var p in _pieces.Values) p.PlacedThisLevel = 0;
            _corridorBuckets = null;   // rebuilt per run (the pool may change between generations)
            _varAccum = 0f;            // FUN_00587480 corridor-variation accumulator (DAT_00c3c9b8)
            _corridorsSkipped = 0;     // interim cap-and-continue counter (reset per generation)
            _aw = Math.Max(0, _areaX1 - _areaX0); _ah = Math.Max(0, _areaY1 - _areaY0);
            LastFailReason = "";
            if (_aw < 4 || _ah < 4 || _pool.Count == 0) { LastFailReason = $"area/pool ({_aw}x{_ah}, pool={_pool.Count})"; return false; }
            _grid = new int[_aw * _ah];
            _filled = 0;
            _seedState = -1;   // FUN_00589790:62 (+0x64:= +0x60)

            // ---- precondition: the forced START records (FUN_005896a0 walks the +0x194 list in
            // order, placing each at its STORED area-relative pose verbatim via FUN_005848b0;
            // ANY failure aborts the generation,:40-45 — the story failcount retry channel).
            // The records were registered by lg_set_starttemplatepro: the walked mode-1
            // dig-direction piece first, then any unique-temp composite entries.
            bool anyForced = false;
            foreach (var (tcl, gx, gy) in _forcedRecords)
            {
                var p = ResolveOrLoad(tcl);
                if (p == null || p.W <= 0 || p.H <= 0)
                { LastFailReason = $"forced '{tcl}' unresolvable"; return Fail(); }
                if (!ForcePlace(p, gx, gy))
                { LastFailReason = $"forced '{tcl}' unplaceable @grid({gx},{gy}) — {LastForceDetail}"; return Fail(); }
                anyForced = true;
            }

            // ---- ROUTE the connect records (FUN_00589790:66-116 → FUN_00587480): carve a corridor
            // for each start↔utemp connection BEFORE the general growth fills around them. Order
            // mirrors the native: forced placement → route → (mark-area) → grow.
            //
            // INTERIM SAFETY — cap-and-continue. The native FAILS the whole generation
            // when a corridor cannot carve (FUN_00587480 returns 0 → FUN_00589790:11 rollback →
            // the story failcount retry). To guarantee a placed story cave NEVER silently vanishes we
            // instead SKIP an unroutable connection and leave the utemp placed. This is safe because
            // TryRouteBetween is ATOMIC (nothing is emitted unless every path cell resolves to an
            // exact-mask piece — see MatchBucket), so no half-corridor or uncapped corridor door is
            // introduced; the now-open src/dst frontier doors are then capped by the normal grow loop
            // (or, if genuinely uncappable, the existing closure law:262 fails the gen exactly as the
            // native would). With the exact-mask pick a route only fails to carve when the pool lacks
            // a required corridor mask — the same condition under which FUN_00587030 would return 0.
            if (RouterEnabled)
                foreach (var conn in _connections)
                    if (!RouteCorridor(conn))
                        _corridorsSkipped++;

            PaintReservations();   // ---- MARK-AREA / courtyard reservation (FUN_00589790:117-171) ----

            // ---- grow loop (FUN_00589790:172-259). story.tcl's ratio a=0 ⇒ single pass. ----
            bool ok;
            var costBuf = new List<(int cost, string tcl)>();
            do
            {
                if (!SeedOrAdvance(anyForced)) { LastFailReason = "seed/advance failed"; return Fail(); }   //:173-178
                ok = true;
                int guard = 0;
                while (_frontier.Count > 0 && guard++ < 8192)
                {
                    var P = _frontier[0];                            //:186-191 — always the HEAD door
                    // cost-sort the pool for THIS door (FUN_00586f40 + quicksort; INT_MAX pieces
                    // stay listed as last resorts — the native dock loop has NO cost cutoff)
                    costBuf.Clear();
                    foreach (var t in _pool)
                    {
                        var c = Resolve(t);
                        if (c == null || c.W <= 0 || c.H <= 0) continue;
                        costBuf.Add((Cost(c, P), t));
                    }
                    costBuf.Sort((a, b) => a.cost.CompareTo(b.cost));

                    ok = false;                                      //:198
                    foreach (var (_, tcl) in costBuf)
                    {
                        var c = Resolve(tcl)!;
                        foreach (var e in c.Edges)
                        {
                            if (((e.Dir - P.Dir) & 3) != 2) continue;    //:223 opposite-edge filter
                            if (TryPlace(c, P.X - e.Lx, P.Y - e.Ly, Margin, 0, false)) { ok = true; break; }
                        }
                        if (ok) break;
                    }
                    if (!ok) break;                                  //:252 — head door undockable
                }
            }
            while ((float)_filled / (_aw * _ah) < _ratioA && ok);    //:259 (+0x70 target)

            if (!ok) { LastFailReason = $"closure failed: frontier head undockable ({_frontier.Count} open, {_placed.Count} placed, fill {_filled})"; return Fail(); }   //:262

            // FUN_00585390 — post-grow-loop FINALIZE / dead-end-branch prune (the last generation
            // step). Removes pointless stubby dead-ends; failure-tolerant (reverts to the pre-finalize
            // placements). Runs AFTER the closure check, BEFORE emitting _placedOut/_level.
            FinalizePrune();

            foreach (var (p, gx, gy, _) in _placed)
            {
                _placedOut.Add((p, _areaX0 + gx, _areaY0 + gy));
                _level.Add((p.TclName, _areaX0 + gx, _areaY0 + gy));
            }
            // NOTE: _forcedRecords survive the run — cleared/re-registered by the next
            // lg_set_starttemplatepro (a bare lg_start retry replays the same records, like the
            // native +0x194 list persisting across a failed FUN_00589790).
            return _placed.Count > 0;
        }

        bool Fail() { _placed.Clear(); _level.Clear(); _placedOut.Clear(); return false; }

        // FUN_00589790:117-171 — stamp each composite `mat` type-0 rect's cells = 1 in the placement
        // grid, BEFORE the seed, so the grow loop leaves the composite's inter-building COURTYARDS open
        // (a keep-clear; the native writes 1 unconditionally at:156, bounds-clamped to the area). Both
        // 1 and 2 block TryPlace (grid!=0), so overwriting a placed composite cell is harmless.
        void PaintReservations()
        {
            foreach (var (rx0, ry0, rx1, ry1) in _reservedRects)
            {
                int cx0 = Math.Max(0, rx0), cy0 = Math.Max(0, ry0);
                int cx1 = Math.Min(_aw, rx1), cy1 = Math.Min(_ah, ry1);
                for (int y = cy0; y < cy1; y++)
                    for (int x = cx0; x < cx1; x++)
                        _grid[y * _aw + x] = 1;
            }
        }

        // ================= FINALIZE / DEAD-END PRUNE — FUN_00585390 =================
        // The last step of a successful generation. It removes POINTLESS stubby dead-end BRANCHES:
        // a placement that is flag==0 (grow-loop, NOT a forced/composite piece — forced pieces carry
        // flag==1 and are never pruned), SINGLE-door (Edges.Count==1,:76), THIN (W==4 || H==4,
        //:81-86) and EMPTY (ObjTotal==0,:88-107). Per dead-end (scan restarts from 0 after each
        // change,:302 goto): DELETE it (FUN_00584d10 — clear its grid footprint + drop from _placed),
        // find the neighbour that shared its door (FUN_00584f80), then
        // • JUNCTION neighbour (>2 doors, flag==0, non-object-bearing) with a POOL variant carrying
        // its doors MINUS the one facing the dead-end (FUN_00585130): re-place the neighbour with
        // that capped variant (:22) — the branch is truly pruned; ELSE
        // • STUB-CAP (:305-401): re-place a clean 4×4 single-door pool piece (door dir == the
        // dead-end's) at the dead-end's door, re-mating the neighbour.
        // A ≤2-door neighbour is a spur corridor: the walk DELETES it and continues to its OTHER door
        // (:301), chaining the whole dead-end branch back to the nearest junction / object / forced
        // piece. Every re-place uses flag=1 + skipPaintValidate (FUN_005842c0 param_6=1, param_7=1 =
        // no MergeEdges, no validate), so a re-placed piece is never re-scanned ⇒ the pass terminates.
        // ATOMIC / FAILURE-TOLERANT: the CALLER snapshots the whole
        // placement list before FUN_00585390 (:280-301) and, if it returns 0 (any non-clean exit),
        // RESTORES the snapshot (:37). FUN_00585390 returns 1 ONLY when a re-scan finds no
        // dead-end left (LAB_00585d65) — i.e. every prune in the sequence succeeded; ANY failure
        // (LAB_00585d51) returns 0 ⇒ the ENTIRE pass is reverted (all-or-nothing). This port snapshots
        // inside FinalizePrune and reverts on the first PruneDeadEndChain failure — equivalent, and it
        // guarantees a valid closed cluster is never left broken (so the pass is safely inert whenever
        // it cannot complete cleanly). The frontier (+0x8c/+0x90) is NOT touched (FUN_00584d10 doesn't
        // update it; the re-places skip MergeEdges) — the deleted pieces' doors were already mated.
        void FinalizePrune()
        {
            if (_placed.Count == 0) return;
            var savedPlaced = new List<(Piece, int, int, byte)>(_placed);
            var savedGrid = (int[])_grid.Clone();
            int savedFilled = _filled;

            int guard = 0;
            while (guard++ < 8192)
            {
                int deadIdx = -1;
                for (int i = 0; i < _placed.Count; i++)
                {
                    var (p, _, _, flag) = _placed[i];
                    if (flag != 0) continue;                       //:64 forced/protected → skip
                    if (p.Edges.Count != 1) continue;              //:76 exactly one door
                    if (p.W != 4 && p.H != 4) continue;            //:81-86 thin (a dim == 4)
                    if (p.ObjTotal != 0) continue;                 //:88-107 empty (no filter-class objects)
                    deadIdx = i; break;
                }
                if (deadIdx < 0) break;                            // no dead-end left → done

                if (!PruneDeadEndChain(deadIdx))                   // a re-place failed → revert (failure-tolerant)
                {
                    _placed.Clear(); _placed.AddRange(savedPlaced);
                    _grid = savedGrid; _filled = savedFilled;
                    return;
                }
            }
        }

        // FUN_00585390 while(true) (:118): walk + prune one dead-end branch. `curIdx` starts as the
        // dead-end and, on a ≤2-door spur neighbour, ADVANCES to that neighbour (continuing to delete
        // the chain). Returns TRUE when the branch is cleanly capped/pruned; FALSE on ANY non-clean
        // exit — a failed re-place (:266-279), no mated neighbour (:182-183 → LAB_00585d51), or a
        // doors-exhausted spur (:150-154 → LAB_00585d51). Every FALSE = the native returning 0, which
        // makes the CALLER (FUN_00589790:37) restore the pre-finalize snapshot ⇒ FinalizePrune
        // reverts the WHOLE pass (atomic, all-or-nothing).
        bool PruneDeadEndChain(int curIdx)
        {
            int door = 0;                                          // uVar4 — door index within the current piece
            int guard = 0;
            while (guard++ < 8192)
            {
                var (cp, cgx, cgy, _) = _placed[curIdx];
                if (door >= cp.Edges.Count) return false;          //:150-154 doors exhausted → LAB_00585d51 (revert)
                var e = cp.Edges[door];
                int wx = cgx + e.Lx, wy = cgy + e.Ly, ddir = e.Dir;   //:159-180 world door + dir
                DeletePlacement(curIdx);                           //:181 FUN_00584d10 (clear footprint + drop)
                int found = FindRecordWithDoorAt(wx, wy);          //:182 FUN_00584f80 (the mated neighbour)
                if (found < 0) return false;                       //:182-183 no neighbour → LAB_00585d51 (revert)
                int neighRec = found & 0xffff, neighDoor = found >> 16;
                var (np, ngx, ngy, nflag) = _placed[neighRec];
                if (nflag != 0 || np.ObjectBearing)                //:202/:207 protected/object neighbour → stub-cap
                    return StubCap(wx, wy, ddir);
                if (np.Edges.Count > 2)                            //:208 junction → try the capped variant
                {
                    var reduced = new List<Edge>(np.Edges);
                    reduced.RemoveAt(neighDoor);                   //:237-246 drop the door facing the dead-end
                    var variant = FindPoolVariantWithDoors(reduced);   //:247 FUN_00585130
                    if (variant == null) return StubCap(wx, wy, ddir); //:248-249/:298 no variant → stub-cap
                    if (!DeletePlacement(neighRec)) return false;                     //:266
                    if (!TryPlace(variant, ngx, ngy, 0, 1, true)) return false;       //:267 re-place capped (flag=1)
                    return true;                                   //:281 changed → FinalizePrune re-scans
                }
                //:208 false — a ≤2-door spur neighbour: walk to its OTHER door and keep deleting.
                curIdx = neighRec;
                door = neighDoor == 0 ? 1 : 0;                     //:301 uVar4 = (neighDoor==0)?1:0
            }
            return true;
        }

        // FUN_00584d10 — remove a placement: CLEAR its W×H grid footprint (:53-79) then drop it from
        // _placed (:80-94). The frontier is NOT updated (mated doors already left it). Returns false
        // only if the footprint is out of the area grid (the native:44-51 map-inset guard — a
        // can't-happen for a legitimately-placed piece). NOTE the native clears ONLY the W×H body, NOT
        // the margin cells a grow-loop TryPlace painted on doorless sides — those stay =2 (a faithful
        // quirk; the pass is the last step and the grid is not read afterwards). _filled is left
        // unchanged (the native does not decrement +0xa0 on delete; it is unused post-grow).
        bool DeletePlacement(int idx)
        {
            if (idx < 0 || idx >= _placed.Count) return false;
            var (p, gx, gy, _) = _placed[idx];
            if (gx < 0 || gy < 0 || gx + p.W > _aw || gy + p.H > _ah) return false;
            for (int y = gy; y < gy + p.H; y++)
                for (int x = gx; x < gx + p.W; x++)
                    if (_grid[y * _aw + x] != 0) _grid[y * _aw + x] = 0;
            _placed.RemoveAt(idx);
            return true;
        }

        // FUN_00584f80 — the placed record + door index whose door sits at world (wx,wy). Returns
        // (doorIdx<<16)|recordIdx (first match, scanning from 0), or -1 if none. Called after a
        // dead-end is deleted to find the neighbour that shared its door.
        int FindRecordWithDoorAt(int wx, int wy)
        {
            for (int i = 0; i < _placed.Count; i++)
            {
                var (p, gx, gy, _) = _placed[i];
                for (int d = 0; d < p.Edges.Count; d++)
                    if (gx + p.Edges[d].Lx == wx && gy + p.Edges[d].Ly == wy)
                        return (d << 16) | i;
            }
            return -1;
        }

        // FUN_00585130 — a POOL template whose door set is EXACTLY `reduced` (same count,:34; every
        // reduced door matched by local coords lx/ly,:67/:83; all accounted for,:91). Used to cap a
        // junction by swapping it for a variant with the dead-end-facing door walled. null if none.
        // (The native checks door count + door coords only — NOT W/H — but a matching coord set implies
        // the footprint.)
        Piece? FindPoolVariantWithDoors(List<Edge> reduced)
        {
            foreach (var t in _pool)
            {
                var p = Resolve(t);
                if (p == null || p.Edges.Count != reduced.Count) continue;   //:34 door-count match
                int matched = 0;
                foreach (var r in reduced)                                   //:37-89 every reduced door…
                    foreach (var pe in p.Edges)                             // …must appear in the pool piece
                        if (pe.Lx == r.Lx && pe.Ly == r.Ly) { matched++; break; }
                if (matched == reduced.Count) return p;                      //:91-95 all matched
            }
            return null;
        }

        // FUN_00585390:305-401 — the STUB-CAP fallback: replace the deleted dead-end with a clean 4×4
        // single-door POOL piece whose door faces the same way (dir == the dead-end's door dir,:326/
        //:335/:347), placed so its door lands back on the neighbour's now-open door at (wx,wy) with
        // flag=1 (:390). Random pick among the matching 4×4 caps (:365-370, one RNG draw). Returns
        // false (→ FinalizePrune reverts) when no matching cap exists or the placement can't land —
        // the safe reading of the native's failure exit (which would otherwise leave the neighbour's
        // door open). True on a successful cap.
        bool StubCap(int wx, int wy, int ddir)
        {
            _capBuf.Clear();
            foreach (var t in _pool)
            {
                var p = Resolve(t);
                if (p == null || p.W != 4 || p.H != 4) continue;             //:326 4×4
                if (p.Edges.Count != 1) continue;                            //:335 single door
                if (p.Edges[0].Dir != ddir) continue;                        //:347 dir == the dead-end's
                _capBuf.Add(p);
            }
            if (_capBuf.Count == 0) return false;                            //:364 none → revert (keep the pre-finalize cluster)
            var cap = _capBuf[(Rng.Next() & 0x7fff) % _capBuf.Count];        //:365-370 random pick
            var e0 = cap.Edges[0];
            return TryPlace(cap, wx - e0.Lx, wy - e0.Ly, 0, 1, true);        //:390 door lands at (wx,wy), flag=1
        }
        readonly List<Piece> _capBuf = new();

        // ================= CORRIDOR ROUTER — FUN_00587480 (start↔utemp tunnel carver) =================
        // FAITHFUL port of the RECOVERED FUN_00587480 (router_587480_decompiled.c + the standalone
        // 0x588ce1..0x5890c6 region + router_587480_disasm.txt). The algorithm, step by step:
        // 1. look up SRC (node,door) + DST (node,door) grid pos + facing (:370-435 via FUN_0058ef30
        // node{tplId,x,y,flag} + FUN_0058f160 template + FUN_00441fe0 door{lx,ly,orient}); both
        // doors must be OPEN frontier doors (:443-485 scan of +0x8c/+0x90).
        // 2. each door's 2-tile STEP-OFF (out) + INSIDE (in) point (:503-589 switch A on SRC orient,
        // switch B on DST orient). Opposite-coincident step-offs ⇒ already adjacent (:487-493 /
        //:625) → commit directly.
        // 3. FLOOD (else branch, disasm 0x587a41): seed the DST STEP-OFF cell high16 (0xffff,
        // disasm 0x587a9d-0x587aa2), spread a DECREASING cost over cells whose LOW16==0 (EMPTY —
        // disasm 0x587b6e `MOVZX word[cell];TEST;JNZ skip`) stepping ±4 (story-cell lattice, the
        // step-offs sit at ≡2 mod 4 = 4×4-block centres). FUN_0058ee60 is the external wavefront
        // queue; a per-relaxation rand jitter (disasm 0x587b79) rides the SHARED stream — see
        // the RNG note below.
        // 4. if the SRC step-off got a cost (:796), GRADIENT-ASCEND from it toward the DST step-off
        // picking the highest-cost ±4 neighbour, probe order N,S,W,E (decompiled.c:823-933) —
        // a 4-aligned shortest path; else clear the markers + FAIL (:965-982 → return false).
        // 5. DIRECTION/VARIATION pass (region 0x588ce1): for each interior path cell derive the
        // allowed-direction bitmask from the segment dx/dy + the frontier doors (:1085-1147),
        // then (probabilistically, accumulator DAT_00c3c9b8 vs a rand gate, disasm 0x588eb4)
        // pick a perpendicular via switch C = ((r1&3)+(r2&3))>>1 (disasm 0x588f25) and, if the
        // ±4-offset cell (switch D, disasm 0x588f8a) is EMPTY and mates no frontier door
        // (:1233-1253 / disasm 0x589060), OR that branch bit into the cell's door-mask — a
        // corridor side-branch, NOT a reroute (the path cells are unchanged).
        // 6. SEGMENT-TYPE template pass (:1284-1531): per path cell pick a random matching 4×4
        // corridor template from the door-mask bucket (native +0xac table / straight 5/10
        // buckets +0x154/+0x130, one FUN_0049c670 draw each) and APPEND {tplId,x,y}.
        // 7. COMMIT loop (:1531): FUN_005842c0(tplId,x,y,0,0,0) each; ANY fail ⇒ return false.
        // Documented interpretations (each flagged at its site):
        // • the exact native door pick (story-template +0x38 entry door) is unmodeled → -1 doors
        // mean "auto-pick the first routable open-door pair" (RouteCorridor).
        // • FUN_0058ee60's flood body + its per-relaxation rand jitter are external + queue-order
        // dependent → NOT byte-reproducible; the flood is realised as a BFS cost-spread over
        // committable 4×4 blocks (all-16-empty, so the emitted pieces never overlap = the native
        // FUN_005842c0 overlap-check that would otherwise fail the route), and the jitter draws
        // are omitted. The RECOVERABLE draws — the variation gate (1/interior cell), switch C
        // (2 on trigger) and the per-segment template pick (1/segment) — ARE consumed in order.
        // • the variation gate's two floats DAT_00737058 (rate scale) + DAT_007376dc (K) and the
        // PERSISTENT global accumulator DAT_00c3c9b8 are not in the extracted.rdata set →
        // modelled by _varRate/_varK with _varAccum reset per generation (a fresh-session start,
        // so short corridors stay straight, matching the near-zero accumulator).
        // • PickCorridor separates the REQUIRED path-direction doors (connectivity) from the
        // OPTIONAL variation branch bit: exact→superset on required|optional, else fall back to
        // required alone. A route fails only when NO pooled 4×4 piece carries the required doors
        // (genuinely untileable — the native's FUN_00587030 all-shapes gate would also fail).
        bool RouteCorridor(Connection conn)
        {
            // OOB node indices → trivial success (FUN_00587480:313-324 returns false only on a real
            // routing failure; an out-of-range/-1 node is a no-op connect). _placed[i] == the forced
            // record i at route time (grow loop hasn't run yet).
            if (conn.SrcRec < 0 || conn.SrcRec >= _placed.Count) return true;
            if (conn.DstRec < 0 || conn.DstRec >= _placed.Count) return true;
            var (sp, sgx, sgy, _) = _placed[conn.SrcRec];
            var (dp, dgx, dgy, _) = _placed[conn.DstRec];

            var srcDoors = DoorCandidates(sp, sgx, sgy, conn.SrcDoor);
            var dstDoors = DoorCandidates(dp, dgx, dgy, conn.DstDoor);
            if (srcDoors.Count == 0 || dstDoors.Count == 0) return true;   // no open door ⇒ -1 trivial path (:310-312)

            int dcx = dgx + dp.W / 2, dcy = dgy + dp.H / 2;
            srcDoors.Sort((a, b) => Sq(a.X - dcx, a.Y - dcy).CompareTo(Sq(b.X - dcx, b.Y - dcy)));
            foreach (var s in srcDoors)
            {
                dstDoors.Sort((a, b) => Sq(a.X - s.X, a.Y - s.Y).CompareTo(Sq(b.X - s.X, b.Y - s.Y)));
                foreach (var d in dstDoors)
                    if (TryRouteBetween(s, d)) return true;   // first routable door pair wins
            }
            return false;   // no corridor could be carved ⇒ the generation fails (native:11)
        }

        static long Sq(int dx, int dy) => (long)dx * dx + (long)dy * dy;

        // A record's open doors as grid-local {X,Y,Dir}: piece Edges → grid pos, kept only if still
        // present in the frontier (a mated door is already connected). doorIdx>=0 selects one edge.
        List<OpenDoor> DoorCandidates(Piece p, int gx, int gy, int doorIdx)
        {
            var list = new List<OpenDoor>();
            for (int i = 0; i < p.Edges.Count; i++)
            {
                if (doorIdx >= 0 && i != doorIdx) continue;
                var e = p.Edges[i];
                int x = gx + e.Lx, y = gy + e.Ly;
                bool open = false;
                foreach (var f in _frontier) if (f.X == x && f.Y == y) { open = true; break; }
                if (open) list.Add(new OpenDoor { X = x, Y = y, Dir = e.Dir });
            }
            return list;
        }

        // 2-tile step-off OUT along the door facing (FUN_00587480:459-482; port dir labels
        // 0=left 1=top 2=right 3=bottom vs the native 0=top 1=right 2=bottom 3=left rotation).
        static void StepOff(OpenDoor d, out int sx, out int sy)
        {
            sx = d.X; sy = d.Y;
            switch (d.Dir) { case 0: sx -= 2; break; case 1: sy -= 2; break; case 2: sx += 2; break; default: sy += 2; break; }
        }

        // Route one door→door corridor (FUN_00587480 steps 3-7): FLOOD a decreasing cost from the DST
        // step-off, GRADIENT-ASCEND back from the SRC step-off (N,S,W,E), run the DIRECTION/VARIATION
        // pass, then emit one 4×4 corridor piece per path cell. Atomic — nothing is placed unless
        // every cell resolves to a piece carrying the REQUIRED doors (the FUN_00587030 tile-ability
        // gate; a genuine miss ⇒ route fails, faithfully).
        bool TryRouteBetween(OpenDoor s, OpenDoor d)
        {
            StepOff(s, out int sx, out int sy);      // switch A step-off (FUN_00587480:503-528)
            StepOff(d, out int dx, out int dy);      // switch B step-off (:564-589)
            var path = GradientPath(sx, sy, dx, dy); // FLOOD + gradient-ascent (:789-961)
            if (path == null) return false;          // SRC step-off never got a cost ⇒ FAIL (:965-982)

            // ext anchors the endpoint door sides: [srcDoor, srcStep … dstStep, dstDoor]. The native
            // anchors on the door-INSIDE points (local_28/2c … local_30/38,:798-945); the door cell
            // gives the identical per-cell facing since inside→step-off is the same axis.
            var ext = new List<(int x, int y)>(path.Count + 2) { (s.X, s.Y) };
            ext.AddRange(path);
            ext.Add((d.X, d.Y));
            int inner = ext.Count - 2;               // interior path cells (== path.Count)

            // REQUIRED door-mask per interior cell = dir-to-prev | dir-to-next (region:1085-1147).
            var req = new int[inner];
            for (int i = 1; i < ext.Count - 1; i++)
                req[i - 1] = (1 << SideTo(ext[i].x, ext[i].y, ext[i - 1].x, ext[i - 1].y))
                           | (1 << SideTo(ext[i].x, ext[i].y, ext[i + 1].x, ext[i + 1].y));

            // DIRECTION/VARIATION pass (region 0x588ce1): OPTIONAL branch bit per interior cell.
            var opt = VaryCorridor(ext, req);

            // SEGMENT-TYPE pass + tile-ability gate (:1284-1531): a matching corridor piece per cell.
            var picks = new List<(int ox, int oy, string tcl)>(inner);
            for (int i = 0; i < inner; i++)
            {
                var (cx, cy) = ext[i + 1];
                var tcl = PickCorridor(req[i], opt[i]);
                if (tcl == null) return false;       // no piece carries the required doors ⇒ FAIL
                picks.Add((cx - 2, cy - 2, tcl));
            }

            // COMMIT loop (:1531 → FUN_005842c0): place each 4×4 corridor piece. The flood only
            // walked all-16-empty blocks (BlockFree) so these never overlap — the native FUN_005842c0
            // overlap-check therefore cannot fail here. MergeEdges mates the chain (src/dst frontier
            // doors consumed, internal doors cancel).
            foreach (var (ox, oy, tcl) in picks)
            {
                var cp = Resolve(tcl)!;
                MergeEdges(cp, ox, oy);
                for (int y = oy; y < oy + 4; y++)
                    for (int x = ox; x < ox + 4; x++)
                        _grid[y * _aw + x] = 2;
                _filled += cp.W * cp.H;
                _placed.Add((cp, ox, oy, 2));
            }
            return true;
        }

        // Side of cell (fx,fy) that faces (tx,ty) — port labels 0=left 1=top 2=right 3=bottom.
        // Consecutive lattice/anchor points differ in exactly one axis, so this is well defined.
        static int SideTo(int fx, int fy, int tx, int ty)
        {
            if (ty < fy) return 1;   // top
            if (ty > fy) return 3;   // bottom
            if (tx < fx) return 0;   // left
            return 2;                 // right
        }

        // FLOOD + GRADIENT-ASCENT (FUN_00587480 step 3-4). The native seeds the DST step-off cell
        // high16 = 0xffff (disasm 0x587a9d) and FUN_0058ee60 spreads a DECREASING cost outward over
        // cells whose LOW16==0 (EMPTY, disasm 0x587b6e) stepping ±4; then it gradient-ASCENDS from the
        // SRC step-off, each step to the highest-cost ±4 neighbour, probe order N,S,W,E (decompiled.c
        //:823-933). We realise this as a BFS distance field from the DST step-off (cost ≡ BIG−dist,
        // so highest cost = lowest dist) over committable 4×4 blocks, then the same N,S,W,E ascent.
        // Returns the SRC→DST path of block centres, or null if the SRC step-off is unreachable
        // (== the native `SRC step-off got no marker` FAIL,:796/:965-982).
        List<(int x, int y)>? GradientPath(int sx, int sy, int dx, int dy)
        {
            if (!BlockFree(sx, sy) || !BlockFree(dx, dy)) return null;
            var dist = new Dictionary<(int, int), int>();
            var q = new Queue<(int, int)>();
            dist[(dx, dy)] = 0; q.Enqueue((dx, dy));
            var steps = new (int, int)[] { (4, 0), (-4, 0), (0, 4), (0, -4) };
            while (q.Count > 0)
            {
                var c = q.Dequeue();
                if (c.Item1 == sx && c.Item2 == sy) break;   // SRC step-off costed — path exists
                foreach (var (ox, oy) in steps)
                {
                    var n = (c.Item1 + ox, c.Item2 + oy);
                    if (dist.ContainsKey(n) || !BlockFree(n.Item1, n.Item2)) continue;
                    dist[n] = dist[c] + 1; q.Enqueue(n);
                }
            }
            if (!dist.ContainsKey((sx, sy))) return null;

            // gradient-ASCEND: from the SRC step-off step to the min-dist (= max native-cost) neighbour
            // that strictly improves, probe order N,S,W,E (the native `uVar1 < neighbourCost` chain,
            // decompiled.c:823-933 — first-found kept on ties). dist strictly decreases → terminates.
            var probe = new (int, int)[] { (0, -4), (0, 4), (-4, 0), (4, 0) };   // N,S,W,E
            var pathR = new List<(int x, int y)> { (sx, sy) };
            var cur = (sx, sy);
            int guard = 0;
            while (cur != (dx, dy) && guard++ < 100000)
            {
                int cd = dist[cur];
                (int, int)? best = null; int bestD = cd;
                foreach (var (ox, oy) in probe)
                {
                    var n = (cur.Item1 + ox, cur.Item2 + oy);
                    if (dist.TryGetValue(n, out int nd) && nd < bestD) { bestD = nd; best = n; }
                }
                if (best == null) return null;   // unreachable (shouldn't happen on a costed field)
                cur = best.Value; pathR.Add(cur);
            }
            return pathR;   // SRC→DST
        }

        // A 4×4 block at centre (cx,cy) (origin cx-2,cy-2) is free iff inside the area and every
        // covered tile is empty (grid==0). Occupied (=2) forced pieces + their tiles block routing.
        // (The native gradient tests only the block-CENTRE tile's LOW16, disasm 0x587b6e; requiring
        // the full block empty is stricter but keeps the emitted 4×4 pieces from overlapping — the
        // job the native's FUN_005842c0 commit-time overlap-check does, unified here into the flood.)
        bool BlockFree(int cx, int cy)
        {
            int ox = cx - 2, oy = cy - 2;
            if (ox < 0 || oy < 0 || ox + 4 > _aw || oy + 4 > _ah) return false;
            for (int y = oy; y < oy + 4; y++)
                for (int x = ox; x < ox + 4; x++)
                    if (_grid[y * _aw + x] != 0) return false;
            return true;
        }

        // ---- corridor DIRECTION/VARIATION pass (FUN_00587480 region 0x588ce1). Per interior path
        // cell it draws ONE gate rand (disasm 0x588ec9); when accum*K beats it, resets the
        // accumulator and draws switch C's TWO rands (0x588efd/0x588f0a) → a perpendicular (dir,bit);
        // if that bit isn't already required and the ±4-offset cell (switch D, 0x588f8a) is EMPTY and
        // mates no frontier door (0x589060 scan), the bit becomes an OPTIONAL branch door (0x5890bc
        // BTS) — a corridor side-branch into empty space, NOT a reroute (path cells are unchanged).
        // The two gate floats DAT_00737058/DAT_007376dc and the persistent accumulator DAT_00c3c9b8
        // are un-extracted → _varRate/_varK conservative + _varAccum reset per generation (fresh
        // session ⇒ short corridors stay straight). Native bits are 1<<orient (0=top/1=right/2=bottom/
        // 3=left); expressed here in the port's 0=left/1=top/2=right/3=bottom space, consistent with
        // the door-mask buckets.
        int[] VaryCorridor(List<(int x, int y)> ext, int[] req)
        {
            int inner = req.Length;
            var opt = new int[inner];
            for (int i = 0; i < inner; i++)
            {
                _varAccum += _varRate;                                   // accum += rate (0x588eb9)
                int gate = Rng.Next() & 0x7fff;                          // gate draw (0x588ec9)
                if (_varAccum * _varK <= gate) continue;                 // no kink (0x588eed JBE)
                _varAccum = 0f;                                          // reset accum (0x588ef3)
                int r1 = Rng.Next() & 0x7fff, r2 = Rng.Next() & 0x7fff;  // switch C draws (0x588efd/f0a)
                int sel = ((r1 & 3) + (r2 & 3)) >> 1;                    // switch C (0x588f25)
                // sel→(port dir,bit): native 0→(3,8)W 1→(0,1)N 2→(2,4)S 3→(1,2)E, mapped to port dirs
                // W=left(0) N=top(1) S=bottom(3) E=right(2):
                (int dir, int bit) = sel switch { 0 => (0, 1), 1 => (1, 2), 2 => (3, 8), 3 => (2, 4), _ => (0, 1) };
                if ((req[i] & bit) != 0) continue;                       // already connected (0x588f59)
                var (cx, cy) = ext[i + 1];
                int ox = cx, oy = cy;                                    // switch D ±4 offset (0x588f8a)
                switch (dir) { case 0: ox -= 4; break; case 1: oy -= 4; break; case 2: ox += 4; break; default: oy += 4; break; }
                if (!BlockFree(ox, oy)) continue;                        // offset cell must be EMPTY (0x58904c)
                bool matesDoor = false;                                  // …and mate no frontier door (0x589060)
                foreach (var f in _frontier)
                    if ((f.X & ~3) == (ox & ~3) && (f.Y & ~3) == (oy & ~3)) { matesDoor = true; break; }
                if (matesDoor) continue;
                opt[i] |= bit;                                           // OR the branch bit (0x5890bc)
            }
            return opt;
        }
        // Corridor-variation gate (FUN_00587480 region 0x588ce1). RECOVERED from Diggles.exe.rdata
        //. Per interior cell: accum += rate; then `accum*K <= rand&0x7fff` ⇒ NO
        // kink (region:145-147; disasm 0x588eb9 ADDSS accum). So `accum` IS the per-step kink
        // PROBABILITY, and:
        // • K = DAT_007376dc @0x7376dc = 32768.0 (hex 00000047) — the SAME rand-scale idiom as
        // FUN_005fc9d0:120 (`rand&0x7fff <= p*_DAT_007376dc`). The old GUESS 250 kinked ~never
        // (250<=rand true 99.2% ⇒ P(kink)≈0.008); the real value ramps 0.25→0.5→0.75→1.0.
        // • rate = base × DAT_00737058 @0x737058 = 0.25 (hex 0000803e; disasm 0x588d0b
        // MULSS [EBP+0x1c],[0x737058], applied ONCE at region entry). The pre-scale base slot
        // [EBP+0x1c] has no surviving writer in the recovered body (the standalone region warns
        // "Unable to track spacebase") ⇒ we take the natural unit base 1.0 ⇒ increment 0.25.
        // _varAccum == DAT_00c3c9b8 — a BSS-resident PERSISTENT accumulator (0 at rest: its VA is
        // beyond raw.data ⇒ zero-init, consistent with the idle dumps and its ABSENCE from every
        // decompiled function except this dropped region). Native keeps it across the whole session;
        // we reset per generation (a fresh-session start ⇒ short corridors near-straight; a session
        // with several utemp caves would carry it — a negligible cosmetic delta on OPTIONAL branches).
        float _varAccum;
        const float _varRate = 0.25f;    // DAT_00737058 (base 1.0 × 0.25 per-cell increment)
        const float _varK = 32768f;      // DAT_007376dc (recovered; was a wrong 250 guess)

        // Interim cap-and-continue diagnostic: connect records the router could not carve this run
        // (the utemp stays placed & uncorridored rather than failing the whole generation). Observable
        // by the headless real-chain diag; a non-zero value on a SUCCESSFUL gen = an uncorridored cave.
        int _corridorsSkipped;
        public int CorridorsSkipped => _corridorsSkipped;

        // Corridor-piece buckets == the native general[16] table at CLevelGenerator+0xac, POPULATED
        // by FUN_00587030 (RECOVERED as the population site the earlier pass could not
        // find — it is NOT merely the emit gate). FUN_00587030 scans the ACTIVE POOL (param_1+0x40
        // ids / +0x44 count — the SAME pool the seed FUN_005862c0:27-56 and cost-sort
        // FUN_00589790:196 iterate, == _pool here) and, per 4×4 piece (W==4&&H==4,:130), buckets its
        // id under general[mask] with mask = OR(1<<edge.dir) (:142-189) — bit0..3 = dir0..3, EXACTLY
        // the `1<<e.Dir` keying in BuildCorridorBuckets. So the port's _pool SOURCE was already
        // faithful; B3's real defect was the superset fallback (see MatchBucket), NOT the source.
        // (FUN_00587030 ALSO fills tunnel-flag straight tables +0x12c/+0x154 indexed by run-length for
        // its MERGED-straight variation; the port emits single 4×4 straights from general[5]/[10],
        // which is faithful for the un-merged case — run-merging is a cosmetic longer-piece
        // refinement, not a connectivity change, and is left unported.)
        Dictionary<int, List<string>>? _corridorBuckets;
        // REQUIRED = path-direction doors (connectivity); OPTIONAL = the variation branch bit. The
        // native ORs the branch INTO the cell mask and indexes general[required|optional] DIRECTLY
        // (region:248-249,:319), so try that combined mask first (a matching branched piece), then
        // fall back to `required` alone (drop the branch = a straight/corner cell). Both are EXACT
        // lookups now — never a superset.
        string? PickCorridor(int required, int optional)
        {
            if (_corridorBuckets == null) BuildCorridorBuckets();
            var hit = MatchBucket(required | optional);                 // branched piece, if the pool has that exact shape
            if (hit == null && optional != 0) hit = MatchBucket(required);   // else the plain required-door piece
            return hit;
        }

        // EXACT-mask pick — faithful to the native emit, which indexes general[mask] DIRECTLY
        // (region 0x588ce1:319-352 / FUN_00587480 decompiled.c:672-694: `general[finalMask].ids
        // [rand % count]`, NO superset scan) and is guaranteed non-empty because FUN_00587030
        // pre-validates general[1..15] ALL populated (:195-206) before the route emits anything.
        // The old SUPERSET fallback (first bucket ⊇ mask) was a PORT INVENTION: it emitted a piece
        // carrying EXTRA open doors → an unmated frontier door → the uncapped wall gap / disconnection
        // that IS symptom B3. Removed — an emitted corridor piece now carries EXACTLY the required
        // doors and can never introduce an uncappable gap. A genuinely-absent mask ⇒ null ⇒ the route
        // cap-and-continues (Start), the same condition under which FUN_00587030 would return 0.
        // One rand consumed per hit (the native per-segment FUN_0049c670 draw).
        string? MatchBucket(int mask)
        {
            if (_corridorBuckets!.TryGetValue(mask, out var exact) && exact.Count > 0)
                return exact[(Rng.Next() & 0x7fff) % exact.Count];
            return null;
        }

        void BuildCorridorBuckets()
        {
            _corridorBuckets = new Dictionary<int, List<string>>();
            foreach (var t in _pool)
            {
                var p = Resolve(t);
                if (p == null || p.W != 4 || p.H != 4 || p.Edges.Count == 0) continue;
                int m = 0; foreach (var e in p.Edges) m |= 1 << e.Dir;
                if (!_corridorBuckets.TryGetValue(m, out var l)) { l = new List<string>(); _corridorBuckets[m] = l; }
                if (!l.Contains(t)) l.Add(t);
            }
        }

        // FUN_005862c0 — seed: first pool piece with W==4 && H==4 && edgeCount==2 (:45-53);
        // ≤16 random 4-aligned positions ≥4 inside the area (:79-102). A forced start satisfies
        // the FIRST call (:24 — +0x198!=0 && state!=-2 → success without a new seed).
        bool SeedOrAdvance(bool anyForced)
        {
            if (_seedState == -1 && anyForced) { _seedState = -2; return true; }
            var seed = FindSeedPiece() ?? (_pool.Count > 0 ? Resolve(_pool[0]) : null);   //:60 fallback
            if (seed == null || seed.W <= 0 || seed.H <= 0) return false;
            int spanX = _aw - seed.W - 4, spanY = _ah - seed.H - 4;                        //:76-78
            if (spanX <= 0 || spanY <= 0) return false;
            int tries = Math.Min(16, ((spanX >> 2) * spanY) >> 2);                          //:81-87
            for (int t = 0; t < Math.Max(1, tries); t++)
            {
                int x = ((Rng.Next() & 0x7fff) % spanX & ~3) + 4;                           //:89-95
                int y = ((Rng.Next() & 0x7fff) % spanY & ~3) + 4;
                if (TryPlace(seed, x, y, 0, 0, false)) { _seedState = -2; return true; }
            }
            return false;
        }

        Piece? FindSeedPiece()
        {
            foreach (var t in _pool)
            {
                var p = Resolve(t);
                if (p != null && p.W == 4 && p.H == 4 && p.Edges.Count == 2) return p;
            }
            return null;
        }

        // FUN_005842c0 — the placement primitive. Grid codes: 0 empty, 2 occupied (interior AND
        // margins), 0x80 = tentative reservation between the paint and the validation.
        bool TryPlace(Piece p, int gx, int gy, int margin, byte flag, bool skipPaintValidate)
        {
            if (((gx | gy) & 3) != 0) return false;                       //:40
            margin &= ~3;                                                  //:41
            if (gx < 0 || gy < 0) return false;                            //:47
            if (gx + p.W > _aw || gy + p.H > _ah) return false;            //:51/:55
            for (int y = gy; y < gy + p.H; y++)                            //:57-79 overlap
                for (int x = gx; x < gx + p.W; x++)
                    if (_grid[y * _aw + x] != 0) return false;
            if (!skipPaintValidate) MergeEdges(p, gx, gy);                 //:81-83
            // margin window on DOORLESS sides (:84-106)
            int f = p.OpenSideFlags;
            int wy0 = (f & 1) != 0 ? gy : Math.Max(0, gy - margin);
            int wy1 = (f & 4) != 0 ? gy + p.H : Math.Min(_ah, gy + p.H + margin);
            int wx0 = (f & 8) != 0 ? gx : Math.Max(0, gx - margin);
            int wx1 = (f & 2) != 0 ? gx + p.W : Math.Min(_aw, gx + p.W + margin);
            for (int y = wy0; y < wy1; y++)                                //:11 tentative 0x80
                for (int x = wx0; x < wx1; x++)
                    _grid[y * _aw + x] |= 0x80;
            if (skipPaintValidate || ValidateFrontier())                   //:144 (FUN_00584120)
            {
                for (int y = wy0; y < wy1; y++)                            //:145-180 window:= 2
                    for (int x = wx0; x < wx1; x++)
                        _grid[y * _aw + x] = 2;
                _filled += p.W * p.H;                                      //:182
                foreach (var kv in p.ObjCounts)                            //:183-198
                    _objPlaced[kv.Key] = (_objPlaced.TryGetValue(kv.Key, out var v) ? v : 0f) + kv.Value;
                p.PlacedThisLevel++;                                       //:200
                _placed.Add((p, gx, gy, flag));                            //:199-218
                if (p.ObjectBearing) _objPieces.Add((gx, gy, p.W * p.H));  //:219-231
                return true;
            }
            for (int y = wy0; y < wy1; y++)                                //:235-270 rollback
                for (int x = wx0; x < wx1; x++)
                    _grid[y * _aw + x] &= 0x7f;
            MergeEdges(p, gx, gy);                                          //:271 undo the toggle
            return false;
        }

        // FUN_005848b0 — the forced/start template placement: window CLAMPED to the area (partial
        // overhang allowed), map inset gate, live-map probe, no margins, flag byte 1. Runs the
        // full frontier validator (:145 → FUN_00584120) with NO door dropping — a forced piece
        // whose door lands <2 cells from the area border FAILS, and FUN_005896a0 then fails the
        // whole generation (the story failcount retry channel). The old "drop border doors"
        // stand-in predated the FUN_0058a510/FUN_005fc0a0 RE: the native never needs it because
        // the pre-stepped anchor + the dig-direction rect keep the start piece's doors interior.
        bool ForcePlace(Piece p, int gx, int gy)
        {
            if (((gx | gy) & 3) != 0) { LastForceDetail = $"not 4-aligned ({gx},{gy})"; return false; }   //:29
            int wx = _areaX0 + gx, wy = _areaY0 + gy;
            if (MapW > 0 && MapH > 0                                        //:37-46 map inset ≥8
                && (wx < 8 || wy < 8 || wx + p.W >= MapW - 8 || wy + p.H >= MapH - 8))
            { LastForceDetail = $"map inset: world({wx},{wy}) +{p.W}x{p.H} vs map {MapW}x{MapH}"; return false; }
            if (TileBlocked != null)                                        //:48-61 live-map probe
                for (int y = wy; y < wy + p.H; y++)
                    for (int x = wx; x < wx + p.W; x++)
                        if (TileBlocked(x, y)) { LastForceDetail = $"map blocked (HasTpl) @({x},{y})"; return false; }
            int cx0 = Math.Max(0, gx), cy0 = Math.Max(0, gy);               //:63-79 clamp
            int cx1 = Math.Min(_aw, gx + p.W), cy1 = Math.Min(_ah, gy + p.H);
            for (int y = cy0; y < cy1; y++)                                 //:80-102 overlap
                for (int x = cx0; x < cx1; x++)
                    if (_grid[y * _aw + x] != 0) { LastForceDetail = $"grid overlap @({x},{y})=g{_grid[y * _aw + x]} (area {_aw}x{_ah})"; return false; }
            MergeEdges(p, gx, gy);                                          //:103
            for (int y = cy0; y < cy1; y++)                                 //:104-140 paint 2
                for (int x = cx0; x < cx1; x++)
                    _grid[y * _aw + x] = 2;
            if (!ValidateFrontier())                                        //:145
            {
                for (int y = cy0; y < cy1; y++)                             //:171-196 rollback
                    for (int x = cx0; x < cx1; x++)
                        _grid[y * _aw + x] = 0;
                MergeEdges(p, gx, gy);                                      //:197
                return false;
            }
            _filled += (cx1 - cx0) * (cy1 - cy0);                           //:148
            _placed.Add((p, gx, gy, 1));                                    //:150-168 flag=1
            return true;
        }

        // FUN_00583f60 — frontier toggle: COORDINATE match (no dir test) removes (swap-last),
        // else append.
        void MergeEdges(Piece p, int dx, int dy)
        {
            foreach (var e in p.Edges)
            {
                int x = e.Lx + dx, y = e.Ly + dy;
                int mi = -1;
                for (int i = 0; i < _frontier.Count; i++)
                    if (_frontier[i].X == x && _frontier[i].Y == y) { mi = i; break; }
                if (mi >= 0)
                {
                    _frontier[mi] = _frontier[_frontier.Count - 1];
                    _frontier.RemoveAt(_frontier.Count - 1);
                }
                else _frontier.Add(new OpenDoor { X = x, Y = y, Dir = e.Dir });
            }
        }

        // set by ForcePlace/ValidateFrontier on failure — surfaced in LastFailReason so a Play log
        // names WHICH check rejected a forced (start/composite) record.
        public string LastForceDetail = "";

        // FUN_00584120 — every open frontier point must be ≥2 cells inside the area border
        // (:35-43) and must keep ≥1 of its 4 diagonal grid cells free (:44-83).
        bool ValidateFrontier()
        {
            foreach (var d in _frontier)
            {
                if (d.X < 2 || d.Y < 2 || d.X > _aw - 2 || d.Y > _ah - 2)
                { LastForceDetail = $"frontier-validate: door({d.X},{d.Y}) <2 tiles from border (area {_aw}x{_ah})"; return false; }
                if (_grid[(d.Y - 1) * _aw + d.X - 1] != 0 && _grid[(d.Y - 1) * _aw + d.X] != 0
                    && _grid[d.Y * _aw + d.X - 1] != 0 && _grid[d.Y * _aw + d.X] != 0)
                { LastForceDetail = $"frontier-validate: door({d.X},{d.Y}) buried (all 4 corner cells filled)"; return false; }
            }
            return true;
        }

        // FUN_00586710 — the cost heuristic. All constants extracted from Diggles.exe.rdata:
        // 1/32768 @00736f94, 0.3 @00737060, 0.5 @0073708c, 0.6 @0073709c, 0.8 @007370d4,
        // 1.0 @00737138, 1.3 @00737180, 1.5 @007371c0, 2.0 @00737330, 2.5 @007373d8,
        // 3.0 @00737428, 10 @00737534, 100 @0073760c, 200 @00737644, 250 @00737650,
        // −1 @0073773c. Lower = better; INT_MAX-family values stay in the sorted candidate
        // list as last resorts.
        int Cost(Piece p, OpenDoor P)
        {
            // 1) per-class MAX rejects (:33-56)
            foreach (var cls in _filterClasses)
            {
                if (!_objCons.TryGetValue(cls, out var c)) continue;
                float placed = _objPlaced.TryGetValue(cls, out var v) ? v : 0f;
                int add = p.ObjCounts.TryGetValue(cls, out var a) ? a : 0;
                if (placed + add > c.Max) return int.MaxValue;
            }
            // 2) object-bearing: once per SESSION + spacing |dx|*|dy| < r*1.5 (:58-91). The
            // once-gate is +0x50 (EmittedThisSession) NOT +0x24 (PlacedThisLevel): a variant
            // ships at most once per SESSION (bumped by lg_get_level, never reset), which
            // THINS the object-cave pool across generations and is what forces later
            // breakthroughs onto different object caves. (The old PlacedThisLevel gate was
            // once-per-GENERATION — it re-admitted the same object caves every generation, the
            // core of "same caves generated repeatedly". Within one level the native allows
            // repeats, held back only by the spacing check below + the explicit levelcount cap.)
            if (p.ObjectBearing)
            {
                if (p.EmittedThisSession > 0) return int.MaxValue;
                foreach (var (ox, oy, r) in _objPieces)
                    if (Math.Abs(ox - P.X) * Math.Abs(oy - P.Y) < r * 1.5f) return int.MaxValue;
            }
            // 3) fill progress vs the OPT ratio (:92-96)
            float total = _aw * _ah;
            float progress = _ratioOpt == 0f ? 1f : ((float)_filled / total) / _ratioOpt;
            // 4) seed pieces are last resorts mid-growth (:97-100)
            if (p.W == 4 && p.H == 4) return 0x7ffffff9 + p.Edges.Count;
            // 5) overshoot vs the MAX ratio (:101-119)
            int area = p.W * p.H;
            float over = 1f;
            if (area != 0 && _ratioMax != 0f)
            {
                over = ((_filled + area) / total) / _ratioMax;
                if (over > 1f) return int.MaxValue;
            }
            // 6) level cap (:120-122)
            if (p.LevelCap <= p.PlacedThisLevel) return int.MaxValue;
            // 7) size pressure (:123-126): |20−(W+H)| * (2 − progress), doubled in the final sum
            float sizeTerm = Math.Abs(20 - p.H - p.W) * (2f - progress);
            // 8) door-count band (:127-139): the target door count shrinks as the cluster fills;
            // past 80% the 1-door cap preference is ×10 — this is what CLOSES the cluster.
            int edges = p.Edges.Count;
            float band = 0f;
            if (progress >= 0f && progress < 0.3f) band = Math.Abs(3.0f - edges);
            else if (progress >= 0.3f && progress < 0.6f) band = Math.Abs(2.5f - edges);
            else if (progress >= 0.6f && progress < 0.8f) band = Math.Abs(2.0f - edges);
            else if (progress >= 0.8f) band = Math.Abs(1.0f - edges) * 10f;
            // 9) object-count pressure (:140-208): rewards pieces moving deficit classes toward
            // the stride target (ore/mushroom spread)
            float sum = 0f; int n = 0;
            foreach (var cls in _filterClasses)
            {
                if (!_objCons.TryGetValue(cls, out var c) || c.Stride == -1f) continue;
                n++;
                if (c.Stride == 0f) { sum += 1.3f; continue; }
                float placed = _objPlaced.TryGetValue(cls, out var v) ? v : 0f;
                int add = p.ObjCounts.TryGetValue(cls, out var a) ? a : 0;
                float a1 = progress != 0f ? Math.Abs(c.Stride - placed / progress) : 0f;
                float a2 = Math.Abs(c.Stride - (placed + add) / over);
                sum += (a2 - a1) / c.Stride;
            }
            float objPressure = n == 0 ? 1f : Math.Abs(sum / n + 1f);
            // 10) tunnel directional constraint + clearance probes (:219-262). Native dir labels
            // are 0=top 1=right 2=bottom 3=left (FUN_005826d0:285-330): flag 1 gates on
            // native {1,3} = LEFT/RIGHT doors (port {0,2}) with vertical clearance, flag 2 on
            // native {0,2} = TOP/BOTTOM doors (port {1,3}) with horizontal clearance.
            if (p.TunnelFlag == 1)
            {
                if (P.Dir != 0 && P.Dir != 2) return int.MaxValue;
                if (P.Y - 4 < 0 || P.Y + 4 >= _ah || P.X < 0 || P.X >= _aw) return int.MaxValue;
                if (_grid[(P.Y - 4) * _aw + P.X] != 0 || _grid[(P.Y + 4) * _aw + P.X] != 0) return 0x7ffffffe;
            }
            else if (p.TunnelFlag == 2)
            {
                if (P.Dir != 1 && P.Dir != 3) return int.MaxValue;
                if (P.X - 4 < 0 || P.X + 4 >= _aw || P.Y < 0 || P.Y >= _ah) return int.MaxValue;
                if (_grid[P.Y * _aw + P.X - 4] != 0 || _grid[P.Y * _aw + P.X + 4] != 0) return 0x7ffffffe;
            }
            // 11) final (:210-268). capTerm = (objectBearing ? min(+0x1c gamecount-remaining, 5)
            //: 5) * 200 (FUN_00586710:28). +0x1c is the -gamecount REMAINING, NOT the
            // object-spawn count: with shipped data it is INT_MAX for every piece except the
            // four lginit -gamecount-1 entries, so capTerm = 1000 flat EXCEPT the two
            // object-bearing set-piece caves urw_gng_051_a/054_a (→200, an intentional −800
            // discount for the one-shot Wuker/Schatztonne caves). The old min(ObjTotal,5)
            // reading gave 58 of 185 in-pool pieces a spurious −200..−800 discount that
            // dwarfed every legitimate cost separation (band 100, size ~13) ⇒ the same
            // low-object object pieces won every dock deterministically — THE headline
            // "same caves" bug.
            float capTerm = (p.ObjectBearing ? Math.Min(p.GameCap, 5) : 5) * 200f;
            float repeat = p.PlacedThisLevel * 250f;
            float jitter = (Rng.Next() & 0x7fff) * (1f / 32768f) * (1f - progress);
            double cost = (2f * sizeTerm + band * 100f + capTerm + repeat + jitter + 0.5f)
 * p.Value * 100f * objPressure;
            if (cost >= int.MaxValue) return int.MaxValue - 8;
            if (cost < int.MinValue + 8) return int.MinValue + 8;
            return (int)Math.Round(cost);
        }

        // lg_get_level → Tcl list of {name x y} triples (empty on failure). The ENGINE BINDING
        // also marks each placement's story-cell rect (FUN_00652d10:276-279, via LastLevelRects),
        // clears the per-generation objcount constraints, and — the growth PACING law — zeroes
        // the StoryMgr dig-pressure counter when placements were emitted (FUN_00652d10:73/:280:
        // local_30 = FUN_005f8080 is the STORYMGR singleton, +0x98 = the DigNotify weight).
        public string GetLevel()
        {
            var sb = new StringBuilder();
            foreach (var (tcl, x, y) in _level) { if (sb.Length > 0) sb.Append(' '); sb.Append('{').Append(tcl).Append(' ').Append(x).Append(' ').Append(y).Append('}'); }
            return sb.ToString();
        }

        // lg_get_level bumps +0x50 (EmittedThisSession) once per emitted OBJECT-BEARING placement
        // (FUN_00652d10:84-93) — the session-thinning that stops an object-cave variant from being
        // generated again this session (the object-piece cost gate rejects on >0). NEVER reset within
        // a generator's life. Call ONCE per successful lg_get_level, from the engine binding.
        public void CommitSessionEmission()
        {
            foreach (var (p, _, _) in _placedOut)
                if (p.ObjectBearing) p.EmittedThisSession++;
        }

        public void ClearObjCounts() => _objCons.Clear();   // per-generation constraints

        // Placement rects for the story-map marking (world tiles).
        public IReadOnlyList<(string tcl, int x, int y, int w, int h)> LastLevelRects()
        {
            var list = new List<(string, int, int, int, int)>();
            foreach (var (p, x, y) in _placedOut) list.Add((p.TclName, x, y, p.W, p.H));
            return list;
        }

        // lg_sort_level sx sy {triples} → same set, nearest-first by piece origin.
        public string SortLevel(int sx, int sy, IEnumerable<(string tcl, int x, int y)> items)
        {
            var list = new List<(string tcl, int x, int y)>(items);
            list.Sort((a, b) => Dist2(a, sx, sy).CompareTo(Dist2(b, sx, sy)));
            var sb = new StringBuilder();
            foreach (var (tcl, x, y) in list) { if (sb.Length > 0) sb.Append(' '); sb.Append('{').Append(tcl).Append(' ').Append(x).Append(' ').Append(y).Append('}'); }
            return sb.ToString();
            static long Dist2((string tcl, int x, int y) it, int sx, int sy) { long dx = it.x - sx, dy = it.y - sy; return dx * dx + dy * dy; }
        }

        // ===================== metadata loading (.tcl → pmp base,.pmp dims,.ent doors) =====================

        Piece? Resolve(string tcl)
        {
            tcl = TrimTcl(tcl);
            if (!_pieces.TryGetValue(tcl, out var p)) return null;
            if (!p.MetaLoaded) LoadMeta(p);
            return p;
        }

        // Resolve, creating transient metadata for non-pool templates (composite sub-templates).
        Piece? ResolveOrLoad(string tcl)
        {
            tcl = TrimTcl(tcl);
            if (tcl.Length == 0) return null;
            if (!tcl.EndsWith(".tcl", StringComparison.OrdinalIgnoreCase)) tcl += ".tcl";
            if (!_pieces.ContainsKey(tcl)) _pieces[tcl] = CreatePiece(tcl, "transient");
            return Resolve(tcl);
        }

        void UnregisterInvalidPiece(Piece p)
        {
            _pieces.Remove(p.TclName);
            _order.Remove(p);
            _pool.RemoveAll(t => string.Equals(t, p.TclName, StringComparison.OrdinalIgnoreCase));
            foreach (var group in _groups.Values)
                group.Tcls.RemoveAll(t => string.Equals(t, p.TclName, StringComparison.OrdinalIgnoreCase));
        }

        void LoadMeta(Piece p)
        {
            p.MetaLoaded = true;   // mark first so a failed load doesn't retry every tick
            try
            {
                // 1) pmp base = the `map_template <base>.pmp` arg inside the template's
                // MapTemplateSet; SPLIT parents carry dims in the header instead. While
                // scanning, count `new <Class>` spawns of the tracked filter classes
                // (the def+0x34 object metadata the native loader builds).
                string tclPath = FindFile(Path.Combine("templates", "tcl", p.TclName))
                              ?? FindFile(Path.Combine("templates", p.TclName));
                bool split = false;
                int detail = 2;   // the running loading_objdetail bracket (native local_230, init 2)
                if (tclPath != null)
                {
                    foreach (var raw in File.ReadLines(tclPath))
                    {
                        int si = raw.IndexOf("split_template", StringComparison.Ordinal);
                        if (si >= 0 && !split)
                        {
                            var st = raw.Substring(si).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                            if (st.Length >= 5 && int.TryParse(st[3], out var sw) && int.TryParse(st[4], out var sh))
                            { p.W = sw; p.H = sh; }
                            // A split parent's DIMS come from the header (arg3/arg4 = W H); its DOORS
                            // come from the MERGED pmp-base.ent (arg2) — the split CHILDREN's.ents
                            // are empty. The native resolves EVERY template through FUN_00583080 →
                            // FUN_005826d0, whose.ent door read (:146+) runs OUTSIDE the split guard
                            // (:86 only skips the pmp-dims re-read for split, +0x40). Losing these
                            // doors was THE composite bug: a doorless split sub-piece never MATES
                            // (MergeEdges is a coord-toggle), so its tiling neighbour's reciprocal
                            // seam door stays in the frontier and gets BURIED once both bodies paint
                            // around it → FUN_00584120 rejects → the whole composite generation fails
                            // (verified: voodoo_dorf dies on vodo_002 burying vodo_003's (108,46);
                            // einsiedler dies on gng_003_a's (92,34) buried by doorless raetsel_001).
                            if (st.Length >= 3 && st[2].EndsWith(".pmp", StringComparison.OrdinalIgnoreCase)
                                && p.PmpBase.Length == 0)
                                p.PmpBase = Path.GetFileNameWithoutExtension(st[2]);
                            split = true;
                            continue;
                        }
                        int mi = raw.IndexOf("map_template", StringComparison.Ordinal);
                        if (mi >= 0 && p.PmpBase.Length == 0)
                        {
                            var rest = raw.Substring(mi + "map_template".Length).Trim();
                            var tok = rest.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                            if (tok.Length > 0 && tok[0].EndsWith(".pmp", StringComparison.OrdinalIgnoreCase))
                                p.PmpBase = Path.GetFileNameWithoutExtension(tok[0]);
                        }
                        // loading_objdetail <level> — the deco-entry detail bracket
                        // (FUN_00581d30:316-333: 'h'→2 'm'→1 'l'→0 'o'(ff)→0, digits verbatim).
                        int li = raw.IndexOf("loading_objdetail", StringComparison.Ordinal);
                        if (li >= 0)
                        {
                            var arg = raw.Substring(li + "loading_objdetail".Length).Trim();
                            detail = arg.Length == 0 ? detail
                                : arg[0] switch
                                {
                                    'h' => 2,
                                    'm' => 1,
                                    'l' => 0,
                                    'o' => 0,
                                    _ => int.TryParse(arg.Split(' ', '\t')[0], out var dv) ? dv : detail,
                                };
                            continue;
                        }
                        // object spawns: `... [new Pilz...]` / `new Troll...`
                        int ni = raw.IndexOf("new ", StringComparison.Ordinal);
                        if (ni >= 0)
                        {
                            var tok = raw.Substring(ni + 4).Split(new[] { ' ', '\t', '[', ']' }, StringSplitOptions.RemoveEmptyEntries);
                            if (tok.Length > 0)
                            {
                                var cls = tok[0].Trim('"', '{', '}');
                                foreach (var fc in _filterClasses)
                                    if (string.Equals(fc, cls, StringComparison.OrdinalIgnoreCase))
                                    {
                                        p.ObjCounts[fc] = (p.ObjCounts.TryGetValue(fc, out var c) ? c : 0) + 1;
                                        p.ObjTotal++;
                                        p.ObjectBearing = true;
                                        break;
                                    }
                                // The during-dig deco set (FUN_00581d30:125-268) — non-split
                                // templates only (a split header carries +0x48 = -2 = no deco;
                                // its sub-templates are recursed for objCounts, not deco).
                                if (!split && TryParseDecoSpawn(raw, ni, out var de))
                                {
                                    de.Detail = detail;
                                    p.Deco.Add(de);
                                }
                            }
                        }
                    }
                }
                p.Split = split;
                if (string.IsNullOrEmpty(p.PmpBase)) return;
                // 2) dims from the PMP header — NON-SPLIT only. A split parent keeps its split-header
                // W/H (FUN_005826d0:86 skips the pmp-dims re-read when the +0x40 split flag is set).
                if (!split)
                {
                    string? pmpPath = FindFile(Path.Combine("Templates", "pmp", p.PmpBase + ".pmp"))
                                    ?? FindFile(Path.Combine("templates", "pmp", p.PmpBase + ".pmp"));
                    if (pmpPath != null)
                    {
                        var map = PmpParser.Parse(pmpPath);
                        p.W = (int)map.Width; p.H = (int)map.Height;
                    }
                }
                // 3) doors from the.ent sidecar: `map_entry 1 Xpx Ypx` — the EXACT FUN_005826d0
                // law (:219-231): 4-aligned base ((px+sign*7)>>3 & ~3) plus a HALF-CELL +2
                // offset derived from the in-cell remainders (px%32 vs py%32) that encodes the
                // edge orientation: (qx+2, qy) = a horizontal (top/bottom) edge door,
                // (qx, qy+2) = a vertical (left/right) edge door. The dir then comes STRICTLY
                // from the boundary (Lx==0 left / Ly==0 top / Lx==W right / Ly==H bottom,
                //:285-330); non-boundary doors are DROPPED (:334-346). The old plain-corner
                // quantization collapsed the +2 — every 4-wide vertical corridor's doors read
                // as LEFT doors, which broke vertical docking and the closure law.
                string? entPath = FindFile(Path.Combine("templates", "ent", p.PmpBase + ".ent"));
                if (entPath != null)
                    foreach (var raw in File.ReadLines(entPath))
                    {
                        var t = raw.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (t.Length < 4 || t[0] != "map_entry" || t[1] != "1") continue;
                        if (!int.TryParse(t[2], out int xp) || !int.TryParse(t[3], out int yp)) continue;
                        int qx = Quant(xp), qy = Quant(yp);
                        int rx = ((xp % 32) + 32) % 32, ry = ((yp % 32) + 32) % 32;   //:225-231
                        if (ry < rx)
                        {
                            if (rx + ry < 32) qx += 2;              // top/bottom edge, mid-x (:234-243)
                            else { qx += 4; qy += 2; }              // right edge (:245-254)
                        }
                        else
                        {
                            if (rx + ry < 32) qy += 2;              // left/right edge, mid-y (:256-263)
                            else { qx += 2; qy += 4; }              // bottom edge (:265-274)
                        }
                        // boundary → dir (:285-330; port labels 0=left 1=top 2=right 3=bottom)
                        int dir = qy == 0 ? 1 : qx == 0 ? 0 : qx == p.W ? 2 : qy == p.H ? 3 : -1;
                        if (dir < 0) continue;                       // non-boundary door dropped (:334)
                        p.Edges.Add(new Edge { Lx = qx, Ly = qy, Dir = dir });
                    }
                RecomputeOpenSides(p);
            }
            catch { /* missing/garbled template → no doors; the grower just won't extend from it */ }
        }

        static void RecomputeOpenSides(Piece p)
        {
            int f = 0;
            foreach (var e in p.Edges)
                f |= e.Dir switch { 1 => 1, 2 => 2, 3 => 4, _ => 8 };
            p.OpenSideFlags = f;
        }

        static int Quant(int px) { int s = px >= 0 ? 1 : -1; return ((px + s * 7) >> 3) & ~3; }

        // Parse one template deco spawn line — the FUN_00581d30:125-268 law over the uniform
        // machine-generated shape `new <Class> "" "[expr {X + $xoffset}] [expr {Y + $yoffset}] Z"
        // "rx ry rz"`: X/Y = the leading strtod of each [expr …] (the template-relative offset
        // before the $xoffset/$yoffset add), Z = the float after the position string's last
        // `]`, rotation = the three floats of the next quoted string (native separator
        // `" "` =.rdata 0x72a864). Lines without the [expr position form carry no deco
        // (native:150-158 falls through to the objcount scan only).
        static bool TryParseDecoSpawn(string raw, int ni, out DecoEntry de)
        {
            de = default;
            var rest = raw.Substring(ni + 4);
            int e = 0;
            while (e < rest.Length && !char.IsWhiteSpace(rest[e]) && rest[e] != '"' && rest[e] != ']') e++;
            var cls = rest.Substring(0, e).Trim('{', '}');
            if (cls.Length == 0) return false;

            int i1 = rest.IndexOf("[expr", StringComparison.Ordinal);
            if (i1 < 0) return false;
            if (!LeadFloat(rest, SkipExprHead(rest, i1), out float x)) return false;
            int i2 = rest.IndexOf("[expr", i1 + 5, StringComparison.Ordinal);
            if (i2 < 0) return false;
            int yEnd = SkipExprHead(rest, i2);
            if (!LeadFloat(rest, yEnd, out float y)) return false;
            // Z: after the second expr's closing bracket (native strstr from the y cursor).
            int zi = rest.IndexOf(']', yEnd);
            if (zi < 0) return false;
            zi++;
            while (zi < rest.Length && (rest[zi] == ']' || rest[zi] == ' ' || rest[zi] == '\t')) zi++;
            if (!LeadFloat(rest, zi, out float z)) return false;
            // Rotation: the next `" "` quoted-string boundary, three floats.
            float rx = 0, ry = 0, rz = 0;
            int qi = rest.IndexOf("\" \"", zi, StringComparison.Ordinal);
            if (qi >= 0)
            {
                int c = qi + 3;
                if (LeadFloat(rest, c, out rx))
                {
                    c = SkipFloat(rest, c);
                    if (LeadFloat(rest, c, out ry))
                    {
                        c = SkipFloat(rest, c);
                        LeadFloat(rest, c, out rz);
                    }
                }
            }
            de = new DecoEntry { ClassName = cls, X = x, Y = y, Z = z, Rx = rx, Ry = ry, Rz = rz };
            return true;
        }

        // Skip past `[expr ` / `[expr {` to the leading numeral (native +6/+7).
        static int SkipExprHead(string s, int i)
        {
            i += 5;
            while (i < s.Length && (s[i] == ' ' || s[i] == '{')) i++;
            return i;
        }

        // The strtod shape: optional sign + digits + optional fraction (culture-invariant).
        static bool LeadFloat(string s, int i, out float v)
        {
            v = 0;
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
            int st = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
            if (i == st || (i == st + 1 && !char.IsDigit(s[st]))) return false;
            return float.TryParse(s.Substring(st, i - st), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out v);
        }

        static int SkipFloat(string s, int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
            return i;
        }

        string? FindFile(string rel)
        {
            if (string.IsNullOrEmpty(DataRoot)) return null;
            string full = Path.Combine(DataRoot, rel);
            return File.Exists(full) ? full : null;
        }

        static string TrimTcl(string s) => s.Trim().Trim('{', '}', '"', ' ');

        // ---- test seam: inject a synthetic piece (no file IO) ----
        public void AddSyntheticPiece(string name, int w, int h, params (int lx, int ly, int dir)[] doors)
        {
            var p = CreatePiece(name, "syn");
            p.PmpBase = name; p.W = w; p.H = h; p.MetaLoaded = true;
            foreach (var (lx, ly, dir) in doors) p.Edges.Add(new Edge { Lx = lx, Ly = ly, Dir = dir });
            RecomputeOpenSides(p);
            _pieces[name] = p;
            if (!_pool.Contains(name)) _pool.Add(name);
        }

        // Test seam for the during-dig deco pass: a fully synthetic template with a deco set
        // (no file IO — inject the matching shape via MapDecoBeautifier.InjectShape).
        public Piece AddSyntheticDecoPiece(string name, int w, int h, int zone,
            params (string cls, float x, float y, float z, int detail)[] deco)
        {
            var p = CreatePiece(name, "syn");
            p.PmpBase = name; p.W = w; p.H = h; p.MetaLoaded = true; p.Zone = zone;
            foreach (var (cls, x, y, z, detail) in deco)
                p.Deco.Add(new DecoEntry { ClassName = cls, X = x, Y = y, Z = z, Detail = detail });
            _pieces[name] = p;
            return p;
        }
        // Object-bearing synthetic piece (spawns objClass) with a -gamecount cap → the cost capTerm
        // discount (gameCap 1 ⇒ capTerm 200 vs the flat 1000) + the per-session emit gate. For the
        // 5a/5b variety-law tests.
        public void AddSyntheticObjectPiece(string name, int w, int h, string objClass, int gameCap,
                                            params (int lx, int ly, int dir)[] doors)
        {
            AddFilterClass(objClass);
            var p = CreatePiece(name, "syn");
            p.PmpBase = name; p.W = w; p.H = h; p.MetaLoaded = true;
            p.ObjectBearing = true; p.ObjTotal = 1; p.GameCap = gameCap;
            p.ObjCounts[objClass] = 1;
            foreach (var (lx, ly, dir) in doors) p.Edges.Add(new Edge { Lx = lx, Ly = ly, Dir = dir });
            RecomputeOpenSides(p);
            _pieces[name] = p;
            if (!_pool.Contains(name)) _pool.Add(name);
        }
        public IReadOnlyList<(string tcl, int x, int y)> LastLevel => _level;
        // The PMP base a template's meta resolved to (tests compare stamped world data verbatim).
        public string? GetPmpBase(string tcl) { var p = Resolve(TrimTcl(tcl)); return string.IsNullOrEmpty(p?.PmpBase) ? null : p!.PmpBase; }
        // Door metadata (test seam): the composite door-mating fix asserts a SPLIT parent now loads
        // its merged-ent doors (was 0 before — the buried-seam composite bug).
        // ResolveOrLoad (not Resolve) so the piece loads on-demand from disk even when the pool
        // isn't registered — DoorCount/GetDoors are test/inspection helpers on a bare generator.
        public int DoorCount(string tcl) { var p = ResolveOrLoad(tcl); return p?.Edges.Count ?? 0; }
        public IReadOnlyList<(int lx, int ly, int dir)> GetDoors(string tcl)
        {
            var list = new List<(int, int, int)>();
            var p = ResolveOrLoad(tcl);
            if (p != null) foreach (var e in p.Edges) list.Add((e.Lx, e.Ly, e.Dir));
            return list;
        }
        public int PoolCount => _pool.Count;
        public bool HasGroup(string name) => _groups.ContainsKey(name);
        public string DebugGroups() => string.Join(" | ", _groups.Keys);
        public int PieceCount => _pieces.Count;
        public int GroupLevelCount(string name) => _groups.TryGetValue(name, out var g) ? g.LevelCount : int.MaxValue;
        // Object-cave variety laws: -gamecount → the capTerm
        // discount (+0x1c); the per-SESSION object-emit gate (+0x50).
        public int GetGameCap(string tcl) => Resolve(TrimTcl(tcl)) is { } p ? p.GameCap : int.MaxValue;
        public bool IsObjectBearing(string tcl) => Resolve(TrimTcl(tcl))?.ObjectBearing ?? false;
        public int SessionEmitCount(string tcl) => _pieces.TryGetValue(TrimTcl(tcl), out var p) ? p.EmittedThisSession : 0;

        // ---- test seam (B10): run the FUN_00585390 finalize on a HAND-BUILT placement set. Inits an
        // empty run grid, paints each placement's footprint (=2) and appends it to _placed with the
        // given flag, then runs FinalizePrune and publishes _placed to LastLevel / LastLevelRects. The
        // pass reads only _placed + _grid (never the frontier), so this exercises it in isolation.
        public void TestRunFinalize(int areaW, int areaH, IEnumerable<(string tcl, int gx, int gy, byte flag)> placements)
        {
            SetArea(0, 0, areaW, areaH);
            _aw = areaW; _ah = areaH; _grid = new int[_aw * _ah];
            _placed.Clear(); _frontier.Clear(); _filled = 0; _level.Clear(); _placedOut.Clear();
            foreach (var (tcl, gx, gy, flag) in placements)
            {
                var p = Resolve(tcl);
                if (p == null || p.W <= 0 || p.H <= 0) continue;
                for (int y = gy; y < gy + p.H; y++)
                    for (int x = gx; x < gx + p.W; x++)
                        if (x >= 0 && y >= 0 && x < _aw && y < _ah) _grid[y * _aw + x] = 2;
                _placed.Add((p, gx, gy, flag));
                _filled += p.W * p.H;
            }
            FinalizePrune();
            foreach (var (p, gx, gy, _) in _placed)
            { _placedOut.Add((p, _areaX0 + gx, _areaY0 + gy)); _level.Add((p.TclName, _areaX0 + gx, _areaY0 + gy)); }
        }
        // Placements + their flag bytes after a run (B10: 1 = forced/re-placed/protected, 0 = grow-loop).
        public IReadOnlyList<(string tcl, int x, int y, int flag)> TestPlacedWithFlags()
        {
            var list = new List<(string, int, int, int)>();
            foreach (var (p, gx, gy, flag) in _placed) list.Add((p.TclName, gx, gy, flag));
            return list;
        }

        // ---- test seam (B8): init an empty run grid, paint the registered reserved (courtyard) rects
        // (the FUN_00589790:117-171 mark-area step), and report whether cell (cellX,cellY) is now
        // blocked (=1, so TryPlace would reject it). Deterministic — exercises the reservation paint
        // without a grow.
        public bool TestReservationBlocks(int areaW, int areaH, int cellX, int cellY)
        {
            SetArea(0, 0, areaW, areaH);
            _aw = areaW; _ah = areaH; _grid = new int[_aw * _ah];
            PaintReservations();
            return cellX >= 0 && cellY >= 0 && cellX < _aw && cellY < _ah && _grid[cellY * _aw + cellX] != 0;
        }
    }
}
