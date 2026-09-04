#nullable enable

namespace Diggles.Runtime.Tcl
{
    using System.Text;

    // Substitution pass — runs over a "raw word" returned by the parser.
    // Handles three substitution forms inside a single word:
    // $var or $var(idx) → variable value
    // ${name} → variable value (allows special chars in name)
    // [cmd args] → command substitution
    // \X → backslash escape
    //
    // Brace-quoted words come from parser prefixed with a NUL marker —
    // we strip the marker and return the rest verbatim (no substitution).
    public static class TclSubst
    {
        public static string Substitute(TclInterpreter interp, string rawWord)
        {
            if (rawWord.Length > 0 && rawWord[0] == TclParser.BraceMarker)
                return rawWord.Substring(1);

            var sb = new StringBuilder(rawWord.Length);
            int i = 0;
            int n = rawWord.Length;
            while (i < n)
            {
                char c = rawWord[i];

                if (c == '\\' && i + 1 < n)
                {
                    char next = rawWord[i + 1];
                    sb.Append(BackslashChar(next));
                    i += 2;
                    continue;
                }

                if (c == '$')
                {
                    if (i + 1 >= n) { sb.Append('$'); i++; continue; }
                    char nxt = rawWord[i + 1];

                    if (nxt == '{')
                    {
                        // ${name}
                        int end = rawWord.IndexOf('}', i + 2);
                        if (end < 0) throw new TclException("missing '}' in variable name");
                        string name = rawWord.Substring(i + 2, end - i - 2);
                        sb.Append(interp.GetVar(name) ??
                                  (interp.TolerateMissingVars
                                      ? ""
                                      : throw new TclException($"can't read \"{name}\": no such variable")));
                        i = end + 1;
                        continue;
                    }
                    if (IsVarChar(nxt))
                    {
                        int j = i + 1;
                        while (j < n && IsVarChar(rawWord[j])) j++;
                        // array element: $name(...)
                        if (j < n && rawWord[j] == '(')
                        {
                            int parenEnd = FindMatchingParen(rawWord, j);
                            if (parenEnd < 0) throw new TclException("missing ')' in array index");
                            string vname = rawWord.Substring(i + 1, j - i - 1);
                            string rawIdx = rawWord.Substring(j + 1, parenEnd - j - 1);
                            string idx = Substitute(interp, rawIdx);
                            string full = $"{vname}({idx})";
                            sb.Append(interp.GetVar(full) ??
                                      throw new TclException($"can't read \"{full}\": no such variable"));
                            i = parenEnd + 1;
                            continue;
                        }
                        string name = rawWord.Substring(i + 1, j - i - 1);
                        sb.Append(interp.GetVar(name) ??
                                  (interp.TolerateMissingVars
                                      ? ""
                                      : throw new TclException($"can't read \"{name}\": no such variable")));
                        i = j;
                        continue;
                    }
                    sb.Append('$');
                    i++;
                    continue;
                }

                if (c == '[')
                {
                    // [cmd args] — find balanced close.
                    int end = FindMatchingBracket(rawWord, i);
                    if (end < 0) throw new TclException("missing ']'");
                    string body = rawWord.Substring(i + 1, end - i - 1);
                    sb.Append(interp.Eval(body));
                    i = end + 1;
                    continue;
                }

                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        static bool IsVarChar(char c)
            => char.IsLetterOrDigit(c) || c == '_';
        // Tcl's `::` namespace separator is not supported in the bare $var
        // form; ${ns::var} would be needed for that (Diggles never uses it).

        static int FindMatchingBracket(string s, int openIdx)
        {
            int depth = 1;
            int n = s.Length;
            for (int i = openIdx + 1; i < n; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < n) { i++; continue; }
                if (c == '[') depth++;
                else if (c == ']')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        static int FindMatchingParen(string s, int openIdx)
        {
            int depth = 1;
            int n = s.Length;
            for (int i = openIdx + 1; i < n; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < n) { i++; continue; }
                if (c == '(') depth++;
                else if (c == ')')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        static char BackslashChar(char c) => c switch
        {
            'n' => '\n',
            't' => '\t',
            'r' => '\r',
            'a' => '\a',
            'b' => '\b',
            'f' => '\f',
            'v' => '\v',
            '0' => '\0',
            '\\' => '\\',
            _ => c,
        };
    }
}
