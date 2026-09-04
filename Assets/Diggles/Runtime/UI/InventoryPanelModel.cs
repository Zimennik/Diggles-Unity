#nullable enable

namespace Diggles.Runtime.UI
{
    using System.Collections.Generic;

    // The gnome inventory panel — POCO view-model (ViWinInventory), separate from the bottom
    // context menu: it is a slot grid whose size follows the worn basket (Holzkiepe /
    // Grosse_Holzkiepe / Schubkarren / Hamsterkarren; InvGetSize FUN_005cde10 — base 6 + the
    // carried-container bonus), with its own interactions (crate click → placement; drag-drop
    // not yet implemented).
    public sealed class InventorySlot
    {
        public int ItemHandle;
        public string Icon = "";        // item class name (IconLoader convention)
        public string Tooltip = "";
    }

    public sealed class InventoryPanelModel
    {
        public bool Visible;            // a player gnome/baby is selected
        public string Title = "";

        // The display splits into two grids (the original look): the gnome's own slots and the
        // worn-basket bonus slots. Items fill main first, the rest render in extra; the extra
        // container hides entirely when ExtraSlotCount is 0. Overflow beyond main+bonus lands in
        // extra too, so nothing an entity actually carries is ever hidden.
        public int MainSlotCount;       // base grid capacity (filled + empty cells to render)
        public int ExtraSlotCount;      // basket grid capacity (0 = no basket, hide the container)
        public int SlotCount;           // legacy total = MainSlotCount + ExtraSlotCount

        public readonly List<InventorySlot> Items = new();
        public int Version;             // bumped per rebuild
    }
}
