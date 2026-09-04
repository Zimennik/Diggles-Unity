#nullable enable

namespace Diggles.Runtime.Tcl
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;
    using UnityEngine;

    public static class TclBuiltins
    {
        public static void RegisterAll(TclInterpreter interp)
        {
            interp.RegisterCommand("set",       Set);
            interp.RegisterCommand("unset",     Unset);
            interp.RegisterCommand("proc",      Proc);
            interp.RegisterCommand("return",    Return);
            interp.RegisterCommand("if",        If);
            interp.RegisterCommand("while",     While);
            interp.RegisterCommand("for",       For);
            interp.RegisterCommand("foreach",   Foreach);
            interp.RegisterCommand("break",     (i, a) => throw new TclBreakException());
            interp.RegisterCommand("continue",  (i, a) => throw new TclContinueException());
            interp.RegisterCommand("eval",      Eval);
            interp.RegisterCommand("catch",     Catch);
            interp.RegisterCommand("error",     Error);

            interp.RegisterCommand("puts",      Puts);
            interp.RegisterCommand("incr",      Incr);
            interp.RegisterCommand("expr",      Expr);

            interp.RegisterCommand("list",      List);
            interp.RegisterCommand("lindex",    LIndex);
            interp.RegisterCommand("lrange",    LRange);
            interp.RegisterCommand("llength",   LLength);
            interp.RegisterCommand("lappend",   LAppend);
            interp.RegisterCommand("lrem",      LRem);
            interp.RegisterCommand("lrep",      LRep);
            interp.RegisterCommand("concat",    Concat);
            interp.RegisterCommand("split",     Split);
            interp.RegisterCommand("join",      Join);

            interp.RegisterCommand("string",    StringCmd);
            interp.RegisterCommand("format",    Format);
            interp.RegisterCommand("scan",      Scan);
            interp.RegisterCommand("info",      Info);
            interp.RegisterCommand("global",    Global);

            interp.RegisterCommand("upvar",     Upvar);
            interp.RegisterCommand("uplevel",   Uplevel);
            interp.RegisterCommand("array",     ArrayCmd);
            interp.RegisterCommand("switch",    SwitchCmd);
            interp.RegisterCommand("source",    Source);

            interp.RegisterCommand("append",    Append);
            interp.RegisterCommand("lsearch",   LSearch);
            interp.RegisterCommand("lreplace",  LReplace);
            interp.RegisterCommand("linsert",   LInsert);
            interp.RegisterCommand("lsort",     LSort);
            interp.RegisterCommand("lreverse",  LReverse);
            interp.RegisterCommand("subst",     Subst);
            interp.RegisterCommand("glob",      Glob);
        }

        // ----- additional list / string built-ins -----

        // append varName ?value...? → string-append to existing var.
        static string Append(TclInterpreter interp, string[] args)
        {
            if (args.Length < 2) throw new TclException("wrong # args for append");
            string cur = interp.GetVar(args[1]) ?? "";
            var sb = new StringBuilder(cur);
            for (int i = 2; i < args.Length; i++) sb.Append(args[i]);
            string val = sb.ToString();
            interp.SetVar(args[1], val);
            return val;
        }

        // lsearch ?-flag...? list pattern → index of first match, or -1.
        // Implements -exact, -glob (default, as in real Tcl 8.x — Skirmish.tcl does
        // `lsearch $enummap "$eName *"` with no mode flag), -nocase, -all, -inline.
        static string LSearch(TclInterpreter interp, string[] args)
        {
            int ai = 1;
            bool glob = true, regexp = false, nocase = false, all = false, inline = false;
            while (ai < args.Length && args[ai].Length > 0 && args[ai][0] == '-')
            {
                switch (args[ai])
                {
                    case "-exact":  glob = regexp = false; break;
                    case "-glob":   glob = true;   regexp = false; break;
                    case "-regexp": regexp = true; glob = false; break;
                    case "-nocase": nocase = true; break;
                    case "-all":    all = true; break;
                    case "-inline": inline = true; break;
                    case "--":      ai++; goto done;
                    default:        goto done;
                }
                ai++;
            }
            done:
            if (args.Length - ai != 2) throw new TclException("wrong # args for lsearch");
            var list = TclList.Parse(args[ai]);
            string pat = args[ai + 1];
            var hits = new List<int>();
            var inlineHits = new List<string>();
            var cmp = nocase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            for (int i = 0; i < list.Count; i++)
            {
                bool m = regexp
                    ? System.Text.RegularExpressions.Regex.IsMatch(list[i], pat,
                        nocase ? System.Text.RegularExpressions.RegexOptions.IgnoreCase
                               : System.Text.RegularExpressions.RegexOptions.None)
                    : glob ? StringMatchGlobCI(pat, list[i], nocase)
                    : string.Compare(list[i], pat, cmp) == 0;
                if (m)
                {
                    if (inline) inlineHits.Add(list[i]);
                    else hits.Add(i);
                    if (!all) break;
                }
            }
            if (inline) return TclList.Format(inlineHits);
            if (all) return TclList.Format(System.Linq.Enumerable.Select(hits, h => h.ToString(CultureInfo.InvariantCulture)));
            return hits.Count > 0 ? hits[0].ToString(CultureInfo.InvariantCulture) : "-1";
        }

        static bool StringMatchGlobCI(string pattern, string str, bool nocase)
        {
            if (!nocase) return StringMatchGlob(pattern, str);
            return StringMatchGlob(pattern.ToLowerInvariant(), str.ToLowerInvariant());
        }

        // Tcl index word: integer, "end", or "end±N". `endValue` is what plain
        // "end" maps to — count-1 for element refs, count for linsert's append slot.
        static int ParseEndRelativeIndex(string s, int endValue)
        {
            if (s.StartsWith("end", StringComparison.Ordinal))
            {
                if (s.Length == 3) return endValue;
                if (s.Length > 4 && (s[3] == '-' || s[3] == '+') &&
                    int.TryParse(s.Substring(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out int off))
                    return s[3] == '-' ? endValue - off : endValue + off;
            }
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx)) return idx;
            throw new TclException($"bad index \"{s}\": must be integer or end?-integer?");
        }

        // lreplace list first last ?element...?
        static string LReplace(TclInterpreter interp, string[] args)
        {
            if (args.Length < 4) throw new TclException("wrong # args for lreplace");
            var list = TclList.Parse(args[1]);
            int first = ParseEndRelativeIndex(args[2], list.Count - 1);
            int last  = ParseEndRelativeIndex(args[3], list.Count - 1);
            if (first < 0) first = 0;
            if (last  >= list.Count) last = list.Count - 1;
            var result = new List<string>();
            for (int i = 0; i < first && i < list.Count; i++) result.Add(list[i]);
            for (int i = 4; i < args.Length; i++) result.Add(args[i]);
            // last < first deletes nothing — pure insertion before `first` (Tcl semantics).
            for (int i = System.Math.Max(last + 1, first); i < list.Count; i++) result.Add(list[i]);
            return TclList.Format(result);
        }

        // linsert list index element ?element...? — schreinerei.tcl / bar.tcl splice
        // accident/relief anims into prod action lists at random indices. For linsert
        // "end" means the append slot (= llength), per Tcl_LinsertObjCmd.
        static string LInsert(TclInterpreter interp, string[] args)
        {
            if (args.Length < 4)
                throw new TclException("wrong # args: should be \"linsert list index element ?element ...?\"");
            var list = TclList.Parse(args[1]);
            int idx = ParseEndRelativeIndex(args[2], list.Count);
            if (idx < 0) idx = 0;
            if (idx > list.Count) idx = list.Count;
            for (int i = 3; i < args.Length; i++) list.Insert(idx + (i - 3), args[i]);
            return TclList.Format(list);
        }

        // lsort ?options? list
        // Game data uses: plain (ascii), -integer, and -index N combined with
        // -real / -integer / -decreasing in any flag order (z_spare_*, std_ai,
        // Trigger_*). The sparetime emergency sort (z_spare_main.tcl:422) relies
        // on the Tcl default: ASCII compare even when keys look numeric. Tcl's
        // lsort is a stable mergesort — equal keys keep input order — so ties
        // break on the original index here.
        static string LSort(TclInterpreter interp, string[] args)
        {
            if (args.Length < 2) throw new TclException("wrong # args: should be \"lsort ?options? list\"");
            bool integer = false, real = false;
            bool ascending = true, unique = false, nocase = false;
            string? indexSpec = null;

            // Tcl_GetIndexFromObj accepts unique prefixes ("-int" → -integer).
            string[] options = { "-ascii", "-decreasing", "-dictionary", "-increasing",
                                 "-index", "-integer", "-nocase", "-real", "-unique" };
            int ai = 1;
            while (ai < args.Length - 1)   // last arg is always the list
            {
                string opt = args[ai];
                string? match = null;
                foreach (var o in options)
                {
                    if (!o.StartsWith(opt, StringComparison.Ordinal)) continue;
                    if (match != null) { match = null; break; }   // ambiguous prefix
                    match = o;
                }
                switch (match)
                {
                    case "-ascii":      integer = real = false; break;
                    case "-integer":    integer = true; real = false; break;
                    case "-real":       real = true; integer = false; break;
                    case "-dictionary": break;   // unused by game data; ascii is close enough
                    case "-increasing": ascending = true; break;
                    case "-decreasing": ascending = false; break;
                    case "-unique":     unique = true; break;
                    case "-nocase":     nocase = true; break;
                    case "-index":
                        if (ai + 1 >= args.Length - 1)
                            throw new TclException("\"-index\" option must be followed by list index");
                        indexSpec = args[++ai];
                        break;
                    default:
                        throw new TclException($"bad option \"{opt}\" for lsort");
                }
                ai++;
            }

            var list = TclList.Parse(args[^1]);

            string KeyOf(string item)
            {
                if (indexSpec == null) return item;
                var sub = TclList.Parse(item);
                int idx = ParseEndRelativeIndex(indexSpec, sub.Count - 1);
                if (idx < 0 || idx >= sub.Count)
                    throw new TclException($"element {indexSpec} missing from sublist \"{item}\"");
                return sub[idx];
            }

            int Cmp(string a, string b)
            {
                if (integer)
                    return long.Parse(a.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture)
                        .CompareTo(long.Parse(b.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture));
                if (real)
                    return double.Parse(a.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture)
                        .CompareTo(double.Parse(b.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture));
                return string.Compare(a, b,
                    nocase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            }

            var keyed = new (string Item, string Key, int Ord)[list.Count];
            for (int k = 0; k < list.Count; k++) keyed[k] = (list[k], KeyOf(list[k]), k);
            Array.Sort(keyed, (x, y) =>
            {
                int c = Cmp(x.Key, y.Key);
                if (!ascending) c = -c;
                return c != 0 ? c : x.Ord - y.Ord;
            });

            var result = new List<string>(keyed.Length);
            for (int k = 0; k < keyed.Length; k++)
            {
                // -unique drops all but the last of a run of equal keys (Tcl semantics).
                if (unique && k + 1 < keyed.Length && Cmp(keyed[k].Key, keyed[k + 1].Key) == 0) continue;
                result.Add(keyed[k].Item);
            }
            return TclList.Format(result);
        }

        static string LReverse(TclInterpreter interp, string[] args)
        {
            if (args.Length != 2) throw new TclException("wrong # args for lreverse");
            var list = TclList.Parse(args[1]);
            list.Reverse();
            return TclList.Format(list);
        }

        // subst — performs $var / [cmd] / \X substitution on its arg.
        // Supports -nobackslashes / -nocommands / -novariables (Tcl 8.4+).
        static string Subst(TclInterpreter interp, string[] args)
        {
            int ai = 1;
            // Flags accepted but minimally honoured (we always do all subs).
            while (ai < args.Length && args[ai].StartsWith("-"))
            {
                if (args[ai] == "--") { ai++; break; }
                ai++;
            }
            if (ai >= args.Length) throw new TclException("wrong # args for subst");
            return TclSubst.Substitute(interp, args[ai]);
        }

        // glob — extremely minimal; returns empty list (Diggles uses it
        // only in startup paths we don't yet drive). Accepts -nocomplain.
        static string Glob(TclInterpreter interp, string[] args) => "";

        // ----- variables -----

        static string Set(TclInterpreter interp, string[] args)
        {
            if (args.Length == 2)
                return interp.GetVar(args[1]) ??
                       throw new TclException($"can't read \"{args[1]}\": no such variable");
            if (args.Length == 3)
            {
                interp.SetVar(args[1], args[2]);
                return args[2];
            }
            throw new TclException("wrong # args: should be \"set varName ?newValue?\"");
        }

        static string Unset(TclInterpreter interp, string[] args)
        {
            for (int i = 1; i < args.Length; i++) interp.UnsetVar(args[i]);
            return "";
        }

        static string Global(TclInterpreter interp, string[] args)
        {
            // Make global variables visible in current scope as aliases onto the
            // active global namespace (per-object override while a body runs, else
            // shared interp-global). Captured at declaration — fine because a scope's
            // override is fixed for the proc's lifetime.
            for (int i = 1; i < args.Length; i++)
            {
                interp.SetAlias(args[i], interp.GlobalScope, args[i]);
            }
            return "";
        }

        static string Incr(TclInterpreter interp, string[] args)
        {
            if (args.Length < 2 || args.Length > 3)
                throw new TclException("wrong # args: should be \"incr varName ?increment?\"");
            int delta = 1;
            if (args.Length == 3 && !int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out delta))
                throw new TclException($"expected integer but got \"{args[2]}\"");
            string cur = interp.GetVar(args[1]) ?? "0";
            if (!int.TryParse(cur, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                throw new TclException($"expected integer but got \"{cur}\"");
            v += delta;
            string newVal = v.ToString(CultureInfo.InvariantCulture);
            interp.SetVar(args[1], newVal);
            return newVal;
        }

        // ----- procs -----

        static string Proc(TclInterpreter interp, string[] args)
        {
            if (args.Length != 4)
                throw new TclException(
                    $"wrong # args ({args.Length}, expected 4 = proc name args body); " +
                    $"first arg = \"{(args.Length > 1 ? args[1] : "<none>")}\"");
            string name = args[1];
            var paramSpec = TclList.Parse(args[2]);
            var paramNames = new string[paramSpec.Count];
            var paramDefaults = new string?[paramSpec.Count];
            bool hasArgsTail = false;
            for (int i = 0; i < paramSpec.Count; i++)
            {
                var parts = TclList.Parse(paramSpec[i]);
                paramNames[i] = parts[0];
                paramDefaults[i] = parts.Count > 1 ? parts[1] : null;
                if (paramNames[i] == "args" && i == paramSpec.Count - 1)
                    hasArgsTail = true;
            }
            interp.RegisterProc(new TclProc
            {
                Name = name,
                ParamNames = paramNames,
                ParamDefaults = paramDefaults,
                HasArgsTail = hasArgsTail,
                Body = args[3],
            });
            return "";
        }

        static string Return(TclInterpreter interp, string[] args)
        {
            throw new TclReturnException(args.Length >= 2 ? args[1] : "");
        }

        // ----- control flow -----

        static string If(TclInterpreter interp, string[] args)
        {
            // if cond ?then? body ?elseif cond body...? ??else? body?
            int i = 1;
            while (i < args.Length)
            {
                if (i >= args.Length) throw new TclException("missing if condition");
                string cond = args[i++];
                if (i < args.Length && args[i] == "then") i++;
                if (i >= args.Length) throw new TclException("missing if body");
                string body = args[i++];
                if (TclExpr.EvalBool(interp, cond))
                    return interp.Eval(body);
                if (i < args.Length && args[i] == "elseif") { i++; continue; }
                if (i < args.Length && args[i] == "else")
                {
                    if (i + 1 >= args.Length) throw new TclException("missing else body");
                    return interp.Eval(args[i + 1]);
                }
                // Real Tcl: the `else` keyword is optional — a single trailing word after the
                // then-body is the else branch (`if {c} {then} {else}`, Tcl `if` man page).
                // Diggles scripts use this form, e.g. putdown_anim z_procs.tcl:669
                // `if {[get_gnomeposition this]} {return putwall} {return bend}`. Regression:
                // dropping it made putdown_anim return "" → the buildup chain queued a
                // zero-arg `play_anim ` task (unpack_pos_unfixed z_procs.tcl:1184) → the
                // proc-arity error `wrong # args: should be "play_anim anim args"` broke the
                // state task.
                if (i == args.Length - 1) return interp.Eval(args[i]);
                break;
            }
            return "";
        }

        static string While(TclInterpreter interp, string[] args)
        {
            if (args.Length != 3) throw new TclException("wrong # args: should be \"while cond body\"");
            string cond = args[1], body = args[2];
            while (TclExpr.EvalBool(interp, cond))
            {
                try { interp.Eval(body); }
                catch (TclBreakException) { break; }
                catch (TclContinueException) { /* loop */ }
            }
            return "";
        }

        static string For(TclInterpreter interp, string[] args)
        {
            if (args.Length != 5)
                throw new TclException("wrong # args: should be \"for init cond next body\"");
            interp.Eval(args[1]);
            while (TclExpr.EvalBool(interp, args[2]))
            {
                try { interp.Eval(args[4]); }
                catch (TclBreakException) { break; }
                catch (TclContinueException) { }
                interp.Eval(args[3]);
            }
            return "";
        }

        static string Foreach(TclInterpreter interp, string[] args)
        {
            if (args.Length != 4)
                throw new TclException("wrong # args: should be \"foreach var list body\"");
            string varName = args[1];
            var items = TclList.Parse(args[2]);
            string body = args[3];
            foreach (var item in items)
            {
                interp.SetVar(varName, item);
                try { interp.Eval(body); }
                catch (TclBreakException) { break; }
                catch (TclContinueException) { }
            }
            return "";
        }

        static string Eval(TclInterpreter interp, string[] args)
        {
            if (args.Length < 2) throw new TclException("wrong # args: should be \"eval arg ?arg ...?\"");
            var sb = new StringBuilder();
            for (int i = 1; i < args.Length; i++)
            {
                if (i > 1) sb.Append(' ');
                sb.Append(args[i]);
            }
            return interp.Eval(sb.ToString());
        }

        static string Catch(TclInterpreter interp, string[] args)
        {
            if (args.Length < 2) throw new TclException("wrong # args: should be \"catch body ?varName?\"");
            try
            {
                string r = interp.Eval(args[1]);
                if (args.Length > 2) interp.SetVar(args[2], r);
                return "0";
            }
            catch (TclReturnException) { throw; }
            catch (TclBreakException) { throw; }
            catch (TclContinueException) { throw; }
            catch (Exception ex)
            {
                if (args.Length > 2) interp.SetVar(args[2], ex.Message);
                return "1";
            }
        }

        static string Error(TclInterpreter interp, string[] args)
        {
            throw new TclException(args.Length >= 2 ? args[1] : "");
        }

        // ----- I/O -----

        static string Puts(TclInterpreter interp, string[] args)
        {
            // puts ?-nonewline? ?channelId? string
            int i = 1;
            bool nonewline = false;
            if (i < args.Length && args[i] == "-nonewline") { nonewline = true; i++; }
            // ignore channelId (stdout/stderr) — all goes to Debug.Log
            if (i + 1 < args.Length && (args[i] == "stdout" || args[i] == "stderr")) i++;
            if (i >= args.Length) throw new TclException("wrong # args: should be \"puts ?-nonewline? ?channelId? string\"");
            string msg = args[i];
            Debug.Log(nonewline ? msg : msg);
            return "";
        }

        // ----- math -----

        static string Expr(TclInterpreter interp, string[] args)
        {
            if (args.Length < 2) throw new TclException("wrong # args: should be \"expr arg ?arg ...?\"");
            var sb = new StringBuilder();
            for (int i = 1; i < args.Length; i++)
            {
                if (i > 1) sb.Append(' ');
                sb.Append(args[i]);
            }
            try { return TclExpr.Eval(interp, sb.ToString()); }
            catch (TclException) when (interp.TolerateExprErrors)
            {
                // Common cause: TolerateMissingVars turned an undefined
                // `$foo` into "", which mangles `[expr {$foo + 1}]` etc.
                // Return "0" so callers (`set x [expr...]`, `for`) chug along.
                return "0";
            }
        }

        // ----- lists -----

        static string List(TclInterpreter interp, string[] args)
        {
            var items = new List<string>(args.Length - 1);
            for (int i = 1; i < args.Length; i++) items.Add(args[i]);
            return TclList.Format(items);
        }

        static string LIndex(TclInterpreter interp, string[] args)
        {
            if (args.Length != 3) throw new TclException("wrong # args: should be \"lindex list idx\"");
            var items = TclList.Parse(args[1]);
            int idx;
            if (args[2] == "end") idx = items.Count - 1;
            else if (!int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out idx))
                throw new TclException($"bad index \"{args[2]}\"");
            if (idx < 0 || idx >= items.Count) return "";
            return items[idx];
        }

        static string LRange(TclInterpreter interp, string[] args)
        {
            if (args.Length != 4) throw new TclException("wrong # args: should be \"lrange list first last\"");
            var items = TclList.Parse(args[1]);
            int first = args[2] == "end" ? items.Count - 1
                : int.Parse(args[2], CultureInfo.InvariantCulture);
            int last = args[3] == "end" ? items.Count - 1
                : int.Parse(args[3], CultureInfo.InvariantCulture);
            if (first < 0) first = 0;
            if (last >= items.Count) last = items.Count - 1;
            if (first > last) return "";
            return TclList.Format(items.GetRange(first, last - first + 1));
        }

        static string LLength(TclInterpreter interp, string[] args)
        {
            if (args.Length != 2) throw new TclException("wrong # args: should be \"llength list\"");
            return TclList.Parse(args[1]).Count.ToString(CultureInfo.InvariantCulture);
        }

        static string LAppend(TclInterpreter interp, string[] args)
        {
            if (args.Length < 2) throw new TclException("wrong # args: should be \"lappend varName ?value ...?\"");
            var name = args[1];
            string cur = interp.GetVar(name) ?? "";
            var items = cur.Length == 0 ? new List<string>() : TclList.Parse(cur);
            for (int i = 2; i < args.Length; i++) items.Add(args[i]);
            string newVal = TclList.Format(items);
            interp.SetVar(name, newVal);
            return newVal;
        }

        // lrem listVarName index — pop element at index, mutate var, return popped.
        // Diggles-specific (not in stock Tcl). Used by Campaign.tcl first_level
        // for tunnel positioning and std_ai.tcl for raiding-party allocation.
        // Without this, those scripts use a stubbed "0" return which silently
        // misaligns procedurally-generated levels.
        static string LRem(TclInterpreter interp, string[] args)
        {
            if (args.Length != 3) throw new TclException("wrong # args: should be \"lrem listVarName index\"");
            var name = args[1];
            string cur = interp.GetVar(name) ?? "";
            var items = cur.Length == 0 ? new List<string>() : TclList.Parse(cur);
            if (!int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx))
                throw new TclException($"expected integer but got \"{args[2]}\"");
            if (idx < 0 || idx >= items.Count) return "";  // out-of-range: silent empty
            string popped = items[idx];
            items.RemoveAt(idx);
            interp.SetVar(name, TclList.Format(items));
            return popped;
        }

        // lrep listVarName index value — replace the element at `index` in the named list variable
        // with `value` (in place), growing the list with empty elements when `index` is past the
        // end. Diggles-specific companion to lrem; the sequencer builds followaction_list /
        // idleanim_list / eyefocus_list this way (sq_actor actionlist, seq_enable). Was stubbed →
        // the actor action-queues never populated, so e.g. knitstart never advanced to knitloop.
        static string LRep(TclInterpreter interp, string[] args)
        {
            if (args.Length != 4) throw new TclException("wrong # args: should be \"lrep listVarName index value\"");
            var name = args[1];
            string cur = interp.GetVar(name) ?? "";
            var items = cur.Length == 0 ? new List<string>() : TclList.Parse(cur);
            if (!int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx))
                throw new TclException($"expected integer but got \"{args[2]}\"");
            if (idx < 0) return TclList.Format(items);
            while (items.Count <= idx) items.Add("");
            items[idx] = args[3];
            interp.SetVar(name, TclList.Format(items));
            return TclList.Format(items);
        }

        static string Concat(TclInterpreter interp, string[] args)
        {
            var sb = new StringBuilder();
            for (int i = 1; i < args.Length; i++)
            {
                if (i > 1) sb.Append(' ');
                sb.Append(args[i].Trim());
            }
            return sb.ToString();
        }

        static string Split(TclInterpreter interp, string[] args)
        {
            if (args.Length < 2 || args.Length > 3)
                throw new TclException("wrong # args: should be \"split string ?splitChars?\"");
            string s = args[1];
            string seps = args.Length == 3 ? args[2] : " \t\n\r";
            var parts = new List<string>();
            // Tcl law: an empty splitChars splits between every character. Regression: the old
            // fall-through returned the whole string as one element, so `get_random_of abcd`
            // (drache.tcl:272 `lindex [split $str ""] [irandom...]`) yielded "abcd" → phantom
            // clip names like frau.standloopabcd spamming [3db] GetClip misses.
            if (seps.Length == 0)
            {
                foreach (char c in s) parts.Add(c.ToString());
                return TclList.Format(parts);
            }
            int start = 0;
            for (int i = 0; i < s.Length; i++)
                if (seps.IndexOf(s[i]) >= 0) { parts.Add(s.Substring(start, i - start)); start = i + 1; }
            parts.Add(s.Substring(start));
            return TclList.Format(parts);
        }

        static string Join(TclInterpreter interp, string[] args)
        {
            if (args.Length < 2 || args.Length > 3)
                throw new TclException("wrong # args: should be \"join list ?joinString?\"");
            var items = TclList.Parse(args[1]);
            string sep = args.Length == 3 ? args[2] : " ";
            return string.Join(sep, items);
        }

        // scan string format ?varName...? — scanf-style parse of `string` per `format`.
        // Inline form (no varNames): returns the converted values as a list (this is how the
        // game uses it: `scan [string index $clothing 0] %x` → the clothing-variant index).
        // Var form: assigns conversions to the vars and returns the count (-1 if EOF before any).
        // Supports %d/%i %x/%X %o %c %s %f/%e/%g, an optional field width, `*` (suppress), `%%`,
        // and whitespace skipping. Without this, the clothing hex codes silently read as 0 and
        // every dwarf wears the default outfit (the unknown-command handler returns "0").
        static string Scan(TclInterpreter interp, string[] args)
        {
            if (args.Length < 3)
                throw new TclException("wrong # args: should be \"scan string format ?varName varName ...?\"");
            string str = args[1], fmt = args[2];
            int nVars = args.Length - 3;
            bool inline = nVars == 0;

            static bool IsHex(char c) => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

            var results = new List<string>();
            int si = 0, fi = 0, conversions = 0;
            bool sawEof = false;

            while (fi < fmt.Length)
            {
                char fc = fmt[fi];
                if (char.IsWhiteSpace(fc))   // run of whitespace matches any input whitespace
                {
                    fi++;
                    while (si < str.Length && char.IsWhiteSpace(str[si])) si++;
                    continue;
                }
                if (fc != '%')               // literal char must match
                {
                    if (si < str.Length && str[si] == fc) { si++; fi++; continue; }
                    break;
                }
                fi++;
                if (fi >= fmt.Length) break;
                if (fmt[fi] == '%')          // %% → literal percent
                {
                    if (si < str.Length && str[si] == '%') { si++; fi++; continue; }
                    break;
                }
                bool suppress = false;
                if (fmt[fi] == '*') { suppress = true; fi++; }
                int width = 0; bool hasWidth = false;
                while (fi < fmt.Length && char.IsDigit(fmt[fi])) { width = width * 10 + (fmt[fi] - '0'); hasWidth = true; fi++; }
                if (fi >= fmt.Length) break;
                char conv = fmt[fi++];

                if (conv != 'c')            // %c reads the next char verbatim; the rest skip whitespace
                    while (si < str.Length && char.IsWhiteSpace(str[si])) si++;
                if (si >= str.Length) { sawEof = true; break; }

                int start = si, max = hasWidth ? width : int.MaxValue;
                string? token = null;
                switch (conv)
                {
                    case 'd': case 'i':
                        if (str[si] == '-' || str[si] == '+') si++;
                        while (si < str.Length && char.IsDigit(str[si]) && si - start < max) si++;
                        if (si > start && long.TryParse(str.Substring(start, si - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out var dv))
                            token = dv.ToString(CultureInfo.InvariantCulture);
                        break;
                    case 'x': case 'X':
                        while (si < str.Length && IsHex(str[si]) && si - start < max) si++;
                        if (si > start && long.TryParse(str.Substring(start, si - start), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var xv))
                            token = xv.ToString(CultureInfo.InvariantCulture);
                        break;
                    case 'o':
                        while (si < str.Length && str[si] >= '0' && str[si] <= '7' && si - start < max) si++;
                        if (si > start) { long ov = 0; foreach (var ch in str.Substring(start, si - start)) ov = ov * 8 + (ch - '0'); token = ov.ToString(CultureInfo.InvariantCulture); }
                        break;
                    case 'c':
                        token = ((int)str[si]).ToString(CultureInfo.InvariantCulture); si++;
                        break;
                    case 's':
                        while (si < str.Length && !char.IsWhiteSpace(str[si]) && si - start < max) si++;
                        token = str.Substring(start, si - start);
                        break;
                    case 'f': case 'e': case 'g': case 'E': case 'G':
                        if (str[si] == '-' || str[si] == '+') si++;
                        while (si < str.Length && (char.IsDigit(str[si]) || str[si] == '.' || str[si] == 'e' || str[si] == 'E' || str[si] == '+' || str[si] == '-') && si - start < max) si++;
                        if (si > start && double.TryParse(str.Substring(start, si - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var fv))
                            token = fv.ToString(CultureInfo.InvariantCulture);
                        break;
                    default:
                        throw new TclException($"bad scan conversion character \"%{conv}\"");
                }
                if (token == null) break;        // conversion failed → stop scanning
                if (!suppress) { results.Add(token); conversions++; }
            }

            if (inline) return TclList.Format(results);
            int n = System.Math.Min(results.Count, nVars);
            for (int k = 0; k < n; k++) interp.SetVar(args[3 + k], results[k]);
            return (conversions == 0 && sawEof) ? "-1" : conversions.ToString(CultureInfo.InvariantCulture);
        }

        // ----- string -----

        static string StringCmd(TclInterpreter interp, string[] args)
        {
            if (args.Length < 2) throw new TclException("wrong # args: should be \"string option arg ?arg ...?\"");
            string DumpArgs() {
                var sb = new StringBuilder();
                for (int i = 0; i < args.Length; i++) { if (i > 0) sb.Append(' '); sb.Append('\"').Append(args[i]).Append('\"'); }
                return sb.ToString();
            }
            switch (args[1])
            {
                case "length":
                    if (args.Length != 3) throw new TclException($"wrong # args for string {args[1]} (got: {DumpArgs()})");
                    return args[2].Length.ToString(CultureInfo.InvariantCulture);
                case "index":
                    if (args.Length != 4) throw new TclException($"wrong # args for string {args[1]} (got: {DumpArgs()})");
                    {
                        int i = args[3] == "end" ? args[2].Length - 1 : int.Parse(args[3], CultureInfo.InvariantCulture);
                        if (i < 0 || i >= args[2].Length) return "";
                        return args[2][i].ToString();
                    }
                case "range":
                    if (args.Length != 5) throw new TclException($"wrong # args for string {args[1]} (got: {DumpArgs()})");
                    {
                        string s = args[2];
                        int f = args[3] == "end" ? s.Length - 1 : int.Parse(args[3], CultureInfo.InvariantCulture);
                        int l = args[4] == "end" ? s.Length - 1 : int.Parse(args[4], CultureInfo.InvariantCulture);
                        if (f < 0) f = 0; if (l >= s.Length) l = s.Length - 1;
                        if (f > l) return "";
                        return s.Substring(f, l - f + 1);
                    }
                case "toupper": return args[2].ToUpperInvariant();
                case "tolower": return args[2].ToLowerInvariant();
                case "trim":      return args.Length >= 4 ? args[2].Trim(args[3].ToCharArray())      : args[2].Trim();
                case "trimleft":  return args.Length >= 4 ? args[2].TrimStart(args[3].ToCharArray()) : args[2].TrimStart();
                case "trimright": return args.Length >= 4 ? args[2].TrimEnd(args[3].ToCharArray())   : args[2].TrimEnd();
                case "equal":
                case "compare":
                {
                    // string equal|compare ?-nocase? ?-length int? string1 string2
                    bool nocase = false; int maxLen = -1; int ai = 2;
                    while (ai + 1 < args.Length && args[ai].Length > 0 && args[ai][0] == '-')
                    {
                        if (args[ai] == "-nocase") { nocase = true; ai++; }
                        else if (args[ai] == "-length") { maxLen = int.Parse(args[ai + 1], CultureInfo.InvariantCulture); ai += 2; }
                        else break;
                    }
                    if (args.Length - ai != 2) throw new TclException($"wrong # args for string {args[1]} (got: {DumpArgs()})");
                    string s1 = args[ai], s2 = args[ai + 1];
                    if (maxLen >= 0)
                    {
                        if (s1.Length > maxLen) s1 = s1.Substring(0, maxLen);
                        if (s2.Length > maxLen) s2 = s2.Substring(0, maxLen);
                    }
                    var cmp = nocase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                    if (args[1] == "equal") return string.Equals(s1, s2, cmp) ? "1" : "0";
                    return System.Math.Sign(string.Compare(s1, s2, cmp)).ToString(CultureInfo.InvariantCulture);
                }
                case "match":
                {
                    // string match ?-nocase? pattern string
                    int ai = 2; bool nocase = false;
                    if (ai < args.Length && args[ai] == "-nocase") { nocase = true; ai++; }
                    if (args.Length - ai != 2) throw new TclException($"wrong # args for string {args[1]} (got: {DumpArgs()})");
                    string pat = args[ai], str = args[ai + 1];
                    if (nocase) { pat = pat.ToLowerInvariant(); str = str.ToLowerInvariant(); }
                    return StringMatchGlob(pat, str) ? "1" : "0";
                }
                case "first":
                    if (args.Length < 4) throw new TclException($"wrong # args for string {args[1]} (got: {DumpArgs()})");
                    return args[3].IndexOf(args[2], StringComparison.Ordinal).ToString(CultureInfo.InvariantCulture);
                case "last":
                    if (args.Length < 4) throw new TclException($"wrong # args for string {args[1]} (got: {DumpArgs()})");
                    return args[3].LastIndexOf(args[2], StringComparison.Ordinal).ToString(CultureInfo.InvariantCulture);
                case "map":
                    // string map ?-nocase? mapping string
                    {
                        int ai = 2;
                        bool noCase = false;
                        if (ai < args.Length && args[ai] == "-nocase") { noCase = true; ai++; }
                        if (ai + 2 != args.Length) throw new TclException($"wrong # args for string {args[1]} (got: {DumpArgs()})");
                        var pairs = TclList.Parse(args[ai]);
                        string input = args[ai + 1];
                        var sb = new StringBuilder();
                        int idx = 0;
                        while (idx < input.Length)
                        {
                            bool matched = false;
                            for (int p = 0; p + 1 < pairs.Count; p += 2)
                            {
                                var from = pairs[p];
                                if (from.Length == 0) continue;
                                bool eq = noCase
                                    ? string.Compare(input, idx, from, 0, from.Length, StringComparison.OrdinalIgnoreCase) == 0
                                    : (idx + from.Length <= input.Length && input.AsSpan(idx, from.Length).SequenceEqual(from.AsSpan()));
                                if (eq)
                                {
                                    sb.Append(pairs[p + 1]);
                                    idx += from.Length;
                                    matched = true;
                                    break;
                                }
                            }
                            if (!matched) { sb.Append(input[idx]); idx++; }
                        }
                        return sb.ToString();
                    }
                case "is":
                    // string is class ?-strict? str
                    if (args.Length < 4) throw new TclException($"wrong # args for string {args[1]} (got: {DumpArgs()})");
                    {
                        string cls = args[2];
                        int strIdx = 3;
                        if (args[strIdx] == "-strict" && args.Length > 4) strIdx++;
                        string s = args[strIdx];
                        bool ok = cls switch
                        {
                            "integer" => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
                            "double"  => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _),
                            "boolean" => s == "1" || s == "0" || s == "true" || s == "false" || s == "yes" || s == "no",
                            "alpha"   => System.Linq.Enumerable.All(s, char.IsLetter),
                            "alnum"   => System.Linq.Enumerable.All(s, char.IsLetterOrDigit),
                            "digit"   => System.Linq.Enumerable.All(s, char.IsDigit),
                            "space"   => System.Linq.Enumerable.All(s, char.IsWhiteSpace),
                            "ascii"   => System.Linq.Enumerable.All(s, c => c < 128),
                            // upper/lower — lgtools.tcl's template-group recursion classifies subgroup names
                            // via `string is upper`; without these it never recurses into Std.Gng/.Gsg/.Hol.
                            "upper"   => System.Linq.Enumerable.All(s, char.IsUpper),
                            "lower"   => System.Linq.Enumerable.All(s, char.IsLower),
                            "wordchar" => System.Linq.Enumerable.All(s, c => char.IsLetterOrDigit(c) || c == '_'),
                            "punct"   => System.Linq.Enumerable.All(s, char.IsPunctuation),
                            _ => throw new TclException($"bad class \"{cls}\" for string is"),
                        };
                        return ok ? "1" : "0";
                    }
                case "repeat":
                    // string repeat string count — z_work_common.tcl builds the 8 per-owner
                    // dig-layer attrib names as type + "_"*i; std_ai seeds maps with "0 "*n.
                    if (args.Length != 4) throw new TclException($"wrong # args for string {args[1]} (got: {DumpArgs()})");
                    {
                        if (!int.TryParse(args[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int cnt))
                            throw new TclException($"expected integer but got \"{args[3]}\"");
                        if (cnt <= 0 || args[2].Length == 0) return "";
                        var sb = new StringBuilder(args[2].Length * cnt);
                        for (int k = 0; k < cnt; k++) sb.Append(args[2]);
                        return sb.ToString();
                    }
                case "replace":
                    if (args.Length < 5) throw new TclException($"wrong # args for string {args[1]} (got: {DumpArgs()})");
                    {
                        string s = args[2];
                        int f = args[3] == "end" ? s.Length - 1 : int.Parse(args[3], CultureInfo.InvariantCulture);
                        int l = args[4] == "end" ? s.Length - 1 : int.Parse(args[4], CultureInfo.InvariantCulture);
                        if (f < 0) f = 0; if (l >= s.Length) l = s.Length - 1;
                        if (f > l) return s;
                        string repl = args.Length > 5 ? args[5] : "";
                        return s.Substring(0, f) + repl + s.Substring(l + 1);
                    }
                default:
                    throw new TclException($"bad option \"{args[1]}\" for string (got: {DumpArgs()})");
            }
        }

        static bool StringMatchGlob(string pattern, string str)
        {
            // simple glob: * matches any, ? matches one char
            return GlobMatch(pattern, 0, str, 0);
        }

        static bool GlobMatch(string p, int pi, string s, int si)
        {
            while (pi < p.Length)
            {
                char pc = p[pi];
                if (pc == '*')
                {
                    pi++;
                    if (pi >= p.Length) return true;
                    while (si <= s.Length)
                    {
                        if (GlobMatch(p, pi, s, si)) return true;
                        si++;
                    }
                    return false;
                }
                if (si >= s.Length) return false;
                if (pc == '?') { pi++; si++; continue; }
                if (pc == '\\' && pi + 1 < p.Length) { pi++; pc = p[pi]; }
                if (pc != s[si]) return false;
                pi++; si++;
            }
            return si == s.Length;
        }

        // ----- format -----

        // format formatString ?arg...? — C-style sprintf, Tcl 8.x semantics.
        // The game data needs %4i (std_ai.tcl:3066 supply-table log) and %010o
        // (z_work_prodfill.tcl:210 random prod serial); the rest of the common
        // conversion set is implemented for fidelity. Invariant culture.
        static string Format(TclInterpreter interp, string[] args)
        {
            if (args.Length < 2)
                throw new TclException("wrong # args: should be \"format formatString ?arg arg ...?\"");
            string fmt = args[1];
            var sb = new StringBuilder();
            int argi = 2, i = 0;

            string NextArg()
            {
                if (argi >= args.Length)
                    throw new TclException("not enough arguments for all format specifiers");
                return args[argi++];
            }

            while (i < fmt.Length)
            {
                char ch = fmt[i++];
                if (ch != '%') { sb.Append(ch); continue; }
                if (i < fmt.Length && fmt[i] == '%') { sb.Append('%'); i++; continue; }

                bool left = false, plus = false, space = false, zero = false, alt = false;
                for (; i < fmt.Length; i++)
                {
                    char f = fmt[i];
                    if (f == '-') left = true;
                    else if (f == '+') plus = true;
                    else if (f == ' ') space = true;
                    else if (f == '0') zero = true;
                    else if (f == '#') alt = true;
                    else break;
                }
                int width = 0;
                if (i < fmt.Length && fmt[i] == '*') { i++; width = (int)ParseFormatInt(NextArg()); }
                else while (i < fmt.Length && char.IsDigit(fmt[i])) width = width * 10 + (fmt[i++] - '0');
                if (width < 0) { left = true; width = -width; }
                int precision = -1;
                if (i < fmt.Length && fmt[i] == '.')
                {
                    i++; precision = 0;
                    if (i < fmt.Length && fmt[i] == '*') { i++; precision = (int)ParseFormatInt(NextArg()); }
                    else while (i < fmt.Length && char.IsDigit(fmt[i])) precision = precision * 10 + (fmt[i++] - '0');
                }
                while (i < fmt.Length && (fmt[i] == 'l' || fmt[i] == 'h' || fmt[i] == 'L')) i++; // size mods ignored
                if (i >= fmt.Length)
                    throw new TclException("format string ended in middle of field specifier");
                char conv = fmt[i++];

                string sign = "";
                string body;
                bool numeric = false;
                switch (conv)
                {
                    case 'd': case 'i':
                    {
                        long v = ParseFormatInt(NextArg());
                        numeric = true;
                        ulong mag = unchecked((ulong)(v < 0 ? -v : v));   // works for long.MinValue too
                        sign = v < 0 ? "-" : plus ? "+" : space ? " " : "";
                        body = mag.ToString(CultureInfo.InvariantCulture);
                        if (precision > body.Length) body = body.PadLeft(precision, '0');
                        break;
                    }
                    case 'u':
                    {
                        numeric = true;
                        body = unchecked((ulong)ParseFormatInt(NextArg())).ToString(CultureInfo.InvariantCulture);
                        if (precision > body.Length) body = body.PadLeft(precision, '0');
                        break;
                    }
                    case 'o':
                    {
                        numeric = true;
                        body = Convert.ToString(ParseFormatInt(NextArg()), 8);
                        if (precision > body.Length) body = body.PadLeft(precision, '0');
                        if (alt && body[0] != '0') body = "0" + body;
                        break;
                    }
                    case 'x': case 'X':
                    {
                        numeric = true;
                        long v = ParseFormatInt(NextArg());
                        body = v.ToString(conv == 'x' ? "x" : "X", CultureInfo.InvariantCulture);
                        if (precision > body.Length) body = body.PadLeft(precision, '0');
                        if (alt && v != 0) body = (conv == 'x' ? "0x" : "0X") + body;
                        break;
                    }
                    case 'c':
                        body = char.ConvertFromUtf32((int)ParseFormatInt(NextArg()));
                        break;
                    case 's':
                        body = NextArg();
                        if (precision >= 0 && body.Length > precision) body = body.Substring(0, precision);
                        break;
                    case 'f': case 'e': case 'E': case 'g': case 'G':
                    {
                        numeric = true;
                        double d = ParseFormatDouble(NextArg());
                        sign = d < 0 ? "-" : plus ? "+" : space ? " " : "";
                        double m = System.Math.Abs(d);
                        int p = precision < 0 ? 6 : precision;
                        if (conv == 'f')
                            body = m.ToString("F" + p, CultureInfo.InvariantCulture);
                        else if (conv == 'e' || conv == 'E')
                        {
                            string pat = p > 0 ? "0." + new string('0', p) + "e+00" : "0e+00";
                            body = m.ToString(pat, CultureInfo.InvariantCulture);
                            if (conv == 'E') body = body.ToUpperInvariant();
                        }
                        else
                        {
                            body = m.ToString("G" + (p == 0 ? 1 : p), CultureInfo.InvariantCulture);
                            body = body.Replace("E", conv == 'g' ? "e" : "E");
                        }
                        break;
                    }
                    default:
                        throw new TclException($"bad field specifier \"{conv}\"");
                }

                // '0' pads between sign and digits; ignored for left-justify and
                // for integers carrying an explicit precision (C/Tcl rule).
                if (zero && !left && numeric && width > sign.Length + body.Length &&
                    (precision < 0 || conv == 'f' || conv == 'e' || conv == 'E' || conv == 'g' || conv == 'G'))
                    body = body.PadLeft(width - sign.Length, '0');
                string field = sign + body;
                if (field.Length < width)
                    field = left ? field.PadRight(width) : field.PadLeft(width);
                sb.Append(field);
            }
            return sb.ToString();
        }

        static long ParseFormatInt(string s)
        {
            s = s.Trim();
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v)) return v;
            if ((s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) &&
                long.TryParse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v)) return v;
            throw new TclException($"expected integer but got \"{s}\"");
        }

        static double ParseFormatDouble(string s)
        {
            if (double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return v;
            throw new TclException($"expected floating-point number but got \"{s}\"");
        }

        // ----- upvar / uplevel / array / switch / source -----

        // Parses optional level arg (a digit-leading int = relative levels up,
        // or "#N" = absolute frame). Advances argi past it. Returns the
        // absolute frame index (0 = global, scopes.Count-1 = current).
        static int ResolveLevelArg(TclInterpreter interp, string[] args, ref int argi, int defaultRelative)
        {
            int relative = defaultRelative;
            bool isAbsolute = false;
            int absolute = 0;
            if (argi < args.Length && args[argi].Length > 0)
            {
                string s = args[argi];
                if (s[0] == '#')
                {
                    isAbsolute = true;
                    absolute = int.Parse(s.Substring(1), CultureInfo.InvariantCulture);
                    argi++;
                }
                else if (char.IsDigit(s[0]) || (s[0] == '-' && s.Length > 1 && char.IsDigit(s[1])))
                {
                    relative = int.Parse(s, CultureInfo.InvariantCulture);
                    argi++;
                }
            }
            int frameIdx = isAbsolute ? absolute : interp.Scopes.Count - 1 - relative;
            if (frameIdx < 0 || frameIdx >= interp.Scopes.Count)
                throw new TclException($"bad level — out of range (got frame {frameIdx} of {interp.Scopes.Count})");
            return frameIdx;
        }

        static string Upvar(TclInterpreter interp, string[] args)
        {
            // upvar ?level? otherVar myVar ?otherVar myVar...?
            int argi = 1;
            int srcFrameIdx = ResolveLevelArg(interp, args, ref argi, defaultRelative: 1);
            if ((args.Length - argi) % 2 != 0)
                throw new TclException("wrong # args for upvar");
            var srcScope = interp.Scopes[srcFrameIdx];

            for (; argi + 1 < args.Length; argi += 2)
            {
                string otherName = args[argi];
                string localName = args[argi + 1];
                // Real alias — reads + writes redirect to (srcScope, otherName).
                interp.SetAlias(localName, srcScope, otherName);
            }
            return "";
        }

        static string Uplevel(TclInterpreter interp, string[] args)
        {
            // uplevel ?level? arg ?arg...?
            int argi = 1;
            int frameIdx = ResolveLevelArg(interp, args, ref argi, defaultRelative: 1);
            if (argi >= args.Length) throw new TclException("wrong # args for uplevel");

            var sb = new StringBuilder();
            for (int i = argi; i < args.Length; i++) { if (i > argi) sb.Append(' '); sb.Append(args[i]); }
            string script = sb.ToString();

            // Swap target frame into the top-of-stack slot for the duration
            // of the script. Eval reads CurrentScope as scopes[^1].
            int topIdx = interp.Scopes.Count - 1;
            var saved = interp.Scopes[topIdx];
            interp.Scopes[topIdx] = interp.Scopes[frameIdx];
            try { return interp.Eval(script); }
            finally { interp.Scopes[topIdx] = saved; }
        }

        static string ArrayCmd(TclInterpreter interp, string[] args)
        {
            // array set name {k1 v1 k2 v2...}
            // array get name
            // array exists name
            // array names name
            // array unset name
            // array size name
            if (args.Length < 3) throw new TclException("wrong # args: \"array option name ...\"");
            string op = args[1];
            string name = args[2];
            switch (op)
            {
                case "set":
                    if (args.Length != 4) throw new TclException("wrong # args for array set");
                    {
                        var items = TclList.Parse(args[3]);
                        for (int i = 0; i + 1 < items.Count; i += 2)
                            interp.SetVar($"{name}({items[i]})", items[i + 1]);
                    }
                    return "";
                case "get":
                    {
                        var sb = new StringBuilder();
                        bool first = true;
                        string prefix = name + "(";
                        foreach (var kv in interp.CurrentScope.Vars)
                        {
                            if (!kv.Key.StartsWith(prefix) || !kv.Key.EndsWith(")")) continue;
                            string idx = kv.Key.Substring(prefix.Length, kv.Key.Length - prefix.Length - 1);
                            if (!first) sb.Append(' ');
                            sb.Append(TclList.FormatElement(idx)).Append(' ').Append(TclList.FormatElement(kv.Value));
                            first = false;
                        }
                        return sb.ToString();
                    }
                case "exists":
                    {
                        string prefix = name + "(";
                        foreach (var key in interp.CurrentScope.Vars.Keys)
                            if (key.StartsWith(prefix)) return "1";
                        return "0";
                    }
                case "names":
                    {
                        var sb = new StringBuilder();
                        bool first = true;
                        string prefix = name + "(";
                        foreach (var key in interp.CurrentScope.Vars.Keys)
                        {
                            if (!key.StartsWith(prefix) || !key.EndsWith(")")) continue;
                            string idx = key.Substring(prefix.Length, key.Length - prefix.Length - 1);
                            if (!first) sb.Append(' ');
                            sb.Append(TclList.FormatElement(idx));
                            first = false;
                        }
                        return sb.ToString();
                    }
                case "size":
                    {
                        int n = 0;
                        string prefix = name + "(";
                        foreach (var key in interp.CurrentScope.Vars.Keys)
                            if (key.StartsWith(prefix) && key.EndsWith(")")) n++;
                        return n.ToString(CultureInfo.InvariantCulture);
                    }
                case "unset":
                    {
                        string prefix = name + "(";
                        var toRemove = new List<string>();
                        foreach (var key in interp.CurrentScope.Vars.Keys)
                            if (key.StartsWith(prefix) && key.EndsWith(")")) toRemove.Add(key);
                        foreach (var k in toRemove) interp.CurrentScope.Vars.Remove(k);
                    }
                    return "";
                default:
                    throw new TclException($"bad option \"{op}\" for array");
            }
        }

        // switch ?opts? value pat body pat body...
        // switch ?opts? value {pat body pat body...}
        static string SwitchCmd(TclInterpreter interp, string[] args)
        {
            if (args.Length < 3) throw new TclException("wrong # args for switch");
            int argi = 1;
            // skip flags like -exact, -glob, -regexp, --
            while (argi < args.Length && args[argi].StartsWith("-"))
            {
                if (args[argi] == "--") { argi++; break; }
                argi++;
            }
            if (argi >= args.Length) throw new TclException("missing value");
            string value = args[argi++];

            // Body can be a single arg with all patterns/bodies as a Tcl list.
            List<string> pairs;
            if (args.Length - argi == 1)
                pairs = TclList.Parse(args[argi]);
            else
            {
                pairs = new List<string>();
                for (int i = argi; i < args.Length; i++) pairs.Add(args[i]);
            }

            for (int i = 0; i + 1 < pairs.Count; i += 2)
            {
                string pat = pairs[i];
                string body = pairs[i + 1];
                bool match = pat == "default" || pat == value;
                if (match)
                {
                    if (body == "-")
                    {
                        // fallthrough — find next non- body
                        for (int j = i + 2; j < pairs.Count; j += 2)
                        {
                            if (pairs[j + 1] != "-") return interp.Eval(pairs[j + 1]);
                        }
                        return "";
                    }
                    return interp.Eval(body);
                }
            }
            return "";
        }

        static string Source(TclInterpreter interp, string[] args)
        {
            if (args.Length != 2) throw new TclException("wrong # args: \"source filename\"");
            string path = args[1];
            if (!System.IO.File.Exists(path))
                throw new TclException($"couldn't read file \"{path}\": no such file");
            // Diggles data is single-byte, locale-dependent (Latin-1 for en/de/…, Windows-1251 for ru).
            string content = Diggles.Runtime.DigglesText.ReadAllText(path);
            return interp.Eval(content);
        }

        // ----- info -----

        static string Info(TclInterpreter interp, string[] args)
        {
            if (args.Length < 2) throw new TclException("wrong # args for info");
            switch (args[1])
            {
                case "exist":     // Tcl accepts unambiguous subcommand prefixes; some scripts write `info exist`
                case "exists":
                    if (args.Length != 3) throw new TclException("wrong # args for info exists");
                    return interp.VarExists(args[2]) ? "1" : "0";
                case "commands":
                case "procs":
                    // not tracked individually yet — return empty list
                    return "";
                default:
                    throw new TclException($"bad option \"{args[1]}\" for info");
            }
        }
    }
}
