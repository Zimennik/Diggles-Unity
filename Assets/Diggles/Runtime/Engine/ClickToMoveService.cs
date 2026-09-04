#nullable enable

namespace Diggles.Runtime
{
    using Diggles.Runtime.Tcl.Engine;
    using UnityEngine;
    using VContainer;
    using VContainer.Unity;

    // Click-to-move, fully Tcl-driven (left-click; right-button is camera). Context command on click:
    // • a player dwarf → select it (Shift = add to the multi-selection — the native
    // multiselect key; ctx.Selection carries the whole set, commands
    // fan out over it; right-click clears)
    // • LMB drag (plain) → band-box select own units (the native mode 3: promoted from a
    // held press once |dx|·|dy| > 250 px², committed on release via
    // the FUN_0052d170 law — handle order; gnomes/babies join a
    // boxed structure, further structures are skipped; Shift at
    // release = additive). Clicks resolve on the mouse-up edge,
    // like the native mode-1 consumption.
    // • any other entity → the original event_generator decision (Scripts/init/eventgen.tcl
    // via evtgen_attrib): pickup/useitem/harvest/mine/open_box/switch/
    // objaction/attack/walk… raised as evt_task_* on the selected dwarf
    // (Alt = the alternate-action key); the "select" verb (own
    // buildings and ground crates, eventgen.tcl:161-162) selects — the
    // bottom production window owns all ordering from there.
    // • dig-marked terrain → the world object (subpar 1) through the same generator →
    // evt_task_dig at the mark (eventgen.tcl:132-139) — the "dig here
    // now" order; Alt = plain walk.
    // • F9 → drop the whole inventory (evt_task_dropall, FUN_0052c510:262).
    // • Shift → multiselect only (the native law: the txtpar builder
    // FUN_00530b90:51-53 appends " -multiselect" for VK_SHIFT —
    // Shift never digs). Shift+click = add; Shift+drag = the band
    // box even with a dig tool armed (Shift wins — DigMarkerActive /
    // BoxDragEligible; an earlier revision-deviation Shift+drag dig shortcut
    // is removed, it painted phantom markers during multi-select).
    // • armed dig tool → arms only through the bottom dig menu; a plain left drag on
    // terrain drags a tunnel, release = standard tunnel order
    // (mode-1 dummy marks — feeds StoryMgr world growth) + evt_task_dig;
    // keys 1..4 while dragging = the mode-0 depth tools, 5 = standard
    // (the original drag dig tool, FUN_005300f0). Faithful constraints
    // via DigMarquee (full rule set in its header): the anchor snaps to
    // the 4-tile lattice and the drag corner to the 2-tile lattice with
    // the cross axis clamped to the anchor (FUN_005680b0 — dominant
    // drag axis wins, re-picked every frame), so a tunnel is exactly 2
    // across and only its length extends; the commit rect = [min,
    // max+2) per axis (FUN_00566550:60-67) and per cell the standard
    // tunnel applies the FUN_00566870 validity (lattice pillar + not
    // open-marked + solid support row/flanks).
    // • terrain → walk (action this walk -target …; double-click = run)
    // All routed through the single MetaWalk/work executors (pathfinds via the nav graph + interrupts
    // idle). A VContainer entry point (ITickable, Update PlayerLoop), extracted from DigglesWorldRuntime.
    // Registered after EngineTickService so the click sees the latest sim-stepped positions this frame.
    public sealed class ClickToMoveService : ITickable
    {
        readonly System.Func<DigglesTclContext> contextAccessor;
        DigglesTclContext? _observedContext;
        DigglesTclContext ctx => contextAccessor();

        [Preserve]
        [Inject]
        public ClickToMoveService(IActiveGameSession sessions)
        {
            contextAccessor = () => sessions.Current.Engine.Ctx;
        }

        // Headless tests exercise the input laws without a scene DI scope.
        public ClickToMoveService(DigglesTclContext ctx)
        {
            if (ctx == null) throw new System.ArgumentNullException(nameof(ctx));
            contextAccessor = () => ctx;
        }

        Camera? _cam;

        // ---- dig-order marquee state (the original's drag dig tool, FUN_005300f0) ----
        // The original anchors the order on mouse-down (+0xe4/+0xe8), tracks the moving corner
        // while dragging (+0xf0/+0xf4) and commits on mouse-up; the depth selector is the active
        // dig tool − 1 ∈ 0..3 (+0xdc). Note: the fully faithful original commits a 32×32 cave
        // template stencil per depth (cave0..3.tga, FUN_005661c0:68-89 loads them, FUN_00567cb0
        // stamps stencil cells ≠ 4 via FUN_005671a0) — the rect marquee here is the same data
        // (mode-0 (cnt|8) nibbles + per-order mark id) over a player-chosen rect, the simple
        // interim UI until the template stamp is ported.
        bool _digDrag;
        int _digAx, _digAy;            // anchor tile (mouse-down)
        int _digBx, _digBy;            // moving corner tile (current)
        // Dig tool selector (FUN_005300f0). The bottom dig menu (ViWinDigButtons) arms one of:
        // • DigStandard (-1) → the standard tunnel: a drag-rect mode-1 marquee (FUN_00566550 →
        // the mode-1 painter FUN_005672d0, nibble 3; decode target 11). The only mark kind
        // that feeds StoryMgr world growth (DigNotify's gate (n&8)==0 && n!=0,
        // FUN_005fd060:75-77, rejects mode-0's cnt|8).
        // • 0..3 → the four cave tools (Tiny/Small/Medium/Huge). Each is a template stamp, not a
        // depth marquee: tool i stamps data/templates/cave{i}.tga (32×32 stencil) at the
        // snapped anchor — the mode-0 painter (nibble (cnt|8), FUN_005671a0) over the
        // template's carve cells, with the strict per-cell validity FUN_00566a60 (all-or-
        // nothing) and a CaveRecord appended (FUN_00567cb0). See CaveStamp / CaveTemplates.
        // • DigErase (-2) → the delete tool (dig:undo, FUN_00567410): stroke-erase over plain
        // tunnel nibbles + a click on a cave cell removes the whole cave (see UpdateEraseStroke).
        public const int DigStandard = -1;   // mode-1 dummy (the standard tunnel order)
        public const int DigErase = -2;      // the dig:undo erase/delete tool
        public static int DigDepthCnt = DigStandard;
        // The bottom dig menu arms a tool (native ViWinDigButtons flow): while armed, a plain
        // left-drag on terrain runs the dig marquee and a right-click disarms. Shift never digs
        // (multiselect only) — while Shift is held even an armed tool yields to the band box
        // (DigMarkerActive / BoxDragEligible).
        public static bool DigToolArmed;

        // ---- eraser (dig:undo) stroke state (FUN_00567410, mgr state 2) ----
        bool _eraseDrag;
        int _eraseLastX = int.MinValue, _eraseLastY;   // last window origin (for stroke interpolation)

        // ---- cave-tool template stencils (data/templates/cave{0..3}.tga), lazily loaded ----
        CaveTemplate?[]? _caveTemplates;
        CaveTemplate? CaveTemplateFor(int idx)
        {
            if ((uint)idx >= 4) return null;
            _caveTemplates ??= CaveTemplates.Load(this.ctx.DataRoot);
            return _caveTemplates[idx];
        }

        DigMarqueePreviewLines? _preview;   // runtime-visible marquee lines (GL), created on demand

        // One-shot click-eater diagnostics (selection-loss hunt): every state that can swallow a
        // left mouse-down warns once per session when it first eats one, so the next log names
        // the eater instead of dying silent.
        bool _warnedSeqEater, _warnedClockEater, _warnedNoHit, _warnedGhostlessPlacement;

        // ---- deferred click + band-box selection state (native mode 1 → mode 3) ----
        // The native normal mode consumes clicks on the mouse-up message (FUN_0052fe80:77-87 →
        // FUN_005309c0) and promotes a held LMB to the band-select mode 3 once the pointer has
        // moved |dx|·|dy| > 250 px² from the down point (:64-74, the 0xfa gate). The port
        // mirrors that: a LMB press in normal mode is deferred (_pressPending) — release under
        // the gate = the ordinary click (HandleClick); crossing it = the drag box
        // (SelectionBoxOverlay), whose release band-selects own units (CommitBoxSelect,
        // FUN_0052d170). Dig gestures (marquee/stamp/erase) keep their mouse-down anchor —
        // they are the native mode 2, which never bands.
        bool _pressPending; Vector2 _pressPos;
        bool _boxDrag; Vector2 _boxAnchor;
        SelectionBoxOverlay? _boxOverlay;
        // RMB tracking: the native mode-1 right-button message clears the selection when one
        // exists (FUN_0052fe80:88-103 → FUN_0040b980(0)). Port: only a right click (≤ slop —
        // a camera right-drag moves far past it), and not when the same click just disarmed
        // an armed dig tool.
        Vector2 _rmbDownPos; bool _rmbConsumed;

        // FUN_0052fe80:70 — the native band-promotion gate: the |dx|·|dy| area product must
        // exceed 0xfa = 250 px² (both axes must move; a straight-line wiggle never bands).
        public const float BoxDragAreaPx2 = 250f;
        public static bool BoxDragStarts(Vector2 down, Vector2 now)
            => Mathf.Abs((now.x - down.x) * (now.y - down.y)) > BoxDragAreaPx2;

        // A right click (selection clear) vs a camera right-drag.
        public const float RmbClickSlopPx = 4f;

        void ITickable.Tick()
        {
            EnsureCurrentContext();
            // Watchdog-visible section: click handlers run arbitrary Tcl (event_generator, dig
            // marquee commits, placement) outside sim.step — a freeze here would otherwise be
            // unattributed. Threshold high: the per-frame idle path must not log.
            using var _perf = Diggles.Runtime.Diagnostics.PerfTrace.Section("input.click", 200);
            try { TickInner(); }
            catch (System.Exception e)
            {
                // VContainer's TickableLoopItem rethrows when no EntryPointExceptionHandler is
                // registered (PlayerLoopItem.cs:175) and PlayerLoopRunner.Run has no catch —
                // one exception here aborts every later ITickable this frame (SelectionAura…)
                // and can leave half-updated click state. Contain it and drop any mid-gesture
                // mouse ownership so a throw can never wedge the marquee.
                _digDrag = false;
                _eraseDrag = false;
                _pressPending = false;
                _boxDrag = false;
                HideBoxOverlay();
                ClearPreview();
                World.DigMarkOverlay.ClearHover();
                Debug.LogException(e);
            }
        }

        void EnsureCurrentContext()
        {
            DigglesTclContext current = ctx;
            if (ReferenceEquals(_observedContext, current)) return;
            _observedContext = current;
            _cam = null;
            _caveTemplates = null;
            _digDrag = false;
            _eraseDrag = false;
            _eraseLastX = int.MinValue;
            _pressPending = false;
            _boxDrag = false;
            _rmbConsumed = false;
            _selSnapshot.Clear();
            _hoverScratch.Clear();
            _warnedSeqEater = false;
            _warnedClockEater = false;
            _warnedNoHit = false;
            _warnedGhostlessPlacement = false;
            DigToolArmed = false;
            DigDepthCnt = DigStandard;
            HideBoxOverlay();
            ClearPreview();
            World.DigMarkOverlay.ClearHover();
        }

        void TickInner()
        {
            if (this.ctx.Interp == null) return;
            // Gated during cutscenes (released on the active→inactive edge). A cutscene
            // starting mid-drag cancels the marquee.
            if (this.ctx.SequenceActive)
            {
                if (Input.GetMouseButtonDown(0) && !_warnedSeqEater)
                { _warnedSeqEater = true; Debug.LogWarning("[ClickToMove] EATER: click swallowed by the cutscene gate (ctx.SequenceActive) — if no cutscene is visible, the sequence flag is stuck"); }
                _digDrag = false; _pressPending = false; _boxDrag = false; HideBoxOverlay();
                ClearPreview(); World.DigMarkOverlay.ClearHover(); return;
            }
            if (_cam == null) _cam = Camera.main;
            if (_cam == null) return;

            // F9 = drop the whole inventory. RE ground truth (the game key handler
            // FUN_0052c510:242-276): on VK 0x78 = F9 the native formats
            // "set_event %d evt_task_dropall -target %d" for every selected unit and evals it —
            // evt_task_dropall_proc (z_events.tcl:821-850) then clears the task list and
            // beamto_world_all's the whole inventory where the gnome stands. (F8 was initially
            // assumed but verified against the exe: 0x77/F8 instead toggles the camera-follow
            // handle at +0xa0d4,:277-292; the drop-all key is F9.) Fans out over the whole
            // selection like the native loop.
            if (Input.GetKeyDown(KeyCode.F9)) FireDropAll();

            // An active dig marquee / erase stroke / band-box drag / pending deferred click
            // owns the mouse until release.
            if (_digDrag) { UpdateDigMarquee(); return; }
            if (_eraseDrag) { UpdateEraseStroke(); return; }
            if (_boxDrag) { UpdateBoxSelect(); return; }
            if (_pressPending) { UpdatePress(); return; }

            // Placement mode owns the pointer (the native wnd+0x134 == 4 input mode,
            // FUN_00530e80:176-177): the cursor drags the building ghost every frame, a left
            // click while placing is the confirm (FUN_00530b90:115-120 → FUN_00531310), Esc
            // cancels. Normal click verbs are suspended until the mode exits.
            var placement = this.ctx.Placement;
            if (placement != null && placement.Active) { UpdatePlacement(placement); World.DigMarkOverlay.ClearHover(); return; }

            // Top of the RMB / world-click precedence ladder: while the pointer is over a uGUI
            // element, world input is untouched — no RMB deselect / dig-tool disarm / right-panel
            // toggle, no LMB select/walk, no dig hover. This is the gate that stops a world-RMB
            // from ever firing on the production order icons the prod panel owns (ResolveRmbClick's
            // OverUI rung — interplay with the prod-freeze track's RMB-minus-1 fix). Camera orbit
            // is a separate controller and is unaffected (RMB drag still orbits).
            if (PointerOverUI()) { World.DigMarkOverlay.ClearHover(); return; }

            // Right button: the down edge disarms an armed dig tool (the native cancel; the
            // camera drag still works — ResolveRmbClick's DisarmDigTool rung, handled on the down
            // edge so the tool drops the instant the button goes down). The up edge, when it was a
            // click (≤ slop) that didn't just disarm a tool, runs the rest of the ladder:
            // something selected → deselect (the native mode-1 right-button law,
            // FUN_0052fe80:88-103: selection non-empty → FUN_0040b980(0)); nothing selected →
            // request the right-side finder-panel toggle (RightPanelModel).
            if (Input.GetMouseButtonDown(1))
            {
                _rmbDownPos = Input.mousePosition;
                _rmbConsumed = DigToolArmed;
                if (DigToolArmed) DigToolArmed = false;
            }
            if (Input.GetMouseButtonUp(1))
            {
                bool wasClick = ((Vector2)Input.mousePosition - _rmbDownPos).sqrMagnitude
                                <= RmbClickSlopPx * RmbClickSlopPx;
                if (!_rmbConsumed && wasClick)
                {
                    if (this.ctx.Selection.Count > 0)
                    {
                        this.ctx.Selection.Clear();
                        Debug.Log("[ClickToMove] RMB click → deselect (native mode-1 law)");
                    }
                    else
                    {
                        // Nothing selected → toggle the right-side gnome/building finder panel.
                        // The panel view is authored in the Unity editor and does not exist yet;
                        // this raises the model seam (Debug.Log'd so it is observable in Play).
                        RightPanelModel.RequestToggle();
                        Debug.Log($"[ClickToMove] RMB click (nothing selected) → right-panel toggle requested (open={RightPanelModel.Open})");
                    }
                }
                _rmbConsumed = false;
            }

            // Pre-click hover indicator: with a dig tool armed and Shift not held (Shift =
            // multiselect only — DigMarkerActive), the anchor block a click would mark
            // highlights on the terrain face under the cursor (see DigMarkOverlay).
            UpdateDigHover();

            // LEFT-click only (right-click / drag = camera rotate). Context command: dwarf=select,
            // building=select, terrain=walk (or the armed dig tool's marquee).
            if (!Input.GetMouseButtonDown(0)) return;

            // The work clock owns the pointer while hovered/dragged — don't let a marker drag or
            // a dial click fall through to a world walk underneath (the original routes input
            // through the ViWin window chain before the world; ViWinClock::HandleMsg FUN_0050c2b0
            // eats its messages).
            if (WorkClockWidget.PointerCaptured)
            {
                if (!_warnedClockEater)
                { _warnedClockEater = true; Debug.LogWarning("[ClickToMove] EATER: click swallowed by WorkClockWidget.PointerCaptured — if the cursor is NOT over the clock, its hover/drag capture is stuck"); }
                return;
            }

            // Mouse-down split. The native press anchor is screen-space only: FUN_0052fe80:53-54
            // stores the down coords into +0x138/+0x13c with no world pick, and the mode 1→3
            // band promotion (:64-74) tests only those screen deltas against the 250 px² gate —
            // the world pick happens at the up consumption (:85 → FUN_005309c0). So the press
            // arms even when the ray hits nothing (empty air): the drag can still band-box (the
            // box used to need a collider under the down point). Only the dig-tool gestures
            // consume the down edge (they anchor on terrain, the native mode-2 tools,
            // FUN_005300f0); everything else — select / command / walk — defers to the release,
            // where HandleClick re-picks and an air click is inert.
            bool picked = TryPickWorld(out var hit, out var target);
            bool digGesture = picked && TryStartDigGesture(hit, target);
            if (!PressArmsDeferred(picked, digGesture)) return;
            _pressPending = true;
            _pressPos = Input.mousePosition;
        }

        // The world pick shared by the mouse-down split and the deferred click.
        // Nearest PICKABLE hit, not nearest collider: an entity with `set_hoverable 0` is
        // transparent to the cursor in the native (obj flag 0x400 — the emptied Schatztonne
        // keeps its body ~10s while it sinks, misc_items.tcl:233, but must not eat clicks),
        // and so is a cloaked one. Skip those and fall through to whatever is behind
        // (usually the terrain).
        bool TryPickWorld(out RaycastHit hitOut, out DigglesTclEntity? targetOut)
        {
            var ray = _cam!.ScreenPointToRay(Input.mousePosition);
            var hits = Physics.RaycastAll(ray, 10000f);
            System.Array.Sort(hits, (h1, h2) => h1.distance.CompareTo(h2.distance));
            // Native pick law: the pick is render-accurate, not collider-box
            // accurate. Every drawn object whose live display mesh is under the cursor becomes a
            // candidate (the render-side hover list DAT_00772400+0xa0c4/+0xa0c8 — CDisplay::
            // DrawObjects FUN_00473cc0:674-707 keys the hover highlight off it, FUN_0052fe80:36
            // latches +0xa0d8→+0xa0c0); the click consumes the single hovered object +0xa0cc
            // (FUN_00530b90:104-121, eventgen FUN_0055aaa0:164). A big building body therefore
            // never swallows a crate lying inside its box: the crate's own drawn pixels are under
            // the cursor, the building's are not. The port's box colliders are only a broad
            // phase — each entity box hit is refined against the entity's current display mesh
            // triangles (the same live-mesh record the native tests, FUN_004059a0's 0xE8-stride
            // CMesh — see NativePickBody); a box hit whose mesh is not actually under the cursor
            // is discarded. Characters keep the pick preference among true hits (the
            // builder-beside-campfire law).
            RaycastHit hit = default;
            bool hitAny = false, hitIsCharacter = false, hitExact = false;
            float bestDist = float.MaxValue;
            foreach (var h in hits)
            {
                var ho = h.collider.GetComponentInParent<DigglesObject>();
                var he = ho != null ? FindEntityByGameObject(ho.gameObject) : null;
                if (he != null && IsCursorTransparent(he)) continue;
                if (he != null && RenderVisibilityBlocksCursor(he)) continue;
                if (he == null)
                {
                    // A spawned prop outside the entity graph (marker-7 map-deco seed, cave
                    // beams/boards in loaded saves) is cursor-transparent like native deco —
                    // it must never swallow the click aimed at the gnome behind it.
                    if (ho != null) continue;
                    // Terrain (mesh collider — already triangle-accurate): the rock face closes
                    // the pick window; nothing behind it is pickable.
                    if (!hitAny) { hit = h; hitAny = true; hitIsCharacter = false; bestDist = h.distance; }
                    break;
                }
                string hk = he.Class?.Kind ?? "";
                bool isChar = hk == "gnome" || hk == "baby";
                // Triangle refinement against the entity's live display mesh; a box-only graze
                // (cursor inside the collider box but off the drawn model) is no candidate —
                // except characters and small pickups, which get the near-miss ring
                // (TryRefineMeshHitTolerant): thin silhouettes were too fiddly to click.
                bool exact = TryRefineMeshHit(ray, h, out float trueDist);
                if (!exact)
                {
                    bool tolerantEligible = isChar || hk == "material" || hk == "item";
                    if (!tolerantEligible ||
                        !TryRefineMeshHitTolerant(_cam, Input.mousePosition, h, out trueDist)) continue;
                }
                // Order: characters win overlaps (even a near-miss gnome beats an exactly-hit
                // crate/building — restoring the pre-refinement box behavior); within
                // the same character-ness an exact hit beats a tolerant one; then nearest.
                bool better = !hitAny
                    || (isChar != hitIsCharacter ? isChar
                        : exact != hitExact ? exact
                        : trueDist < bestDist);
                if (better) { hit = h; hitAny = true; hitIsCharacter = isChar; hitExact = exact; bestDist = trueDist; }
            }
            hitOut = hit;
            targetOut = null;
            if (!hitAny)
            {
                // Empty air is a legitimate press point now (the band box arms anywhere on
                // screen, FUN_0052fe80:53-54) — an air click is inert by design, so this is
                // informational, once: it only signals breakage if it fires over visible terrain.
                if (!_warnedNoHit)
                { _warnedNoHit = true; Debug.Log("[ClickToMove] pick: ray hit NO collider (empty air) — press still arms the band box; a plain air click is inert. If this fires over visible terrain, collider setup is broken."); }
                return false;
            }
            var hitObj = hit.collider.GetComponentInParent<DigglesObject>();
            targetOut = hitObj != null ? FindEntityByGameObject(hitObj.gameObject) : null;
            return true;
        }

        // The dig-tool gestures anchor on mouse-down (FUN_005300f0): a menu-armed tool over open
        // terrain — an entity under the cursor always consumed the click first in the pre-band
        // flow, so target must be null. Ctrl is the debug carve (deferred to the click), never a
        // gesture. Shift is purely the multiselect key: it never starts a
        // dig, and while it is held even an armed tool yields to the drag box (DigMarkerActive).
        bool TryStartDigGesture(in RaycastHit hit, DigglesTclEntity? target)
        {
            var terrain = this.ctx.Terrain;
            if (target != null || terrain == null || this.ctx.SpawnParent == null) return false;
            if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) return false;
            if (!DigMarkerActive(DigToolArmed, MultiselectHeld())) return false;

            Vector3 nat = RuntimeWorldSpawner.LocalToNative(this.ctx.SpawnParent.InverseTransformPoint(hit.point));
            int tool = DigDepthCnt;
            // Eraser (dig:undo): a click on a cave-marked cell removes the whole cave
            // (EraseCaveAt flood); otherwise begin a stroke-erase drag over plain tunnel nibbles.
            if (tool == DigErase)
            {
                var (etx, ety) = terrain.WorldToTile(nat);
                if (terrain.EraseCaveAt(etx, ety, 0))
                {
                    Debug.Log($"[ClickToMove] erase: removed cave at ({etx},{ety})");
                    return true;
                }
                _eraseDrag = true;
                _eraseLastX = int.MinValue;
                EraseStrokeAt(terrain, etx, ety);
                Debug.Log($"[ClickToMove] erase stroke start at ({etx},{ety}) — drag to erase tunnel marks, release to stop");
                return true;
            }
            // Cave tools (0..3): stamp the fixed template stencil at the snapped anchor
            // (click-to-stamp; the tool stays armed for repeat stamps). No drag/resize.
            if (tool >= 0)
            {
                StampCave(terrain, nat, tool);
                return true;
            }
            // Standard tunnel: the drag-rect marquee (anchor on down, commit on up).
            var (tx, ty) = DigMarquee.SnapAnchor(nat.x, nat.y);
            _digDrag = true;
            _digAx = _digBx = tx; _digAy = _digBy = ty;
            Debug.Log($"[ClickToMove] dig marquee anchor ({tx},{ty}) (4-lattice snap) — drag to extend " +
                      $"(tunnels are 2 across, dominant axis = length), release to order");
            return true;
        }

        // The deferred normal-mode click, fired on the mouse-UP edge (the native FUN_005309c0
        // consumption). Re-picks at the release point — the pointer is within the band gate of
        // the down point by construction.
        void HandleClick()
        {
            if (this.ctx.Interp == null || _cam == null) return;
            if (this.ctx.SequenceActive) return;
            // A press that outlived a mode change (uGUI button fired placement on its own
            // pointer-up this frame, a cutscene started…) must not double-act.
            if (this.ctx.Placement != null && this.ctx.Placement.Active) return;
            if (WorkClockWidget.PointerCaptured) return;
            if (!TryPickWorld(out var hit, out var target)) return;
            string tcls = target?.Class?.Name ?? "";
            if (target != null) Debug.Log($"[ClickToMove] hit {tcls}#{target.Handle} " +
                $"(selected={this.ctx.SelectedHandle}, count={this.ctx.Selection.Count})");

            // Click a dwarf/baby → select it, but only if it is the local player's. The native UI
            // selects/commands own units only; a foreign-tribe gnome (e.g. the player-1 herbalists
            // of ErsterKontakt, found via `sq_actor find Zwerg 300 2 1` — owner filter 1) is not
            // controllable. Without this gate the player could select+command the herbalists after
            // their cutscene — their Owner stays 1, the bug was purely the missing selection gate
            // (obj_query owner filtering itself is faithful).
            if (target != null && (tcls == "Zwerg" || tcls == "Frau" || tcls == "Baby"))
            {
                if (target.Owner == this.ctx.LocalPlayer)
                {
                    // The native click-select law (FUN_005309c0): Shift (the multiselect key)
                    // adds the unit to the selection, a plain click replaces it.
                    this.ctx.Selection.ClickSelect(target.Handle, MultiselectHeld());
                    Debug.Log($"[ClickToMove] selected {tcls}#{target.Handle} " +
                              $"(count={this.ctx.Selection.Count})");
                    return;
                }
                // Foreign-tribe dwarf: not selectable — but with own units selected the click
                // must still fall through to the event generator, which resolves the diplomacy
                // itself (eventgen.tcl:474-510: enemy -> evt_task_attack "attack" verb, neutral
                // -> inert). The old unconditional return also swallowed attack orders on enemy
                // gnomes. No selection either way.
                if (this.ctx.SelectedHandle == 0) return;
            }

            // Ctrl + click on diggable terrain = test excavation: carve the clicked tile exactly as the
            // engine does (tile → flat-floor block 59 + its solid neighbours → the BDB's own cliff-edge
            // blocks) and re-mesh into a real depression. No dwarf required — a debug shortcut for the dig RE.
            var terrain = this.ctx.Terrain;
            if (terrain != null && this.ctx.SpawnParent != null &&
                (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)))
            {
                Vector3 cnat = RuntimeWorldSpawner.LocalToNative(this.ctx.SpawnParent.InverseTransformPoint(hit.point));
                var (cx, cy) = terrain.WorldToTile(cnat);
                bool carved = terrain.CarveTileNow(cx, cy);
                Debug.Log(carved ? $"[ClickToMove] CARVE tile ({cx},{cy})" : $"[ClickToMove] not diggable at ({cx},{cy})");
                return;
            }

            // A click on an own building/crate selects it regardless of whether a dwarf is
            // selected — the native window law (FUN_00529360) keys the bottom production window
            // purely on the selection; without this, a crate was unclickable until a gnome was
            // selected first.
            if (target != null && target.Owner == this.ctx.LocalPlayer && target.Class != null)
            {
                string tkind = target.Class.Kind;
                if (tkind == "production" || tkind == "store" || tkind == "energy"
                    || tkind == "protection" || tkind == "elevator")
                {
                    bool crateBoxed = target.VarsScope.Vars.TryGetValue("_boxed", out var bxv)
                        && (bxv == "1" || bxv == "true");
                    bool haveDwarf = this.ctx.SelectedHandle != 0
                        && this.ctx.Entities.TryGetValue(this.ctx.SelectedHandle, out var selDw)
                        && (selDw.Class?.Kind == "gnome");
                    // With a dwarf selected the eventgen path below decides (its "select" verb
                    // selects too); with no dwarf the click is pure selection.
                    if (!haveDwarf)
                    {
                        this.ctx.SelectedHandle = target.Handle;
                        Debug.Log($"[ClickToMove] selected building {tcls}#{target.Handle} (boxed={crateBoxed})");
                        return;
                    }
                }
            }

            // Shift is the product pickup override for an own boxed building crate. Plain and Alt
            // defer to the original generator: plain returns "select" (eventgen.tcl:161-162),
            // while Alt reaches evt_task_pickup for a ground crate without `use`
            // (eventgen.tcl:13,164,186-189).
            if (target != null && target.Owner == this.ctx.LocalPlayer && target.Class != null)
            {
                string bkind = target.Class.Kind;
                bool bBuilding = bkind == "production" || bkind == "store" || bkind == "energy"
                    || bkind == "protection" || bkind == "elevator";
                bool bBoxed = target.VarsScope.Vars.TryGetValue("_boxed", out var bxv2)
                    && (bxv2 == "1" || bxv2 == "true");
                bool bGnomeSel = this.ctx.SelectedHandle != 0
                    && this.ctx.Entities.TryGetValue(this.ctx.SelectedHandle, out var bsel)
                    && (bsel.Class?.Kind == "gnome");
                if (ResolveCrateClick(bBuilding && bBoxed, bGnomeSel,
                        MultiselectHeld(), AlternateHeld()) == CrateClickVerb.PickupOrder)
                {
                    int n = FirePickupOrder(target.Handle);
                    Debug.Log($"[ClickToMove] boxed crate {tcls}#{target.Handle} → pickup order to {n} gnome(s)");
                    return;
                }
            }

            // Otherwise it's a context command on the selection (sel = the primary for the
            // single-subject decisions; the eventgen/walk consumption below fans out over the
            // whole set — the native FUN_00530b90 command loop).
            var sel = (this.ctx.SelectedHandle != 0 && this.ctx.Entities.TryGetValue(this.ctx.SelectedHandle, out var s)) ? s : null;
            if (sel == null || sel.GameObject == null) return;

            // Click an entity → the original context-command decision: evaluate the unmodified
            // Scripts/init/eventgen.tcl event_generator (registered by classinit.tcl:111) with
            // objpar = the selected dwarf, subpar = the clicked object, pospar = the hit point
            // (native), txtpar = {-command [-alternate]}, then raise the evtgen_attrib spec
            // (evt_task_pickup / useitem / harvest / mine / open_box / switch / objaction /
            // attack / walk …) on the dwarf — exactly the native UI's click consumption of the
            // FUN_0063eb70 spec. This replaces the old hardcoded Pilz/Feuerstelle special cases:
            // Pilz now routes to evt_task_harvest through eventgen.tcl:236-243.
            if (target != null && this.ctx.SpawnParent != null)
            {
                Vector3 natHit = RuntimeWorldSpawner.LocalToNative(this.ctx.SpawnParent.InverseTransformPoint(hit.point));
                // Alt = the original's alternate-action key (use the object): the native click
                // txtpar builder FUN_00530b90:51-58 appends " -multiselect" for VK_SHIFT (0x10)
                // and " -alternate" for VK_MENU/ALT (0x12). eventgen.tcl:13 turns
                // -alternate into evt_task_useitem for any class with a `use` method;
                // -multiselect makes the generator answer "select" immediately (:38-40) —
                // the Shift+click add path.
                bool alt = AlternateHeld();
                bool multi = MultiselectHeld();
                string verbTrimmed = "";
                bool anyFired = false;
                try
                {
                    // The native command-click loop (FUN_00530b90:76-158): the event generator
                    // runs once per selected object (objpar = selection[i] via FUN_0055aaa0 →
                    // FUN_005e3690), every iteration with the same pospar — each unit raises
                    // its own event at the same target (no formation offsets in the native;
                    // the walk executor's floor relax packs them like the original).
                    foreach (var u in SelectedUnitsSnapshot())
                    {
                        if (u.GameObject == null) continue;
                        string verb = DigglesTclEngine.EvtGenRun(
                            this.ctx, u.Handle, target.Handle, natHit, CommandTxtPar(alt, multi));
                        verbTrimmed = verb.Trim();
                        bool fired = DigglesTclEngine.EvtGenFire(this.ctx, u.Handle);
                        anyFired |= fired;
                        Debug.Log($"[ClickToMove] eventgen #{u.Handle}→{tcls}#{target.Handle}: verb='{verb}' " +
                                  $"evt='{this.ctx.EvtGen.EvtId}' fired={fired} desc='{this.ctx.EvtGen.Desc}'");
                    }
                    if (anyFired) return;
                    // "placement" verb without an event spec → enter placement mode on the
                    // clicked object (the native result handler FUN_00530b90:85-105 →
                    // FUN_00530e80): a translucent ghost follows the cursor; a valid-spot click
                    // confirms via the branch in PlacementController.Confirm. This is how a
                    // command-click on an own half-built building re-orders its construction
                    // through the selected gnome (eventgen.tcl:219-228 — the manual resume
                    // path). Was a documented dead end (buildup-carrier-re.md §9.3).
                    // Single-subject by nature — entered once, off the last verb (the native
                    // latches +0xa190 the same way).
                    if (verbTrimmed == "placement" && this.ctx.Placement != null)
                    {
                        bool entered = this.ctx.Placement.EnterPlacement(target.Handle);
                        Debug.Log($"[ClickToMove] placement mode for {tcls}#{target.Handle}: entered={entered}");
                        return;
                    }
                }
                catch (System.Exception e) { Debug.LogWarning($"[ClickToMove] eventgen: {e.Message}"); }

                // No event spec (verb = select/placement/standard). eventgen's "select" simply
                // selects — the native law for an own building and an own ground crate alike
                // (evtgen_gnome's structure gate `!otherenemy && !alternate && !isininv` →
                // "select" before the boxed branch, eventgen.tcl:161-162): the bottom production
                // window (BottomMenuController) then owns ordering, unpack, pack, resume, repair;
                // its unpack cell is the only placement entry (PlacementController.OrderBuild —
                // FUN_005242b0 case 1). The old OrderBuild build-button stand-in (crate click →
                // instant ghost placement) is removed — the crate click now selects, and the
                // production panel handles ordering. The old cook shortcut that fired from
                // occluded clicks and cooked without an order is gone too.
                // Consumption runs once after the loop off the latched verb (FUN_005309c0):
                // Shift = additive include; a plain click / an incompatible join replaces.
                if (verbTrimmed == "select" && target.Owner == this.ctx.LocalPlayer)
                {
                    this.ctx.Selection.ClickSelect(target.Handle, multi);
                    Debug.Log($"[ClickToMove] selected {tcls}#{target.Handle} (verb=select, " +
                              $"count={this.ctx.Selection.Count})");
                }
                return;
            }

            if (this.ctx.SpawnParent == null) return;
            // hit.point is world-space; the walk verb expects native coords. Convert once.
            Vector3 nat = RuntimeWorldSpawner.LocalToNative(this.ctx.SpawnParent.InverseTransformPoint(hit.point));

            // (The armed-tool dig gestures — marquee, cave stamp, erase — anchored on the
            // mouse-down edge already: TryStartDigGesture, the native mode-2 tools.)

            // A click on dig-marked terrain with a dwarf selected = the native force-dig.
            // RE-verified hover law (ViWinRoot_Tick 0x00529c10, raw 0052a482/0052a5dd-0052a646):
            // the per-frame hover handle defaults to 0; with no scene object under the cursor the
            // pick FUN_004796d0:175-192 tests the cursor tile's +0x08 nibble for the current
            // layer (`tile[+8] & (0xf << (CGame[0x64]*4))` — the owner dig-mark nibble this port
            // models as RawDigNibble; its writer FUN_005671a0:50 is the mode-0 mark painter) and
            // a non-zero nibble reports the world object — handle 1, the 0x7890 sentinel branch.
            // Which tile: not the terrain surface under the ray — the native un-projects the
            // cursor at the front plane z = 15 (FUN_004796d0:177-179 `local_7c = 15.0` →
            // FUN_00479150 screen→world) and rounds that to the tile (:180-181). The mark plane
            // mesh itself renders at z = 15 (FUN_0056d0b0:544 / DigMarkOverlay.PlaneY), so this
            // is exactly "the click landed on the visible marker" — the terrain-surface tile is
            // parallax-shifted under the tilted camera and used to miss (clicks on the marker
            // object did nothing; only the block behind worked).
            // eventgen's `$subpar == 1` then answers "dig" → evt_task_dig -pos1 <hit>
            // (eventgen.tcl:132-139); ALT flips it to plain walk (:133-134) — all decided by the
            // UNCHANGED generator, exactly the FUN_00530b90 click consumption. evt_task_dig_proc
            // (z_events.tcl:1019-1055) then runs the same dig_starttask → get_digedge →
            // walk_pos_dig chain the marquee commit uses, seeded at THIS mark.
            if (sel != null && terrain != null)
            {
                // The click ray at the native front plane: two ray points → native space → solve
                // z = 15 (affine maps keep the ray a line, so the lerp is exact). Fallback to the
                // terrain-surface tile only if the ray is parallel to the plane (never in play).
                var ray = _cam.ScreenPointToRay(Input.mousePosition);
                Vector3 rn0 = RuntimeWorldSpawner.LocalToNative(this.ctx.SpawnParent.InverseTransformPoint(ray.origin));
                Vector3 rn1 = RuntimeWorldSpawner.LocalToNative(this.ctx.SpawnParent.InverseTransformPoint(ray.origin + ray.direction * 100f));
                if (!TryFrontPlanePoint(rn0, rn1, out var natMark)) natMark = nat;
                var (mtx, mty) = terrain.WorldToTile(natMark);
                if (terrain.RawDigNibble(mtx, mty, 0) != 0)   // owner 0 = the human player's mark layer
                {
                    try
                    {
                        // pospar = the mark-plane pick point (the sentinel branch has no terrain
                        // hit in the native either); z is irrelevant downstream — evt_task_dig's
                        // dig_starttask feeds it to get_digedge, which reads only x,y.
                        // Fan-out: the same FUN_00530b90 command loop — every selected unit
                        // gets the dig order at this mark (the whole crew comes to dig).
                        bool dAnyFired = false;
                        foreach (var u in SelectedUnitsSnapshot())
                        {
                            if (u.GameObject == null) continue;
                            string dverb = DigglesTclEngine.EvtGenRun(
                                this.ctx, u.Handle, WorldHandle, natMark, CommandTxtPar(AlternateHeld(), MultiselectHeld()));
                            bool dfired = DigglesTclEngine.EvtGenFire(this.ctx, u.Handle);
                            dAnyFired |= dfired;
                            Debug.Log($"[ClickToMove] eventgen #{u.Handle}→WORLD@({mtx},{mty}) marked: verb='{dverb}' " +
                                      $"evt='{this.ctx.EvtGen.EvtId}' fired={dfired}");
                        }
                        if (dAnyFired) return;
                    }
                    catch (System.Exception e) { Debug.LogWarning($"[ClickToMove] marked-terrain eventgen: {e.Message}"); }
                }
            }

            // Terrain → walk, raised as the original user event (evtgen_walk, eventgen.tcl:154/394:
            // `evtgen_attrib -evtid evt_task_walk -pos1 $pospar`). evt_task_walk_proc (z_events.tcl:
            // ~333) then runs the full user-order discipline the old direct `action walk` bypassed:
            // gnome_failed_work + tasklist_clear + stop_prod — a walk order interrupts work;
            // last_event/event_repeat — a repeated click on ~the same spot = run (walk_action
            // z_dignwalk.tcl: event_repeat → speedtype 2), the native double-click run;
            // tasklist_add walk_pos + state_triggerfresh task — the walk runs inside the state
            // machine (no state_disable needed);
            // and Baby's own evt_task_walk_proc (baby.tcl:353-384) gets its toddler semantics —
            // stand up from school (freeing the guest slot), the 1/5 fall-and-cry rolls, the
            // only-walk-near-an-adult gate — instead of obeying like an adult.
            // Fan-out over the whole selection (the native command loop): every selected
            // gnome/baby walks to the same point — the native passes one pospar to all
            // (FUN_00530b90 hands the identical &pos to every FUN_0055aaa0 iteration); a
            // selected structure answers "" through evtgen_prod and never walks.
            int walked = FireWalkOrder(nat);
            if (walked > 1) Debug.Log($"[ClickToMove] walk order fanned out to {walked} units");
        }

        // The uGUI over-panel gate (top of the RMB / world-click precedence ladder): true while
        // the pointer is over an EventSystem-raycastable UI element. Null-guarded — an EditMode
        // host / a scene with no EventSystem has EventSystem.current == null.
        static bool PointerOverUI()
            => UnityEngine.EventSystems.EventSystem.current != null
            && UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject();

        // A cursor-transparent entity (native obj flag 0x400 cleared by `set_hoverable 0`, or a
        // cloaked one) is skipped by both the click pick and the hover pick — it never enters the
        // candidate set, so the cursor falls through to whatever is behind. Deco beautifier spawns
        // (dug-cave vault beams, loot props — MapDecoBeautifier) are marked `set_hoverable 0` at
        // spawn, so this is the single gate that keeps them from ever highlighting or being
        // clicked (the "vault beams highlight" bug). Pure over the entity so the deco/pilz/enemy
        // candidate filtering is headless-testable.
        public static bool IsCursorTransparent(DigglesTclEntity e)
            => (e.VarsScope.Vars.TryGetValue("_hoverable", out var hv) && (hv == "0" || hv == "false"))
            || (e.VarsScope.Vars.TryGetValue("_cloaked", out var cv) && (cv == "1" || cv == "true"));

        // Legacy raw-byte classifier kept for the older pure tests. It is deliberately not the
        // input gate: gray-fog visibility depends on the object/class viewinfog bits set by
        // FUN_00680620:72-87 / FUN_00613b20:80-95.
        public static bool FogBlocksCursor(byte fogByte) => fogByte == 0;

        // Shared render/input law. DigglesGameSim computes FowHidden from the sampled fog byte,
        // the native 0x80 render threshold (FUN_004845a0:34-36) and the entity/class
        // _viewinfog fallback, then
        // DigglesTclEngine.SyncRenderVisible consumes this exact field. Hover and click consume
        // the same result instead of sampling a second position and reimplementing fog policy.
        public static bool RenderVisibilityBlocksCursor(DigglesTclEntity e) => e.FowHidden;

        // Plain and Alt keep the original event-generator contract (eventgen.tcl:13,
        // 161-164,186-189). Shift pickup is the product override and does not combine with Alt.
        public enum CrateClickVerb { EventGen, PickupOrder, Select }
        public static CrateClickVerb ResolveCrateClick(bool ownBoxedCrate, bool gnomeSelected,
            bool shiftHeld, bool altHeld)
        {
            if (!(ownBoxedCrate && gnomeSelected)) return CrateClickVerb.EventGen;
            if (shiftHeld && !altHeld) return CrateClickVerb.PickupOrder;
            return CrateClickVerb.EventGen;
        }

        // The RMB-click precedence ladder as a pure decision — the runtime spreads these rungs
        // across the down edge (DisarmDigTool) and the up edge (Deselect / ToggleRightPanel),
        // with placement handled by the early-return above; this function is the
        // single-source-of-truth table the tests pin. Top rung wins.
        public enum RmbClickAction { OverUI, CancelPlacement, DisarmDigTool, Deselect, ToggleRightPanel }
        public static RmbClickAction ResolveRmbClick(bool pointerOverUI, bool placementActive,
            bool digToolArmed, bool hasSelection)
        {
            if (pointerOverUI) return RmbClickAction.OverUI;
            if (placementActive) return RmbClickAction.CancelPlacement;
            if (digToolArmed) return RmbClickAction.DisarmDigTool;
            if (hasSelection) return RmbClickAction.Deselect;
            return RmbClickAction.ToggleRightPanel;
        }

        // The boxed-crate PICK UP order over the selected GNOMES — public +
        // Input-free so the pickup law is headless-testable (like FireWalkOrder). Raises
        // evt_task_pickup with the crate as -subject1 — the form eventgen_attrib builds
        // (eventgen.tcl:188) and evt_task_pickup_proc reads via `event_get this -subject1`
        // (z_events.tcl:410). Only gnomes carry crates; a boxed structure never picks itself up.
        // Returns how many gnomes were ordered.
        public int FirePickupOrder(int crateHandle)
        {
            if (this.ctx.Interp == null) return 0;
            string cmd = "set_event this evt_task_pickup -subject1 " + crateHandle;
            int fired = 0;
            foreach (var u in SelectedUnitsSnapshot())
            {
                if ((u.Class?.Kind ?? "") != "gnome") continue;      // only gnomes carry crates
                if (u.Owner != this.ctx.LocalPlayer) continue;       // never command a foreign unit
                var prev = this.ctx.This;
                this.ctx.This = u;
                try { this.ctx.Interp.Eval(cmd); fired++; }
                catch (System.Exception e) { Debug.LogWarning($"[ClickToMove] pickup #{u.Handle}: {e.Message}"); }
                finally { this.ctx.This = prev; }
            }
            return fired;
        }

        // The terrain-walk order over the SELECTION — public + Input-free so the fan-out law
        // is headless-testable. Returns how many units were ordered.
        public int FireWalkOrder(Vector3 nat)
        {
            if (this.ctx.Interp == null) return 0;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string cmd = "set_event this evt_task_walk -pos1 {" + nat.x.ToString(inv) + " " +
                         nat.y.ToString(inv) + " " + nat.z.ToString(inv) + "}";
            int walked = 0;
            foreach (var u in SelectedUnitsSnapshot())
            {
                string k = u.Class?.Kind ?? "";
                if (k != "gnome" && k != "baby") continue;      // structures never walk
                if (u.Owner != this.ctx.LocalPlayer) continue;  // never command a foreign unit
                var prev = this.ctx.This;
                this.ctx.This = u;
                try { this.ctx.Interp.Eval(cmd); walked++; }
                catch (System.Exception e) { Debug.LogWarning($"[ClickToMove] walk #{u.Handle}: {e.Message}"); }
                finally { this.ctx.This = prev; }
            }
            return walked;
        }

        // Snapshot of the selection resolved to live entities (stable under mid-loop selection
        // mutation — Tcl event handlers may reselect). Reused list, no per-click alloc.
        readonly System.Collections.Generic.List<DigglesTclEntity> _selSnapshot = new();
        System.Collections.Generic.List<DigglesTclEntity> SelectedUnitsSnapshot()
        {
            _selSnapshot.Clear();
            foreach (var h in this.ctx.Selection.Handles)
                if (this.ctx.Entities.TryGetValue(h, out var e)) _selSnapshot.Add(e);
            return _selSnapshot;
        }

        // ---- deferred press / band-box tracking ----

        // A pending deferred click: release under the band gate = the ordinary click; crossing
        // the |dx|·|dy| > 250 px² gate = promote to the drag box (the native mode 1 → 3
        // transition, FUN_0052fe80:64-74). Lost release (focus loss) = drop silently.
        void UpdatePress()
        {
            var release = MarqueeReleaseFor(Input.GetMouseButton(0), Input.GetMouseButtonUp(0));
            if (release == MarqueeRelease.CancelLostRelease) { _pressPending = false; return; }
            Vector2 cur = Input.mousePosition;
            if (release == MarqueeRelease.Continue)
            {
                // Never band while a dig tool is armed AND Shift is not held — the native mode 2
                // owns the mouse (only mode 1 promotes to mode 3). But Shift = multiselect wins
                // over an armed tool, so a Shift+drag always band-boxes
                // regardless of the armed tool (BoxDragEligible).
                if (BoxDragEligible(DigToolArmed, MultiselectHeld()) && BoxDragStarts(_pressPos, cur))
                {
                    _pressPending = false;
                    _boxDrag = true;
                    _boxAnchor = _pressPos;
                    var ov = BoxOverlay();
                    ov.Active = true; ov.A = _boxAnchor; ov.B = cur;
                }
                return;
            }
            _pressPending = false;
            HandleClick();   // the native mouse-UP click consumption
        }

        // The live drag box (native mode 3, FUN_00530770): track the rectangle each frame,
        // band-select on release (FUN_0052d170), clear the visual either way.
        void UpdateBoxSelect()
        {
            var release = MarqueeReleaseFor(Input.GetMouseButton(0), Input.GetMouseButtonUp(0));
            if (release == MarqueeRelease.CancelLostRelease)
            {
                _boxDrag = false; HideBoxOverlay();
                Debug.LogWarning("[ClickToMove] box select: mouse-up edge LOST (focus loss?) — cancelled");
                return;
            }
            Vector2 cur = Input.mousePosition;
            var ov = BoxOverlay();
            ov.Active = true; ov.A = _boxAnchor; ov.B = cur;
            if (release != MarqueeRelease.Commit) return;
            _boxDrag = false;
            HideBoxOverlay();
            // Shift sampled at COMMIT (FUN_0052d170:110-113 GetAsyncKeyState on entry): held =
            // ADD to the selection, otherwise the box replaces it.
            CommitBoxSelect(_boxAnchor, cur, MultiselectHeld());
        }

        // The band-select commit (FUN_0052d170): every OWN, selectable, band-kind object whose
        // projected position lies inside the screen rectangle joins through the native include
        // law (SelectionSet.BoxIncludeStep — first candidate enters, gnomes/babies join it,
        // further structures are skipped), in handle order like the native table walk (:595).
        // Deviations, documented: the native also
        // intersects a world-space band volume between the z=7/z=14.5 planes (its rect visual
        // lives on the world front plane) — the port tests the screen rect only, which is what
        // the player sees; fog-hidden foreigners are excluded by the owner gate already.
        public void CommitBoxSelect(Vector2 a, Vector2 b, bool additive)
        {
            EnsureCurrentContext();
            if (_cam == null) return;
            if (!additive) this.ctx.Selection.Clear();
            float x0 = Mathf.Min(a.x, b.x), x1 = Mathf.Max(a.x, b.x);
            float y0 = Mathf.Min(a.y, b.y), y1 = Mathf.Max(a.y, b.y);
            var picked = new System.Collections.Generic.List<int>();
            foreach (var e in this.ctx.Entities.Values)
            {
                if (!BoxEligible(e, this.ctx.LocalPlayer)) continue;
                var go = e.GameObject;
                if (go == null || !go.activeInHierarchy) continue;   // not drawn → not bandable
                // One point per object like the native (+0xc/+0x10/+0x14 centre projected,
                // FUN_0052d170:614-631): the collider bounds centre when present, else the root.
                var col = go.GetComponentInChildren<Collider>();
                Vector3 wp = col != null ? col.bounds.center : go.transform.position;
                Vector3 sp = _cam.WorldToScreenPoint(wp);
                if (sp.z <= 0f) continue;
                if (sp.x < x0 || sp.x > x1 || sp.y < y0 || sp.y > y1) continue;
                picked.Add(e.Handle);
            }
            picked.Sort();   // the native iterates the handle table in order
            foreach (var h in picked) this.ctx.Selection.BoxIncludeStep(h);
            Debug.Log($"[ClickToMove] box select: {picked.Count} candidate(s) in rect, " +
                      $"selection = {this.ctx.Selection.Count} (primary #{this.ctx.SelectedHandle}, additive={additive})");
            // diagnostic: an empty band over a world that HAS own dwarves is the manual test
            // "can't select anything" symptom — dump each dwarf's gate results once per commit
            // so the failing gate is visible in the log instead of guessed at.
            if (picked.Count == 0)
            {
                foreach (var e in this.ctx.Entities.Values)
                {
                    if (e.Class?.Kind != "gnome") continue;
                    var go = e.GameObject;
                    Vector3 screen = go != null
                        ? _cam.WorldToScreenPoint(go.transform.position)
                        : new Vector3(float.NaN, float.NaN, float.NaN);
                    Debug.LogWarning(
                        $"[ClickToMove] band-miss diag #{e.Handle} '{e.Class?.Name}': " +
                        $"owner={e.Owner}/local={this.ctx.LocalPlayer} " +
                        $"eligible={BoxEligible(e, this.ctx.LocalPlayer)} " +
                        $"go={(go == null ? "null" : go.activeInHierarchy ? "active" : "INACTIVE")} " +
                        $"screen=({screen.x:F0},{screen.y:F0},z{screen.z:F1}) " +
                        $"rect=({x0:F0}..{x1:F0},{y0:F0}..{y1:F0}) cam={_cam.name}");
                }
            }
        }

        // The model-level band eligibility (owner + kind + flags — FUN_0052d170:607-613), pure
        // over the entity so it is headless-testable; the GameObject/screen gates live in the
        // commit above.
        public static bool BoxEligible(DigglesTclEntity e, int localPlayer)
        {
            if (e.Owner != localPlayer) return false;
            if (!Tcl.Engine.SelectionSet.KindBandSelectable(e.Class?.Kind)) return false;
            if (e.VarsScope.Vars.TryGetValue("_cloaked", out var cv) && (cv == "1" || cv == "true")) return false;
            if (e.VarsScope.Vars.TryGetValue("_selectable", out var sv) && (sv == "0" || sv == "false")) return false;
            return true;
        }

        SelectionBoxOverlay BoxOverlay()
        {
            if (_boxOverlay == null)
            {
                var go = new GameObject("SelectionBoxOverlay") { hideFlags = HideFlags.DontSave };
                _boxOverlay = go.AddComponent<SelectionBoxOverlay>();
            }
            return _boxOverlay;
        }

        void HideBoxOverlay()
        {
            if (_boxOverlay != null) _boxOverlay.Active = false;
        }

        // Per-frame placement tracking (the native placement tick FUN_0046b450 + the
        // FUN_00530b90:115-120 click routing): pick TERRAIN ONLY under the cursor, drag the
        // ghost (UpdateCursor quantizes, seats it on the floor cell and recomputes the
        // +0xcc/+0xcd validity/asbox), LMB = confirm, Esc = cancel. There is NO asbox key:
        // the native computes +0xcd per frame from the spot (building vs box-only) — the
        // deprecated PlacementController.ToggleAsBox shim remains for tests/UI only.
        void UpdatePlacement(PlacementController placement)
        {
            // An INVISIBLE placement mode (the cursor ghost failed to build — e.g. a class
            // outside the model map with no GameObject to clone) is indistinguishable from
            // "clicks just died" for the player: name it, once, the moment it eats input.
            if (placement.CursorGhost == null && !_warnedGhostlessPlacement)
            { _warnedGhostlessPlacement = true; Debug.LogWarning($"[ClickToMove] EATER: placement mode active for #{placement.PlacingHandle} with NO cursor ghost (invisible mode) — LMB confirms, RMB/Esc cancels"); }
            // Esc OR right-click cancels (the native placement exit). The RMB path matters
            // doubly: placement entered through an eventgen "placement" verb can be INVISIBLE
            // when the ghost model fails to build — the mode then silently ate every click
            if (Input.GetKeyDown(KeyCode.Escape) || (Input.GetMouseButtonDown(1) && !PointerOverUI()))
            {
                placement.Cancel();
                Debug.LogWarning("[Placement] EXIT (cancelled) — clicks are normal again");
                return;
            }
            var ray = _cam!.ScreenPointToRay(Input.mousePosition);
            if (this.ctx.SpawnParent != null && TryPickTerrain(ray, out var hitPoint))
            {
                Vector3 nat = RuntimeWorldSpawner.LocalToNative(this.ctx.SpawnParent.InverseTransformPoint(hitPoint));
                placement.UpdateCursor(nat);
            }
            if (Input.GetMouseButtonDown(0) && !WorkClockWidget.PointerCaptured && !PointerOverUI())
            {
                bool valid = placement.Valid;
                bool ok = placement.Confirm();
                Debug.Log($"[ClickToMove] placement confirm: ok={ok} valid={valid} asbox={placement.AsBox} pos={placement.GhostPos}");
            }
        }

        // The placement cursor picks the TERRAIN surface only — the native pick (FUN_0046aec0)
        // reads the world heightfield under the mouse; world objects never occlude it. That is
        // also what lets the player place into LOWER CAVES: point at a visible cave floor
        // through the opening and the ghost seats on it (camera does the rest — no keys, no
        // level cycling). Filter: only the DiggableTerrain chunk MeshColliders count (entities
        // carry BoxColliders and may share the same world-root ancestor, so both checks are
        // needed; water submeshes have no collider). NOTE: the native pick also rejects
        // UNEXPLORED tiles (tile+0x34[player], FUN_0046aec0:31-66) — the port has no fog-of-war
        // state yet; that gate awaits FoW.
        // Reused buffer: TryPickTerrain runs EVERY FRAME while the dig tool is armed or a
        // placement ghost is live — Physics.RaycastAll allocated a fresh RaycastHit[] per frame
        readonly RaycastHit[] _terrainHitBuf = new RaycastHit[64];

        bool TryPickTerrain(Ray ray, out Vector3 point)
        {
            point = default;
            int hitCount = Physics.RaycastNonAlloc(ray, _terrainHitBuf, 10000f, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            for (int i = 0; i < hitCount; i++)
            {
                var h = _terrainHitBuf[i];
                if (h.distance >= best) continue;
                if (!(h.collider is MeshCollider)) continue;
                if (h.collider.GetComponentInParent<World.DiggableTerrain>() == null) continue;
                best = h.distance;
                point = h.point;
            }
            return best < float.MaxValue;
        }

        // The marquee's release decision, PURE so the stuck state is headless-testable
        // (selection-loss bug): Input.GetMouseButtonUp is a polled EDGE —
        // losing focus mid-drag (alt-tab, an OS/editor overlay stealing the pointer, an
        // EventSystem modal) drops that edge; _digDrag then stayed true FOREVER and every
        // Tick early-returned at the marquee gate: NOTHING was selectable again.
        // up-edge → Commit (the normal FUN_005300f0 mouse-up commit);
        // still held → Continue dragging;
        // neither → the release happened UNSEEN → CancelLostRelease (drop the
        // marquee, no phantom order from a stale corner).
        public enum MarqueeRelease { Continue, Commit, CancelLostRelease }
        public static MarqueeRelease MarqueeReleaseFor(bool mouseHeld, bool mouseUpEdge)
            => mouseUpEdge ? MarqueeRelease.Commit
             : mouseHeld ? MarqueeRelease.Continue
             : MarqueeRelease.CancelLostRelease;

        // The ALTERNATE-action modifier = ALT (RE ground truth: the native txtpar builder
        // FUN_00530b90:51-58 checks GetAsyncKeyState(0x12)/VK_MENU for " -alternate" — and
        // VK_SHIFT(0x10) for " -multiselect", so Shift was never the alternate key). The
        // append happens BEFORE the cursor-vs-command branch, so BOTH the click command AND
        // the hover cursor spec carry -alternate while ALT is held.
        public static bool AlternateHeld()
            => Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);

        // The MULTISELECT modifier = SHIFT (the native txtpar builder FUN_00530b90:51-53
        // appends " -multiselect" for VK_SHIFT/0x10; the same key gates the click-ADD in
        // FUN_005309c0 and the box-ADD in FUN_0052d170).
        public static bool MultiselectHeld()
            => Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

        // ---- the SHIFT-precedence law (item: "while Shift is held a
        // cave/tunnel MARKER appears — that's wrong") ----
        // User-verified native law (and the code's own header, FUN_00530b90:51-53): Shift is
        // PURELY the multiselect key. The port once carried a Shift+drag dig SHORTCUT (a
        // deviation) and armed the hover marker on Shift — that leaked the dig preview into
        // plain multi-select. The fix pins the precedence: while Shift is held the dig marker,
        // the dig ghost AND the dig gesture are ALL suppressed (multi-select wins regardless of
        // an armed tool) and the drag box takes over. The dig marker/gesture needs a MENU-armed
        // tool AND Shift not held; the box drag runs whenever no tool is armed OR Shift is held.
        // DigMarkerActive — is the dig marker/ghost shown & is a dig gesture allowed to start?
        // BoxDragEligible — may a held press promote to the multi-select drag box?
        public static bool DigMarkerActive(bool digToolArmed, bool multiselectHeld)
            => digToolArmed && !multiselectHeld;
        public static bool BoxDragEligible(bool digToolArmed, bool multiselectHeld)
            => !digToolArmed || multiselectHeld;

        // The LMB down-edge press-arm law (manual test residual an empty-air drag must
        // band-box). Native ground truth: the mode-1 press anchor is stored SCREEN-SPACE ONLY
        // (FUN_0052fe80:53-54 — the down coords go to +0x138/+0x13c with no world pick) and the
        // 1→3 band promotion (:64-74) tests only the screen deltas; picking happens at the UP
        // consumption (:85 → FUN_005309c0). Port law: ONLY a started dig gesture (which itself
        // needs terrain under the cursor) consumes the down edge — a world-pick MISS must NOT
        // block the deferred press. worldHit is deliberately unused: the parameter exists so the
        // table test pins that a miss changes nothing.
        public static bool PressArmsDeferred(bool worldHit, bool digGestureStarted)
            => !digGestureStarted;

        // The eventgen txtpar for a command click (pure — pins the RE'd token law in tests).
        public static string CommandTxtPar(bool alternate)
            => CommandTxtPar(alternate, false);

        public static string CommandTxtPar(bool alternate, bool multiselect)
        {
            string t = "-command";
            if (multiselect) t += " -multiselect";
            if (alternate) t += " -alternate";
            return t;
        }

        // The native MARK pick plane (FUN_004796d0:177-179): the cursor ray un-projected at the
        // NATIVE front plane z = 15 — the plane the DigMark mesh itself renders on
        // (FUN_0056d0b0:544 / DigMarkOverlay.PlaneY). Pure line∩plane over two native-space ray
        // points; false only when the ray runs parallel to the plane.
        public const float MarkPlaneZ = World.DigMarkOverlay.PlaneY;
        public static bool TryFrontPlanePoint(Vector3 nat0, Vector3 nat1, out Vector3 at)
        {
            at = default;
            float dz = nat1.z - nat0.z;
            if (Mathf.Abs(dz) < 1e-6f) return false;
            float t = (MarkPlaneZ - nat0.z) / dz;
            at = nat0 + (nat1 - nat0) * t;
            return true;
        }

        // The WORLD object's handle — a hardcoded literal in the native pick itself
        // (ViWinRoot_Tick raw 0052a646: `mov dword [CGame+0xa0cc], 1` on the terrain-hit
        // sentinel; the click handler FUN_00530b90:16 treats handles 0/1 as non-object
        // sentinels), matched by eventgen.tcl:132's LITERAL `$subpar == 1` ("dig"). The port
        // does not reserve the handle (a boot's first `new` could theoretically take 1 — in
        // practice the menu-time actors consume the low handles before gameplay); the click
        // layer only ever passes this constant for marked-terrain clicks, where the world IS
        // the intended subject.
        public const int WorldHandle = 1;

        // The native drop-all key command, verbatim (FUN_0052c510:262: the format string
        // "set_event %d evt_task_dropall -target %d" — both %d = the selected unit's handle).
        public static string DropAllCommand(int handle)
            => "set_event " + handle + " evt_task_dropall -target " + handle;

        // F9 (see TickInner): raise evt_task_dropall on EVERY selected OWN unit — the native
        // key handler formats the command per selected unit (FUN_0052c510:242-276 loops the
        // selection). Public so headless tests can drive the key law without Input.
        public void FireDropAll()
        {
            EnsureCurrentContext();
            if (this.ctx.Interp == null) return;
            int fired = 0;
            foreach (var u in SelectedUnitsSnapshot())
            {
                if (u.Owner != this.ctx.LocalPlayer) continue;   // never command a foreign unit
                try { this.ctx.Interp.Eval(DropAllCommand(u.Handle)); fired++; }
                catch (System.Exception e) { Debug.LogWarning($"[ClickToMove] dropall #{u.Handle}: {e.Message}"); }
            }
            if (fired > 0) Debug.Log($"[ClickToMove] F9 drop-all → {fired} unit(s)");
        }

        // Per-frame marquee tracking (FUN_005300f0's drag phase): depth keys, the moving corner
        // (+0xf0/+0xf4 analogue) from a fresh terrain raycast, a debug preview, commit on release.
        void UpdateDigMarquee()
        {
            // RELEASE GUARD first: a missed mouse-up edge must never own the mouse forever.
            var release = MarqueeReleaseFor(Input.GetMouseButton(0), Input.GetMouseButtonUp(0));
            if (release == MarqueeRelease.CancelLostRelease)
            {
                _digDrag = false;
                ClearPreview();
                World.DigMarkOverlay.ClearHover();
                Debug.LogWarning("[ClickToMove] dig marquee: mouse-up edge was LOST (focus loss?) — marquee cancelled, clicks are normal again");
                return;
            }

            // The rect marquee only ever runs the STANDARD tunnel now — tools 1..4 are the cave
            // template STAMPS (click-to-stamp, not a resizable rect), so the old mid-drag depth-key
            // switch is gone. DigDepthCnt stays DigStandard for the whole drag (the drag only starts
            // when the standard tool is armed).
            var terrain = this.ctx.Terrain;
            if (terrain == null || this.ctx.SpawnParent == null)
            { _digDrag = false; ClearPreview(); World.DigMarkOverlay.ClearHover(); return; }

            var ray = _cam!.ScreenPointToRay(Input.mousePosition);
            if (Physics.Raycast(ray, out var hit, 10000f))
            {
                Vector3 nat = RuntimeWorldSpawner.LocalToNative(this.ctx.SpawnParent.InverseTransformPoint(hit.point));
                // FUN_005680b0 mode != 0 (DigMarquee.SnapDragCorner): the live pick snaps
                // per-axis to the 2-tile lattice and the CROSS axis clamps to the anchor —
                // the dominant drag axis becomes the tunnel length, re-decided every frame
                // (mid-drag orientation switching is the original behavior). This is what
                // makes fat rectangles impossible to order, like the original tool.
                (_digBx, _digBy) = DigMarquee.SnapDragCorner(_digAx, _digAy, nat.x, nat.y);
            }
            // The per-cell verdicts render as the native DigMark plane mesh — the original draws
            // the drag rect as a translucent vertex-colored FILLED marching-squares mesh on the
            // front plane (valid = pulsating green A=0x33, invalid = red-orange A=0xAF; DigMark
            // FUN_0056d0b0:397-477) — via the dig-mark overlay's hover layer.
            PublishRectHover(terrain, _digAx, _digAy, _digBx, _digBy);

            if (release == MarqueeRelease.Commit)
            {
                _digDrag = false;
                ClearPreview();
                World.DigMarkOverlay.ClearHover();
                CommitDigMarquee(terrain);
            }
        }

        void ClearPreview()
        {
            if (_preview != null) _preview.Segments.Clear();
        }

        // ---- pre-click hover indicator (armed dig tool / Shift held) ----
        // The NATIVE hover: CDisplay_DrawObjects (0x00473cc0:968-998) calls the DigMark preview
        // every frame the dig tool is selected — mode 0 draws a single-cell pulsating-green
        // OUTLINE ring at the picked cell BEFORE any click (FUN_0056d0b0's single-cell path,
        // local_48=1 → outline-only tessellation + the brighter 0x50 job factor; suppressed once
        // a drag starts at:86). The port highlights the 2×2 anchor crossroads block a click
        // would actually mark (SnapAnchor + the zero-drag SnapRect — exactly the commit's rect),
        // with the same per-cell verdicts the marquee preview shows. Published to
        // DigMarkOverlay's hover layer, rendered as the native plane mesh (outline rings here).
        readonly System.Collections.Generic.List<(int tx, int ty, bool valid)> _hoverScratch = new();

        void UpdateDigHover()
        {
            var terrain = this.ctx.Terrain;
            // The dig marker follows a MENU-armed tool, never Shift (Shift = multiselect only —
            //). While Shift is held the marker is suppressed even with a tool
            // armed, so multi-select never paints a phantom cave/tunnel ghost. (DigMarkerActive.)
            bool armed = DigMarkerActive(DigToolArmed, MultiselectHeld());
            if (!armed || terrain == null || this.ctx.SpawnParent == null || WorkClockWidget.PointerCaptured)
            { World.DigMarkOverlay.ClearHover(); return; }
            var ray = _cam!.ScreenPointToRay(Input.mousePosition);
            if (!TryPickTerrain(ray, out var hitPoint)) { World.DigMarkOverlay.ClearHover(); return; }
            Vector3 nat = RuntimeWorldSpawner.LocalToNative(this.ctx.SpawnParent.InverseTransformPoint(hitPoint));
            int tool = DigDepthCnt;
            // CAVE tool → the whole template silhouette follows the mouse, each carve cell colored
            // by its FUN_00566a60 verdict (valid green / invalid red-orange, all-or-nothing on click).
            if (tool >= 0) { PublishCaveHover(terrain, nat, tool); return; }
            // ERASER → the opaque-gray 2×2 delete brush at the cursor tile.
            if (tool == DigErase) { PublishEraseHover(terrain, nat); return; }
            // STANDARD tunnel → the native single-anchor ring at the 4-lattice crossroads.
            var (ax, ay) = DigMarquee.SnapAnchor(nat.x, nat.y);
            PublishRectHover(terrain, ax, ay, ax, ay, outline: true);
        }

        // The cave stamp's ghost preview (FUN_0056c530): publish the template's carve-cell MASK at
        // the snapped anchor, each cell carrying its per-cell CaveStamp.Validate verdict — the SAME
        // all-or-nothing check the commit runs, so what turns red-orange in the preview is exactly
        // what would abort the stamp.
        void PublishCaveHover(World.DiggableTerrain terrain, Vector3 nat, int tool)
        {
            var tpl = CaveTemplateFor(tool);
            if (tpl == null) { World.DigMarkOverlay.ClearHover(); return; }
            var (ax, ay) = CaveStamp.SnapAnchor(nat.x, nat.y);
            _hoverScratch.Clear();
            CaveStamp.Validate(tpl, ax, ay, terrain, 0, _hoverScratch);
            World.DigMarkOverlay.PublishHover(_hoverScratch, outline: false);
        }

        // The eraser cursor: the 2×2 window a stroke step would clear (FUN_00567410), gray brush.
        void PublishEraseHover(World.DiggableTerrain terrain, Vector3 nat)
        {
            var (tx, ty) = terrain.WorldToTile(nat);
            _hoverScratch.Clear();
            for (int dy = 0; dy < 2; dy++)
                for (int dx = 0; dx < 2; dx++)
                    _hoverScratch.Add((tx + dx, ty + dy, true));
            World.DigMarkOverlay.PublishHover(_hoverScratch, outline: false, World.DigMarkOverlay.HoverStyle.Erase);
        }

        // Shared by the pre-click hover (zero-drag anchor block, outline=true — the native ring)
        // and the live drag marquee (outline=false — filled cells): publish the grown
        // [min, max+2) rect's per-cell verdicts to the overlay hover layer.
        // Same 1024-cell cap as DrawDigPreview (a runaway drag skips the per-cell paint).
        void PublishRectHover(World.DiggableTerrain terrain, int ax, int ay, int bx, int by, bool outline = false)
        {
            var (x0, y0, x1, y1) = DigMarquee.SnapRect(ax, ay, bx, by);
            bool standard = DigDepthCnt < 0;
            _hoverScratch.Clear();
            if ((x1 - x0 + 1) * (y1 - y0 + 1) <= 1024)
                for (int ty = y0; ty <= y1; ty++)
                    for (int tx = x0; tx <= x1; tx++)
                        _hoverScratch.Add((tx, ty,
                            DigMarquee.CellMarkable(tx, ty, standard) && CellAllowed(terrain, tx, ty, standard)));
            World.DigMarkOverlay.PublishHover(_hoverScratch, outline);
        }

        // ---- the per-cell verdict shared by preview + commit ----
        // STANDARD tunnel → the full FUN_00566870 validity (DigMarquee.CellValid) wired over
        // DiggableTerrain:
        // neighbourSolid — the original reads the neighbour BLOCK's aux byte +0x00 (the 8×8
        // heightmap's NW corner, FUN_004093b0 = base + idx*0x59) and requires > 1;
        // approximated with GetHmap (= AuxMaxCorner — diverges only on blocks whose NW
        // corner is carved below 2 while another corner is still up) until DiggableTerrain
        // (owned elsewhere) grows a NW-corner accessor; OOB = 15 = solid, like the original
        // never reaching OOB through its rim bounds.
        // openMarked — the owner-0 nibble's OPEN bit 8 (tile+0x08, FUN_00566870:25/:34).
        // exposed — the tile MATERIAL TYPE tile+0x18 & 3 ∈ {1,2} (FUN_00566870:26-28; the
        // painter re-checks it, FUN_005672d0:41-44) — now the faithful
        // DiggableTerrain.TileMaterialDiggable (PMP terrain byte: rejects STONE/granite = 0
        // and fully OPEN/air = 3 —/M3; the old HasGeometry stand-in
        // let granite AND partially-open air tiles through). The commit loop runs this per
        // cell too, so invalid (air/granite) cells are silently DROPPED on mouse-up exactly
        // like the native painter (FUN_00566550:112-123 → FUN_005672d0 skips, no abort).
        // DEPTH tools (1..4) → the original's mode-0 painter FUN_005671a0 has no lattice or
        // neighbour validity, but DOES gate on the same material band when the tile is explored
        // (:33-38 rejects type 0/3; no-fog port = always explored) — type check + the old
        // HasGeometry belt (approximates the unmodelled FUN_005968b0 rim-vs-threshold block).
        World.DiggableTerrain? _validityFor;
        System.Func<int, int, bool>? _vSolid, _vOpen, _vExposed;
        bool CellAllowed(World.DiggableTerrain t, int tx, int ty, bool standard, bool painterGate = false)
        {
            // The painters' RIM RE-MARK gate (FUN_005672d0:40-53 mode-1 / FUN_005671a0:32-45
            // mode-0): a cell whose node rim can no longer reach the PAINTED nibble's decoded
            // target is dropped — marking a standard tunnel (target 11) over an ALREADY-CARVED
            // tunnel (rim ≤ 7) paints nothing. This gate lives in the
            // PAINTER only: the native drag PREVIEW (FUN_0056d0b0:25) runs FUN_00566870
            // WITHOUT it, so dragging over an already-dug tunnel shows the cells VALID (green)
            // and the paint silently drops them on mouse-up (user-verified original behavior,
            // round-3 note). `painterGate` is therefore true only from the COMMIT loop.
            if (painterGate)
            {
                int paintNibble = standard ? 3 : (Mathf.Clamp(DigDepthCnt, 0, 3) | 8);
                if (!t.MarkRimGate(tx, ty, paintNibble)) return false;
            }
            if (!standard) return t.TileMaterialDiggable(tx, ty) && t.HasGeometry(tx, ty);
            if (!ReferenceEquals(_validityFor, t))
            {
                _validityFor = t;
                _vSolid = (x, y) => t.NwCornerHeight(x, y) > 1;   // FUN_00566870:35-50 NW-corner aux byte
                _vOpen = (x, y) => (t.RawDigNibble(x, y, 0) & 8) != 0;   // owner 0 = human player
                _vExposed = (x, y) => t.TileMaterialDiggable(x, y);   // FUN_00566870:26-28
            }
            return DigMarquee.CellValid(tx, ty, t.Width, t.Height, _vSolid!, _vOpen!, _vExposed!);
        }

        DigMarqueePreviewLines Preview()
        {
            if (_preview == null)
            {
                var go = new GameObject("DigMarqueePreview") { hideFlags = HideFlags.DontSave };
                _preview = go.AddComponent<DigMarqueePreviewLines>();
            }
            return _preview;
        }

        // Runtime preview of the snapped marquee (rebuilt each drag frame into the GL line pool —
        // visible in the real Game view, unlike Debug.DrawLine): yellow outline = the grown commit
        // rect ([min, max+2), DigMarquee.SnapRect over the SNAPPED anchor/corner — exactly 2
        // across for the standard tunnel); per cell a green diamond = will be marked, grey X =
        // fails the FUN_00566870 validity / has no geometry (the original preview FUN_0056d0b0:
        // 25 greys exactly these), red X = lattice pillar (unreachable inside a snapped
        // standard rect — kept as defense). Tile t spans native [t-0.5, t+0.5) (WorldToTile
        // rounds), each cell drawn at its own hmap height.
        void DrawDigPreview(World.DiggableTerrain terrain)
        {
            var sp = this.ctx.SpawnParent;
            if (sp == null) return;
            var seg = Preview().Segments;
            seg.Clear();
            void L(Vector3 a, Vector3 b, Color c) => seg.Add((a, b, c));
            var (x0, y0, x1, y1) = DigMarquee.SnapRect(_digAx, _digAy, _digBx, _digBy);
            bool standard = DigDepthCnt < 0;
            Vector3 W(float nx, float ny, float nz) =>
                sp.TransformPoint(RuntimeWorldSpawner.NativeToLocal(new Vector3(nx, ny, nz)));

            // Outline through the 4 corner tiles (each at its own height, lifted a touch).
            Vector3 c00 = W(x0 - 0.5f, y0 - 0.5f, terrain.HmapToZ(x0, y0) + 0.2f);
            Vector3 c10 = W(x1 + 0.5f, y0 - 0.5f, terrain.HmapToZ(x1, y0) + 0.2f);
            Vector3 c11 = W(x1 + 0.5f, y1 + 0.5f, terrain.HmapToZ(x1, y1) + 0.2f);
            Vector3 c01 = W(x0 - 0.5f, y1 + 0.5f, terrain.HmapToZ(x0, y1) + 0.2f);
            L(c00, c10, Color.yellow);
            L(c10, c11, Color.yellow);
            L(c11, c01, Color.yellow);
            L(c01, c00, Color.yellow);

            // Per-cell verdicts (capped — a runaway drag falls back to the outline alone).
            if ((x1 - x0 + 1) * (y1 - y0 + 1) > 1024) return;
            for (int ty = y0; ty <= y1; ty++)
                for (int tx = x0; tx <= x1; tx++)
                {
                    float z = terrain.HmapToZ(tx, ty) + 0.2f;
                    bool pillar = !DigMarquee.CellMarkable(tx, ty, standard);
                    bool ok = CellAllowed(terrain, tx, ty, standard);
                    if (pillar || !ok)
                    {   // X = skipped (red lattice pillar / grey validity failure)
                        Color xc = pillar ? Color.red : Color.grey;
                        L(W(tx - 0.35f, ty - 0.35f, z), W(tx + 0.35f, ty + 0.35f, z), xc);
                        L(W(tx - 0.35f, ty + 0.35f, z), W(tx + 0.35f, ty - 0.35f, z), xc);
                        continue;
                    }
                    // diamond = will be marked
                    Vector3 e = W(tx + 0.35f, ty, z), w = W(tx - 0.35f, ty, z);
                    Vector3 n = W(tx, ty - 0.35f, z), s = W(tx, ty + 0.35f, z);
                    L(e, n, Color.green); L(n, w, Color.green);
                    L(w, s, Color.green); L(s, e, Color.green);
                }
        }

        // Mouse-up commit (FUN_00566550/FUN_00567cb0 analogue): paint the owner-0 dig nibble + the
        // current mark id over every lattice-markable geometric tile of the GROWN rect (min 2×2,
        // DigMarquee.SnapRect), bump the per-order mark-id counter
        // (FUN_00567cb0:229-236 — one id per ORDER, so dig_next's same-vein preference groups this
        // rect), then raise the faithful evt_task_dig on the selected dwarf seeded at the marked
        // tile nearest the rect centre (tourn_2100 pattern: `set_event <z> evt_task_dig -target <z>
        // -pos1 {tx ty}`). The STANDARD order (default tool) paints MODE-1 dummy nibbles
        // (FUN_005672d0: 3) — the kind StoryMgr DigNotify's growth gate accepts; DecodeTarget
        // maps that native dummy nibble to target 11, one shared-rim transition per marked node;
        // the 1..4 depth tools paint mode-0 (cnt|8) and don't feed growth (faithful).
        // Still unmodeled vs the original painter: the tile+0x18 exposure / FUN_005968b0 rim vs
        // FUN_00597430 threshold gate (needs the exposure layer) — HasGeometry stands in.
        void CommitDigMarquee(World.DiggableTerrain terrain)
        {
            // Faithful rect (FUN_00566550:26-67 via DigMarquee.SnapRect): [min, max+2) per axis
            // over the SNAPPED anchor/corner — a zero-drag click marks the anchor's 2×2
            // crossroads block, the standard tunnel is exactly 2 across its clamped axis and
            // grows in 2-tile steps along the drag axis (the same corners fed the preview,
            // FUN_0056d0b0:95-96).
            var (x0, y0, x1, y1) = DigMarquee.SnapRect(_digAx, _digAy, _digBx, _digBy);
            bool standard = DigDepthCnt < 0;   // mode-1 dummy = the standard tunnel order
            int cnt = standard ? 0 : Mathf.Clamp(DigDepthCnt, 0, 3);
            float cx = (x0 + x1) * 0.5f, cy = (y0 + y1) * 0.5f;
            int marked = 0, seedX = -1, seedY = -1; float seedD = float.MaxValue;
            for (int ty = y0; ty <= y1; ty++)
                for (int tx = x0; tx <= x1; tx++)
                {
                    // Per-cell verdict — STANDARD tunnel: the full FUN_00566870 validity
                    // (lattice pillar + not open-marked + solid support row/flanks; see
                    // CellAllowed) exactly as the painter FUN_005672d0:56 applies it. The
                    // 1..4 depth tools paint mode-0 marks (FUN_005671a0), which the original
                    // does NOT lattice/validity-gate — HasGeometry only.
                    if (!CellAllowed(terrain, tx, ty, standard, painterGate: true)) continue;
                    terrain.SetDigMark(tx, ty, 0, cnt, dummy: standard);   // owner 0 = the human player's layer
                    marked++;
                    float d = (tx - cx) * (tx - cx) + (ty - cy) * (ty - cy);
                    if (d < seedD) { seedD = d; seedX = tx; seedY = ty; }
                }
            if (marked == 0)
            {
                Debug.Log($"[ClickToMove] dig marquee ({x0},{y0})..({x1},{y1}): nothing to dig");
                return;
            }
            terrain.IncPlayerDigMarkId();   // next order = a new vein id

            var sel = (this.ctx.SelectedHandle != 0 && this.ctx.Entities.TryGetValue(this.ctx.SelectedHandle, out var s)) ? s : null;
            Debug.Log($"[ClickToMove] dig order: rect=({x0},{y0})..({x1},{y1}) marked={marked} " +
                      $"tool={(standard ? "standard(mode-1)" : "depth cnt " + cnt)} seed=({seedX},{seedY}) dwarf#{sel?.Handle ?? 0}");
            if (sel == null || this.ctx.Interp == null) return;   // marks stay — the dispatcher can pick them up
            // Fresh per-dig trace file (diggles_work_chain.log in the project root) so it holds exactly
            // THIS dig's chain — the shared log would otherwise be filled (+ hit its 6000-line cap) by the
            // menu cutscenes before you click. Only when the chain trace is on (Verbose Chain Trace).
            if (DigglesTclEngine.VerboseScriptLog)
                Diggles.Runtime.Diagnostics.DigglesDebugLog.Begin("diggles_work_chain.log");
            var prevD = this.ctx.This;
            this.ctx.This = sel;
            try { this.ctx.Interp.Eval("set_event this evt_task_dig -target " + sel.Handle + " -pos1 {" + seedX + " " + seedY + "}"); }
            catch (System.Exception e) { Debug.LogWarning($"[ClickToMove] dig: {e.Message}"); }
            finally { this.ctx.This = prevD; }
        }

        // ---- CAVE TEMPLATE STAMP (tools 1..4 — FUN_00567cb0 mode-3, BOTTOM_MENU_RE.md §3) ----
        // Snap the anchor (SnapStencilAnchor), run the all-or-nothing FUN_00566a60 validation and,
        // if it passes, paint the template's carve cells as mode-0 (cnt|8) marks (steel-blue in the
        // persistent overlay), append a CaveRecord and bump the per-order mark id — all inside
        // CaveStamp.Commit. The stamp's mouth-row seed then rides the SAME evt_task_dig funnel the
        // marquee uses, and the (cnt|8) nibbles feed the dispatcher's dig-request scan
        // (ProdDispatcher.MaintainDigRequests reads RawDigNibble != 0) exactly like any other mark.
        void StampCave(World.DiggableTerrain terrain, Vector3 nat, int tool)
        {
            var tpl = CaveTemplateFor(tool);
            if (tpl == null)
            {
                Debug.LogWarning($"[ClickToMove] cave tool {tool}: template cave{tool}.tga not loaded (missing under {this.ctx.DataRoot}/templates?)");
                return;
            }
            var (ax, ay) = CaveStamp.SnapAnchor(nat.x, nat.y);
            int painted = CaveStamp.Commit(tpl, ax, ay, terrain, 0, out int mouthX, out int mouthY);
            if (painted == 0)
            {
                Debug.Log($"[ClickToMove] cave stamp (template {tool}) at anchor ({ax},{ay}): INVALID — nothing stamped (a carve cell failed FUN_00566a60)");
                return;
            }
            var sel = (this.ctx.SelectedHandle != 0 && this.ctx.Entities.TryGetValue(this.ctx.SelectedHandle, out var s)) ? s : null;
            Debug.Log($"[ClickToMove] cave stamp: template {tool} anchor ({ax},{ay}) painted={painted} " +
                      $"seed=({mouthX},{mouthY}) dwarf#{sel?.Handle ?? 0}");
            // Immediate order for the selected dwarf (same as the marquee); otherwise the marks lie
            // waiting for the native dig dispatcher to pick them up.
            if (sel == null || this.ctx.Interp == null) return;
            var prev = this.ctx.This;
            this.ctx.This = sel;
            try { this.ctx.Interp.Eval("set_event this evt_task_dig -target " + sel.Handle + " -pos1 {" + mouthX + " " + mouthY + "}"); }
            catch (System.Exception e) { Debug.LogWarning($"[ClickToMove] cave dig order: {e.Message}"); }
            finally { this.ctx.This = prev; }
        }

        // ---- ERASER STROKE (dig:undo — FUN_00567410 mode 2) ----
        // Per-frame while _eraseDrag: clear plain tunnel nibbles under the cursor (EraseStrokeWindow
        // skips bit-8 cave cells — those are removed whole by the mouse-DOWN cave-click), interpolate
        // between the last and current window so a fast drag leaves no gaps, and show the gray brush.
        // Reuses the marquee's release guard (a lost mouse-up edge must never own the mouse forever).
        void UpdateEraseStroke()
        {
            var release = MarqueeReleaseFor(Input.GetMouseButton(0), Input.GetMouseButtonUp(0));
            if (release == MarqueeRelease.CancelLostRelease)
            {
                _eraseDrag = false;
                World.DigMarkOverlay.ClearHover();
                Debug.LogWarning("[ClickToMove] erase stroke: mouse-up edge LOST (focus loss?) — cancelled, clicks are normal again");
                return;
            }
            var terrain = this.ctx.Terrain;
            if (terrain == null || this.ctx.SpawnParent == null)
            { _eraseDrag = false; World.DigMarkOverlay.ClearHover(); return; }

            var ray = _cam!.ScreenPointToRay(Input.mousePosition);
            if (Physics.Raycast(ray, out var hit, 10000f))
            {
                Vector3 nat = RuntimeWorldSpawner.LocalToNative(this.ctx.SpawnParent.InverseTransformPoint(hit.point));
                var (tx, ty) = terrain.WorldToTile(nat);
                EraseStrokeAt(terrain, tx, ty);
                // gray brush at the current window
                _hoverScratch.Clear();
                for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                        _hoverScratch.Add((tx + dx, ty + dy, true));
                World.DigMarkOverlay.PublishHover(_hoverScratch, outline: false, World.DigMarkOverlay.HoverStyle.Erase);
            }

            if (release == MarqueeRelease.Commit)
            {
                _eraseDrag = false;
                World.DigMarkOverlay.ClearHover();
            }
        }

        // Clear the 2×2 tunnel-mark window at (tx,ty), interpolating a straight line from the last
        // step so a fast drag erases continuously (the native's interpolated stroke, FUN_00567410).
        void EraseStrokeAt(World.DiggableTerrain terrain, int tx, int ty)
        {
            if (_eraseLastX == int.MinValue) { terrain.EraseStrokeWindow(tx, ty, 0); _eraseLastX = tx; _eraseLastY = ty; return; }
            int x0 = _eraseLastX, y0 = _eraseLastY, dx = Mathf.Abs(tx - x0), dy = Mathf.Abs(ty - y0);
            int sx = x0 < tx ? 1 : -1, sy = y0 < ty ? 1 : -1, err = dx - dy, guard = 0;
            while (guard++ < 4096)
            {
                terrain.EraseStrokeWindow(x0, y0, 0);
                if (x0 == tx && y0 == ty) break;
                int e2 = 2 * err;
                if (e2 > -dy) { err -= dy; x0 += sx; }
                if (e2 < dx) { err += dx; y0 += sy; }
            }
            _eraseLastX = tx; _eraseLastY = ty;
        }

        DigglesTclEntity? FindEntityByGameObject(GameObject go)
        {
            foreach (var e in this.ctx.Entities.Values)
                if (e.GameObject == go) return e;
            return null;
        }

        // ---- render-accurate pick refinement (the native OBB→mesh law, see the pick loop) ----
        // A box-collider hit is confirmed only if the mouse ray actually intersects the entity's
        // CURRENT display mesh (the MeshFilter on the collider's GO — DigglesAnimator swaps the
        // frame mesh there, so this IS the live silhouette the native picks against). Returns the
        // true nearest intersection distance along the ray (world units). Entities without a
        // readable mesh (fallback octahedron, bare outer) accept the box hit as-is — the box was
        // the native's whole test for a modelless record too. Shared with the hover pick
        // (SelectionAuraService), which runs per frame — the vertex/index arrays are CACHED per
        // Mesh (Mesh.vertices/.triangles allocate copies on every access).
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Mesh, (Vector3[] v, int[] t)?[]>
            s_meshGeomCache = new();
        static (Vector3[] v, int[] t) MeshGeom(Mesh mesh)
        {
            var box = s_meshGeomCache.GetValue(mesh, _ => new (Vector3[] v, int[] t)?[1]);
            box[0] ??= (mesh.vertices, mesh.triangles);
            return box[0]!.Value;
        }

        public static bool TryRefineMeshHit(Ray ray, in RaycastHit boxHit, out float dist)
        {
            dist = boxHit.distance;
            var mf = boxHit.collider.GetComponent<MeshFilter>();
            var mesh = mf != null ? mf.sharedMesh : null;
            if (mesh == null || !mesh.isReadable || mesh.vertexCount == 0) return true;   // box fallback
            if (!MeshRayT(ray, boxHit.collider.transform, mesh, out float bestT))
                return false;                                       // box grazed, mesh missed
            // t is in "direction lengths" of the UNnormalized local dir = the same parameter in
            // world space (linear transforms preserve the ray parameter) → world distance = t·|dir|.
            dist = bestT * ray.direction.magnitude;
            return true;
        }

        // NEAR-MISS TOLERANCE: the exact triangle law made thin
        // silhouettes (a gnome's limbs, small pickups) fiddly to click — the old box collider
        // forgave a few pixels, the exact law forgives none. When the true cursor ray misses the
        // drawn mesh, characters and small pickups get a second chance from a ring of rays offset
        // by a few SCREEN pixels. Buildings/deco stay exact-only — the point of the render-
        // accurate law is that a building's box must not swallow a crate. This is a deliberate,
        // small deviation from the strict native pixel law, per the player's explicit request
        // (natively the same forgiveness comes from models simply covering enough pixels).
        public const float PickTolerancePx = 3.5f;
        public static bool TryRefineMeshHitTolerant(Camera cam, Vector3 mouseScreen, in RaycastHit boxHit, out float dist)
        {
            dist = boxHit.distance;
            var mf = boxHit.collider.GetComponent<MeshFilter>();
            var mesh = mf != null ? mf.sharedMesh : null;
            if (mesh == null || !mesh.isReadable || mesh.vertexCount == 0) return false; // box fallback took the exact pass
            var tr = boxHit.collider.transform;
            for (int i = 0; i < 8; i++)
            {
                float a = i * Mathf.PI / 4f;
                var off = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f) * PickTolerancePx;
                var jray = cam.ScreenPointToRay(mouseScreen + off);
                if (MeshRayT(jray, tr, mesh, out float t))
                {
                    dist = t * jray.direction.magnitude;
                    return true;
                }
            }
            return false;
        }

        // Mesh-space nearest-triangle scan shared by the exact and tolerant passes.
        static bool MeshRayT(Ray ray, Transform tr, Mesh mesh, out float bestT)
        {
            // ray → model space (handles the inner Q_x(-90) + ModelToWorldScale chain)
            Vector3 o = tr.InverseTransformPoint(ray.origin);
            Vector3 d = tr.InverseTransformVector(ray.direction);   // NOT normalized — t maps back in callers
            var (verts, tris) = MeshGeom(mesh);
            bestT = float.MaxValue;
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                if (RayTriangle(o, d, verts[tris[i]], verts[tris[i + 1]], verts[tris[i + 2]], out float t)
                    && t < bestT) bestT = t;
            }
            return bestT != float.MaxValue;
        }

        // Möller–Trumbore, double-sided (the native OBB test has no facing either; picking must
        // not care about winding). Epsilon scaled for raw model units (~200 u meshes).
        static bool RayTriangle(Vector3 o, Vector3 d, Vector3 v0, Vector3 v1, Vector3 v2, out float t)
        {
            t = 0f;
            Vector3 e1 = v1 - v0, e2 = v2 - v0;
            Vector3 p = Vector3.Cross(d, e2);
            float det = Vector3.Dot(e1, p);
            if (det > -1e-8f && det < 1e-8f) return false;
            float inv = 1f / det;
            Vector3 s = o - v0;
            float u = Vector3.Dot(s, p) * inv;
            if (u < 0f || u > 1f) return false;
            Vector3 q = Vector3.Cross(s, e1);
            float v = Vector3.Dot(d, q) * inv;
            if (v < 0f || u + v > 1f) return false;
            t = Vector3.Dot(e2, q) * inv;
            return t > 0f;
        }
    }

    // Runtime-visible line pool for the dig marquee (regression E,): Debug.DrawLine
    // renders ONLY in the Scene view / a gizmo-enabled Game view, so the player saw no dig
    // preview at all in Play. This draws the same segments as real GL lines after each camera
    // renders — OnRenderObject is invoked by URP/SRP too. ZTest Always keeps the order overlay
    // readable over cliffs (it is a UI-grade cue, not world geometry).
    public sealed class DigMarqueePreviewLines : MonoBehaviour
    {
        public readonly System.Collections.Generic.List<(Vector3 a, Vector3 b, Color c)> Segments = new();

        static Material? s_mat;
        static Material Mat()
        {
            if (s_mat == null)
            {
                // The classic GL vertex-colour shader; ships in builds (used by Unity's own handles).
                var sh = Shader.Find("Hidden/Internal-Colored");
                if (sh == null) sh = Shader.Find("Sprites/Default");   // fallback, still vertex-coloured
                s_mat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
                s_mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                s_mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                s_mat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                s_mat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                s_mat.SetInt("_ZWrite", 0);
            }
            return s_mat;
        }

        void OnRenderObject()
        {
            if (Segments.Count == 0) return;
            Mat().SetPass(0);
            GL.PushMatrix();
            GL.Begin(GL.LINES);
            foreach (var (a, b, c) in Segments)
            {
                GL.Color(c);
                GL.Vertex(a);
                GL.Vertex(b);
            }
            GL.End();
            GL.PopMatrix();
        }
    }
}
