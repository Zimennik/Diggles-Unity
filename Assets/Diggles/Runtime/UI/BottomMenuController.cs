#nullable enable

namespace Diggles.Runtime.UI
{
    using System.Collections.Generic;
    using System.Globalization;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using UnityEngine;
    using VContainer;
    using VContainer.Unity;

    // The bottom context menu's DI controller (POCO, ITickable): watches the selection and
    // rebuilds BottomMenuModel; dispatches entry clicks back into the engine seams.
    //
    // Ground truth: Docs/PRODUCTION_PANEL_RE.md (ViWinProduction — one icon row mixing product
    // and action cells; boxed → only unpack; energy → only the on/off toggle; repair appended
    // at HP<1; count badge 0..10 with 10 = "oo"; icons = data/gui/icons/<Class>.tga with
    // Red<Class> when not buildable). The dig-tools window, unlock law, and undo semantics are
    // still being reverse-engineered — the Dig mode here is the interim surface (standard
    // tunnel + the 4 depth tools the port already drives via ClickToMoveService.DigDepthCnt).
    public sealed class BottomMenuController : ITickable
    {
        readonly System.Func<DigglesTclContext> contextAccessor;
        DigglesTclContext? _observedContext;
        DigglesTclContext ctx => contextAccessor();

        [Preserve]
        [Inject]
        public BottomMenuController(IActiveGameSession sessions)
        {
            contextAccessor = () => sessions.Current.Engine.Ctx;
        }

        // Headless production/UI law tests provide a fixed context directly.
        public BottomMenuController(DigglesTclContext ctx)
        {
            if (ctx == null) throw new System.ArgumentNullException(nameof(ctx));
            contextAccessor = () => ctx;
        }

        public readonly BottomMenuModel Model = new();

        int _lastSelected = int.MinValue;
        private int _lastSelVersion = int.MinValue;
        float _nextRefresh;
        private int? _lastProductionMutationHash;

        void ITickable.Tick()
        {
            EnsureCurrentContext();
            if (ctx.Interp == null) return;
            ctx.Selection.PruneContained();
            bool hidden = ctx.SequenceActive || !ctx.IsStoryMgr
                          || (ctx.Placement != null && ctx.Placement.Active);
            if (hidden)
            {
                if (Model.Mode != BottomMenuMode.None) { Model.Mode = BottomMenuMode.None; Model.Entries.Clear(); Model.Version++; }
                _lastSelected = int.MinValue;
                _lastSelVersion = int.MinValue;
                _lastProductionMutationHash = null;
                return;
            }
            // The native row changes wholesale on its boxed/built/request gates
            // (FUN_00521c80:352-361) and product counts also gate cell presentation
            // (FUN_00520620:483-491). These fields are mutated outside this controller, so
            // compare their authoritative state before applying the 4 Hz progress-bar poll.
            int? productionMutationHash = SelectedProductionMutationHash();
            // Rebuild on selection or live production-state changes immediately; otherwise
            // refresh at 4 Hz (buildable gates and production progress move under the player's feet).
            if (ctx.SelectedHandle == _lastSelected && ctx.Selection.Version == _lastSelVersion
                                                    && productionMutationHash == _lastProductionMutationHash
                                                    && Time.unscaledTime < _nextRefresh) return;
            _lastSelected = ctx.SelectedHandle;
            _lastSelVersion = ctx.Selection.Version;
            _nextRefresh = Time.unscaledTime + 0.25f;
            Rebuild();
        }

        void EnsureCurrentContext()
        {
            DigglesTclContext current = ctx;
            if (ReferenceEquals(_observedContext, current)) return;
            _observedContext = current;
            _lastSelected = int.MinValue;
            _lastSelVersion = int.MinValue;
            _nextRefresh = 0f;
            _lastContentHash = int.MinValue;
            _lastProductionMutationHash = null;
            Model.Mode = BottomMenuMode.None;
            Model.Title = "";
            Model.Entries.Clear();
            Model.Version++;
        }

        void Rebuild()
        {
            Model.Entries.Clear();
            Model.Title = "";

            DigglesTclEntity? sel = null;
            if (ctx.SelectedHandle != 0) ctx.Entities.TryGetValue(ctx.SelectedHandle, out sel);
            string kind = sel?.Class?.Kind ?? "";

            // The native window law (FUN_00529360, Docs/BOTTOM_MENU_RE.md §1): one selected
            // building → Production; one selected adult gnome → the inventory panel (separate
            // control) and the bottom row hides; everything else — nothing selected, a baby,
            // items/crates-in-world — shows the dig buttons.
            if (kind == "production" || kind == "store" || kind == "energy"
                || kind == "protection" || kind == "elevator")
            {
                BuildProduction(sel!);
            }
            else if (kind == "gnome")
            {
                Model.Mode = BottomMenuMode.None;   // the inventory panel owns this selection
            }
            else
            {
                BuildDigMenu();
            }
            // Change-gate the version bump: the 4 Hz refresh rebuilt an identical model most
            // ticks, and every unconditional Version++ made BottomMenuView re-render the whole
            // panel (SetActive/sprite/text churn 4×/s, steady). Hash the built content and only
            // publish when it actually changed — a moving progress bar still re-renders (its
            // Progress float lands in the hash).
            int h = ContentHash();
            if (h != _lastContentHash)
            {
                _lastContentHash = h;
                Model.Version++;
            }
            _lastProductionMutationHash = SelectedProductionMutationHash();
        }

        int _lastContentHash;

        int ContentHash()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + (int)Model.Mode;
                h = h * 31 + Model.Title.GetHashCode();
                var entries = Model.Entries;
                for (int i = 0; i < entries.Count; i++)
                {
                    var e = entries[i];
                    h = h * 31 + e.Id.GetHashCode();
                    h = h * 31 + e.Icon.GetHashCode();
                    h = h * 31 + e.Tooltip.GetHashCode();
                    h = h * 31 + e.Count;
                    h = h * 31 + ((e.Toggle ? 1 : 0) | (e.ToggledOn ? 2 : 0) | (e.Selectable ? 4 : 0)
                                | (e.Highlighted ? 8 : 0) | (e.Dimmed ? 16 : 0)
                                | (e.Disabled ? 32 : 0));
                    h = h * 31 + e.Progress.GetHashCode();
                    h = h * 31 + (int)e.ProgressKind;
                    h = h * 31 + (int)e.Invention;
                }
                return h;
            }
        }

        private int? SelectedProductionMutationHash()
        {
            if (ctx.SelectedHandle == 0
                || !ctx.Entities.TryGetValue(ctx.SelectedHandle, out var selected)) return null;
            string kind = selected.Class?.Kind ?? "";
            if (kind != "production" && kind != "store" && kind != "energy"
                && kind != "protection" && kind != "elevator") return null;

            return ProductionMutationHash(selected);
        }

        private static int ProductionMutationHash(DigglesTclEntity building)
        {
            bool boxed = building.VarsScope.Vars.TryGetValue("_boxed", out var boxedValue)
                         && (boxedValue == "1" || boxedValue == "true");
            bool showPack = !boxed && building.BuildupComplete;
            int flags = (building.PackRequested ? 1 : 0)
                        | (boxed ? 2 : 0)
                        | (building.BuildupComplete ? 4 : 0)
                        | (showPack ? 8 : 0)
                        | (building.UnpackRequested ? 16 : 0)
                        | (building.BuildupRequested ? 32 : 0)
                        | (building.OrderGhostShown ? 64 : 0);

            unchecked
            {
                int hash = 17;
                hash = hash * 31 + flags;
                hash = hash * 31 + building.ProdSlots.Count;
                foreach (var slot in building.ProdSlots)
                {
                    hash = hash * 31 + slot.Key.GetHashCode();
                    hash = hash * 31 + slot.Value.GetHashCode();
                }
                return hash;
            }
        }

        // ---- Dig tools (ViWinDigButtons — Docs/BOTTOM_MENU_RE.md §2/§3) ----
        // Native: 6 buttons — Tunnel + 4 cave sizes + Undo. Cave sizes unlock by the best
        // gnome's exp_Stein (enable_digbrushes, thresholds tttexp_digbrush 0.15/0.30/0.70;
        // computed here directly — the Tcl driver reads `gamestats attribmax`, still stubbed).
        // Tool mapping: dig:std → the standard-tunnel drag marquee; dig:cnt0..3 → the four cave
        // template stamps (cave0..3.tga, ClickToMoveService.StampCave); dig:undo → the
        // erase/delete tool (ClickToMoveService.DigErase). Undo is enabled iff any dig tool is
        // (Σ digenable0..4 > 0.5) — std + Tiny are always on post-tutorial, so it stays enabled.
        void BuildDigMenu()
        {
            Model.Mode = BottomMenuMode.Dig;
            Model.Title = "";
            float maxStone = BestOwnGnomeStoneExp();
            bool armed = ClickToMoveService.DigToolArmed;
            int cur = ClickToMoveService.DigDepthCnt;
            // Locked tools are hidden, not dimmed. Native law: the DigButtons tick
            // FUN_0050e2a0:13 writes `digenable_i >= 0.5` straight into each button's
            // +0x1c, and +0x1c is the widget visible flag — the hit-test refuses a widget with
            // +0x1c == 0 (FUN_00505dd0:18) and the generic hide path is
            // `if (+0x1c) { invalidate; +0x1c = 0 }` (FUN_00506230:104-107, FUN_00507180:89-92).
            // A locked cave size therefore does not exist on screen at all.
            // Tooltips = the native messages.txt keys (English until localization lands).
            AddTool("dig:std", "BuddelnG", "Tunnel", armed && cur == ClickToMoveService.DigStandard);
            AddTool("dig:cnt0", "BuddelnH0", "Tiny", armed && cur == 0);
            if (maxStone >= 0.15f) AddTool("dig:cnt1", "BuddelnH1", "Small", armed && cur == 1);
            if (maxStone >= 0.30f) AddTool("dig:cnt2", "BuddelnH2", "Medium", armed && cur == 2);
            if (maxStone >= 0.70f) AddTool("dig:cnt3", "BuddelnH3", "Large", armed && cur == 3);
            AddTool("dig:undo", "BuddelnUndo", "Delete", armed && cur == ClickToMoveService.DigErase);
        }

        void AddTool(string id, string icon, string tooltip, bool highlighted, bool dimmed = false)
            => Model.Entries.Add(new BottomMenuEntry
            {
                Id = id, Icon = icon, Tooltip = tooltip,
                Selectable = true, Highlighted = highlighted, Dimmed = dimmed,
            });

        // enable_digbrushes' `gamestats attribmax $own exp_Stein` computed natively-equivalent:
        // the maximum stone experience across the local player's gnomes. The reader is
        // get_attrib — per-gnome exp values live on the normal attrib store (prod_invent reads
        // them with `get_attrib this <exp>`, z_work_common.tcl:229); `get_expattrib` is the
        // name-enumeration command (the ordered exp_* list, z_methods.tcl:425) and returned the
        // name list here, which parsed as 0 → every gate read "no experience".
        float BestOwnGnomeStoneExp()
        {
            float best = 0f;
            foreach (var e in ctx.Entities.Values)
            {
                if (e.Owner != ctx.LocalPlayer || e.Class == null || e.Class.Kind != "gnome") continue;
                var v = SafeEval($"get_attrib {e.Handle} exp_Stein");
                if (float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f > best)
                    best = f;
            }
            return best;
        }

        // ---- Building / crate (the ViWinProduction law — PRODUCTION_PANEL_RE.md) ----
        void BuildProduction(DigglesTclEntity b)
        {
            Model.Mode = BottomMenuMode.Production;
            Model.Title = SafeEval($"get_objname {b.Handle}");
            var pc = ctx.Placement;
            var btn = pc?.QueryButtons(b.Handle);

            // Packed in a crate → only unpack (FUN_00521c80; info_prods.tcl:59).
            if (btn != null && btn.ShowBuild)
            {
                AddPackAction("act:unpack", btn.BuildPending ? "S1unpack" : "unpack",
                    "Build (choose a spot)", btn.BuildPending);
                return;
            }

            // Energy building → only the on/off toggle (info_prods.tcl:61-71).
            if (SafeEval($"get_objtype {b.Handle}") == "energy")
            {
                bool on = SafeEval($"get_prod_enabled {b.Handle}") == "1";
                Add("act:switch", on ? "energyon" : "energyoff", on ? "On" : "Off",
                    toggle: true, on: on);
                return;
            }

            // Pack armed → the whole catalog hides and only the active-lamp pack button stays,
            // now meaning Cancel: the native window build fills the product cells only while
            // rec+0x29 (pack), rec+0x2c (packing lamp) and rec+0x3d (order ghost) are all clear
            // (FUN_00521c80:352-357 gates FUN_00520620/FUN_00521310), and the armed button
            // renders as S1packtobox with btn+0x5c = 1 + tooltip "Cancel" (:743-757). The click
            // routes to CancelPack — the byte drop is the whole native cancel (the walking packer
            // stands down at the prod_builddown gate, z_work_common.tcl:443-446).
            if (b.PackRequested)
            {
                AddPackAction("act:pack", "S1packtobox", "Cancel");
                return;
            }

            // Half-built → resume + pack (FUN_00521c80:503-639 aufbau; FUN_005242b0 case 2 pack).
            if (btn != null && btn.ShowResume)
            {
                Add("act:resume", btn.ResumePending ? "S1aufbau" : "aufbau",
                    "Resume construction", dimmed: btn.ResumePending);
                AddPackAction("act:pack", "packtobox", "Pack into a crate");
                return;
            }

            // Built production building → the full catalog: invented products order normally;
            // not-yet-invented ones still show (the native window's second, tech-gated pass
            // FUN_00521310 — objects that could still be invented) as dimmed cells. Ordering an
            // invention (the Bp<item> production job, genericprod.tcl:705) wires up with the
            // material/haul dispatch.
            var list = TclList.Parse(SafeEval($"get_prod_slot_list {b.Handle}"));
            // Production progress surfaces (model contract: Progress 0..100 + ProgressKind).
            // Native bar law (window draw FUN_00520620:483-562):
            // • a bar exists only on the cell whose item is the record's bound current item
            // (rec+0x48, set when the collector first services the slot — FUN_00436750:
            // 146-158; port: entity.CurrentProdItem) and whose order count ≥ 1 (the cell
            // count gate at:491 — cancelling the order to 0 hides the bar instantly);
            // • solid blue while the progress pair is invalid or the record is in the haul
            // state (`+0x50 < 1 || +0x4c < 0 || +0x1c == 3`,:492-494; colors 0xa00000c8/
            // 0xa02828e6 — the pct is forced 0 so the full-width back draws as a solid
            // blue bar);
            // • green fill over gray once the pair is valid (colors 0xa028e628/0xa0646464,
            // pct = clamp(cur*100/max, 0, 100),:526-562) — the actual crafting/invention
            // anim phase.
            // Walk-leg timing: the native kind-2/kind-3 plans are muetze → walk → workat-emit
            // (FUN_00438e80:78/:91/:108, FUN_004391f0:75/:83/:100) — evt_autoprod_workat fires
            // only when the gnome has arrived, and the emit itself re-invalidates the pair
            // (FUN_0043a8a0:79-80/:91-92), so the Tcl's immediate prod_getitemtask feed
            // (info_progress_prod → the pair, ungated — TCL:CTclObjMethods::InfoProgresProd
            // FUN_0069def0:139-141) can only land after the walk. The port emits the workat at
            // dispatch (the Tcl chain walks itself via walk_dummy), so the feed arrives before
            // the walk; the model therefore gates green on the claimed worker being at the place
            // — the FUN_00437370 "at the workplace" box, the same bounds the material-absorb law
            // uses (port stand-in for the native plan walk-leg phase). The haul state (+0x1c==3,
            // written FUN_0043c660:97/:235) maps to a live kind-4 plan into this place. The
            // native blue is always the solid full-width back, not a partial "delivered
            // fraction" fill.
            var disp = ctx.ProdDispatcher;
            bool switchmode = SafeEval($"get_prod_switchmode {b.Handle}") == "1";
            for (int slotIndex = 0; slotIndex < list.Count; slotIndex++)
            {
                if (slotIndex >= ProductionSlotOverrides.PackSlotIndex) break;
                string cls = list[slotIndex];
                ProductionSlotOverride slotOverride = ctx.ProdSlotOverrides[slotIndex];
                if (slotOverride == ProductionSlotOverride.Hidden) continue;
                bool invented = SafeEval($"get_prod_slot_invented {b.Handle} {cls}") == "1";
                bool buildable = invented && SafeEval($"get_prod_slot_buildable {b.Handle} {cls}") == "1";
                int cnt = b.ProdSlots.TryGetValue(cls, out var n) ? (int)n : 0;
                var invState = invented ? BottomMenuInventionState.None : InventionStateFor(b, cls);
                var entry = new BottomMenuEntry
                {
                    Id = "prod:" + cls,
                    Icon = cls,                     // the view asks IconLoader.GetProduct(cls, buildable)
                    Tooltip = invented ? cls : cls + " (invent)",
                    // The switchmode place (farm.tcl:119): the order is an on/off toggle with the
                    // green lamp, never a counted badge.
                    Count = switchmode ? -1 : (cnt > 0 ? cnt : -1),
                    Toggle = switchmode,
                    ToggledOn = switchmode && cnt > 0,
                    Dimmed = invented && !buildable
                             || slotOverride == ProductionSlotOverride.Disabled,
                    Disabled = slotOverride == ProductionSlotOverride.Disabled,
                    Invention = invState,
                };
                // A cancelled order reaches zero before evt_zwerg_break tears down its worker
                // (misc/genericprod.tcl:40-47; classes/zwerg/z_events.tcl:2062-2074). Native
                // release invalidates the logical pair (FUN_0043a0c0:37-38/:59-60), while the
                // panel independently hides the overlay at count zero (FUN_00520620:483-491).
                bool hasActiveOrder = cnt > 0;
                if (hasActiveOrder && b.CurrentProdItem == cls)
                {
                    // rec+0x1c == 3 (hauling) forces blue regardless of the pair (:492-494).
                    bool hauling = disp != null && disp.TryGetHaulFor(b.Handle, out _);
                    // The native plan walk-leg phase: green only once the claimed worker is
                    // at the place (FUN_00437370 box — see the walk-leg note above).
                    bool arrived = b.CurrentWorkerHandle >= 0
                        && ctx.Entities.TryGetValue(b.CurrentWorkerHandle, out var worker)
                        && Diggles.Runtime.Sim.ProdDispatcher.AtWorkplace(worker.Position, b.Position);
                    // A live invention (the bound cell is not yet invented) renders the solid
                    // blue bar for its whole run: the Tcl feed freezes the pair at 1/2 for the
                    // entire invent anim (prod_getitemtask's Bp branch — info_progress_prod
                    // this 1 [llength {prod_invent, prod_finishitem}], genericprod.tcl:711-717)
                    // — a meaningless static half-fill; the original's observed look for a
                    // pending/live invention is the full blue back. The raw draw law
                    // (FUN_00520620:492/:548) would paint that frozen pair as a static 50%
                    // green — the verified original look wins over the frozen half-fill.
                    if (invented && b.ProdProgressMax > 0 && !hauling && arrived)
                    {
                        entry.Progress = b.ProdProgressPct;   // green cur*100/max (:548-562)
                        entry.ProgressKind = BottomMenuProgressKind.Producing;
                    }
                    else   // ordered / assigned / walking / hauling / inventing → solid blue back
                    {
                        entry.Progress = 100;
                        entry.ProgressKind = BottomMenuProgressKind.Delivering;
                    }
                }
                Model.Entries.Add(entry);
            }
            // Repair — appended when damaged (info_prods.tcl:93-96).
            if (float.TryParse(SafeEval($"get_attrib {b.Handle} atr_Hitpoints"),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out var hp) && hp < 1f)
                Add("act:repair", "repair", "Repair");
            if (btn != null && btn.ShowPack)
                AddPackAction("act:pack", "packtobox", "Pack into a crate");
        }

        // Invention readiness: requirements = the class method prod_item_attribs <item>
        // (tttinvent_* attrib/value pairs, genericprod.tcl:119-122).
        // Red = no own gnome meets them all; White = a qualified gnome is in his work window
        // (get_remaining_sparetime == 0 — the native plan switch is pure polling of it);
        // Yellow = qualified gnomes exist but all are on sparetime.
        static bool _loggedReqFail;
        readonly HashSet<string> _healedInvents = new();   // one tttinvent re-seed try per item

        BottomMenuInventionState InventionStateFor(DigglesTclEntity b, string item)
        {
            // tttinvent_* is a list of sublists: {{exp_Nahrung 0.039}} (techtreetunes.tcl) —
            // flatten sublist elements into attrib/value tokens (a flat {exp_X 0.1} shape
            // flattens to the same). The old flat-pairs read saw one token per sublist → zero
            // requirements → everything white.
            var raw = SafeEval($"call_method_static {b.Class!.Name} prod_item_attribs {item}");
            var tokens = new List<string>();
            foreach (var el in TclList.Parse(raw)) tokens.AddRange(TclList.Parse(el));
            if (tokens.Count == 0 && _healedInvents.Add(item))
            {
                // Heals the shared-global tttinvent clobber: an item that is itself a production
                // site (Hauklotz/Steinmetz/…) is also a class, and that class's own load ends in
                // genericprod.tcl:140 `catch {unset … tttinvent_$classname …}`. In the original
                // every class loads in its own interp, so Feuerstelle's copy survives; the port's
                // class loads share one global scope, so the later load destroyed the var the
                // prod_item_attribs method reads at call time (`global tttinvent_$item`,
                // genericprod.tcl:120-122) → empty requirements → the cell read white while
                // non-class items (Grillhamster) stayed correct. Re-running the item's
                // techtreetunes section restores the canonical values (it is the same idempotent
                // data the class load consumed). Once per item — a genuinely requirement-free
                // tttinvent stays empty and must not re-parse the 1400-line tunes file at the
                // panel's 4 Hz refresh.
                SafeEval($"set tttsection_tocall {{{item}}}");
                SafeEval("call scripts/misc/techtreetunes.tcl");
                raw = SafeEval($"call_method_static {b.Class!.Name} prod_item_attribs {item}");
                foreach (var el in TclList.Parse(raw)) tokens.AddRange(TclList.Parse(el));
            }
            if (tokens.Count == 0 && !_loggedReqFail)
            {
                _loggedReqFail = true;
                Debug.LogWarning($"[BottomMenu] prod_item_attribs {b.Class.Name}/{item} → '{raw}' (no requirements parsed — check the tttinvent chain)");
            }
            bool anyQualified = false, anyWorking = false;
            foreach (var e in ctx.Entities.Values)
            {
                if (e.Owner != ctx.LocalPlayer || e.Class == null || e.Class.Kind != "gnome") continue;
                bool ok = true;
                for (int i = 0; i + 1 < tokens.Count; i += 2)
                {
                    // get_attrib = the exp value reader (see BestOwnGnomeStoneExp note).
                    if (!float.TryParse(SafeEval($"get_attrib {e.Handle} {tokens[i]}"),
                            NumberStyles.Float, CultureInfo.InvariantCulture, out var have)
                        || !float.TryParse(tokens[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var need)
                        || have < need) { ok = false; break; }
                }
                if (!ok) continue;
                anyQualified = true;
                if (float.TryParse(SafeEval($"get_remaining_sparetime {e.Handle}"),
                        NumberStyles.Float, CultureInfo.InvariantCulture, out var spare) && spare <= 0f)
                { anyWorking = true; break; }
            }
            return !anyQualified ? BottomMenuInventionState.Red
                 : anyWorking ? BottomMenuInventionState.White
                 : BottomMenuInventionState.Yellow;
        }

        // ---- click dispatch (the view calls this) ----
        public void Click(string id, bool rightButton)
        {
            EnsureCurrentContext();
            if (ctx.Interp == null || string.IsNullOrEmpty(id)) return;
            DigglesTclEntity? sel = null;
            if (ctx.SelectedHandle != 0) ctx.Entities.TryGetValue(ctx.SelectedHandle, out sel);

            if (id.StartsWith("dig:", System.StringComparison.Ordinal))
            {
                // Locked cave sizes don't select (the native button enable gate).
                var entry = Model.Entries.Find(x => x.Id == id);
                if (entry != null && entry.Dimmed) return;
                int tool = id switch
                {
                    "dig:std" => ClickToMoveService.DigStandard,
                    "dig:cnt0" => 0, "dig:cnt1" => 1, "dig:cnt2" => 2, "dig:cnt3" => 3,
                    "dig:undo" => ClickToMoveService.DigErase,
                    _ => int.MinValue,
                };
                if (tool == int.MinValue) return;
                // Click = arm the tool (plain LMB drags then dig; RMB cancels — the native flow);
                // clicking the armed tool again disarms it.
                if (ClickToMoveService.DigToolArmed && ClickToMoveService.DigDepthCnt == tool)
                {
                    ClickToMoveService.DigToolArmed = false;
                }
                else
                {
                    ClickToMoveService.DigDepthCnt = tool;
                    ClickToMoveService.DigToolArmed = true;
                }
            }
            else if (id.StartsWith("prod:", System.StringComparison.Ordinal) && sel != null)
            {
                // Uninvented cells click exactly like invented ones — the native window routes
                // both through the same "prodincrement" callback on the plain class
                // (FUN_005242b0:281/:289; the uninvented fill pass FUN_00521310 stores the same
                // plain-class product index). The slot store is never keyed "Bp<item>": the Bp
                // prefix lives only on the owner attrib (the invented flag) and on the workat
                // event's -text1 marker. The dispatcher decides invent-vs-produce from the Bp
                // attrib (ProdDispatcher.CollectProductionJobs → -text1 Bp<item>,
                // FUN_0043a8a0:96) and the unchanged Tcl runs prod_invent → add_owner_attrib
                // Bp<item> 1 (genericprod.tcl:705-716, z_work_common.tcl:8-32); the surviving
                // order then rolls into a real production on the next sweep. So: no gate here.
                // Left = prodincrement (cap 10), right = proddecrement (FUN_005242b0:357-474).
                string cls = id.Substring(5);
                var productList = TclList.Parse(SafeEval($"get_prod_slot_list {sel.Handle}"));
                int slotIndex = productList.IndexOf(cls);
                if (slotIndex < 0 || slotIndex >= ProductionSlotOverrides.PackSlotIndex
                    || ctx.ProdSlotOverrides[slotIndex] != ProductionSlotOverride.Normal) return;
                int cur = sel.ProdSlots.TryGetValue(cls, out var n) ? (int)n : 0;
                int next;
                if (SafeEval($"get_prod_switchmode {sel.Handle}") == "1")
                {
                    // Switchmode (farm.tcl:119): the click toggles the slot 0↔10 — the native
                    // arms switch slots with the sentinel 10, not a finite count
                    // (FUN_005242b0:232-255); info_end_prod preserves the sentinel
                    // (FUN_0069e210:82-96). Arming with 1 made the first completion decrement
                    // to 0, which farm.tcl:171-175 reads as cancellation → free_parenthamsters
                    // (escapes, a dead breed button, random cancels).
                    // Exclusivemode (farm.tcl:120): turning a slot on turns the others off
                    // (the farm breeds one species at a time — its event0 picks the single
                    // current_slot from the ordered ones, farm.tcl:160-168).
                    next = cur > 0 ? 0 : 10;
                    if (next == 10 && SafeEval($"get_prod_exclusivemode {sel.Handle}") == "1")
                        foreach (var kv in new List<KeyValuePair<string, double>>(sel.ProdSlots))
                            if (kv.Key != cls && kv.Value > 0)
                                SafeEval($"set_prod_slot_cnt {sel.Handle} {kv.Key} 0");
                }
                else
                {
                    next = rightButton ? Mathf.Max(0, cur - 1) : Mathf.Min(10, cur + 1);
                }
                SafeEval($"set_prod_slot_cnt {sel.Handle} {cls} {next}");
                // The native window follows every button action with evt_btn_on on the building
                // ('set_event %i evt_btn_on -target %i -num1 0 -text1 <verb> -text2 <item>',
                // FUN_005235a0:44, called from the prodincrement/decrement handlers
                // FUN_005242b0:351/:472). genericprod's handler (genericprod.tcl:33-52) is the
                // order-cancel law: a working cook whose slot fell to 0 gets evt_zwerg_break, and
                // an idle building runs free_unneeded_items — the delivered materials lying
                // locked in front of it are released back to the world (release_item,
                // genericprod.tcl:520-537). Without this raise a cancelled order left its
                // ingredients locked forever.
                SafeEval($"set_event {sel.Handle} evt_btn_on -target {sel.Handle} -num1 0 "
                    + $"-text1 {(rightButton ? "proddecrement" : "prodincrement")} -text2 {cls}");
            }
            else if (sel != null)
            {
                if ((id == "act:pack" || id == "act:unpack")
                    && ctx.ProdSlotOverrides[ProductionSlotOverrides.PackSlotIndex]
                       != ProductionSlotOverride.Normal) return;
                switch (id)
                {
                    case "act:unpack": ctx.Placement?.OrderBuild(sel.Handle); break;
                    case "act:resume": ctx.Placement?.OrderResume(sel.Handle); break;
                    case "act:pack":
                        // Armed → the button is the cancel (S1packtobox lamp state,
                        // FUN_00521c80:743-757). Alt held → the move variant ("Umzug"):
                        // FUN_005242b0:314-329 branches on the Alt key byte (FUN_004ecd40+0x11a
                        // = GetAsyncKeyState(VK_MENU), FUN_004ed0b0:74) into the ghost placement
                        // + deferred pack-then-unpack. Else the direct pack (case 2:315-318).
                        if (sel.PackRequested) ctx.Placement?.CancelPack(sel.Handle);
                        else if ((Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt))
                                 && ctx.Placement != null && ctx.Placement.OrderMove(sel.Handle))
                        { /* move placement entered — the world click confirms */ }
                        else ctx.Placement?.OrderPack(sel.Handle);
                        break;
                    case "act:switch":
                    {
                        bool on = SafeEval($"get_prod_enabled {sel.Handle}") == "1";
                        SafeEval($"set_prod_enabled {sel.Handle} {(on ? 0 : 1)}");
                        break;
                    }
                    case "act:repair":
                        // The native dispatches repair through its own message (FUN_005242b0:45-68)
                        // — the Tcl-side chain hookup is a follow-up; log so a click is visible.
                        Debug.Log("[BottomMenu] repair — chain hookup pending");
                        break;
                }
            }
            _nextRefresh = 0f;   // reflect the click immediately on the next tick
        }

        string SafeEval(string cmd)
        {
            try { return ctx.Interp.Eval(cmd).Trim(); }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[BottomMenu] {cmd}: {e.Message}");
                return "";
            }
        }

        void Add(string id, string icon, string tooltip,
                 bool toggle = false, bool on = false, bool dimmed = false, bool disabled = false)
            => Model.Entries.Add(new BottomMenuEntry
            {
                Id = id, Icon = icon, Tooltip = tooltip,
                Toggle = toggle, ToggledOn = on, Dimmed = dimmed, Disabled = disabled,
            });

        void AddPackAction(string id, string icon, string tooltip, bool dimmed = false)
        {
            ProductionSlotOverride state = ctx.ProdSlotOverrides[ProductionSlotOverrides.PackSlotIndex];
            if (state == ProductionSlotOverride.Hidden) return;
            bool disabled = state == ProductionSlotOverride.Disabled;
            Add(id, icon, tooltip, dimmed: dimmed || disabled, disabled: disabled);
        }
    }
}
