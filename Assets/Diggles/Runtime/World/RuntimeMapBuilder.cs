#nullable enable

namespace Diggles.Runtime.World
{
    using System.Collections.Generic;
    using System.IO;
    using Diggles.Runtime.Tcl.Engine;
    using UnityEngine;

    // Runtime IDigglesMapBuilder. Records the Tcl-driven sequence of
    // `map create` / `map_template` / `map_setlayer2` calls, resolves
    // filenames against the data root, and on EmitTerrain routes through
    // BdbPmpWorldBuilder.BuildFromTclDriven to produce the BDB-block terrain
    // mesh + nav graph. Used by both the editor Tcl Spawn Test and the
    // runtime DigglesWorldRuntime loader.
    //
    // Markers DON'T come through here — they spawn via the Tcl `new`
    // command + EditorEntitySpawner (Phase 4). So EmitTerrain runs with
    // `spawnObjectMarkers: false`.
    //
    // Path resolution mirrors TclCampaignParser.ResolveDataPath but tries
    // more subdirs because the Tcl-driven path sees BARE filenames from
    // inside MapTemplateSet bodies (e.g. `map_template hol_024.pmp...`),
    // not just the data-rel paths the static parser caught.
    public sealed class RuntimeMapBuilder : IDigglesMapBuilder
    {
        readonly string _dataRoot;
        readonly string _bdbPath;
        readonly List<(string absPath, int x, int y)> _pmps = new();
        readonly List<(string absPath, int x, int y)> _l2ms = new();
        readonly List<string> _diagnostics = new();
        int _width, _height;
        BdbPmpWorldBuilder.TclWorldResult? _worldResult;
        DiggableTerrain? _dig;   // live-mode target (set by EmitTerrain)
        private bool _worldRecreationPending;

        public int Width => _width;
        public int Height => _height;
        public IReadOnlyList<string> Diagnostics => _diagnostics;
        public int PmpsLoaded => _pmps.Count;
        public int L2msLoaded => _l2ms.Count;
        public BdbPmpWorldBuilder.TclWorldResult? WorldResult => _worldResult;

        /// <summary>
        /// Gets whether a <c>map create</c> replaced an already emitted world and the host still
        /// needs to emit and activate the rebuilt arrays.
        /// </summary>
        public bool WorldRecreationPending => _worldRecreationPending;

        /// <summary>
        /// Raised when <c>map create</c> replaces an emitted world. The notification only stages
        /// the host swap; terrain emission remains deferred until the surrounding Tcl batch ends.
        /// </summary>
        public event System.Action<RuntimeMapBuilder>? WorldRecreationRequested;

        public RuntimeMapBuilder(string dataRoot, string bdbPath)
        {
            _dataRoot = dataRoot;
            _bdbPath = bdbPath;
        }

        public void CreateMap(int width, int height)
        {
            // Deferred native handoff: single_Tutorial.tcl:2-4 clears objects then calls
            // oberwelt.tcl:1 (`map create 500 200`), whose templates are stamped at:15-20.
            // Drop live mode now so those stamps fill fresh arrays; the host emits after Eval/Step.
            bool recreatedAfterEmit = _worldResult != null;
            _pmps.Clear();
            _l2ms.Clear();
            _stoneOps.Clear();
            _worldResult = null;
            _dig = null;
            _width = width;
            _height = height;
            _diagnostics.Add($"map create {width}x{height}");

            if (!recreatedAfterEmit) return;

            _worldRecreationPending = true;
            WorldRecreationRequested?.Invoke(this);
        }

        // True once EmitTerrain ran: subsequent map_template/map_setlayer2 calls (the StoryMgr's
        // runtime world GROWTH, load_next → MapTemplateSet) blit straight into the LIVE world.
        public bool LiveMode => _worldResult != null;

        public void AttachLiveWorld(
            BdbPmpWorldBuilder.TclWorldResult worldResult,
            DiggableTerrain terrain)
        {
            if (worldResult == null) throw new System.ArgumentNullException(nameof(worldResult));
            if (terrain == null) throw new System.ArgumentNullException(nameof(terrain));
            if (worldResult.Width != terrain.Width || worldResult.Height != terrain.Height)
            {
                throw new System.ArgumentException(
                    "The live map builder world and terrain dimensions do not match.",
                    nameof(terrain));
            }

            _width = worldResult.Width;
            _height = worldResult.Height;
            EnsureTemplateRecordWriteMask(worldResult);
            _worldResult = worldResult;
            _dig = terrain;
            _worldRecreationPending = false;
            _diagnostics.Add($"attached restored live world {_width}x{_height}");
        }

        /// <summary>
        /// Completes a host-managed recreation after the rebuilt terrain has been activated.
        /// </summary>
        public void CompleteWorldRecreation()
        {
            if (!_worldRecreationPending) return;
            if (_worldResult == null)
                throw new System.InvalidOperationException(
                    "The recreated map must be emitted before its world swap can complete.");
            _worldRecreationPending = false;
        }

        public void LoadPmp(string pmpRelPath, int worldX, int worldY)
        {
            var abs = ResolvePmpPath(pmpRelPath);
            if (abs == null)
            {
                _diagnostics.Add($"  SKIP pmp '{pmpRelPath}' @({worldX},{worldY}) — not found");
                return;
            }
            if (_worldResult != null) { LiveBlitPmp(abs, worldX, worldY); return; }
            _pmps.Add((abs, worldX, worldY));
        }

        public void LoadL2m(string l2mRelPath, int worldX, int worldY)
        {
            var abs = ResolveL2mPath(l2mRelPath);
            if (abs == null)
            {
                _diagnostics.Add($"  SKIP l2m '{l2mRelPath}' @({worldX},{worldY}) — not found");
                return;
            }
            if (_worldResult != null) { LiveBlitL2m(abs, worldX, worldY); return; }
            _l2ms.Add((abs, worldX, worldY));
        }

        // ----- sm_draw_stone (FUN_005fc9d0 via FUN_0065e090) -----
        // Boot-time calls (Campaign.tcl:3 `sm_draw_stone -border` runs right after `map create`,
        // BEFORE any template stamps and before EmitTerrain materialises the arrays) are
        // buffered and applied inside EmitTerrain AFTER the boot blits with a tile-record-write
        // skip — order-equivalent to native "paint first, non-transparent blits overwrite +0x18".
        // Live calls paint the shared Terrain[] directly and publish the native rect±1 effect
        // band: RENDERJOBS only (FUN_005fc9d0:127-138; dispatcher FUN_005a46d0:333-401). The
        // delayed-port ordering uses the same tile-record-write mask as boot so authored terrain
        // retains the pre-epic result while native-transparent padding remains paintable.
        // The material writer preserves the high tile+0x18 flags, so nav is unchanged.
        readonly List<(int x0, int y0, int x1, int y1, uint flags, int cls, float strength, System.Random rng)> _stoneOps = new();

        public void DrawStone(int x0, int y0, int x1, int y1, uint dirFlags, int matClass, float strength, System.Random rng)
        {
            if (_worldResult is { } r)
            {
                BdbPmpWorldBuilder.DrawStoneIntoWorld(
                    r.Terrain,
                    r.TemplateRecordWrites,
                    r.Width,
                    r.Height,
                    x0, y0, x1, y1, dirFlags, matClass, strength, rng);
                SyncNativeTerrainMaterials(r, x0, y0, x1, y1);
                _dig?.RefreshMaterialRect(System.Math.Max(0, x0 - 1), System.Math.Max(0, y0 - 1),
                    System.Math.Min(r.Width - 1, x1), System.Math.Min(r.Height - 1, y1));
                return;
            }
            _stoneOps.Add((x0, y0, x1, y1, dirFlags, matClass, strength, rng));
        }

        // ----- live mode -----
        // After the one-shot EmitTerrain, a stamped template must NOT re-EmitTerrain (destructive:
        // re-Inits DiggableTerrain → loses dig state). Instead: blit the PMP/L2M into the EXISTING
        // TclWorldResult arrays (shared by reference with DiggableTerrain + NavWorld), extend the
        // render bbox, then refresh the rect: chunks remesh on the usual debounce, while nav keeps
        // the stamped PMP's authored +0x3c bytes and runs EFFBLOCK. The direct native 0x40000
        // composite omits bit0 EFFPASSWALK (FUN_005992b0:574-606; FUN_005a3fd0:36-41);
        // full-load 0x5f validation and post-dig 0x10000 dirtying supply bit0 separately.

        void LiveBlitPmp(string abs, int wx, int wy)
        {
            // Names the per-stamp blit+nav-revalidate in a Play freeze log.
            // Alloc-free (constant label). The measured growth storm is model loading, not this —
            // this labels any residual stamp/nav hitch during a growth generation.
            using var _perf = Diggles.Runtime.Diagnostics.PerfTrace.Section("worldgen.stamp", 40);
            var r = _worldResult!;
            try
            {
                var pmp = PmpParser.Parse(abs);
                var dig = _dig;
                EnsureTemplateRecordWriteMask(r);
                void OnTileStamped(int x, int y)
                {
                    r.TemplateRecordWrites[y * r.Width + x] = true;
                    dig?.SyncStampedTile(x, y);
                }
                // TEMPLATE WINS: the native blit is destination-blind (FUN_005992b0 — the only
                // skip is source transparency at:292); it overwrites player-carved tiles
                // verbatim and preserves the dig-mark nibbles (+0x08 never touched), so the
                // dwarf digs on through the stamped template mouth. No carve-wins skip.
                BdbPmpWorldBuilder.BlitPmpIntoWorld(pmp, wx, wy, r.Width, r.Height,
                    r.BlockIdx, r.Zone, r.Terrain, r.HasTpl,
                    r.NavigationPlane.Length == r.Width * r.Height * PmpMap.SubGridLength
                        ? r.NavigationPlane
                        : null,
                    // Per-written-tile state sync plus the delayed-paint mask. The callback is
                    // below the native transparency gate, so padding carries neither record data
                    // nor a skip bit (FUN_005992b0:292-454).
                    OnTileStamped);
                int x1 = wx + (int)pmp.Width - 1, y1 = wy + (int)pmp.Height - 1;
                // Re-derive tile+0x18 material/state over the stamped rect AFTER the whole blit
                // (the per-tile sync above runs mid-blit and can read pre-stamp S/E/SE bytes) —
                // a freshly grown cave's authored byte-5 cutout must land as STATE 3 or its
                // rim renders opaque (the rim law reads NativeTerrainFlags & 3).
                BdbPmpWorldBuilder.SyncNativeTerrainMaterials(r, wx, wy, x1, y1);
                r.ExtendRenderBbox(wx, wy, x1, y1);
                _dig?.RefreshStampedRect(wx, wy, x1, y1);
                _diagnostics.Add($"  LIVE pmp '{Path.GetFileName(abs)}' @({wx},{wy}) {pmp.Width}x{pmp.Height}");
            }
            catch (System.Exception e)
            {
                _diagnostics.Add($"  LIVE pmp '{Path.GetFileName(abs)}' @({wx},{wy}) FAILED: {e.Message}");
            }
        }

        private static void EnsureTemplateRecordWriteMask(
            BdbPmpWorldBuilder.TclWorldResult result)
        {
            int tileCount = checked(result.Width * result.Height);
            if (result.TemplateRecordWrites.Length != tileCount)
                result.TemplateRecordWrites = new System.Collections.BitArray(tileCount);
        }

        void LiveBlitL2m(string abs, int wx, int wy)
        {
            var r = _worldResult!;
            try
            {
                var l2m = L2mParser.Parse(abs);
                BdbPmpWorldBuilder.BlitL2mIntoWorld(l2m, wx, wy, r.Width, r.Height, r.CellId);
                // CellId changes ground texturing only (no heights/nav) — re-mesh the rect's chunks,
                // no nav pass. (A bare RequestRemesh is not enough any more: the chunked rebuild
                // only re-emits chunks that were explicitly marked dirty.)
                r.ExtendRenderBbox(wx, wy, wx + (int)l2m.Width - 1, wy + (int)l2m.Height - 1);
                _dig?.MarkRenderRectDirty(wx, wy, wx + (int)l2m.Width - 1, wy + (int)l2m.Height - 1);
            }
            catch (System.Exception e)
            {
                _diagnostics.Add($"  LIVE l2m '{Path.GetFileName(abs)}' @({wx},{wy}) FAILED: {e.Message}");
            }
        }

        // Drive BdbPmpWorldBuilder over the accumulated PMPs/L2Ms to
        // produce proper BDB-block-based 3-sub-mesh terrain (cliff
        // geometry, bowl shape, diggable cave regions) under `root`.
        // Markers are explicitly skipped (they spawn via Tcl `new` +
        // EditorEntitySpawner).
        //
        // No axis-conversion wrapper needed: BdbPmpWorldBuilder emits
        // in the same (X, palette.z, -tile_Y) local frame the marker
        // spawner uses, so both land in the same world space under the
        // rotated sceneRoot (Q_x(-90)).
        public void EmitTerrain(GameObject root)
        {
            if (_width <= 0 || _height <= 0)
            {
                _diagnostics.Add("EmitTerrain: world not initialised (map create not called)");
                return;
            }
            if (string.IsNullOrEmpty(_bdbPath) || !File.Exists(_bdbPath))
            {
                _diagnostics.Add($"EmitTerrain: BDB not found at '{_bdbPath}'");
                return;
            }
            _worldResult = BdbPmpWorldBuilder.BuildFromTclDriven(
                root, ctx: null,
                bdbPath: _bdbPath,
                pmps: _pmps,
                l2ms: _l2ms,
                worldWidth: _width, worldHeight: _height,
                dataRoot: _dataRoot);

            // Buffered boot-time sm_draw_stone paints (Campaign.tcl:3-4) — applied over the
            // finished arrays with the non-transparent tile-record-write skip (see DrawStone).
            // Transparent template padding remains paintable, matching FUN_005992b0:292-454.
            // The world-border stone
            // band lands here: the outer ~16 tiles of the top/left/right map edges become
            // stone class 0 (undiggable), tapering quadratically inward — what stops a player
            // digging to the visible world edge in the original.
            if (_worldResult is { } wr && _stoneOps.Count > 0)
            {
                foreach (var (x0, y0, x1, y1, flags, cls, strength, rng) in _stoneOps)
                {
                    BdbPmpWorldBuilder.DrawStoneIntoWorld(wr.Terrain, wr.TemplateRecordWrites, wr.Width, wr.Height,
                        x0, y0, x1, y1, flags, cls, strength, rng);
                    SyncNativeTerrainMaterials(wr, x0, y0, x1, y1);
                    // the paint lands after the boot chunks were built — re-emit the affected
                    // chunks inside the render bbox so the band's stone texture shows at boot
                    // (the native paints before its render jobs run; class drives texturing)
                    wr.Chunks?.MarkRectDirty(System.Math.Max(0, x0), System.Math.Max(0, y0),
                        System.Math.Min(wr.Width - 1, x1), System.Math.Min(wr.Height - 1, y1));
                }
                wr.Chunks?.RebuildDirty();
            }
            _stoneOps.Clear();

            // FAITHFUL navigation (the mesh-sampled NavGraph is RETIRED): the data layer is
            // NavWorld (built by DiggableTerrain.Init from the +0x3c sub-grid + BDB aux), and
            // NavWorldService runs Calc1 over its FUN_005a6350-derived tile graph. After a dig
            // the navRebuild callback re-derives the tile graph (heights track the block swap
            // automatically — NavWorld shares the mutated BlockIdx array).
            if (_worldResult != null && File.Exists(_bdbPath))
            {
                var dig = root.GetComponent<DiggableTerrain>() ?? root.AddComponent<DiggableTerrain>();
                // Post-dig nav updates are handled INSIDE DiggableTerrain.Update via the MapValidator-
                // faithful dirty-rect revalidation (EFFPASSWALK→EFFBLOCK); no extra callback needed.
                dig.Init(_worldResult, BdbParser.Parse(_bdbPath), _dataRoot, () => { });
                _dig = dig;   // live-mode growth stamps refresh through it (LoadPmp post-emit)
                // Dig-mark overlay: the native DigMark plane meshes (committed dig orders + the
                // armed-tool hover) — separate MeshRenderers on the front plane, not a shader pass.
                var marks = root.GetComponent<DigMarkOverlay>() ?? root.AddComponent<DigMarkOverlay>();
                marks.Init(dig);
                if (root.GetComponent<Diggles.Runtime.Navigation.NavWorldService>() == null)
                    root.AddComponent<Diggles.Runtime.Navigation.NavWorldService>();
                // Debug viz of the walkability data: cyan ground-line floor strips + magenta climb cells.
                if (root.GetComponent<Diggles.Runtime.Navigation.NavWorldGizmo>() == null)
                    root.AddComponent<Diggles.Runtime.Navigation.NavWorldGizmo>();
            }

            // Click input is owned by ClickToMoveService (select/walk/dig/carve via the Tcl
            // chain) — the legacy DigglesObject-based DigglesInputController stack is deleted (P7).
        }

        private static void SyncNativeTerrainMaterials(
            BdbPmpWorldBuilder.TclWorldResult result,
            int x0,
            int y0,
            int x1,
            int y1) =>
            BdbPmpWorldBuilder.SyncNativeTerrainMaterials(result, x0, y0, x1, y1);


        // --- path resolution ---

        // Strip `data/` prefix if present, try a few canonical subdirs.
        string? ResolveGeneric(string rel, string[] subdirs)
        {
            rel = rel.Replace('\\', '/');
            if (rel.StartsWith("data/", System.StringComparison.OrdinalIgnoreCase))
                rel = rel.Substring(5);

            // 1. As-given (in case it's already a usable relative path).
            var direct = Path.Combine(_dataRoot, rel);
            if (File.Exists(direct)) return direct;

            // 2. Try canonical subdirs prepended to the bare filename.
            var bare = Path.GetFileName(rel);
            foreach (var sub in subdirs)
            {
                var p = Path.Combine(_dataRoot, sub, bare);
                if (File.Exists(p)) return p;
            }
            return null;
        }

        string? ResolvePmpPath(string rel) => ResolveGeneric(rel, new[]
        {
            Path.Combine("Templates", "pmp"),
            Path.Combine("Templates", "pmp", "split"),
            "Templates",
        });

        string? ResolveL2mPath(string rel) => ResolveGeneric(rel, new[]
        {
            Path.Combine("Templates", "l2m"),
            Path.Combine("Templates", "l2m", "split"),
            "Templates",
        });
    }
}
