#nullable enable

namespace Diggles.Tests.Editor
{
    using Diggles.Runtime.Tcl;
    using NUnit.Framework;

    // Bitwise operators in `expr`. The sparetime code iterates bitmasks with
    // `set i [expr {$i<<1}]` and `if {$i & $mask}`; without these operators the shift
    // returned 0 and the loop never terminated → hard editor freeze.
    public class TclExprBitwiseTests
    {
        static string E(string expr)
        {
            var i = new TclInterpreter();
            return i.Eval($"expr {{{expr}}}");
        }

        [Test] public void ShiftLeft()  => Assert.AreEqual("8", E("1 << 3"));
        [Test] public void ShiftRight() => Assert.AreEqual("2", E("16 >> 3"));
        [Test] public void BitAnd()     => Assert.AreEqual("4", E("6 & 12"));
        [Test] public void BitOr()      => Assert.AreEqual("14", E("6 | 12"));
        [Test] public void BitXor()     => Assert.AreEqual("10", E("6 ^ 12"));
        [Test] public void BitNot()     => Assert.AreEqual("-1", E("~0"));

        [Test]
        public void Precedence_ShiftBelowAddAboveCompare()
        {
            // 1+1 << 2 → (2)<<2 = 8 (shift below additive)
            Assert.AreEqual("8", E("1 + 1 << 2"));
            // 1 << 2 == 4 → (4)==4 = 1 (shift above equality)
            Assert.AreEqual("1", E("1 << 2 == 4"));
        }

        [Test]
        public void BitMaskIterationTerminates()
        {
            // The exact pattern from the sparetime code — must advance, not loop.
            var i = new TclInterpreter();
            i.Eval("set i 1; set n 0; while {$i <= 256} { incr n; set i [expr {$i << 1}] }");
            Assert.AreEqual("9", i.GetVar("n"));   // 1,2,4,...,256 → 9 iterations
        }
    }
}
