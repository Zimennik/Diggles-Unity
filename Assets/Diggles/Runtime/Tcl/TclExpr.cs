#nullable enable

namespace Diggles.Runtime.Tcl
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;

    // Expression evaluator for `expr` and conditions (`if`, `while`, `for`).
    // Tokenizer + recursive-descent parser. Handles arithmetic, comparison,
    // logical operators, parens, numbers, strings, variables, [cmd] subst,
    // and a set of math functions.
    public static class TclExpr
    {
        // Chars that force a quoted-string token through TclSubst at consume time.
        static readonly char[] StringSubstTriggers = { '$', '[', '\\' };

        // Token-list cache: expressions are immutable body strings re-evaluated every logic tick
        // (1.17M `if` evals per 30 game-seconds on save_test — tokenizing each was the single
        // biggest interpreter cost after the script parse-cache). Tokenization is value-
        // independent (Var/Cmd/String tokens hold raw text; resolution happens in the parser),
        // so the list caches by the expression string. Static like the script parse-cache:
        // pure immutable data, main-thread only, capped against runaway unique expressions.
        static readonly Dictionary<string, List<Tok>> _tokenCache = new();
        const int TokenCacheCap = 32768;

        public static string Eval(TclInterpreter interp, string expr)
        {
            // Tokenize the raw expression: `$var` and `[cmd]` become value tokens
            // resolved during parsing, not pre-substituted into the text. Pre-
            // substitution lost empty-string operands (`$x==""` with x="" → `==""`,
            // a dangling operator) and multi-word string values.
            if (!_tokenCache.TryGetValue(expr, out var tokens))
            {
                tokens = Tokenize(expr);
                if (_tokenCache.Count >= TokenCacheCap) _tokenCache.Clear();
                _tokenCache[expr] = tokens;
            }
            var parser = new Parser(interp, tokens);
            object result = parser.ParseExpr();
            parser.Expect(TokKind.End);
            return ValueToString(result);
        }

        public static bool EvalBool(TclInterpreter interp, string expr)
        {
            try
            {
                string r = Eval(interp, expr);
                return ToBool(r);
            }
            catch (TclException) when (interp.TolerateExprErrors)
            {
                return false;
            }
        }

        // ----- value helpers -----

        public static string ValueToString(object v)
        {
            if (v is double d)
            {
                // integer-valued doubles printed as int
                if (d == Math.Truncate(d) && !double.IsInfinity(d) && Math.Abs(d) < 1e15)
                    return ((long)d).ToString(CultureInfo.InvariantCulture);
                return d.ToString("R", CultureInfo.InvariantCulture);
            }
            // Defensive float branch (see the "rand" note): a boxed float must never reach the
            // v.ToString fallback — that prints under CurrentCulture (',' decimals on uk-UA).
            if (v is float f) return ((double)f).ToString("R", CultureInfo.InvariantCulture);
            if (v is long l) return l.ToString(CultureInfo.InvariantCulture);
            if (v is int i) return i.ToString(CultureInfo.InvariantCulture);
            if (v is bool b) return b ? "1" : "0";
            return v?.ToString() ?? "";
        }

        public static bool ToBool(string s)
        {
            if (s == "1" || s == "true" || s == "yes" || s == "on") return true;
            if (s == "0" || s == "false" || s == "no" || s == "off" || s == "") return false;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                return d != 0;
            return false;
        }

        static double ToDouble(object v) => v switch
        {
            double d => d,
            float f => (double)f,
            long l => (double)l,
            int i => (double)i,
            bool b => b ? 1 : 0,
            string s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : 0,
            _ => 0,
        };

        static long ToLong(object v) => v switch
        {
            double d => (long)d,
            float f => (long)f,
            long l => l,
            int i => (long)i,
            bool b => b ? 1 : 0,
            string s => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r
                        : double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (long)d : 0,
            _ => 0,
        };

        static bool IsInteger(object v) => v is long || v is int ||
            (v is string s && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out _));

        // ----- tokens -----

        enum TokKind { End, Number, String, Ident, Op, LParen, RParen, Comma, Var, Cmd }

        readonly struct Tok
        {
            public readonly TokKind Kind;
            public readonly string Text;
            public Tok(TokKind k, string t) { Kind = k; Text = t; }
        }

        static List<Tok> Tokenize(string s)
        {
            var toks = new List<Tok>();
            int i = 0;
            int n = s.Length;
            while (i < n)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }

                // $var / ${var} → a value token (resolved at parse time).
                if (c == '$')
                {
                    i++;
                    string name;
                    if (i < n && s[i] == '{')
                    {
                        int j = ++i;
                        while (i < n && s[i] != '}') i++;
                        name = s.Substring(j, i - j);
                        if (i < n) i++;   // skip '}'
                    }
                    else
                    {
                        int j = i;
                        while (i < n && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                        name = s.Substring(j, i - j);
                    }
                    toks.Add(new Tok(TokKind.Var, name));
                    continue;
                }

                // [cmd] → command substitution (resolved at parse time). Balanced.
                if (c == '[')
                {
                    int depth = 0, j = i;
                    while (i < n)
                    {
                        if (s[i] == '[') depth++;
                        else if (s[i] == ']') { depth--; if (depth == 0) { i++; break; } }
                        i++;
                    }
                    string inner = s.Substring(j + 1, System.Math.Max(0, i - j - 2));
                    toks.Add(new Tok(TokKind.Cmd, inner));
                    continue;
                }
                if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(s[i + 1])))
                {
                    int j = i;
                    bool hasDot = false, hasExp = false;
                    while (j < n)
                    {
                        char cc = s[j];
                        if (char.IsDigit(cc)) { j++; continue; }
                        if (cc == '.' && !hasDot) { hasDot = true; j++; continue; }
                        if ((cc == 'e' || cc == 'E') && !hasExp)
                        {
                            hasExp = true; hasDot = true; j++;
                            if (j < n && (s[j] == '+' || s[j] == '-')) j++;
                            continue;
                        }
                        break;
                    }
                    toks.Add(new Tok(TokKind.Number, s.Substring(i, j - i)));
                    i = j; continue;
                }
                if (char.IsLetter(c) || c == '_')
                {
                    int j = i;
                    while (j < n && (char.IsLetterOrDigit(s[j]) || s[j] == '_')) j++;
                    toks.Add(new Tok(TokKind.Ident, s.Substring(i, j - i)));
                    i = j; continue;
                }
                if (c == '"')
                {
                    var sb = new StringBuilder();
                    i++;
                    while (i < n && s[i] != '"')
                    {
                        // keep backslash + char so TclSubst processes the escape; keep
                        // $ / [ verbatim so TclSubst substitutes vars/cmds inside quotes.
                        if (s[i] == '\\' && i + 1 < n) { sb.Append(s[i]).Append(s[i + 1]); i += 2; continue; }
                        sb.Append(s[i]); i++;
                    }
                    if (i < n) i++;
                    // Raw text — TclSubst runs when the parser consumes the token, so the token
                    // list is value-independent and cacheable by expression string.
                    toks.Add(new Tok(TokKind.String, sb.ToString()));
                    continue;
                }
                if (c == '(') { toks.Add(new Tok(TokKind.LParen, "(")); i++; continue; }
                if (c == ')') { toks.Add(new Tok(TokKind.RParen, ")")); i++; continue; }
                if (c == ',') { toks.Add(new Tok(TokKind.Comma, ",")); i++; continue; }

                // two-char ops first
                if (i + 1 < n)
                {
                    string two = s.Substring(i, 2);
                    if (two == "==" || two == "!=" || two == "<=" || two == ">=" ||
                        two == "&&" || two == "||" || two == "<<" || two == ">>")
                    {
                        toks.Add(new Tok(TokKind.Op, two));
                        i += 2; continue;
                    }
                }
                if ("+-*/%<>!&|^~?:".IndexOf(c) >= 0)
                {
                    toks.Add(new Tok(TokKind.Op, c.ToString()));
                    i++; continue;
                }
                throw new TclException($"unexpected char '{c}' in expression");
            }
            toks.Add(new Tok(TokKind.End, ""));
            return toks;
        }

        // ----- parser -----

        sealed class Parser
        {
            readonly TclInterpreter interp;
            readonly List<Tok> toks;
            int pos;

            public Parser(TclInterpreter interp, List<Tok> toks)
            {
                this.interp = interp;
                this.toks = toks;
            }

            Tok Peek() => toks[pos];
            Tok Next() => toks[pos++];

            public void Expect(TokKind k)
            {
                if (Peek().Kind != k) throw new TclException($"expected {k}, got {Peek().Kind} '{Peek().Text}'");
                pos++;
            }

            // Tcl operator precedence (low → high):
            // expr = ternary
            // ternary = orExpr ('?' expr ':' expr)?
            // orExpr = andExpr ('||' andExpr)*
            // andExpr = bitOr ('&&' bitOr)*
            // bitOr = bitXor ('|' bitXor)*
            // bitXor = bitAnd ('^' bitAnd)*
            // bitAnd = equalExpr ('&' equalExpr)*
            // equalExpr = relExpr (('==' | '!=') relExpr)*
            // relExpr = shift (('<'|'<='|'>'|'>=') shift)*
            // shift = addExpr (('<<'|'>>') addExpr)*
            // addExpr = mulExpr (('+'|'-') mulExpr)*
            // mulExpr = unary (('*'|'/'|'%') unary)*
            // unary = ('+'|'-'|'!'|'~')? primary
            // primary = number | string | ident '(' args ')' | '(' expr ')'

            public object ParseExpr() => ParseTernary();

            object ParseTernary()
            {
                var c = ParseOr();
                if (Peek().Kind == TokKind.Op && Peek().Text == "?")
                {
                    Next();
                    var t = ParseExpr();
                    if (!(Peek().Kind == TokKind.Op && Peek().Text == ":"))
                        throw new TclException("expected ':' in ternary");
                    Next();
                    var f = ParseExpr();
                    return ToBool(ValueToString(c)) ? t : f;
                }
                return c;
            }

            object ParseOr()
            {
                var a = ParseAnd();
                while (Peek().Kind == TokKind.Op && Peek().Text == "||")
                {
                    Next();
                    var b = ParseAnd();
                    a = (ToBool(ValueToString(a)) || ToBool(ValueToString(b))) ? 1L : 0L;
                }
                return a;
            }

            object ParseAnd()
            {
                var a = ParseBitOr();
                while (Peek().Kind == TokKind.Op && Peek().Text == "&&")
                {
                    Next();
                    var b = ParseBitOr();
                    a = (ToBool(ValueToString(a)) && ToBool(ValueToString(b))) ? 1L : 0L;
                }
                return a;
            }

            // Bitwise levels (Tcl precedence: | below ^ below &, all above == below &&).
            // Integer-only — operands coerced to long. Without these, `expr $i<<1`
            // (bit-iteration loops in the sparetime code) silently failed → infinite loop.
            object ParseBitOr()
            {
                var a = ParseBitXor();
                while (Peek().Kind == TokKind.Op && Peek().Text == "|")
                {
                    Next();
                    var b = ParseBitXor();
                    a = ToLong(a) | ToLong(b);
                }
                return a;
            }

            object ParseBitXor()
            {
                var a = ParseBitAnd();
                while (Peek().Kind == TokKind.Op && Peek().Text == "^")
                {
                    Next();
                    var b = ParseBitAnd();
                    a = ToLong(a) ^ ToLong(b);
                }
                return a;
            }

            object ParseBitAnd()
            {
                var a = ParseEq();
                while (Peek().Kind == TokKind.Op && Peek().Text == "&")
                {
                    Next();
                    var b = ParseEq();
                    a = ToLong(a) & ToLong(b);
                }
                return a;
            }

            object ParseEq()
            {
                var a = ParseRel();
                while (Peek().Kind == TokKind.Op && (Peek().Text == "==" || Peek().Text == "!="))
                {
                    string op = Next().Text;
                    var b = ParseRel();
                    bool eq;
                    if (IsNumeric(a) && IsNumeric(b))
                        eq = ToDouble(a) == ToDouble(b);
                    else
                        eq = ValueToString(a) == ValueToString(b);
                    a = (op == "==" ? eq : !eq) ? 1L : 0L;
                }
                return a;
            }

            object ParseRel()
            {
                var a = ParseShift();
                while (Peek().Kind == TokKind.Op &&
                       (Peek().Text == "<" || Peek().Text == "<=" || Peek().Text == ">" || Peek().Text == ">="))
                {
                    string op = Next().Text;
                    var b = ParseShift();
                    bool r;
                    if (IsNumeric(a) && IsNumeric(b))
                    {
                        double da = ToDouble(a), db = ToDouble(b);
                        r = op switch
                        {
                            "<" => da < db, "<=" => da <= db,
                            ">" => da > db, ">=" => da >= db,
                            _ => false,
                        };
                    }
                    else
                    {
                        // Real Tcl: ordering operators compare as strings when either operand
                        // is non-numeric (Tcl `expr` man page; ParseEq above already does this
                        // for ==/!=). The old ToDouble-garbage→0 reading diverged with real
                        // consequences: parse_actors' `$item <= $nrofactors` filter
                        // (sequencer.tcl:828-831) must reject a garbage actor word — e.g.
                        // st008_end_2.tcl:42 `sq_actor express good_awake` passes the feeling
                        // as the who; real Tcl string-compares "good_awake" > "4" → filtered →
                        // the line no-ops, exactly like the shipped game. Reading it as 0<=4
                        // let it through to `lindex $actors good_awake` → TclException — which
                        // the skip drain's `while {[parse_next 1]}` condition tolerance turned
                        // into a silent early loop exit: shut_sequencer ran before the trailing
                        // `+sq_object delete all`, leaking every cutscene summon on skip.
                        int c = string.CompareOrdinal(ValueToString(a), ValueToString(b));
                        r = op switch
                        {
                            "<" => c < 0, "<=" => c <= 0,
                            ">" => c > 0, ">=" => c >= 0,
                            _ => false,
                        };
                    }
                    a = r ? 1L : 0L;
                }
                return a;
            }

            object ParseShift()
            {
                var a = ParseAdd();
                while (Peek().Kind == TokKind.Op && (Peek().Text == "<<" || Peek().Text == ">>"))
                {
                    string op = Next().Text;
                    var b = ParseAdd();
                    long la = ToLong(a), lb = ToLong(b);
                    a = op == "<<" ? la << (int)lb : la >> (int)lb;
                }
                return a;
            }

            object ParseAdd()
            {
                var a = ParseMul();
                while (Peek().Kind == TokKind.Op && (Peek().Text == "+" || Peek().Text == "-"))
                {
                    string op = Next().Text;
                    var b = ParseMul();
                    a = ApplyArith(a, b, op);
                }
                return a;
            }

            object ParseMul()
            {
                var a = ParseUnary();
                while (Peek().Kind == TokKind.Op &&
                       (Peek().Text == "*" || Peek().Text == "/" || Peek().Text == "%"))
                {
                    string op = Next().Text;
                    var b = ParseUnary();
                    a = ApplyArith(a, b, op);
                }
                return a;
            }

            object ParseUnary()
            {
                if (Peek().Kind == TokKind.Op &&
                    (Peek().Text == "-" || Peek().Text == "+" || Peek().Text == "!" || Peek().Text == "~"))
                {
                    string op = Next().Text;
                    var v = ParseUnary();
                    if (op == "!") return ToBool(ValueToString(v)) ? 0L : 1L;
                    if (op == "~") return ~ToLong(v);
                    if (op == "-")
                    {
                        if (IsInteger(v)) return -ToLong(v);
                        return -ToDouble(v);
                    }
                    return v;
                }
                return ParsePrimary();
            }

            object ParsePrimary()
            {
                var t = Peek();
                if (t.Kind == TokKind.Var)
                {
                    Next();
                    return interp.GetVar(t.Text) ?? "";   // empty/undefined → "" operand
                }
                if (t.Kind == TokKind.Cmd)
                {
                    Next();
                    return interp.Eval(t.Text);
                }
                if (t.Kind == TokKind.Number)
                {
                    Next();
                    if (t.Text.Contains('.') || t.Text.Contains('e') || t.Text.Contains('E'))
                        return double.Parse(t.Text, NumberStyles.Float, CultureInfo.InvariantCulture);
                    return long.Parse(t.Text, NumberStyles.Integer, CultureInfo.InvariantCulture);
                }
                if (t.Kind == TokKind.String)
                {
                    Next();
                    // Escapes/$vars/[cmds] inside quotes substitute at consume time (the token
                    // list is cached raw); a plain literal skips the substitution entirely.
                    return t.Text.IndexOfAny(StringSubstTriggers) >= 0
                        ? TclSubst.Substitute(interp, t.Text)
                        : t.Text;
                }
                if (t.Kind == TokKind.LParen)
                {
                    Next();
                    var v = ParseExpr();
                    Expect(TokKind.RParen);
                    return v;
                }
                if (t.Kind == TokKind.Ident)
                {
                    Next();
                    if (Peek().Kind == TokKind.LParen)
                    {
                        // function call
                        Next();
                        var args = new List<object>();
                        if (Peek().Kind != TokKind.RParen)
                        {
                            args.Add(ParseExpr());
                            while (Peek().Kind == TokKind.Comma)
                            {
                                Next();
                                args.Add(ParseExpr());
                            }
                        }
                        Expect(TokKind.RParen);
                        return CallFunc(t.Text, args);
                    }
                    // bare identifier — boolean literal or string
                    return t.Text switch
                    {
                        "true" or "yes" or "on" => 1L,
                        "false" or "no" or "off" => 0L,
                        _ => (object)t.Text,
                    };
                }
                throw new TclException($"unexpected '{t.Text}' in expression");
            }

            static bool IsNumeric(object o)
            {
                if (o is double || o is float || o is long || o is int) return true;
                if (o is string s)
                    return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
                return false;
            }

            static object ApplyArith(object a, object b, string op)
            {
                // Tcl 8.3: two integer operands stay integer for all of + - * / %, with floored
                // division/modulo (-7/2 = -4; % takes the divisor's sign). Excluding "/" here made
                // 37/10+1 = 4.7 (double), which `incr` then rejected — regression: the
                // sparetime_talk work chain broke with "expected integer but got 4.7"
                // (z_spare_talk.tcl:574).
                bool intish = IsInteger(a) && IsInteger(b);
                if (intish)
                {
                    long la = ToLong(a), lb = ToLong(b);
                    return op switch
                    {
                        "+" => la + lb, "-" => la - lb, "*" => la * lb,
                        "/" => lb != 0 ? FloorDiv(la, lb) : throw new TclException("divide by zero"),
                        "%" => lb != 0 ? FloorMod(la, lb) : throw new TclException("modulo by zero"),
                        _ => throw new TclException($"bad op {op}"),
                    };
                }
                double da = ToDouble(a), db = ToDouble(b);
                return op switch
                {
                    "+" => da + db, "-" => da - db, "*" => da * db,
                    "/" => db != 0 ? da / db : throw new TclException("divide by zero"),
                    "%" => db != 0 ? da % db : throw new TclException("modulo by zero"),
                    _ => throw new TclException($"bad op {op}"),
                };
            }

            // Tcl's integer division rounds toward −∞ (C#'s toward 0); the remainder carries the
            // divisor's sign so that q*b + r == a always holds with the floored quotient.
            static long FloorDiv(long a, long b)
            {
                long q = a / b;
                if (a % b != 0 && (a ^ b) < 0) q--;
                return q;
            }

            static long FloorMod(long a, long b)
            {
                long r = a % b;
                if (r != 0 && (r ^ b) < 0) r += b;
                return r;
            }

            static object CallFunc(string name, List<object> args)
            {
                double D(int i) => ToDouble(args[i]);
                return name switch
                {
                    "abs" => args[0] is long ? (object)Math.Abs(ToLong(args[0])) : Math.Abs(D(0)),
                    "double" => D(0),
                    "int" => (long)D(0),
                    "round" => (long)Math.Round(D(0)),
                    "ceil" => Math.Ceiling(D(0)),
                    "floor" => Math.Floor(D(0)),
                    "sqrt" => Math.Sqrt(D(0)),
                    "sin" => Math.Sin(D(0)),
                    "cos" => Math.Cos(D(0)),
                    "tan" => Math.Tan(D(0)),
                    "asin" => Math.Asin(D(0)),
                    "acos" => Math.Acos(D(0)),
                    "atan" => Math.Atan(D(0)),
                    "atan2" => Math.Atan2(D(0), D(1)),
                    "exp" => Math.Exp(D(0)),
                    "log" => Math.Log(D(0)),
                    "pow" => Math.Pow(D(0), D(1)),
                    "min" => Math.Min(D(0), D(1)),
                    "max" => Math.Max(D(0), D(1)),
                    // Must box as double: regression — a boxed float fell through IsNumeric (no
                    // float case) into the string relational branch, where CurrentCulture printing
                    // (uk-UA: ',' decimal) made `rand < 0.01` compare "0,929…" < "0.01" ordinally
                    // — ',' (0x2C) < '.' (0x2E) ⇒ always true. Every rand gate in the shipped
                    // scripts (57 sites in 25 files) fired unconditionally — a stone from every dug
                    // block, etc.
                    "rand" => (double)UnityEngine.Random.value,
                    _ => throw new TclException($"unknown math function \"{name}\""),
                };
            }
        }
    }
}
