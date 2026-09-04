#nullable enable

namespace Diggles.Runtime.UI
{
    using System.Collections.Generic;

    // The bottom context menu — POCO view-model (the native bottom windows keyed on the
    // selection: ViWinDigButtons / ViWinInventory / ViWinProduction; see
    // Docs/PRODUCTION_PANEL_RE.md for the production window's ground truth).
    // The controller rebuilds this; the authored view renders it. No UnityEngine types.
    public enum BottomMenuMode
    {
        None,        // hidden (cutscene, placement, a gnome selected — the inventory panel owns that)
        Dig,         // nothing selected → dig tools
        Production,  // a building/crate selected → products + actions in one row (native law)
    }

    // What the progress fill means — the view tints it accordingly (green = the building is
    // producing this item right now; blue = ingredients are being delivered for it).
    public enum BottomMenuProgressKind { None, Producing, Delivering }

    // An uninvented product renders a question mark over the icon, both tinted by state:
    // Red = no own gnome has the required skills; Yellow = a qualified gnome exists but is
    // not working right now (sparetime); White = can be invented now.
    public enum BottomMenuInventionState { None, Red, Yellow, White }

    public sealed class BottomMenuEntry
    {
        // Stable id the view hands back on click: "dig:std" | "dig:cnt0".."cnt3" | "dig:undo"
        // | "prod:<Class>" | "act:unpack|resume|pack|switch|repair".
        public string Id = "";
        public string Icon = "";        // IconLoader name (class name or action icon name)
        public string Tooltip = "";
        public int Count = -1;          // order badge 0..10 (10 renders as "oo"); -1 = no badge
        public bool Toggle;             // render as an on/off cell (energy buildings)
        public bool ToggledOn;
        // Tool-selection highlight (the dig tools): the native brightens the selected tool's
        // icon — no toggle marks. Selectable marks the cell as part of a highlight group.
        public bool Selectable;
        public bool Highlighted;
        public bool Dimmed;             // not buildable / locked (the Red<class> icon also applies)
        public bool Disabled;           // visible but input-blocked by prodslot_override
        public float Progress = -1f;    // 0..100; -1 = no progress bar
        public BottomMenuProgressKind ProgressKind = BottomMenuProgressKind.None;
        public BottomMenuInventionState Invention = BottomMenuInventionState.None;
    }

    public sealed class BottomMenuModel
    {
        public BottomMenuMode Mode = BottomMenuMode.None;
        public string Title = "";
        public readonly List<BottomMenuEntry> Entries = new();
        // Bumped on every rebuild — the view re-renders when it sees a new version.
        public int Version;
    }
}
