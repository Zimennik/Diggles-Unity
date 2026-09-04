#nullable enable

namespace Diggles.Runtime.Tcl
{
    using System.Collections.Generic;
    using System.Text;

    // Tcl lists are strings: items separated by whitespace, items
    // containing whitespace or special chars are brace-grouped.
    // The format helpers reverse this — given a list of strings,
    // produce a Tcl-list-formatted string.
    public static class TclList
    {
        // Split a Tcl list string into elements. Handles brace-grouped
        // and quote-grouped elements (matching Tcl 8.x semantics).
        public static List<string> Parse(string s)
        {
            var result = new List<string>();
            int i = 0;
            int n = s.Length;
            while (i < n)
            {
                // skip whitespace
                while (i < n && IsWs(s[i])) i++;
                if (i >= n) break;

                char c = s[i];
                if (c == '{')
                {
                    i++;
                    var sb = new StringBuilder();
                    int depth = 1;
                    while (i < n && depth > 0)
                    {
                        char cc = s[i];
                        if (cc == '\\' && i + 1 < n)
                        {
                            sb.Append(cc); sb.Append(s[i + 1]);
                            i += 2; continue;
                        }
                        if (cc == '{') { depth++; sb.Append(cc); i++; continue; }
                        if (cc == '}')
                        {
                            depth--;
                            if (depth == 0) { i++; break; }
                            sb.Append(cc); i++; continue;
                        }
                        sb.Append(cc); i++;
                    }
                    result.Add(sb.ToString());
                }
                else if (c == '"')
                {
                    i++;
                    var sb = new StringBuilder();
                    while (i < n && s[i] != '"')
                    {
                        if (s[i] == '\\' && i + 1 < n)
                        {
                            sb.Append(BackslashChar(s[i + 1]));
                            i += 2; continue;
                        }
                        sb.Append(s[i]); i++;
                    }
                    if (i < n) i++; // skip closing "
                    result.Add(sb.ToString());
                }
                else
                {
                    var sb = new StringBuilder();
                    while (i < n && !IsWs(s[i]))
                    {
                        if (s[i] == '\\' && i + 1 < n)
                        {
                            sb.Append(BackslashChar(s[i + 1]));
                            i += 2; continue;
                        }
                        sb.Append(s[i]); i++;
                    }
                    result.Add(sb.ToString());
                }
            }
            return result;
        }

        // Build a Tcl list string from elements. Each element gets
        // brace-wrapped if it contains special chars or is empty.
        public static string Format(IEnumerable<string> items)
        {
            var sb = new StringBuilder();
            bool first = true;
            foreach (var it in items)
            {
                if (!first) sb.Append(' ');
                first = false;
                sb.Append(FormatElement(it));
            }
            return sb.ToString();
        }

        public static string FormatElement(string s)
        {
            if (s.Length == 0) return "{}";
            bool needBrace = false;
            foreach (char c in s)
            {
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r' ||
                    c == '{' || c == '}' || c == '[' || c == ']' ||
                    c == '$' || c == '"' || c == ';' || c == '\\')
                {
                    needBrace = true; break;
                }
            }
            return needBrace ? "{" + s + "}" : s;
        }

        static bool IsWs(char c) => c == ' ' || c == '\t' || c == '\n' || c == '\r';

        static char BackslashChar(char c) => c switch
        {
            'n' => '\n',
            't' => '\t',
            'r' => '\r',
            '\\' => '\\',
            '"' => '"',
            '{' => '{',
            '}' => '}',
            '[' => '[',
            ']' => ']',
            '$' => '$',
            ';' => ';',
            ' ' => ' ',
            _ => c,
        };
    }
}
