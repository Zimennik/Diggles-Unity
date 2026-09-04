#nullable enable

namespace Diggles.Runtime.Tcl
{
    using System;
    using System.Collections.Generic;
    using System.Text;

    public delegate string TclCommand(TclInterpreter interp, string[] args);

    public sealed class TclProc
    {
        public string Name = "";
        public string[] ParamNames = Array.Empty<string>();
        public string?[] ParamDefaults = Array.Empty<string?>();
        public bool HasArgsTail;   // last param is "args" (variadic)
        public string Body = "";
    }

    public sealed class TclScope
    {
        public Dictionary<string, string> Vars = new();
        // upvar alias: localName → (targetScope, targetName).
        // Also used by `global x` (alias onto the global namespace).
        // Lookups and writes redirect through this.
        public Dictionary<string, (TclScope scope, string name)> Aliases = new();
        public int Level;
    }

    // Main interpreter. Stateful — variables, commands, procs persist
    // between Eval calls. Use one instance per game session.
    public sealed class TclInterpreter
    {
        readonly Dictionary<string, TclCommand> commands = new();
        readonly Dictionary<string, TclProc> procs = new();
        readonly List<TclScope> scopes = new() { new TclScope { Level = 0 } };  // [0] = global

        public TclScope CurrentScope => scopes[^1];
        // The "global" namespace `global`/SetGlobal/read-fallback bind to. Normally
        // scopes[0] (the shared interpreter global), but while an object body runs we
        // override it with that entity's scope — the original gives each object its
        // own interpreter whose "globals" are its per-instance variables, so
        // `global x` inside a method/obj_init/state/event = the object's own `x`.
        public TclScope GlobalScope => ActiveGlobalOverride ?? scopes[0];
        public TclScope? ActiveGlobalOverride;
        public List<TclScope> Scopes => scopes;  // for upvar/uplevel

        // Stub-mode: when a command isn't found, call this delegate
        // instead of throwing. Returns the command result. Useful for
        // running scripts whose engine commands aren't implemented yet.
        public TclCommand? UnknownCommandHandler;

        // When set, malformed expressions in if / while / for / expr
        // conditions return false instead of throwing. Useful for stub
        // mode where missing engine commands leave conds half-formed
        // (e.g. `==knopf` because `string range "" 9 13` returned "").
        public bool TolerateExprErrors;

        // When set, reading an undefined variable yields "" instead of
        // throwing "no such variable". Needed for partial-data smoke
        // runs where scripts compose values from globals set elsewhere.
        public bool TolerateMissingVars;

        public TclInterpreter()
        {
            TclBuiltins.RegisterAll(this);
        }

        // ----- public API -----

        public void RegisterCommand(string name, TclCommand cmd)
        {
            commands[name] = cmd;
        }

        // Engine hook: capture the class context a proc is defined in (per-class proc table),
        // so per-object proc isolation can be restored despite the shared global table.
        public System.Action<TclProc>? OnProcRegistered;
        // Engine hook: resolve a proc from the ACTIVE object's class first (checked before the
        // shared global proc table in DispatchCommand). Null result → fall through to global.
        public System.Func<string, TclProc?>? ResolveClassProc;

        public void RegisterProc(TclProc proc)
        {
            procs[proc.Name] = proc;
            OnProcRegistered?.Invoke(proc);
        }

        // Recursion guard: nested Eval (proc bodies, `call`/`source`, [cmd] subst) uses the
        // real C# stack. A circular `call` or runaway recursive proc would blow the stack —
        // an uncatchable crash (kills Unity) — so cap the nesting and throw a normal
        // TclException instead, which callers' `catch` can isolate. 100 is well above any
        // legitimate script nesting (class load nests <~20, action/idle chains <10) yet
        // safely under the C# stack limit. Tunable if a legitimate case needs more.
        public int MaxEvalDepth = 100;
        int _evalDepth;

        // Tokenized-script cache. Tcl bodies (proc/state/event/loop bodies) are immutable strings
        // re-evaluated every logic tick; re-tokenizing them each Eval was the dominant sim allocation
        // (TclParser's ctor copies the whole body several times — CRLF-normalize + //-strip — plus a
        // List per command and a StringBuilder per word). Parse once (via the same TclParser, so the
        // tokens are byte-identical) and memoize by the script string; Eval then only substitutes.
        // Value-keyed so identical scripts dedupe; capped so unbounded unique-script generation can't
        // grow it without bound.
        // Static on purpose: every in-game load boots a fresh candidate interpreter, and a
        // per-instance cache re-tokenized every class-definition script on every reload. The entries
        // are pure immutable data (string→ParsedWord[] with no interpreter state), so sharing across
        // instances is safe; identical scripts are identical tokens. Main-thread only, like every
        // Eval caller.
        static readonly Dictionary<string, ParsedCommand[]> _parseCache = new();
        const int ParseCacheCap = 32768;

        public string Eval(string script)
        {
            if (_evalDepth >= MaxEvalDepth)
                throw new TclException(
                    $"eval nesting too deep ({MaxEvalDepth}) — likely infinite recursion / circular call");
            _evalDepth++;
            try
            {
                var commands = GetParsed(script);
                string result = "";
                for (int c = 0; c < commands.Length; c++)
                {
                    var words = commands[c].Words;
                    int n = words.Length;
                    if (n == 0) continue;
                    var args = new string[n];
                    for (int i = 0; i < n; i++)
                    {
                        var w = words[i];
                        args[i] = w.Dynamic ? TclSubst.Substitute(this, w.Value) : w.Value;
                    }
                    result = EvalArgs(args);
                }
                return result;
            }
            finally { _evalDepth--; }
        }

        // Evaluate a script with an isolated top-level namespace. The original creates one Tcl
        // interpreter per AI owner; sharing commands/procs while swapping this scope reproduces
        // those independent globals without duplicating the world-facing command registry.
        public string EvalInScope(string script, TclScope globalScope)
        {
            if (ReferenceEquals(CurrentScope, globalScope) &&
                ReferenceEquals(ActiveGlobalOverride, globalScope))
                return Eval(script);

            var previousGlobal = ActiveGlobalOverride;
            int previousLevel = globalScope.Level;
            globalScope.Level = scopes.Count;
            scopes.Add(globalScope);
            ActiveGlobalOverride = globalScope;
            try
            {
                return Eval(script);
            }
            finally
            {
                scopes.RemoveAt(scopes.Count - 1);
                globalScope.Level = previousLevel;
                ActiveGlobalOverride = previousGlobal;
            }
        }

        // Optional per-command trace hook (cmdName, args, result-or-error). When set,
        // invoked after every command (native / proc / unknown) executes — including
        // nested [cmd] substitutions, since those re-enter here. For debugging: lets a
        // host record exactly what a script does, with args and return values.
        public System.Action<string, string[], string>? CommandTrace;

        // Evaluate a single command given as a list of fully-substituted words. Kept for the engine's
        // eval/uplevel-style builtins that build a word list directly; Eval itself now goes through
        // EvalArgs to avoid the extra List→array copy.
        public string EvalWords(List<string> words)
        {
            if (words.Count == 0) return "";
            return EvalArgs(words.ToArray());
        }

        // Per-command time profiling. Inclusive time per
        // command name: control-flow commands (if/while/proc) include their children; engine
        // leaf commands are the actionable rows. The diag test flips the flag and prints a top-N.
        public static bool ProfileCommands;
        public static readonly System.Collections.Generic.Dictionary<string, double> ProfileCommandMs = new();
        public static readonly System.Collections.Generic.Dictionary<string, long> ProfileCommandCalls = new();

        // Evaluate one command given its fully-substituted word array (args[0] = command name).
        string EvalArgs(string[] args)
        {
            if (args.Length == 0) return "";
            string cmdName = args[0];

            if (ProfileCommands && CommandTrace == null)
            {
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                try { return DispatchCommand(cmdName, args); }
                finally
                {
                    double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - start)
 * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    ProfileCommandMs.TryGetValue(cmdName, out double total);
                    ProfileCommandMs[cmdName] = total + ms;
                    ProfileCommandCalls.TryGetValue(cmdName, out long calls);
                    ProfileCommandCalls[cmdName] = calls + 1;
                }
            }

            if (CommandTrace == null) return DispatchCommand(cmdName, args);   // fast path

            string res;
            try { res = DispatchCommand(cmdName, args); }
            catch (TclReturnException) { throw; }    // normal control flow — not an error
            catch (TclBreakException) { throw; }      // loop/switch break — control flow, not an error
            catch (TclContinueException) { throw; }   // loop continue — control flow, not an error
            catch (Exception e) { CommandTrace(cmdName, args, "!!ERR " + e.Message); throw; }
            CommandTrace(cmdName, args, res);
            return res;
        }

        // ---- parse cache internals -------------------------------------------------------------

        // One tokenized command: its words pre-classified as literal (final value known) or dynamic
        // (needs per-eval substitution). See GetParsed / Classify.
        sealed class ParsedCommand { public ParsedWord[] Words = System.Array.Empty<ParsedWord>(); }

        readonly struct ParsedWord
        {
            public readonly string Value;   // literal → the final value; dynamic → the raw word
            public readonly bool Dynamic;   // true → TclSubst.Substitute(interp, Value) at eval time
            public ParsedWord(string value, bool dynamic) { Value = value; Dynamic = dynamic; }
        }

        // Chars that make a word need substitution: $ (var), [ (command), \ (escape). A word with none
        // of these (and not a brace-literal) substitutes to itself — TclSubst.Substitute is a no-op on
        // it — so it's stored as a literal and skips substitution (and its allocation) entirely.
        static readonly char[] SubstTriggers = { '$', '[', '\\' };

        // Tokenize + classify a script into commands, memoized by the script string. The tokenizer is
        // the unmodified TclParser, so the produced words are identical to the old per-eval parse —
        // this only removes the RE-parse. Empty commands are dropped (as the old Eval loop did).
        ParsedCommand[] GetParsed(string script)
        {
            if (_parseCache.TryGetValue(script, out var cached)) return cached;
            var cmds = new List<ParsedCommand>();
            var parser = new TclParser(script);
            while (parser.NextCommand(out var rawWords))
            {
                if (rawWords.Count == 0) continue;
                var words = new ParsedWord[rawWords.Count];
                for (int i = 0; i < rawWords.Count; i++) words[i] = Classify(rawWords[i]);
                cmds.Add(new ParsedCommand { Words = words });
            }
            var arr = cmds.ToArray();
            if (_parseCache.Count >= ParseCacheCap) _parseCache.Clear();
            _parseCache[script] = arr;
            return arr;
        }

        // Pre-resolve a raw word into its eval-time form (mirrors TclSubst exactly):
        // • brace word (leading NUL marker) → the stripped content, never substituted;
        // • no $/[/\ → substitutes to itself → stored as the literal;
        // • otherwise → kept raw, substituted per eval (value depends on variables/commands).
        static ParsedWord Classify(string raw)
        {
            if (raw.Length > 0 && raw[0] == TclParser.BraceMarker)
                return new ParsedWord(raw.Substring(1), false);
            if (raw.IndexOfAny(SubstTriggers) < 0)
                return new ParsedWord(raw, false);
            return new ParsedWord(raw, true);
        }

        string DispatchCommand(string cmdName, string[] args)
        {
            if (commands.TryGetValue(cmdName, out var native)) return native(this, args);
            // Active object's class procs win over the shared global table (per-object proc
            // isolation — e.g. the sequencer's `wait_time {}` vs a dwarf's `wait_time {seconds}`).
            if (ResolveClassProc != null)
            {
                var cp = ResolveClassProc(cmdName);
                if (cp != null) return InvokeProc(cp, args);
            }
            if (procs.TryGetValue(cmdName, out var proc)) return InvokeProc(proc, args);
            if (UnknownCommandHandler != null) return UnknownCommandHandler(this, args);
            throw new TclException($"unknown command \"{cmdName}\"");
        }

        public string InvokeProc(TclProc proc, string[] args)
        {
            // args[0] = proc name. Bind parameters.
            var scope = new TclScope { Level = scopes.Count };
            int argIdx = 1;
            for (int i = 0; i < proc.ParamNames.Length; i++)
            {
                string pname = proc.ParamNames[i];
                if (proc.HasArgsTail && i == proc.ParamNames.Length - 1)
                {
                    // gather rest into "args" as a Tcl list
                    var rest = new List<string>();
                    while (argIdx < args.Length) rest.Add(args[argIdx++]);
                    scope.Vars[pname] = TclList.Format(rest);
                    break;
                }
                if (argIdx < args.Length)
                {
                    scope.Vars[pname] = args[argIdx++];
                }
                else if (proc.ParamDefaults[i] != null)
                {
                    scope.Vars[pname] = proc.ParamDefaults[i]!;
                }
                else
                {
                    throw new TclException(
                        $"wrong # args: should be \"{proc.Name} {string.Join(" ", proc.ParamNames)}\"");
                }
            }

            scopes.Add(scope);
            try
            {
                return Eval(proc.Body);
            }
            catch (TclReturnException ret)
            {
                return ret.Value;
            }
            finally
            {
                scopes.RemoveAt(scopes.Count - 1);
            }
        }

        // ----- variable access (alias-aware) -----

        // Resolves a (scope, name) pair following alias chains.
        (TclScope, string) ResolveVar(TclScope scope, string name)
        {
            // Follow alias chain — break on a cycle just in case.
            for (int hops = 0; hops < 32; hops++)
            {
                if (scope.Aliases.TryGetValue(name, out var alias))
                {
                    scope = alias.scope;
                    name = alias.name;
                    continue;
                }
                break;
            }
            return (scope, name);
        }

        public string? GetVar(string name)
        {
            var (s, n) = ResolveVar(CurrentScope, name);
            if (s.Vars.TryGetValue(n, out var v)) return v;
            // Diggles convention: a body reads "globals" without an explicit `global`.
            // Tier 1 = the active global namespace (the object's own scope while a body
            // runs, else the shared interpreter global).
            var g = GlobalScope;
            if (s != g && g.Vars.TryGetValue(name, out var gv)) return gv;
            // Tier 2: genuine shared interpreter-globals stay visible.
            var rg = scopes[0];
            if (g != rg && s != rg && rg.Vars.TryGetValue(name, out var rv)) return rv;
            return null;
        }

        public void SetVar(string name, string value)
        {
            var (s, n) = ResolveVar(CurrentScope, name);
            s.Vars[n] = value;
        }

        public void SetGlobal(string name, string value)
        {
            GlobalScope.Vars[name] = value;
        }

        public bool UnsetVar(string name)
        {
            var (s, n) = ResolveVar(CurrentScope, name);
            return s.Vars.Remove(n);
        }

        public bool VarExists(string name)
        {
            var (s, n) = ResolveVar(CurrentScope, name);
            return s.Vars.ContainsKey(n);
        }

        public void SetAlias(string localName, TclScope targetScope, string targetName)
        {
            CurrentScope.Aliases[localName] = (targetScope, targetName);
        }

        public bool ProcExists(string name) => procs.ContainsKey(name);
        public bool CommandExists(string name)
            => commands.ContainsKey(name) || procs.ContainsKey(name);
    }
}
