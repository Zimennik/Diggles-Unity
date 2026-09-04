#nullable enable

namespace Diggles.Runtime.UI
{
    using Diggles.Runtime.Tcl.Engine;
    using UnityEngine;
    using VContainer;
    using VContainer.Unity;

    // The gnome inventory panel's DI controller (ViWinInventory). Rebuilds the model when a
    // player gnome/baby is selected; a slot click routes through the unchanged event_generator
    // (PlacementController.ClickInventoryCrate) so a crate opens placement, a plain item opens
    // the putdown-place mode (the native drag&drop's click-driven equivalent) and anything else
    // fires its native verb. Click gestures (mirroring the native law):
    // • click → putdown-place mode: pick a world spot, the gnome walks there and puts
    // the item down (deferred by the double-click window — see ClickSlot);
    // • Alt+click → use (evt_task_useitem for any class with a `use` method,
    // eventgen.tcl:13; books → evt_task_open_box:253-258);
    // • Double-click → put the item down where the gnome stands — the native -invclick
    // confirm (FUN_00531310:68-71/:240-242 appends " -invclick" when the
    // placement-mode click lands inside the inventory window; eventgen zeroes
    // pospar → evt_task_putdown -pos1 {0 0 0}, eventgen.tcl:99-103/:262-263).
    //
    // Slot capacity: the base grid + the worn basket upgrades (Holzkiepe / Grosse_Holzkiepe /
    // Schubkarren / Hamsterkarren) — the exact native law is being RE'd; until it lands the
    // capacity shows max(8, items) so nothing is ever hidden.
    public sealed class InventoryPanelController : ITickable
    {
        readonly System.Func<DigglesTclContext> contextAccessor;
        DigglesTclContext? _observedContext;
        DigglesTclContext ctx => contextAccessor();

        [Preserve]
        [Inject]
        public InventoryPanelController(IActiveGameSession sessions)
        {
            contextAccessor = () => sessions.Current.Engine.Ctx;
        }

        // Headless inventory gesture tests provide a fixed context directly.
        public InventoryPanelController(DigglesTclContext ctx)
        {
            if (ctx == null) throw new System.ArgumentNullException(nameof(ctx));
            contextAccessor = () => ctx;
        }

        public readonly InventoryPanelModel Model = new();

        int _lastSelected = int.MinValue;
        float _nextRefresh;

        void ITickable.Tick()
        {
            EnsureCurrentContext();
            if (ctx.Interp == null) return;
            // A plain click waits out the double-click window before arming the putdown-place
            // mode (a second click within the window means "drop where he stands" instead).
            FlushPendingClick(Time.unscaledTimeAsDouble);
            DigglesTclEntity? sel = null;
            if (ctx.SelectedHandle != 0) ctx.Entities.TryGetValue(ctx.SelectedHandle, out sel);
            string kind = sel?.Class?.Kind ?? "";
            // Adult gnome only — a selected baby shows the dig buttons in the native window law
            // (FUN_00529360, Docs/BOTTOM_MENU_RE.md §1).
            bool show = sel != null && kind == "gnome"
                        && !ctx.SequenceActive
                        && (ctx.Placement == null || !ctx.Placement.Active);

            if (!show)
            {
                if (Model.Visible) { Model.Visible = false; Model.Items.Clear(); Model.Version++; }
                _lastSelected = int.MinValue;
                return;
            }
            if (sel!.Handle == _lastSelected && Time.unscaledTime < _nextRefresh) return;
            _lastSelected = sel.Handle;
            _nextRefresh = Time.unscaledTime + 0.25f;

            Model.Visible = true;
            Model.Title = SafeEval($"get_objname {sel.Handle}");
            Model.Items.Clear();
            foreach (var h in sel.Inventory)
            {
                if (!ctx.Entities.TryGetValue(h, out var item) || item.Class == null) continue;
                Model.Items.Add(new InventorySlot
                {
                    ItemHandle = h,
                    Icon = item.Class.Name,
                    Tooltip = item.Class.Name,
                });
            }
            // The native slot law (InvGetSize FUN_005cde10, Docs/BOTTOM_MENU_RE.md §4):
            // Zwerg base 6, plus each carried container's bonus — the shared engine table
            // (DigglesTclEngine.TransportSlotBonus), the same one the capacity gate uses.
            int bonus = 0;
            foreach (var it in Model.Items)
                bonus += DigglesTclEngine.TransportSlotBonus(it.Icon);
            (Model.MainSlotCount, Model.ExtraSlotCount) = SplitSlots(BaseSlots, bonus, Model.Items.Count);
            Model.SlotCount = Model.MainSlotCount + Model.ExtraSlotCount;
            Model.Version++;
        }

        void EnsureCurrentContext()
        {
            DigglesTclContext current = ctx;
            if (ReferenceEquals(_observedContext, current)) return;
            _observedContext = current;
            _lastSelected = int.MinValue;
            _nextRefresh = 0f;
            _pendingClickItem = 0;
            _pendingClickGnome = 0;
            _pendingClickDeadline = 0.0;
            Model.Visible = false;
            Model.Title = "";
            Model.Items.Clear();
            Model.Version++;
        }

        // The gnome's own grid capacity (InvGetSize FUN_005cde10: Zwerg base 6) — the same
        // anchor the engine's is_wearing_pannier flat-model threshold uses, so the walk law
        // and the basket grid always agree on which items are "in the basket".
        public const int BaseSlots = DigglesTclEngine.ZwergBaseInvSlots;

        // The two-grid display split (pure — pinned in tests): main is always the base grid;
        // the basket bonus renders in the extra grid, which also absorbs any overflow beyond
        // main+bonus (engine-side inventories can't normally exceed capacity, but the panel
        // must never hide a carried item).
        public static (int main, int extra) SplitSlots(int baseSlots, int basketBonus, int itemCount)
            => (baseSlots, Mathf.Max(basketBonus, itemCount - baseSlots));

        // The eventgen txtpar for an ALT inventory click (pure — pins the RE'd token law in
        // tests): the native window click builder appends " -alternate" for VK_MENU/ALT
        // (FUN_00530b90:56-58) and the inventory window adds "-invclick" (eventgen.tcl:100-103
        // recognises it and zeroes pospar — inventory items have no world position).
        public static string AltClickTxtPar() => "-command -alternate -invclick";

        // The double-click window (unscaled seconds): a plain click is deferred this long —
        // if a second click on the same slot lands inside it, the pair is the "drop where he
        // stands" gesture; otherwise the single click arms the putdown-place / crate placement
        // mode on flush. (Deferral is required: arming placement on the first click would hide
        // the panel and the double's second click would fall into the world as a confirm.)
        public const float DoubleClickWindow = 0.3f;

        int _pendingClickItem;      // a plain click waiting out the double-click window (0 = none)
        int _pendingClickGnome;     // the selection the pending click was made under
        double _pendingClickDeadline;

        // The eventgen txtpar of the immediate-putdown gesture — verbatim the native confirm
        // txtpar head for a click landing inside the inventory window (FUN_00531310:236-242:
        // "-command -placement -asprod …" + " -invclick"; the -pos/-rot/-snapobj extras are
        // omitted like the crate confirm — they only feed the snapped-special-item branch).
        // eventgen recognises -invclick and zeroes pospar (eventgen.tcl:99-103) → the putdown
        // spec carries -pos1 {0 0 0} → evt_task_putdown_proc's invalid-pos branch drops the
        // item right where the gnome stands (z_events.tcl:750-754/:763-771).
        public static string InvClickPutdownTxtPar() => "-command -placement -asprod -invclick";

        // The UI binding entry (InventoryPanelView passes this as the slot delegate): reads
        // the Alt modifier at click time, like the world click path.
        public void ClickSlot(int itemHandle)
            => ClickSlot(itemHandle, ClickToMoveService.AlternateHeld(), Time.unscaledTimeAsDouble);

        public void ClickSlot(int itemHandle, bool alt)
            => ClickSlot(itemHandle, alt, Time.unscaledTimeAsDouble);

        public void ClickSlot(int itemHandle, bool alt, double now)
        {
            EnsureCurrentContext();
            if (ctx.SelectedHandle == 0 || itemHandle == 0) return;
            if (!alt)
            {
                if (_pendingClickItem == itemHandle && _pendingClickGnome == ctx.SelectedHandle
                    && now <= _pendingClickDeadline)
                {
                    // Double-click → the native -invclick putdown: drop at the gnome's feet.
                    _pendingClickItem = 0;
                    FireInvClickPutdown(itemHandle);
                }
                else
                {
                    // Plain click — defer the default eventgen route (crate → building placement,
                    // plain item → putdown-place mode) until the double-click window passes.
                    _pendingClickItem = itemHandle;
                    _pendingClickGnome = ctx.SelectedHandle;
                    _pendingClickDeadline = now + DoubleClickWindow;
                }
            }
            else if (ctx.Entities.TryGetValue(ctx.SelectedHandle, out var g))
            {
                // Alt+click = use the item (eventgen.tcl:13 → evt_task_useitem for any
                // class with a `use` method; a use-less in-inventory material falls to the
                // open_box read branch:253-258 — books). An Alt click never pends.
                _pendingClickItem = 0;
                try
                {
                    string verb = DigglesTclEngine.EvtGenRun(
                        ctx, ctx.SelectedHandle, itemHandle, g.Position, AltClickTxtPar()).Trim();
                    bool fired = DigglesTclEngine.EvtGenFire(ctx, ctx.SelectedHandle);
                    Debug.Log($"[InventoryPanel] alt-click item#{itemHandle}: verb='{verb}' fired={fired}");
                }
                catch (System.Exception e) { Debug.LogWarning($"[InventoryPanel] alt-click eventgen: {e.Message}"); }
            }
            _nextRefresh = 0f;
        }

        // The deferred single-click action (Tick drives it; public + time-parameterised so
        // headless tests can run the gesture law without Unity time): once the window passes
        // with no second click, route the click through the unchanged eventgen
        // (PlacementController.ClickInventoryCrate — boxed crate → building placement, plain
        // item → putdown-place). Dropped when the selection changed while pending.
        public void FlushPendingClick(double now)
        {
            EnsureCurrentContext();
            if (_pendingClickItem == 0 || now <= _pendingClickDeadline) return;
            int item = _pendingClickItem, gnome = _pendingClickGnome;
            _pendingClickItem = 0;
            if (ctx.SelectedHandle != gnome) return;
            bool entered = ctx.Placement?.ClickInventoryCrate(gnome, item) ?? false;
            Debug.Log($"[InventoryPanel] click item#{item}: placement/putdown-place entered={entered}");
        }

        // The immediate putdown (double-click): the native -invclick confirm round through the
        // unchanged generator, fired on the gnome.
        void FireInvClickPutdown(int itemHandle)
        {
            if (!ctx.Entities.TryGetValue(ctx.SelectedHandle, out var g)) return;
            try
            {
                string verb = DigglesTclEngine.EvtGenRun(
                    ctx, ctx.SelectedHandle, itemHandle, g.Position, InvClickPutdownTxtPar()).Trim();
                bool fired = DigglesTclEngine.EvtGenFire(ctx, ctx.SelectedHandle);
                Debug.Log($"[InventoryPanel] double-click item#{itemHandle}: verb='{verb}' fired={fired} (putdown in place)");
            }
            catch (System.Exception e) { Debug.LogWarning($"[InventoryPanel] double-click eventgen: {e.Message}"); }
        }

        string SafeEval(string cmd)
        {
            try { return ctx.Interp.Eval(cmd).Trim(); }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[InventoryPanel] {cmd}: {e.Message}");
                return "";
            }
        }
    }
}
