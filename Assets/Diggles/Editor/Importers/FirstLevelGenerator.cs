#nullable enable

namespace Diggles.Editor.Importers
{
    using System.Collections.Generic;
    using System.IO;
    using System.Text.RegularExpressions;

    public sealed record FirstLevelConfig(
        int CaveLeftIndex,
        int CaveRightIndex,
        int YOffset,
        int ConnectionIndex,
        int VoodooIndex,
        char LeftTunnelVariant,
        char RightTunnelVariant)
    {
        public static FirstLevelConfig Default => new(
            CaveLeftIndex: 4,
            CaveRightIndex: 0,
            YOffset: 0,
            ConnectionIndex: 3,
            VoodooIndex: 0,
            LeftTunnelVariant: 'a',
            RightTunnelVariant: 'a');
    }

    public static class FirstLevelGenerator
    {
        private static readonly Regex FirstLevelCallRx = new(
            @"^\s*first_level\s+(-?\d+)\s+(-?\d+)",
            RegexOptions.Multiline);

        private static readonly (string Name, int XOffset, int YOffset)[] CaveList =
        {
            ("urw_hol_001_a", 20, 4),
            ("urw_hol_014_b", 20, 12),
            ("urw_hol_018_c", 28, 16),
            ("urw_hol_019_b", 24, 16),
            ("urw_hol_027_a", 16, 4),
        };

        private static readonly (string Prefix, int XStep, int YStep)[] TunnelList =
        {
            ("urw_gng_013_", 4, 0),
            ("urw_gng_002_", 8, 0),
            ("urw_gng_003_", 12, 0),
        };

        private static readonly (int InitialXOffset, int[] Tunnels)[] ConnectionList =
        {
            (0, new[] { 0, 1, 2 }),
            (0, new[] { 0, 2, 1 }),
            (0, new[] { 1, 0, 2 }),
            (4, new[] { 2, 0, 1 }),
            (4, new[] { 2, 1, 0 }),
            (4, new[] { 1, 2, 0 }),
        };

        // 22-entry voodoo template catalogue. Each entry = (prefix, allowed_letter_set).
        // Final template name = prefix + one random letter from the set.
        private static readonly (string Prefix, string Letters)[] VoodooList =
        {
            ("urw_gng_001_", "ab"),       // 0
            ("urw_gng_007_", "abcd"),     // 1
            ("urw_gng_008_", "abc"),      // 2
            ("urw_gng_009_", "abcd"),     // 3
            ("urw_gng_012_", "abcd"),     // 4
            ("urw_gng_019_", "abc"),      // 5
            ("urw_gng_022_", "abdeg"),    // 6
            ("urw_gng_023_", "abc"),      // 7
            ("urw_gng_026_", "abc"),      // 8
            ("urw_gng_028_", "bcd"),      // 9
            ("urw_gng_030_", "a"),        // 10
            ("urw_gng_032_", "ab"),       // 11
            ("urw_gng_034_", "acdfhk"),   // 12
            ("urw_gng_039_", "ab"),       // 13
            ("urw_gng_042_", "ab"),       // 14
            ("urw_gng_045_", "ab"),       // 15
            ("urw_unq_vodo_001_", "a"),   // 16 — Voodoo Master (unique)
            ("urw_gng_004_", "ab"),       // 17
            ("urw_gng_027_", "abc"),      // 18
            ("urw_gng_036_", "ab"),       // 19
            ("urw_gng_043_", "ab"),       // 20
            ("urw_gng_044_", "ab"),       // 21
        };

        // Each VoodooSlot = (vodolist_idx, x_off_from_vodo_x, y_off_from_vodo_y).
        // Each VoodooLayout is a full chain of slots placed relative to (vodo_x, vodo_y).

        private static readonly (int Idx, int Xo, int Yo)[][] VoconList1 =
        {
            new[] { (3,20,0), (8,8,12), (5,20,12), (6,24,12), (16,-24,12) },
            new[] { (1,20,0), (13,4,16), (7,4,28), (9,12,4), (16,-28,12) },
            new[] { (2,20,0), (5,20,8), (6,24,8), (15,8,8), (12,4,16), (6,16,24), (16,-28,12) },
            new[] { (2,20,0), (14,12,8), (4,8,12), (13,0,16), (7,0,28), (16,-32,12) },
            new[] { (2,20,0), (11,0,16), (15,8,8), (5,20,8), (6,24,8), (7,8,28), (16,-32,12) },
            new[] { (1,20,0), (6,16,20), (4,8,8), (14,12,4), (10,4,12), (16,-28,12) },
        };

        private static readonly (int Idx, int Xo, int Yo)[][] VoconList2 =
        {
            new[] { (18,20,0), (0,20,20), (10,24,12), (6,36,20), (16,-12,12) },
            new[] { (20,20,0), (21,32,4), (17,20,20), (10,36,12), (6,48,20), (16,-12,12) },
            new[] { (19,20,0), (1,24,12), (14,16,16), (6,32,8), (16,-16,12) },
        };

        /// <summary>
        /// Picks the right voodoo layout list for a given connection index,
        /// matching Campaign.tcl: conn&lt;2 → list2, conn&lt;4 → concat, else → list1.
        /// </summary>
        private static (int Idx, int Xo, int Yo)[] ResolveVoodoo(int connIdx, int voodooIdx)
        {
            (int, int, int)[][] layouts;
            if (connIdx < 2)
                layouts = VoconList2;
            else if (connIdx < 4)
            {
                layouts = new (int, int, int)[VoconList1.Length + VoconList2.Length][];
                for (int i = 0; i < VoconList1.Length; i++) layouts[i] = VoconList1[i];
                for (int i = 0; i < VoconList2.Length; i++) layouts[VoconList1.Length + i] = VoconList2[i];
            }
            else
                layouts = VoconList1;
            return layouts[((voodooIdx % layouts.Length) + layouts.Length) % layouts.Length];
        }

        public static bool TryFindFirstLevelCall(string campaignText, out int startX, out int startY)
        {
            var match = FirstLevelCallRx.Match(campaignText);
            if (!match.Success)
            {
                startX = 0;
                startY = 0;
                return false;
            }
            startX = int.Parse(match.Groups[1].Value);
            startY = int.Parse(match.Groups[2].Value);
            return true;
        }

        public static IReadOnlyList<CampaignTemplate> Generate(
            int startX, int startY, FirstLevelConfig config,
            string dataRoot, List<string> diagnostics)
        {
            var templates = new List<CampaignTemplate>();

            // Tcl: `incr std_x [lrem conn 0]` happens before cave placement.
            // The first element of the conn list is removed and added to std_x,
            // so it shifts the entire tunnel-cave layout right by that amount.
            var (initialXOff, connTunnels) = ConnectionList[config.ConnectionIndex];

            var stdX = startX + 36 + initialXOff;
            var stdY = startY + 40 + config.YOffset;

            var (leftName, leftXo, leftYo) = CaveList[config.CaveLeftIndex];
            AddTemplate(templates, leftName, stdX - leftXo, stdY - leftYo, dataRoot, diagnostics);
            AddTemplate(templates, $"urw_gng_021_{config.LeftTunnelVariant}",
                stdX - leftXo - 4, stdY, dataRoot, diagnostics);

            var (rightName, rightXo, rightYo) = CaveList[config.CaveRightIndex];
            AddTemplate(templates, rightName, stdX + 24, stdY - rightYo, dataRoot, diagnostics);
            AddTemplate(templates, $"urw_gng_022_{config.RightTunnelVariant}",
                stdX + rightXo + 24, stdY, dataRoot, diagnostics);

            // Tunnel loop: also captures vodo_x at the moment tunnel index 0 is placed.
            var x = stdX;
            int? vodoX = null;
            foreach (var tunnelIdx in connTunnels)
            {
                if (tunnelIdx == 0 && vodoX == null)
                {
                    // Match Tcl: `if {$itemp==0} { set vodo_x [expr {$x-20}] }` runs
                    // before placing the tunnel, so vodo_x = (current x) - 20.
                    vodoX = x - 20;
                }
                var (prefix, xStep, yStep) = TunnelList[tunnelIdx];
                AddTemplate(templates, prefix + 'a', x, stdY + yStep, dataRoot, diagnostics);
                x += xStep;
            }

            // Voodoo chain — fans out below the tunnel where tunnel index 0 was placed.
            // Skip if connTunnels never contained 0 (shouldn't happen in stock data, but safe).
            int vodoTemplates = 0;
            if (vodoX.HasValue)
            {
                int vodoY = stdY + 4;
                var layout = ResolveVoodoo(config.ConnectionIndex, config.VoodooIndex);
                foreach (var (vIdx, xo, yo) in layout)
                {
                    if (vIdx < 0 || vIdx >= VoodooList.Length) continue;
                    var (prefix, letters) = VoodooList[vIdx];
                    // Deterministic letter pick: first letter from the allowed set.
                    var name = prefix + (letters.Length > 0 ? letters[0] : 'a');
                    AddTemplate(templates, name, vodoX.Value + xo, vodoY + yo,
                        dataRoot, diagnostics);
                    vodoTemplates++;
                }
            }

            diagnostics.Add(
                $"first_level @({startX},{startY}): " +
                $"L={leftName} R={rightName} y_off={config.YOffset} " +
                $"conn={config.ConnectionIndex} vodo={config.VoodooIndex} " +
                $"(+{vodoTemplates} voodoo tpl) → {templates.Count} templates");

            return templates;
        }

        private static void AddTemplate(
            List<CampaignTemplate> templates, string name, int worldX, int worldY,
            string dataRoot, List<string> diagnostics)
        {
            var tclPath = Path.Combine(dataRoot, "Templates", "tcl", $"{name}.tcl");
            if (!File.Exists(tclPath))
                tclPath = Path.Combine(dataRoot, "Templates", $"{name}.tcl");

            if (!File.Exists(tclPath))
            {
                diagnostics.Add($"  first_level SKIP {name} @({worldX},{worldY}) — TCL missing");
                return;
            }

            var resolved = TclCampaignParser.ResolveTemplate(
                name, worldX, worldY, tclPath, dataRoot, diagnostics);
            if (resolved != null) templates.Add(resolved);
        }
    }
}
