#nullable enable

namespace Diggles.Runtime
{
    using System;

    public enum RightPanelMode
    {
        Gnomes,
        Buildings
    }

    // Static input/model seam shared by ClickToMoveService and the authored right-panel view.
    // Open is the effective legacy "gnome panel is visible" state. Keeping it derived makes
    // the old RequestToggle/Open call site coherent with the new mode and collapsed latches.
    public static class RightPanelModel
    {
        static RightPanelMode mode = RightPanelMode.Gnomes;
        static bool collapsed;

        public static RightPanelMode Mode => mode;
        public static bool Collapsed => collapsed;
        public static bool Open { get; private set; } = true;

        // Backward-compatible notification: the argument is the new effective Open state.
        public static event Action<bool>? ToggleRequested;
        public static event Action<RightPanelMode>? ModeChanged;
        public static event Action<bool>? CollapsedChanged;

        // Empty-RMB alternates the two list modes. Collapsed remains an independent latch.
        public static void RequestToggle()
        {
            SetMode(mode == RightPanelMode.Gnomes
                ? RightPanelMode.Buildings
                : RightPanelMode.Gnomes, notifyLegacyEvenWhenUnchanged: true);
        }

        public static void SetMode(RightPanelMode value)
        {
            SetMode(value, notifyLegacyEvenWhenUnchanged: false);
        }

        static void SetMode(RightPanelMode value, bool notifyLegacyEvenWhenUnchanged)
        {
            if (mode == value)
            {
                if (notifyLegacyEvenWhenUnchanged) ToggleRequested?.Invoke(Open);
                return;
            }

            bool wasOpen = Open;
            mode = value;
            Open = mode == RightPanelMode.Gnomes && !collapsed;
            ModeChanged?.Invoke(mode);
            if (wasOpen != Open || notifyLegacyEvenWhenUnchanged)
                ToggleRequested?.Invoke(Open);
        }

        public static void SetCollapsed(bool value)
        {
            if (collapsed == value) return;
            bool wasOpen = Open;
            collapsed = value;
            Open = mode == RightPanelMode.Gnomes && !collapsed;
            CollapsedChanged?.Invoke(collapsed);
            if (wasOpen != Open) ToggleRequested?.Invoke(Open);
        }

        // Compatibility mapping: opening selects the Gnomes mode and expands it; closing
        // collapses the current panel. Mode-aware callers should use SetMode/SetCollapsed.
        public static void SetOpen(bool open)
        {
            if (Open == open) return;

            bool wasOpen = Open;
            if (open)
            {
                bool modeChanged = mode != RightPanelMode.Gnomes;
                bool collapseChanged = collapsed;
                mode = RightPanelMode.Gnomes;
                collapsed = false;
                Open = true;
                if (modeChanged) ModeChanged?.Invoke(mode);
                if (collapseChanged) CollapsedChanged?.Invoke(false);
            }
            else
            {
                collapsed = true;
                Open = false;
                CollapsedChanged?.Invoke(true);
            }

            if (wasOpen != Open) ToggleRequested?.Invoke(Open);
        }

        // The legacy EditMode tests historically used ResetForTests as a closed baseline.
        // Buildings + expanded is the mode-aware equivalent: Open is false without retaining
        // a collapsed latch. Runtime initialization still starts in visible Gnomes mode.
        public static void ResetForTests()
        {
            mode = RightPanelMode.Buildings;
            collapsed = false;
            Open = false;
            ToggleRequested = null;
            ModeChanged = null;
            CollapsedChanged = null;
        }
    }
}
