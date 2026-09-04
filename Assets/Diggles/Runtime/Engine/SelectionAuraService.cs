#nullable enable

namespace Diggles.Runtime
{
    using Diggles.Runtime.Tcl.Engine;
    using UnityEngine;
    using VContainer;
    using VContainer.Unity;

    // The selection / hover aura (the original UI's highlight):
    // * every SELECTED entity carries a persistent aura;
    // * the entity under the CURSOR gets a hover aura — gnomes, buildings and interactive
    // objects (barrel, enemy, book, mushroom, …), colour keyed by what it is;
    // * both render over everything (Diggles/Aura shell: ZTest Always, additive), under the
    // screen-space UI by construction.
    // Hover honours the same pick rules as clicking: `set_hoverable 0` and cloaked entities are
    // transparent to the cursor; cutscenes and placement mode suppress the aura entirely.
    //
    // Hover colours use the original cursor-id palette resolved from the event-generator verb.
    public sealed class SelectionAuraService : ITickable
    {
        readonly System.Func<DigglesTclContext> contextAccessor;
        DigglesTclContext? _observedContext;
        DigglesTclContext ctx => contextAccessor();

        [Preserve]
        [Inject]
        public SelectionAuraService(IActiveGameSession sessions)
        {
            contextAccessor = () => sessions.Current.Engine.Ctx;
        }

        // Headless tests and frame benchmarks can provide a fixed context directly.
        public SelectionAuraService(DigglesTclContext ctx)
        {
            if (ctx == null) throw new System.ArgumentNullException(nameof(ctx));
            contextAccessor = () => ctx;
        }

        Camera? _cam;
        int _hoverHandle;       // AuraBandRenderer slot 1 follows this
        bool _hoverAlt;         // the ALT modifier the current hover verb was computed with
        bool _hoverMulti;       // the SHIFT modifier the current hover verb was computed with
        int _hoverSelectionVersion = -1;
        // Multi-select: slots 2.. carry the rest of the selection (the native SelHoverGlow
        // walks the whole selection array — every selected unit glows). Version-gated rebuild;
        // _extraCount remembers how many extra slots are live so stale ones get cleared.
        int _selVersion = -1;
        int _extraCount;
        // Pending resync: a live selected entity had no GameObject at sync time (presentation
        // spawns/restores a frame later than the sim — e.g. selecting right after an F5 load).
        // The version gate alone would then drop the aura until the next selection mutation;
        // this flag keeps re-running the sync each tick (allocation-free — one bool) until every
        // live selected entity has its GameObject. Dead handles do not arm it (they never get one).
        bool _selResyncPending;

        // Read-only seam for the headless tests (the render slots are inert in EditMode).
        public bool SelectionResyncPending => _selResyncPending;

        // Opt-in trace of the hover cursor-verb resolution (the hardware-cursor stand-in log).
        // Default off: it logged on every hover change — a steady editor-console cost in Play.
        public static bool TraceHoverVerb;

        // Reused hover-pick raycast buffer + a stable distance comparer — Physics.RaycastAll allocated a
        // fresh RaycastHit[] every frame (per-frame GC on the hover tick). RaycastNonAlloc fills this
        // instead; 64 is far above the collider count on any single pick ray.
        readonly RaycastHit[] _hitBuf = new RaycastHit[64];
        sealed class HitDistanceComparer : System.Collections.Generic.IComparer<RaycastHit>
        {
            public static readonly HitDistanceComparer Instance = new();
            public int Compare(RaycastHit a, RaycastHit b) => a.distance.CompareTo(b.distance);
        }

        // Compatibility overload kept for existing tests.
        public static bool HoverNeedsRefresh(int prevHandle, bool prevAlt, int handle, bool alt)
            => handle != prevHandle || alt != prevAlt;

        // The hover change key includes both modifiers and the selection version. Native
        // FUN_00530b90:51-60 appends Shift then Alt before its hover/command branch, and the
        // selected object is eventgen's objpar, so any of these changes can change the verb.
        public static bool HoverNeedsRefresh(int prevHandle, bool prevAlt, bool prevMulti,
            int prevSelectionVersion, int handle, bool alt, bool multi, int selectionVersion)
            => handle != prevHandle || alt != prevAlt || multi != prevMulti
                || selectionVersion != prevSelectionVersion;

        public static bool SelectionNeedsRefresh(int observedVersion, int currentVersion)
            => observedVersion != currentVersion;

        // Compatibility overload kept for existing tests.
        public static string HoverTxtPar(bool alternate) => HoverTxtPar(alternate, false);

        // The eventgen txtpar for the hover cursor query (pure — pins FUN_00530b90:51-60).
        public static string HoverTxtPar(bool alternate, bool multiselect)
        {
            string text = "";
            if (multiselect) text = "-multiselect";
            if (alternate) text += text.Length == 0 ? "-alternate" : " -alternate";
            return text;
        }

        // Hover-aura eligibility by selection state: nothing selected → the hover highlight fires
        // only for the local player's own gnomes/babies and own buildings; items, materials, deco
        // and enemy objects get no hover aura. Something selected → any pickable entity is a
        // candidate again (verbs are live: a mushroom shows harvest, a crate pickup …) and
        // HoverColorFor gates the colour/none. Pure over the entity so the candidate-filtering
        // table is headless-testable. (Deco is already excluded one rung earlier by
        // IsCursorTransparent = set_hoverable 0.)
        public static bool HoverEligible(Tcl.Engine.DigglesTclEntity e, int localPlayer, bool hasSelection)
        {
            if (hasSelection) return true;
            if (e.Owner != localPlayer) return false;
            string k = e.Class?.Kind ?? "";
            return k == "gnome" || k == "baby"
                || k == "production" || k == "store" || k == "energy"
                || k == "protection" || k == "elevator";
        }

        // The uGUI over-panel gate (mirrors ClickToMoveService): no hover aura while the pointer
        // is over a UI element. Null-guarded for the EditMode host / EventSystem-less scenes.
        static bool PointerOverUI()
            => UnityEngine.EventSystems.EventSystem.current != null
            && UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject();

        // FUN_00464320:52-184 resolves the eventgen verb to this cursor id. FUN_00464180:21-85
        // supplies its packed colour; CDisplay_DrawObjects FUN_00473cc0:687-708 forces alpha FF.
        public enum HoverCursorId
        {
            None = 0,
            Select = 1,
            Click = 2,
            Use = 3,
            Walk = 4,
            NoWalk = 5,
            Dig = 6,
            Pickup = 7,
            Putdown = 8,
            Attack = 9,
            Pack = 10,
            Unpack = 11,
            Assign = 12,
            Wait = 13,
            Zoom = 14,
            Pan = 15,
            Reserved16 = 16,
        }

        static readonly Color32 SelectionColor = new(255, 255, 255, 255);
        static readonly Color32 HoverWhite = new(255, 255, 255, 255);
        static readonly Color32 HoverGreen = new(0, 255, 0, 255);
        static readonly Color32 HoverRed = new(255, 0, 0, 255);
        static readonly Color32 HoverBlue = new(0, 0, 255, 255);

        public static HoverCursorId CursorIdForVerb(string? verb)
        {
            switch (verb)
            {
                case "none": return HoverCursorId.None;
                case "select": case "standard": return HoverCursorId.Select;
                case "click": return HoverCursorId.Click;
                case "use": return HoverCursorId.Use;
                case "walk": return HoverCursorId.Walk;
                case "nowalk": return HoverCursorId.NoWalk;
                case "dig": return HoverCursorId.Dig;
                case "pickup": return HoverCursorId.Pickup;
                case "putdown": return HoverCursorId.Putdown;
                case "attack": return HoverCursorId.Attack;
                case "pack": return HoverCursorId.Pack;
                case "unpack": return HoverCursorId.Unpack;
                case "assign": return HoverCursorId.Assign;
                case "wait": return HoverCursorId.Wait;
                case "zoom": return HoverCursorId.Zoom;
                case "pan": return HoverCursorId.Pan;
                default: return HoverCursorId.Select;
            }
        }

        public static Color32? HoverColorForCursorId(HoverCursorId cursorId)
        {
            switch (cursorId)
            {
                case HoverCursorId.Select: case HoverCursorId.Click: return HoverWhite;
                case HoverCursorId.Use: case HoverCursorId.Dig: case HoverCursorId.Assign: return HoverGreen;
                case HoverCursorId.Attack: return HoverRed;
                case HoverCursorId.Pickup: case HoverCursorId.Putdown:
                case HoverCursorId.Pack: case HoverCursorId.Unpack: return HoverBlue;
                default: return null;
            }
        }

        // Pure modifier/action seam. Shift only changes the native verb to "select"; the one
        // product exception is the existing own boxed-crate pickup override, whose preview must
        // use the same blue pickup cursor as the click it will issue.
        public static Color32? HoverColorForDecision(string? eventVerb, bool ownBoxedCrate,
            bool gnomeSelected, bool multiselect, bool alternate)
        {
            bool pickupOverride = ClickToMoveService.ResolveCrateClick(
                ownBoxedCrate, gnomeSelected, multiselect, alternate)
                == ClickToMoveService.CrateClickVerb.PickupOrder;
            HoverCursorId cursorId = pickupOverride ? HoverCursorId.Pickup : CursorIdForVerb(eventVerb);
            return HoverColorForCursorId(cursorId);
        }

        void ITickable.Tick()
        {
            EnsureCurrentContext();
            if (ctx.Interp == null) return;
            bool suppressed = ctx.SequenceActive || (ctx.Placement != null && ctx.Placement.Active);
            if (suppressed)
            {
                ClearSelectionAuras();
                ClearHoverAura();
                return;
            }

            // --- selection auras (the whole live set — every selected entity glows white) ---
            SyncSelectionAuras();

            if (_cam == null) _cam = Camera.main;
            if (_cam == null) return;

            Tcl.Engine.DigglesTclEntity? sel = null;
            if (ctx.SelectedHandle != 0) ctx.Entities.TryGetValue(ctx.SelectedHandle, out sel);

            // --- hover aura ---
            // The over-uGUI gate mirrors the click ladder: a hover aura must never light
            // up for a world object the cursor is only visually over through a UI panel.
            Tcl.Engine.DigglesTclEntity? hover = null;
            Vector3 hitPoint = default;
            bool hasSelection = ctx.Selection.Count > 0;
            if (!WorkClockWidget.PointerCaptured && !PointerOverUI())
                (hover, hitPoint) = PickHoverTarget(hasSelection);
            if (hover != null && ctx.Selection.Contains(hover.Handle)) hover = null;   // selection already glows
            bool alternate = ClickToMoveService.AlternateHeld();
            bool multiselect = ClickToMoveService.MultiselectHeld();
            SetHover(hover, sel, hitPoint, alternate, multiselect, ctx.Selection.Version);
        }

        void EnsureCurrentContext()
        {
            DigglesTclContext current = ctx;
            if (ReferenceEquals(_observedContext, current)) return;
            _observedContext = current;
            _cam = null;
            ClearSelectionAuras();
            ClearHoverAura();
            _goMapVersion = -1;
            _goToEntity.Clear();
        }

        // The same render-accurate pick rule as ClickToMoveService (the native hover object
        // +0xa0cc is what the click consumes — one law for both): skip set_hoverable 0 /
        // cloaked; each entity box hit is refined against its live display mesh triangles
        // (ClickToMoveService.TryRefineMeshHit — a box-only graze is no candidate, except the
        // near-miss tolerance ring for characters/small pickups — hover must mirror the click
        // law or the aura preview lies); characters win overlaps; exact beats tolerant;
        // terrain (no entity) closes the window.
        (Tcl.Engine.DigglesTclEntity?, Vector3) PickHoverTarget(bool hasSelection)
        {
            var ray = _cam!.ScreenPointToRay(Input.mousePosition);
            int hitCount = Physics.RaycastNonAlloc(ray, _hitBuf, 10000f);   // reused buffer — no per-frame alloc
            System.Array.Sort(_hitBuf, 0, hitCount, HitDistanceComparer.Instance);
            Tcl.Engine.DigglesTclEntity? best = null;
            Vector3 bestPoint = default;
            bool bestIsChar = false, bestExact = false;
            float bestDist = float.MaxValue;
            for (int hi = 0; hi < hitCount; hi++)
            {
                var h = _hitBuf[hi];
                var ho = h.collider.GetComponentInParent<DigglesObject>();
                if (ho == null) break;                    // terrain closes the pick window
                var e = FindEntityByGameObject(ho.gameObject);
                if (e == null) break;
                if (ClickToMoveService.IsCursorTransparent(e))
                    continue;                             // cursor-transparent (deco/cloaked) — try what's behind
                if (ClickToMoveService.RenderVisibilityBlocksCursor(e))
                    continue;
                // Hover eligibility by selection state: with nothing selected the aura
                // fires only for own gnomes/babies + own buildings — an item/material/enemy is
                // skipped and the cursor falls through to what's behind. With a unit selected the
                // verb path is live, so any pickable entity is a candidate and the colour law
                // (HoverColorFor) decides / suppresses the aura.
                if (!HoverEligible(e, ctx.LocalPlayer, hasSelection))
                    continue;
                string k = e.Class?.Kind ?? "";
                bool isChar = k == "gnome" || k == "baby";
                bool exact = ClickToMoveService.TryRefineMeshHit(ray, h, out float trueDist);
                if (!exact)
                {
                    bool tolerantEligible = isChar || k == "material" || k == "item";
                    if (!tolerantEligible || !ClickToMoveService.TryRefineMeshHitTolerant(
                            _cam!, Input.mousePosition, h, out trueDist))
                        continue;                         // box grazed, drawn mesh missed
                }
                bool better = best == null
                    || (isChar != bestIsChar ? isChar
                        : exact != bestExact ? exact
                        : trueDist < bestDist);
                if (better) { best = e; bestPoint = h.point; bestIsChar = isChar; bestExact = exact; bestDist = trueDist; }
            }
            return (best, bestPoint);
        }

        // Hover colour by the eventgen verb (see the colour-law note above). Runs the real
        // event_generator (the native hover runs the same spec each tick; the port runs it only
        // when its target/modifiers/selection version change) with the selected unit as objpar —
        // no selection ⇒ the native "select" cursor ⇒ white. A null return = no aura (the
        // walk/wait cursor family). The verb is logged as the hardware-cursor stand-in until
        // the cursor system is built.
        Color? HoverColorFor(Tcl.Engine.DigglesTclEntity target, Tcl.Engine.DigglesTclEntity? sel,
            Vector3 worldHit, bool alt, bool multi)
        {
            if (sel == null || ctx.SpawnParent == null) return (Color)HoverWhite;
            string verb;
            try
            {
                Vector3 natHit = RuntimeWorldSpawner.LocalToNative(ctx.SpawnParent.InverseTransformPoint(worldHit));
                verb = DigglesTclEngine.EvtGenRun(
                    ctx, sel.Handle, target.Handle, natHit, HoverTxtPar(alt, multi)).Trim();
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[aura] eventgen hover: {e.Message}");
                return (Color)HoverWhite;
            }
            // Editor console logging is expensive (string alloc + console append per hover
            // change — sweeping the mouse over a drop pile fired it many times a second).
            // Opt-in diagnostic only; flip in a debugger / via reflection when cursor-verb
            // debugging is needed.
            if (TraceHoverVerb)
                Debug.Log($"[aura] hover {target.Class?.Name}#{target.Handle}: verb='{verb}' " +
                          $"alt={alt} multi={multi} (cursor stand-in)");

            string kind = target.Class?.Kind ?? "";
            bool building = kind == "production" || kind == "store" || kind == "energy"
                || kind == "protection" || kind == "elevator";
            bool boxed = target.VarsScope.Vars.TryGetValue("_boxed", out var boxedValue)
                && (boxedValue == "1" || boxedValue == "true");
            Color32? color = HoverColorForDecision(
                verb, target.Owner == ctx.LocalPlayer && building && boxed,
                sel.Class?.Kind == "gnome", multi, alt);
            if (!color.HasValue) return null;
            return (Color)color.Value;
        }

        // Slots 0,2.. mirror selection[0],selection[1..]. A version change rebuilds the whole
        // target set, including the primary slot, through one authoritative mutation signal —
        // plus a pending-resync retry while a live selected entity still lacks its GameObject.
        void SyncSelectionAuras()
        {
            int version = ctx.Selection.Version;
            if (!SelectionNeedsRefresh(_selVersion, version) && !_selResyncPending) return;
            _selVersion = version;
            _selResyncPending = false;
            var handles = ctx.Selection.Handles;
            if (handles.Count > 0 && ctx.Entities.TryGetValue(handles[0], out var primary))
            {
                if (primary.GameObject != null)
                    AuraBandRenderer.Set(0, primary.GameObject, SelectionColor);
                else
                {
                    AuraBandRenderer.Clear(0);
                    _selResyncPending = true;   // live entity, presentation not spawned yet — retry
                }
            }
            else
                AuraBandRenderer.Clear(0);

            int slot = 2;
            for (int i = 1; i < handles.Count && slot < AuraBandRenderer.MaxSlots; i++)
            {
                if (!ctx.Entities.TryGetValue(handles[i], out var e)) continue;
                if (e.GameObject == null) { _selResyncPending = true; continue; }
                AuraBandRenderer.Set(slot++, e.GameObject, SelectionColor);
            }
            int live = slot - 2;
            for (int s = live; s < _extraCount; s++) AuraBandRenderer.Clear(2 + s);
            _extraCount = live;
        }

        void ClearSelectionAuras()
        {
            AuraBandRenderer.Clear(0);
            for (int s = 0; s < _extraCount; s++) AuraBandRenderer.Clear(2 + s);
            _extraCount = 0;
            _selVersion = -1;
            _selResyncPending = false;
        }

        void ClearHoverAura()
        {
            AuraBandRenderer.Clear(1);
            _hoverHandle = 0;
            _hoverAlt = false;
            _hoverMulti = false;
            _hoverSelectionVersion = -1;
        }

        void SetHover(Tcl.Engine.DigglesTclEntity? e, Tcl.Engine.DigglesTclEntity? sel,
            Vector3 worldHit, bool alt, bool multi, int selectionVersion)
        {
            int h = e?.Handle ?? 0;
            if (!HoverNeedsRefresh(_hoverHandle, _hoverAlt, _hoverMulti, _hoverSelectionVersion,
                    h, alt, multi, selectionVersion)) return;
            _hoverHandle = h;
            _hoverAlt = alt;
            _hoverMulti = multi;
            _hoverSelectionVersion = selectionVersion;
            if (e?.GameObject == null) { AuraBandRenderer.Clear(1); return; }
            var color = HoverColorFor(e, sel, worldHit, alt, multi);
            if (color == null) { AuraBandRenderer.Clear(1); return; }   // walk/wait family — no aura
            AuraBandRenderer.Set(1, e.GameObject, color.Value);
        }

        // GameObject → entity reverse map, rebuilt only when the entity registry mutates
        // (ctx.EntitiesVersion bumps at the only mutation sites: RegisterEntity / `del` /
        // world teardown; a GameObject is always assigned within the same synchronous `new`
        // that bumped the version). The previous linear ctx.Entities scan was O(N) per
        // raycast hit per frame — ~700 entities × hover = a steady per-frame cost.
        readonly System.Collections.Generic.Dictionary<GameObject, Tcl.Engine.DigglesTclEntity> _goToEntity = new();
        int _goMapVersion = -1;

        Tcl.Engine.DigglesTclEntity? FindEntityByGameObject(GameObject go)
        {
            if (_goMapVersion != ctx.EntitiesVersion)
            {
                _goMapVersion = ctx.EntitiesVersion;
                _goToEntity.Clear();
                foreach (var e in ctx.Entities.Values)
                    if (e.GameObject != null) _goToEntity[e.GameObject] = e;
            }
            return _goToEntity.TryGetValue(go, out var found) ? found : null;
        }
    }
}
