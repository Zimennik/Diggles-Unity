#nullable enable

namespace Diggles.Editor.Importers
{
    using System.Collections.Generic;
    using System.IO;
    using System.Text.RegularExpressions;

    public sealed record CampaignTemplate(
        string Name, int WorldX, int WorldY,
        string TclPath, string? PmpPath, string? L2mPath,
        IReadOnlyList<CampaignTemplate> SubChunks);

    public sealed record CampaignWorld(
        int WorldWidth, int WorldHeight,
        IReadOnlyList<CampaignTemplate> Templates,
        IReadOnlyList<string> Diagnostics);

    public static class TclCampaignParser
    {
        private static readonly Regex MapCreateRx = new(
            @"map\s+create\s+(\d+)\s+(\d+)", RegexOptions.IgnoreCase);

        private static readonly Regex CallTemplateRx = new(
            @"set\s+temppos\s+\{\s*(-?\d+)\s+(-?\d+)\s*\}\s*;\s*call\s+(\S+\.tcl)",
            RegexOptions.IgnoreCase);

        private static readonly Regex SplitTemplateRx = new(
            @"split_template\s+\S+\s+(\S+\.pmp)\s+\d+\s+\d+\s*\{",
            RegexOptions.IgnoreCase);

        private static readonly Regex SplitChunkRx = new(
            @"\{\s*(\S+\.tcl)\s+(\S+\.pmp)\s+(-?\d+)\s+(-?\d+)\s*\}",
            RegexOptions.IgnoreCase);

        private static readonly Regex MapTemplateRx = new(
            @"map_template\s+(\S+\.pmp)",
            RegexOptions.IgnoreCase);

        private static readonly Regex MapSetLayer2Rx = new(
            @"map_setlayer2\s+\S+\s+\S+\s+(\S+\.l2m)",
            RegexOptions.IgnoreCase);

        public static CampaignWorld ParseCampaign(
            string campaignTclPath, string dataRoot,
            FirstLevelConfig? firstLevelConfig = null)
        {
            var diag = new List<string>();
            var templates = new List<CampaignTemplate>();
            var w = 512;
            var h = 640;

            if (!File.Exists(campaignTclPath))
            {
                diag.Add($"Campaign TCL not found: {campaignTclPath}");
                return new CampaignWorld(w, h, templates, diag);
            }

            var text = File.ReadAllText(campaignTclPath);

            var mapMatch = MapCreateRx.Match(text);
            if (mapMatch.Success)
            {
                w = int.Parse(mapMatch.Groups[1].Value);
                h = int.Parse(mapMatch.Groups[2].Value);
                diag.Add($"map create {w}x{h}");
            }

            foreach (Match m in CallTemplateRx.Matches(text))
            {
                var x = int.Parse(m.Groups[1].Value);
                var y = int.Parse(m.Groups[2].Value);
                var rel = m.Groups[3].Value.Replace('\\', '/');
                var name = Path.GetFileNameWithoutExtension(rel);
                var resolved = ResolveDataPath(rel, dataRoot);
                if (resolved == null)
                {
                    diag.Add($"  SKIP {name} @({x},{y}) — TCL not found: {rel}");
                    continue;
                }
                var tpl = ResolveTemplate(name, x, y, resolved, dataRoot, diag);
                if (tpl != null) templates.Add(tpl);
            }

            if (FirstLevelGenerator.TryFindFirstLevelCall(text, out var flX, out var flY))
            {
                var config = firstLevelConfig ?? FirstLevelConfig.Default;
                var firstLevelTemplates = FirstLevelGenerator.Generate(
                    flX, flY, config, dataRoot, diag);
                templates.AddRange(firstLevelTemplates);
            }

            return new CampaignWorld(w, h, templates, diag);
        }

        public static CampaignTemplate? ResolveTemplate(
            string name, int worldX, int worldY,
            string tclPath, string dataRoot, List<string> diag)
        {
            if (!File.Exists(tclPath))
            {
                diag.Add($"  SKIP {name} — TCL missing: {tclPath}");
                return null;
            }

            var text = File.ReadAllText(tclPath);
            var subs = new List<CampaignTemplate>();

            var splitMatch = SplitTemplateRx.Match(text);
            if (splitMatch.Success)
            {
                var body = ExtractBraceBlock(text, splitMatch.Index + splitMatch.Length - 1);
                if (body != null)
                {
                    foreach (Match c in SplitChunkRx.Matches(body))
                    {
                        var subTclRel = c.Groups[1].Value;
                        var subPmpRel = c.Groups[2].Value;
                        var subOx = int.Parse(c.Groups[3].Value);
                        var subOy = int.Parse(c.Groups[4].Value);
                        var subTclPath = Path.Combine(
                            dataRoot, "Templates", "tcl", "split", subTclRel);
                        var subPmpPath = Path.Combine(
                            dataRoot, "Templates", "pmp", "split", subPmpRel);
                        var subL2mPath = ResolveSplitL2m(subTclPath);
                        subs.Add(new CampaignTemplate(
                            Path.GetFileNameWithoutExtension(subTclRel),
                            worldX + subOx, worldY + subOy,
                            subTclPath,
                            File.Exists(subPmpPath) ? subPmpPath : null,
                            subL2mPath,
                            System.Array.Empty<CampaignTemplate>()));
                    }
                    diag.Add($"  {name} @({worldX},{worldY}): split_template " +
                             $"{subs.Count} chunks");
                    return new CampaignTemplate(name, worldX, worldY, tclPath,
                        null, null, subs);
                }
            }

            var pmpMatch = MapTemplateRx.Match(text);
            var l2mMatch = MapSetLayer2Rx.Match(text);
            string? pmpPath = null, l2mPath = null;
            if (pmpMatch.Success)
                pmpPath = ResolveDataPath(pmpMatch.Groups[1].Value, dataRoot);
            if (l2mMatch.Success)
                l2mPath = ResolveDataPath(l2mMatch.Groups[1].Value, dataRoot);
            diag.Add($"  {name} @({worldX},{worldY}): pmp={Short(pmpPath)} l2m={Short(l2mPath)}");
            return new CampaignTemplate(name, worldX, worldY, tclPath,
                pmpPath, l2mPath, subs);
        }

        private static string? ResolveSplitL2m(string subTclPath)
        {
            if (!File.Exists(subTclPath)) return null;
            var text = File.ReadAllText(subTclPath);
            var m = MapSetLayer2Rx.Match(text);
            if (!m.Success) return null;
            var rel = m.Groups[1].Value;
            var dir = Path.GetDirectoryName(subTclPath) ?? "";
            var splitDir = dir.Replace(
                Path.Combine("tcl", "split"),
                Path.Combine("l2m", "split"));
            var candidate = Path.Combine(splitDir, rel);
            return File.Exists(candidate) ? candidate : null;
        }

        private static string? ResolveDataPath(string relative, string dataRoot)
        {
            var rel = relative.Replace('\\', '/');
            var idx = rel.ToLowerInvariant().IndexOf("data/");
            var tail = idx >= 0 ? rel.Substring(idx + "data/".Length) : rel;
            var abs = Path.Combine(dataRoot, tail);
            if (File.Exists(abs)) return abs;
            var alt = Path.Combine(dataRoot, "Templates", Path.GetFileName(rel));
            return File.Exists(alt) ? alt : null;
        }

        private static string? ExtractBraceBlock(string text, int openBracePos)
        {
            if (openBracePos < 0 || openBracePos >= text.Length) return null;
            if (text[openBracePos] != '{') return null;
            var depth = 0;
            for (var i = openBracePos; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}')
                {
                    depth--;
                    if (depth == 0) return text.Substring(openBracePos + 1, i - openBracePos - 1);
                }
            }
            return null;
        }

        private static string Short(string? p) => p == null
            ? "<none>" : Path.GetFileName(p);
    }
}
