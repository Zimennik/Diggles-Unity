#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using System.Text;
    using Diggles.Runtime.Tcl;
    using NUnit.Framework;

    // Pure interpreter tests (no engine integration). Run via
    // Window → General → Test Runner (EditMode tab), or batch:
    // Unity -batchmode -runTests -testPlatform EditMode -testResults...
    public sealed class TclInterpreterTests
    {
        // ----- helpers -----

        static (TclInterpreter, StringBuilder) MakeInterp()
        {
            var interp = new TclInterpreter();
            var captured = new StringBuilder();
            interp.RegisterCommand("puts", (i, args) =>
            {
                int k = 1;
                if (k < args.Length && args[k] == "-nonewline") k++;
                if (k + 1 < args.Length && (args[k] == "stdout" || args[k] == "stderr")) k++;
                if (k < args.Length) { captured.Append(args[k]); captured.Append('\n'); }
                return "";
            });
            return (interp, captured);
        }

        static void AssertPuts(string script, string expected)
        {
            var (interp, captured) = MakeInterp();
            interp.Eval(script);
            Assert.AreEqual(expected.Trim(), captured.ToString().Trim());
        }

        // ----- core language -----

        [Test]
        public void Set_And_Get_Variable()
        {
            var (i, _) = MakeInterp();
            i.Eval("set x 42");
            Assert.AreEqual("42", i.GetVar("x"));
        }

        [Test]
        public void Expr_Arithmetic()
        {
            var (i, _) = MakeInterp();
            Assert.AreEqual("14", i.Eval("expr 3*4 + 2"));
            Assert.AreEqual("4", i.Eval("expr {10 - 3*2}"));
            Assert.AreEqual("2", i.Eval("expr {10 / 5}"));
            Assert.AreEqual("3", i.Eval("expr {10 % 7}"));
        }

        [Test]
        public void Expr_BoolAndCompare()
        {
            var (i, _) = MakeInterp();
            Assert.AreEqual("1", i.Eval("expr {5 > 3}"));
            Assert.AreEqual("0", i.Eval("expr {5 < 3}"));
            Assert.AreEqual("1", i.Eval("expr {1 && 1}"));
            Assert.AreEqual("0", i.Eval("expr {0 || 0}"));
            Assert.AreEqual("1", i.Eval("expr {1 == 1}"));
            Assert.AreEqual("1", i.Eval("expr {\"foo\" == \"foo\"}"));
        }

        [Test]
        public void Proc_And_Return()
        {
            AssertPuts(@"
                proc add {a b} { return [expr {$a + $b}] }
                puts [add 10 5]", "15");
        }

        [Test]
        public void If_Elseif_Else()
        {
            AssertPuts(@"
                set y 15
                if {$y > 20} { puts ""big"" } elseif {$y > 10} { puts ""mid"" } else { puts ""small"" }",
                "mid");
        }

        [Test]
        public void While_And_Incr()
        {
            AssertPuts(@"
                set i 0
                set sum 0
                while {$i < 5} {
                    set sum [expr {$sum + $i}]
                    incr i
                }
                puts $sum", "10");
        }

        [Test]
        public void For_Loop()
        {
            AssertPuts(@"
                set sum 0
                for {set i 1} {$i <= 4} {incr i} {
                    set sum [expr {$sum + $i}]
                }
                puts $sum", "10");
        }

        [Test]
        public void Foreach_Iterates_List()
        {
            AssertPuts(@"
                foreach x {alpha beta gamma} { puts $x }",
                "alpha\nbeta\ngamma");
        }

        [Test]
        public void Break_Exits_Loop()
        {
            AssertPuts(@"
                set i 0
                while {1} {
                    if {$i >= 3} { break }
                    puts $i
                    incr i
                }", "0\n1\n2");
        }

        [Test]
        public void Continue_Skips_Iteration()
        {
            AssertPuts(@"
                foreach x {1 2 3 4 5} {
                    if {$x == 3} { continue }
                    puts $x
                }", "1\n2\n4\n5");
        }

        [Test]
        public void Catch_Returns_1_On_Error()
        {
            var (i, _) = MakeInterp();
            Assert.AreEqual("0", i.Eval("catch { set x 5 } msg"));
            Assert.AreEqual("1", i.Eval("catch { error boom } msg"));
            Assert.AreEqual("boom", i.GetVar("msg"));
        }

        // ----- substitution -----

        [Test]
        public void DollarVar_Subst()
        {
            AssertPuts(@"
                set name world
                puts ""hello $name""", "hello world");
        }

        [Test]
        public void BraceVar_Subst()
        {
            AssertPuts(@"
                set greet hi
                puts ""${greet} there""", "hi there");
        }

        [Test]
        public void BracketCmd_Subst()
        {
            AssertPuts(@"
                proc doubled {n} { return [expr {$n * 2}] }
                puts ""result = [doubled 7]""", "result = 14");
        }

        [Test]
        public void Backslash_Escape_In_Quoted_String()
        {
            AssertPuts(@"
                puts ""line1\nline2""", "line1\nline2");
        }

        [Test]
        public void Braces_Disable_Subst()
        {
            AssertPuts(@"
                set x 5
                puts {literal $x}", "literal $x");
        }

        // ----- list ops -----

        [Test]
        public void List_Lindex_Llength_Lrange()
        {
            var (i, _) = MakeInterp();
            Assert.AreEqual("a b c", i.Eval("list a b c"));
            Assert.AreEqual("3", i.Eval("llength [list a b c]"));
            Assert.AreEqual("b", i.Eval("lindex [list a b c] 1"));
            Assert.AreEqual("c", i.Eval("lindex [list a b c] end"));
            Assert.AreEqual("b c", i.Eval("lrange [list a b c d] 1 2"));
        }

        [Test]
        public void Lappend_Builds_List()
        {
            var (i, _) = MakeInterp();
            i.Eval("set L {}; lappend L a; lappend L b; lappend L c");
            Assert.AreEqual("a b c", i.GetVar("L"));
        }

        // ----- string ops -----

        [Test]
        public void String_Length_Range_Upper()
        {
            var (i, _) = MakeInterp();
            Assert.AreEqual("5", i.Eval("string length hello"));
            Assert.AreEqual("ell", i.Eval("string range hello 1 3"));
            Assert.AreEqual("HELLO", i.Eval("string toupper hello"));
            Assert.AreEqual("1", i.Eval("string equal abc abc"));
            Assert.AreEqual("0", i.Eval("string equal abc xyz"));
            Assert.AreEqual("1", i.Eval("string match h*o hello"));
        }

        [Test]
        public void Scan_Hex_Decimal_Inline()
        {
            var (i, _) = MakeInterp();
            // The dwarf clothing idiom: a 2-hex-digit code → per-submesh variant index.
            i.Eval("set clothing AB");
            Assert.AreEqual("10", i.Eval("scan [string index $clothing 0] %x"));
            Assert.AreEqual("11", i.Eval("scan [string index $clothing 1] %x"));
            Assert.AreEqual("4", i.Eval("scan 4 %x"));
            Assert.AreEqual("255", i.Eval("scan ff %x"));
            // Decimal + multi-conversion inline form returns a list.
            Assert.AreEqual("42", i.Eval("scan 42 %d"));
            Assert.AreEqual("3 4", i.Eval("scan {3 4} {%d %d}"));
        }

        [Test]
        public void Scan_Into_Vars_ReturnsCount()
        {
            var (i, _) = MakeInterp();
            Assert.AreEqual("2", i.Eval("scan {12 34} {%d %d} a b"));
            Assert.AreEqual("12", i.GetVar("a"));
            Assert.AreEqual("34", i.GetVar("b"));
        }

        // ----- array -----

        [Test]
        public void Array_Set_Size_Names_Exists()
        {
            var (i, _) = MakeInterp();
            i.Eval("array set colors {red 1 green 2 blue 3}");
            Assert.AreEqual("3", i.Eval("array size colors"));
            Assert.AreEqual("1", i.Eval("array exists colors"));
            Assert.AreEqual("0", i.Eval("array exists missing"));
        }

        [Test]
        public void Array_Element_Access_Via_Subst()
        {
            var (i, _) = MakeInterp();
            i.Eval("array set m {alpha A beta B}");
            Assert.AreEqual("A", i.Eval("set k alpha; set m($k)"));
        }

        // ----- switch -----

        [Test]
        public void Switch_Matches_Default()
        {
            AssertPuts(@"
                proc kind x {
                    switch $x { a { return alpha } b { return beta } default { return other } }
                }
                puts [kind a]
                puts [kind b]
                puts [kind z]", "alpha\nbeta\nother");
        }

        // ----- upvar / global -----

        [Test]
        public void Upvar_Real_Alias_WriteBack()
        {
            var (i, _) = MakeInterp();
            i.Eval(@"
                proc setit {varname value} {
                    upvar 1 $varname v
                    set v $value
                }
                setit myvar hello");
            Assert.AreEqual("hello", i.GetVar("myvar"));
        }

        [Test]
        public void Global_Real_Alias_WriteBack()
        {
            var (i, _) = MakeInterp();
            i.Eval(@"
                set g 0
                proc bump {} {
                    global g
                    set g [expr {$g + 1}]
                }
                bump; bump; bump");
            Assert.AreEqual("3", i.GetVar("g"));
        }

        // ----- parser quirks -----

        [Test]
        public void DoubleSlash_Comment_Skipped()
        {
            AssertPuts(@"
                // this is a comment
                puts ""hello""", "hello");
        }

        [Test]
        public void Hash_Comment_After_Semicolon()
        {
            AssertPuts(@"
                set x 1 ;# inline comment
                puts $x", "1");
        }

        [Test]
        public void CRLF_Line_Endings_Tolerated()
        {
            // Real Diggles files are CRLF. Construct a CRLF script
            // and make sure parser doesn't choke on extra \r.
            string crlf = "proc add {a b} {\r\n\treturn [expr {$a + $b}]\r\n}\r\nputs [add 2 3]";
            var (interp, captured) = MakeInterp();
            interp.Eval(crlf);
            Assert.AreEqual("5", captured.ToString().Trim());
        }

        [Test]
        public void Proc_Empty_Args()
        {
            var (i, _) = MakeInterp();
            i.Eval("proc greet {} { return hello }");
            Assert.AreEqual("hello", i.Eval("greet"));
        }

        [Test]
        public void Proc_With_Default_Arg()
        {
            var (i, _) = MakeInterp();
            i.Eval(@"proc say {msg {prefix tcl}} { return ""$prefix: $msg"" }");
            Assert.AreEqual("tcl: hi", i.Eval("say hi"));
            Assert.AreEqual("ZZ: hi", i.Eval("say hi ZZ"));
        }

        [Test]
        public void Unknown_Command_Stubbed_When_Handler_Set()
        {
            var (i, _) = MakeInterp();
            int called = 0;
            i.UnknownCommandHandler = (_, args) => { called++; return "0"; };
            // Unknown command no longer throws — and returns the stub value.
            Assert.AreEqual("0", i.Eval("foobar 1 2 3"));
            Assert.AreEqual(1, called);
        }

        [Test]
        public void Unknown_Command_Throws_By_Default()
        {
            var (i, _) = MakeInterp();
            Assert.Throws<TclException>(() => i.Eval("foobar"));
        }

        // ----- nested substitution -----

        [Test]
        public void Nested_Bracket_Subst()
        {
            var (i, _) = MakeInterp();
            i.Eval("proc inc x { return [expr {$x + 1}] }");
            Assert.AreEqual("4", i.Eval("expr {[inc [inc 2]]}"));
        }

        [Test]
        public void Append_Concatenates_To_Var()
        {
            var (i, _) = MakeInterp();
            i.Eval("set x hello");
            Assert.AreEqual("hello world", i.Eval("append x { world}"));
            Assert.AreEqual("hello world!", i.Eval("append x !"));
        }

        [Test]
        public void LSearch_Finds_Or_Returns_MinusOne()
        {
            var (i, _) = MakeInterp();
            Assert.AreEqual("2", i.Eval("lsearch {a b c d} c"));
            Assert.AreEqual("-1", i.Eval("lsearch {a b c d} z"));
            Assert.AreEqual("3", i.Eval("lsearch -glob {a ab abc abcd} ab??"));
            Assert.AreEqual("0 2", i.Eval("lsearch -all {a b a c} a"));
        }

        [Test]
        public void LReplace_Replaces_Range()
        {
            var (i, _) = MakeInterp();
            Assert.AreEqual("a X Y d", i.Eval("lreplace {a b c d} 1 2 X Y"));
            Assert.AreEqual("a d", i.Eval("lreplace {a b c d} 1 2"));
        }

        [Test]
        public void LSort_Sorts_Various_Modes()
        {
            var (i, _) = MakeInterp();
            Assert.AreEqual("1 10 2 3",  i.Eval("lsort {3 1 10 2}"));               // ascii (default)
            Assert.AreEqual("1 2 3 10",  i.Eval("lsort -integer {3 1 10 2}"));
            Assert.AreEqual("10 3 2 1",  i.Eval("lsort -integer -decreasing {3 1 10 2}"));
            Assert.AreEqual("a b c",     i.Eval("lsort -unique {b a c a b}"));
        }

        [Test]
        public void Subst_Performs_All_Substitutions()
        {
            var (i, _) = MakeInterp();
            i.Eval("set x 42");
            i.Eval("proc dub n { return [expr {$n * 2}] }");
            Assert.AreEqual("42 and 84", i.Eval("subst {$x and [dub $x]}"));
        }

        [Test]
        public void Recursion_Factorial()
        {
            var (i, _) = MakeInterp();
            i.Eval(@"
                proc fact n {
                    if {$n <= 1} { return 1 }
                    return [expr {$n * [fact [expr {$n - 1}]]}]
                }");
            Assert.AreEqual("120", i.Eval("fact 5"));
            Assert.AreEqual("720", i.Eval("fact 6"));
        }
    }
}
