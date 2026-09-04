#nullable enable

namespace Diggles.Runtime.Tcl.Engine
{
    using System.Collections.Generic;

    // The player's selection — natively a DSFastArray of object handles embedded in CGame
    // (data DAT_00772400+0xa0c4, count +0xa0c8), not a single handle. Faithful port of the
    // native selection laws:
    //
    // • caninclude (FUN_005e32b0): owner == local player and the object is selectable and —
    // when the selection is already non-empty — only gnome (native objtype 1) / baby (0xb)
    // units may join. The trailing type-compat clause (t1==t2 or either ∈ {1,0xb}) is dead
    // code: the candidate already passed the gnome/baby gate, so it always satisfies it.
    // Consequences, test-verified against the decompile: a building can never join a
    // non-empty selection (selected alone it stays alone against more structures), but
    // gnomes/babies do join a building selection — the native allows the mixed set with
    // the structure as primary.
    // • include (FUN_005e3350): caninclude gate + duplicate check + append. (The native also
    // flashes the HP/progress floater over the unit — a UI side effect, not modeled here.)
    // • exclude (FUN_005e34c0): linear remove. clear = truncate to 0 (FUN_0040b980(0)).
    // • the click consumption (FUN_005309c0, runs on mouse-up after the eventgen loop): a
    // "select" verb with no hover → clear; else if !caninclude or the multiselect key
    // (Shift, VK 0x10) is not held → clear first; then include. So Shift+click adds (no
    // toggle — including an already-selected unit is the dup-check no-op), plain click
    // replaces, and a Shift+click on something that can't join (a building while gnomes are
    // selected) replaces the selection with it.
    // • the band-box include step (FUN_0052d170:632-652, runs per candidate on drag release,
    // handle order): empty selection → include; non-empty and !caninclude → a gnome
    // (objtype 1 exactly) clears the whole selection and starts fresh, anything else is
    // skipped; else include. Note the gnome-clear branch is defensive dead code in the
    // shipped exe — the band pre-filter (:607-613) already guarantees own+selectable, so
    // an own selectable gnome never fails caninclude; the live outcome of a box over a
    // structure + gnomes is the mixed set in handle order (structure first = primary).
    // Shift not held at release → the selection is cleared before the loop (:110-113).
    //
    // Tcl surface (the native CTclGlobalMethods): get_selectedobject = selection[0] — the
    // primary only (FUN_00647100 reads +0xa0c4[0]); is_selected = list membership
    // (FUN_0069e750 → FUN_005e35a0); `selection check/include/exclude/caninclude/clear`
    // (FUN_0061e7a0) = the raw list ops.
    //
    // Port shape: this POCO lives on DigglesTclContext (ctx.Selection); ctx.SelectedHandle is
    // now a property over it (get = Primary, set = ReplaceWith) so every existing single-select
    // writer/reader — UI controllers, placement, Tcl set_selectedobject — keeps working with
    // the primary, exactly the native get_selectedobject contract. ReplaceWith is deliberately
    // ungated (the programmatic seam the old raw field was — tests/cutscenes force foreign
    // handles); the gated laws run only on the input paths (ClickSelect/BoxIncludeStep/Include).
    public sealed class SelectionSet
    {
        readonly DigglesTclContext ctx;
        readonly List<int> handles = new();

        public SelectionSet(DigglesTclContext ctx) { this.ctx = ctx; }

        // Bumped on every mutation — the binding seam for a future multi-portrait view
        // (controllers compare versions instead of diffing the list).
        public int Version { get; private set; }

        public IReadOnlyList<int> Handles => handles;
        public int Count => handles.Count;

        // selection[0] — what the native get_selectedobject returns (FUN_00647100).
        public int Primary => handles.Count > 0 ? handles[0] : 0;

        public bool Contains(int handle)
        {
            for (int i = 0; i < handles.Count; i++)
                if (handles[i] == handle) return true;
            return false;
        }

        // Native gnome/baby join law: only these kinds may join a non-empty selection
        // (objtype 1 = gnome, 0xb = baby — FUN_005e32b0's count>0 branch).
        public static bool KindJoinsMulti(string? kind) => kind == "gnome" || kind == "baby";

        // The band-box candidate kinds (FUN_0052d170:611-613 objtype ∈ {1,2,5,6,7,8,10,0xb}):
        // characters + monsters + the selectable structure kinds. The include laws sort out
        // the rest (a gnome clears a boxed building).
        public static bool KindBandSelectable(string? kind)
            => kind == "gnome" || kind == "baby" || kind == "monster"
            || kind == "production" || kind == "store" || kind == "energy"
            || kind == "protection" || kind == "elevator";

        DigglesTclEntity? Resolve(int handle)
            => handle > 0 && ctx.Entities.TryGetValue(handle, out var e) ? e : null;

        // FUN_005e32b0 — can this object join the selection as it currently stands?
        // owner gate → obj+0x36c == CGame+0x64 (the local player);
        // selectable flag → obj+0x3d0 & 0x200 (the port models it as the _selectable var —
        // set_selectable 0 clears it; unset = selectable, like the native default);
        // join law → see the class header.
        public bool CanInclude(int handle)
        {
            var e = Resolve(handle);
            if (e == null) return false;
            if (e.Owner != ctx.LocalPlayer) return false;
            if (e.VarsScope.Vars.TryGetValue("_selectable", out var sv) && (sv == "0" || sv == "false"))
                return false;
            if (handles.Count > 0)
            {
                string kind = e.Class?.Kind ?? "";
                if (!KindJoinsMulti(kind)) return false;
                var first = Resolve(handles[0]);
                if (first != null)
                {
                    string fk = first.Class?.Kind ?? "";
                    if (fk != kind && !KindJoinsMulti(fk) && !KindJoinsMulti(kind))
                        return false;   // native compat clause (dead once kind∈{gnome,baby}, kept for shape)
                }
            }
            return true;
        }

        // FUN_005e3350 — gated append (dup-safe). Returns true when the object is now selected.
        public bool Include(int handle)
        {
            if (!CanInclude(handle)) return Contains(handle);
            if (Contains(handle)) return true;
            handles.Add(handle);
            PublishChanged();
            return true;
        }

        // FUN_005e34c0 — remove if present.
        public void Exclude(int handle)
        {
            for (int i = 0; i < handles.Count; i++)
                if (handles[i] == handle)
                {
                    handles.RemoveAt(i);
                    PublishChanged();
                    return;
                }
        }

        public void Clear()
        {
            if (handles.Count == 0) return;
            handles.Clear();
            PublishChanged();
        }

        public bool PruneContained()
        {
            bool removed = false;
            for (int i = handles.Count - 1; i >= 0; i--)
            {
                var entity = Resolve(handles[i]);
                if (entity != null && entity.ContainedIn < 0) continue;
                handles.RemoveAt(i);
                removed = true;
            }
            if (removed) PublishChanged();
            return removed;
        }

        // The programmatic single-select seam (the old raw ctx.SelectedHandle field): ungated —
        // Tcl set_selectedobject, placement, cutscene resets and tests force arbitrary handles
        // (incl. 0/-1 = deselect). Not an input path; no caninclude here.
        public void ReplaceWith(int handle)
        {
            if (handles.Count == 1 && handles[0] == handle) return;
            handles.Clear();
            if (handle > 0) handles.Add(handle);
            PublishChanged();
        }

        private void PublishChanged()
        {
            Version++;
            global::Diggles.Runtime.EnergyRadiusOverlay.PublishSelection(ctx, Primary);
        }

        // FUN_005309c0 — the click-select consumption ("select" verb on mouse-up).
        // additive = the multiselect key (Shift) held.
        public void ClickSelect(int hoverHandle, bool additive)
        {
            if (hoverHandle <= 0) { Clear(); return; }
            if (!CanInclude(hoverHandle) || !additive) Clear();
            Include(hoverHandle);
        }

        // FUN_0052d170's per-candidate include step (the band-box release loop, handle order).
        // The caller pre-filters candidates (owner/kind/screen-rect — see ClickToMoveService).
        // The gnome-clear branch mirrors the native defensive code; with pre-filtered
        // candidates it never fires (see the class header) — kept for shape fidelity.
        public void BoxIncludeStep(int handle)
        {
            if (handles.Count == 0) { Include(handle); return; }
            if (!CanInclude(handle))
            {
                var e = Resolve(handle);
                if (e?.Class?.Kind != "gnome") return;   // native: only objtype 1 overrides
                Clear();
            }
            Include(handle);
        }
    }
}
