#nullable enable

namespace Diggles.Runtime
{
    using System.Text;
    using TMPro;
    using UnityEngine;
    using VContainer;
    using Diggles.Runtime.Tcl.Engine;

    // In-game HUD — Canvas + TMP_Text only (no legacy/IMGUI UI). Wire a Screen-Space-Overlay
    // Canvas with two TMP_Text panels and assign them; this reads the live engine state each frame
    // and fills the text. Lazily binds the DigglesTclContext from DigglesWorldRuntime once the world
    // is up. A dumb view (no game-state ownership) — same pattern as SubtitleView.
    // selectedInfo: the selected unit's name + HP/AL/NU/MO (the ViWinRoot tooltip fields).
    // resourceInfo: population + resource tallies (owner_attrib).
    public sealed class GameplayHud : MonoBehaviour, IRuntimeSessionPresentationParticipant
    {
        [SerializeField] TMP_Text? selectedInfo;
        [SerializeField] TMP_Text? resourceInfo;
        [SerializeField] TMP_Text? inventoryInfo;   // optional: the selected dwarf's carried items
        [SerializeField] float refreshHz = 5f;

        DigglesTclContext? _ctx;
        WorkClockWidget? _workClock;   // the bottom-right ViWinClock work-window editor (code-built)
        NewsTickerWidget? _newsTicker;
        private DocumentWindowWidget? _documentWindow;
        float _next;

        // The 4 vitals the original tooltip shows. Nutrition = fullness (low ⇒ hungry); Alertness =
        // energy (low ⇒ tired). Labelled for the player.
        static readonly string[] Vitals = { "atr_Hitpoints", "atr_Nutrition", "atr_Alertness", "atr_Mood" };
        static readonly string[] VitalLabel = { "Health", "Food", "Energy", "Mood" };

        // Injected by VContainer (registered in DigglesGameInstaller). No scene lookups.
        [Inject]
        public void Construct(IActiveGameSession sessions) => BindSession(sessions.Current);

        public void BindSession(RuntimeGameSession session)
        {
            HideAndDestroy(_workClock);
            HideAndDestroy(_newsTicker);
            HideAndDestroy(_documentWindow);
            _workClock = null;
            _newsTicker = null;
            _documentWindow = null;
            _ctx = session.Engine.Ctx;
            _next = 0f;
            _lastSelected = "\u0000";
            _lastResources = "\u0000";
            _lastInventory = "\u0000";
        }

        static void HideAndDestroy(Component? component)
        {
            if (component == null) return;
            component.gameObject.SetActive(false);
            if (Application.isPlaying) Destroy(component.gameObject);
            else DestroyImmediate(component.gameObject);
        }

        void Update()
        {
            if (_ctx == null) return;
            // The work clock (the original's bottom-right ViWinClock — simultaneously the
            // work-window editor) builds its own uGUI under this HUD rect on first sight of the
            // live context; it shows/hides itself (gameplay only, not menu/cutscene).
            if (_workClock == null) _workClock = WorkClockWidget.Create((RectTransform)transform, _ctx);
            // The original top notification strip is also code-built from its native geometry.
            // Its state is driven by the newsticker Tcl command and scaled simulation time.
            if (_newsTicker == null) _newsTicker = NewsTickerWidget.Create((RectTransform)transform, _ctx);
            // Diary, online-help, tooltip, and quest pages are authored by the unchanged Tcl
            // document scripts and presented by one shared ViWinLayout-style overlay.
            if (_documentWindow == null)
                _documentWindow = DocumentWindowWidget.Create((RectTransform)transform, _ctx);
            if (Time.time < _next) return;
            _next = Time.time + (refreshHz > 0f ? 1f / refreshHz : 0.2f);

            // Change-gate the TMP assigns: an unconditional.text set dirties the label and
            // re-runs TMP layout every refresh (5 Hz × 3 labels) even when nothing changed.
            if (selectedInfo != null) SetIfChanged(selectedInfo, BuildSelected(), ref _lastSelected);
            if (resourceInfo != null) SetIfChanged(resourceInfo, BuildResources(), ref _lastResources);
            if (inventoryInfo != null) SetIfChanged(inventoryInfo, BuildInventory(), ref _lastInventory);
        }

        // The selected dwarf's panel: name, what it's doing, the 4 vitals, the skills it has (>0), and the
        // total knowledge. Mirrors the original's selected-gnome readout. Plain multi-line TMP text.
        string BuildSelected()
        {
            if (_ctx == null || _ctx.SelectedHandle == 0
                || !_ctx.Entities.TryGetValue(_ctx.SelectedHandle, out var e)) return "";
            var sb = new StringBuilder();
            sb.Append(e.Class?.Name ?? "?");
            if (!string.IsNullOrEmpty(e.InstanceName)) sb.Append(" \"").Append(e.InstanceName).Append('"');

            // occupation — what the dwarf is doing right now (current_worktask while working, else occupation)
            string occ = Var(e, "current_worktask");
            if (string.IsNullOrEmpty(occ) || occ == "0") occ = Var(e, "current_occupation");
            if (!string.IsNullOrEmpty(occ) && occ != "0") sb.Append("  (").Append(occ).Append(')');
            sb.Append('\n');

            // Vitals and skills are 0..1 floats (def_attrib ranges, genattribs.tcl:20-23).
            // regression: Mathf.RoundToInt display previously pinned every vital at "1" and
            // every skill at "0" while the real values moved underneath — show live floats.
            for (int i = 0; i < Vitals.Length; i++)
            {
                double v = _ctx.Stats.Get(e.Stats, Vitals[i]);
                if (i > 0) sb.Append("   ");
                sb.Append(VitalLabel[i]).Append(' ').Append(v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
            }

            // skills (exp_*) the dwarf has any of, biggest first, + the total "knowledge".
            double knowledge = 0; var skills = new System.Collections.Generic.List<(string n, double v)>();
            foreach (var kv in e.Stats.Raw)
            {
                if (!kv.Key.StartsWith("exp_", System.StringComparison.Ordinal) || kv.Value <= 0.0) continue;
                knowledge += kv.Value;
                skills.Add((kv.Key.Substring(4), kv.Value));   // strip "exp_"
            }
            skills.Sort((a, b) => b.v.CompareTo(a.v));
            foreach (var (n, v) in skills) sb.Append("\n  ").Append(n).Append(' ').Append(Mathf.RoundToInt((float)v));
            if (knowledge > 0) sb.Append("\nKnowledge ").Append(Mathf.RoundToInt((float)knowledge));
            return sb.ToString();
        }

        // The selected dwarf's carried items, grouped by class (e.g. "Pilzhut x2 Stein x1").
        string BuildInventory()
        {
            if (_ctx == null || _ctx.SelectedHandle == 0
                || !_ctx.Entities.TryGetValue(_ctx.SelectedHandle, out var e) || e.Inventory.Count == 0) return "";
            var counts = new System.Collections.Generic.Dictionary<string, int>();
            foreach (var h in e.Inventory)
                if (_ctx.Entities.TryGetValue(h, out var item))
                {
                    var n = item.Class?.Name ?? "?";
                    counts[n] = counts.TryGetValue(n, out var c) ? c + 1 : 1;
                }
            var sb = new StringBuilder();
            foreach (var kv in counts) { if (sb.Length > 0) sb.Append("   "); sb.Append(kv.Key); if (kv.Value > 1) sb.Append(" x").Append(kv.Value); }
            return sb.ToString();
        }

        string Var(DigglesTclEntity e, string key) => e.VarsScope.Vars.TryGetValue(key, out var v) ? v : "";

        string _lastSelected = "", _lastResources = "", _lastInventory = "";

        static void SetIfChanged(TMP_Text label, string value, ref string last)
        {
            if (value == last) return;
            last = value;
            label.text = value;
        }

        string BuildResources()
        {
            if (_ctx == null) return "";
            int pop = 0;
            foreach (var e in _ctx.Entities.Values)
            {
                if (e.Owner != _ctx.LocalPlayer || e.ContainedIn >= 0) continue;
                var n = e.Class?.Name;
                if (n == "Zwerg" || n == "Frau" || n == "Baby") pop++;
            }
            return $"Dwarves {pop}    Stone {Owned("Stein")}    Wood {Owned("Pilzstamm")}    Food {Owned("Grillpilz")}";
        }

        int Owned(string name)
        {
            if (_ctx != null && _ctx.OwnerAttribs.TryGetValue(_ctx.LocalPlayer, out var bag)
                && bag.TryGetValue(name, out var v)) return Mathf.RoundToInt((float)v);
            return 0;
        }
    }
}
