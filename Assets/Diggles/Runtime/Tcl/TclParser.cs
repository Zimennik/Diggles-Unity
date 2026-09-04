#nullable enable

namespace Diggles.Runtime.Tcl
{
    using System.Collections.Generic;
    using System.Text;

    // Tcl source parser. Splits a script into a stream of commands; each
    // command is a list of substituted words.
    //
    // This handles, in one pass:
    // * Comments (`#` at command start, until end of line)
    // * Whitespace + newlines as command terminators
    // * `;` as command terminator
    // * `\` line continuation
    // * Word forms:
    // bare: chars until whitespace/terminator
    // braced: {...} literal, no substitution, balanced
    // quoted: "..." with substitution, can contain newlines
    // * Substitutions within bare/quoted words:
    // $var or ${var} — variable
    // [cmd] — command substitution
    // \char — backslash escapes
    //
    // The parser does not substitute — it returns the command's raw words
    // and the interpreter substitutes at execution time. Braced words are
    // the exception: they come back with a leading NUL ('\0') marker so the
    // evaluator knows to strip it and skip substitution.
    public sealed class TclParser
    {
        public const char BraceMarker = '\0';

        readonly string src;
        int pos;
        int line = 1;

        public TclParser(string source)
        {
            // 1. Normalize CRLF / lone CR to LF: Diggles scripts use Windows
            // line endings, and a stray `\r` would otherwise be captured
            // by ReadBareWord as an extra word.
            // 2. Strip whole `//` comment lines (a Diggles convention). This
            // must happen on the raw source: brace words count `}` for
            // depth and Diggles comments often contain unbalanced braces
            // that would close a brace word too early.
            src = StripDoubleSlashLines(
                source.Replace("\r\n", "\n").Replace("\r", "\n"));
            pos = 0;
        }

        static string StripDoubleSlashLines(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            int i = 0;
            while (i < s.Length)
            {
                int eol = s.IndexOf('\n', i);
                if (eol < 0) eol = s.Length;
                int j = i;
                while (j < eol && (s[j] == ' ' || s[j] == '\t')) j++;
                bool isCommentLine = j + 1 < eol && s[j] == '/' && s[j + 1] == '/';
                if (!isCommentLine)
                    sb.Append(s, i, eol - i);
                if (eol < s.Length) sb.Append('\n');
                i = eol + 1;
            }
            return sb.ToString();
        }

        public int CurrentLine => line;

        bool AtEnd => pos >= src.Length;

        // Try to read the next command. Returns false if EOF.
        public bool NextCommand(out List<string> words)
        {
            words = new List<string>();
            while (true)
            {
                SkipInterCommandWhitespace();
                if (AtEnd) return words.Count > 0;

                char c = src[pos];

                // `#` starts a comment only at command-start position.
                // Diggles also uses the non-standard `//`, so accept it too.
                if (c == '#' && words.Count == 0)
                {
                    SkipToEndOfLine();
                    continue;
                }
                if (c == '/' && pos + 1 < src.Length && src[pos + 1] == '/' && words.Count == 0)
                {
                    SkipToEndOfLine();
                    continue;
                }
                if (c == ';' || c == '\n')
                {
                    if (c == '\n') line++;
                    pos++;
                    if (words.Count > 0) return true;
                    continue;
                }

                // Parse a word.
                var word = ReadWord();
                words.Add(word);

                // After a word, expect: whitespace (continue) OR;/\n (end).
                if (AtEnd) return true;
                c = src[pos];
                if (c == ';' || c == '\n')
                {
                    if (c == '\n') line++;
                    pos++;
                    return true;
                }
                if (!char.IsWhiteSpace(c))
                {
                    throw new TclException(
                        $"extra characters after word: '{c}'", line);
                }
                // skip intra-word whitespace (but not newlines — those are terminators)
                while (!AtEnd && (src[pos] == ' ' || src[pos] == '\t')) pos++;
                // Backslash-newline continuation: skip newline.
                if (!AtEnd && src[pos] == '\\' && pos + 1 < src.Length && src[pos + 1] == '\n')
                {
                    pos += 2;
                    line++;
                    while (!AtEnd && (src[pos] == ' ' || src[pos] == '\t')) pos++;
                }
            }
        }

        void SkipInterCommandWhitespace()
        {
            // Spaces and tabs only — newlines terminate commands.
            while (!AtEnd)
            {
                char c = src[pos];
                if (c == ' ' || c == '\t') { pos++; continue; }
                // Backslash-newline = continuation, treated as whitespace.
                if (c == '\\' && pos + 1 < src.Length && src[pos + 1] == '\n')
                {
                    pos += 2;
                    line++;
                    continue;
                }
                break;
            }
        }

        void SkipToEndOfLine()
        {
            while (!AtEnd && src[pos] != '\n')
            {
                // Handle backslash-newline inside comment: extend comment to next line
                if (src[pos] == '\\' && pos + 1 < src.Length && src[pos + 1] == '\n')
                {
                    pos += 2;
                    line++;
                    continue;
                }
                pos++;
            }
        }

        string ReadWord()
        {
            char c = src[pos];
            if (c == '{') return ReadBraceWord();
            if (c == '"') return ReadQuotedWord();
            return ReadBareWord();
        }

        // Brace word: literal content between matched balanced braces.
        // Begin with NUL marker so evaluator knows not to substitute.
        string ReadBraceWord()
        {
            int startLine = line;
            pos++; // skip opening brace
            var sb = new StringBuilder();
            sb.Append(BraceMarker);
            int depth = 1;
            while (!AtEnd)
            {
                char c = src[pos];
                if (c == '\\' && pos + 1 < src.Length)
                {
                    // backslash-X is kept verbatim inside braces (no escape processing)
                    sb.Append(c);
                    sb.Append(src[pos + 1]);
                    if (src[pos + 1] == '\n') line++;
                    pos += 2;
                    continue;
                }
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) { pos++; return sb.ToString(); }
                }
                else if (c == '\n') line++;
                sb.Append(c);
                pos++;
            }
            throw new TclException("missing close-brace", startLine);
        }

        // Quoted word: substitution applies. Returns raw text (with $ / [
        // still present); evaluator does substitution.
        string ReadQuotedWord()
        {
            int startLine = line;
            pos++; // skip opening quote
            var sb = new StringBuilder();
            while (!AtEnd)
            {
                char c = src[pos];
                if (c == '"') { pos++; return sb.ToString(); }
                if (c == '\\' && pos + 1 < src.Length)
                {
                    sb.Append(c);
                    sb.Append(src[pos + 1]);
                    if (src[pos + 1] == '\n') line++;
                    pos += 2;
                    continue;
                }
                if (c == '[')
                {
                    sb.Append(ReadBracketSubst());
                    continue;
                }
                if (c == '\n') line++;
                sb.Append(c);
                pos++;
            }
            throw new TclException("missing close-quote", startLine);
        }

        // Bare word: until whitespace/terminator. May contain `$var`,
        // `[cmd]`, `\x` escapes — handled at substitution time.
        string ReadBareWord()
        {
            var sb = new StringBuilder();
            while (!AtEnd)
            {
                char c = src[pos];
                if (c == ' ' || c == '\t' || c == '\n' || c == ';') break;
                if (c == '\\' && pos + 1 < src.Length)
                {
                    sb.Append(c);
                    sb.Append(src[pos + 1]);
                    if (src[pos + 1] == '\n') { line++; }
                    pos += 2;
                    continue;
                }
                if (c == '[')
                {
                    sb.Append(ReadBracketSubst());
                    continue;
                }
                sb.Append(c);
                pos++;
            }
            return sb.ToString();
        }

        // Read [cmd...] including the brackets so evaluator handles it.
        // Keeps balanced brackets nested inside.
        string ReadBracketSubst()
        {
            int startLine = line;
            var sb = new StringBuilder();
            sb.Append('[');
            pos++; // skip opening
            int depth = 1;
            while (!AtEnd)
            {
                char c = src[pos];
                if (c == '\\' && pos + 1 < src.Length)
                {
                    sb.Append(c);
                    sb.Append(src[pos + 1]);
                    if (src[pos + 1] == '\n') line++;
                    pos += 2;
                    continue;
                }
                if (c == '[') depth++;
                else if (c == ']')
                {
                    depth--;
                    if (depth == 0)
                    {
                        sb.Append(']');
                        pos++;
                        return sb.ToString();
                    }
                }
                else if (c == '{')
                {
                    // Need to skip balanced braces inside the command.
                    // Re-emit them as literal `{...}` so the eventual
                    // Eval(body) sees a proper brace word — never let the
                    // BraceMarker NUL leak into the bracket payload.
                    string inner = ReadBraceWord();
                    sb.Append('{');
                    if (inner.Length > 0 && inner[0] == BraceMarker)
                        sb.Append(inner, 1, inner.Length - 1);
                    else
                        sb.Append(inner);
                    sb.Append('}');
                    continue;
                }
                else if (c == '"')
                {
                    sb.Append('"');
                    int qStart = pos;
                    pos++;
                    int d2 = 1;
                    while (!AtEnd && d2 > 0)
                    {
                        char qc = src[pos];
                        if (qc == '\\' && pos + 1 < src.Length)
                        {
                            sb.Append(qc); sb.Append(src[pos + 1]);
                            if (src[pos + 1] == '\n') line++;
                            pos += 2; continue;
                        }
                        if (qc == '"') { sb.Append('"'); pos++; d2--; break; }
                        if (qc == '\n') line++;
                        sb.Append(qc); pos++;
                    }
                    continue;
                }
                else if (c == '\n') line++;
                sb.Append(c);
                pos++;
            }
            throw new TclException("missing close-bracket", startLine);
        }
    }
}
