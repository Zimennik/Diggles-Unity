#nullable enable

namespace Diggles.Editor.Importers
{
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text.RegularExpressions;
    using UnityEngine;

    public sealed record TclObject(string ClassName, Vector3 Position, Vector3 Rotation, int LineNumber);

    public static class TclObjectParser
    {
        private static readonly Regex NewObjectRegex = new(
            @"\[\s*new\s+(\w+)\s+""[^""]*""\s+""([^""]+)""\s+""([^""]+)""\s*\]",
            RegexOptions.Compiled);

        private static readonly Regex ExprNumberRegex = new(
            @"\[\s*expr\s+\{\s*(-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?)\s*(?:\+\s*\$(?:xoffset|yoffset))?\s*\}\s*\]",
            RegexOptions.Compiled);

        public static List<TclObject> Parse(string filePath)
        {
            var results = new List<TclObject>();
            if (!File.Exists(filePath)) return results;

            var text = File.ReadAllText(filePath);

            var lines = text.Split('\n');
            var lineStartOffsets = BuildLineOffsets(lines);

            foreach (Match m in NewObjectRegex.Matches(text))
            {
                var className = m.Groups[1].Value;
                var posRaw = m.Groups[2].Value;
                var rotRaw = m.Groups[3].Value;

                if (!TryParseTriple(posRaw, out var pos)) continue;
                if (!TryParseTriple(rotRaw, out var rot)) rot = Vector3.zero;

                var lineNo = LookupLine(lineStartOffsets, m.Index);
                results.Add(new TclObject(className, pos, rot, lineNo));
            }
            return results;
        }

        private static bool TryParseTriple(string raw, out Vector3 v)
        {
            v = Vector3.zero;
            var parts = SplitTopLevelTokens(raw);
            if (parts.Count != 3) return false;
            try
            {
                v = new Vector3(
                    ParseExprOrNumber(parts[0]),
                    ParseExprOrNumber(parts[1]),
                    ParseExprOrNumber(parts[2]));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static List<string> SplitTopLevelTokens(string raw)
        {
            var tokens = new List<string>();
            var depth = 0;
            var start = 0;
            for (var i = 0; i < raw.Length; i++)
            {
                var ch = raw[i];
                if (ch == '[' || ch == '{') depth++;
                else if (ch == ']' || ch == '}') depth--;
                else if (ch == ' ' && depth == 0)
                {
                    if (i > start) tokens.Add(raw[start..i]);
                    start = i + 1;
                }
            }
            if (start < raw.Length) tokens.Add(raw[start..]);
            tokens.RemoveAll(string.IsNullOrWhiteSpace);
            return tokens;
        }

        private static float ParseExprOrNumber(string token)
        {
            var m = ExprNumberRegex.Match(token);
            if (m.Success)
            {
                return float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            }
            return float.Parse(token, CultureInfo.InvariantCulture);
        }

        private static int[] BuildLineOffsets(string[] lines)
        {
            var offsets = new int[lines.Length];
            var cursor = 0;
            for (var i = 0; i < lines.Length; i++)
            {
                offsets[i] = cursor;
                cursor += lines[i].Length + 1;
            }
            return offsets;
        }

        private static int LookupLine(int[] offsets, int index)
        {
            var lo = 0;
            var hi = offsets.Length - 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                if (offsets[mid] <= index) lo = mid + 1;
                else hi = mid - 1;
            }
            return lo;
        }
    }
}
