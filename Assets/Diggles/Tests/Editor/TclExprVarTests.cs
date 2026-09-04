#nullable enable

namespace Diggles.Tests.Editor
{
    using Diggles.Runtime.Tcl;
    using NUnit.Framework;

    // expr resolves $var / [cmd] as VALUE operands (tokenized raw, not pre-substituted
    // into the text). This preserves empty-string and multi-word values — `$x==""`
    // with x="" must be true, not a dangling-operator parse error (which sent the
    // gnome idle state down the wrong branch → spin).
    public class TclExprVarTests
    {
        static TclInterpreter With(params (string k, string v)[] vars)
        {
            var i = new TclInterpreter();
            foreach (var (k, v) in vars) i.SetVar(k, v);
            return i;
        }

        [Test] public void EmptyVarEqualsEmptyString()
            => Assert.AreEqual("1", With(("x", "")).Eval("expr {$x==\"\"}"));

        [Test] public void NonEmptyVarNotEqualEmpty()
            => Assert.AreEqual("0", With(("x", "foo")).Eval("expr {$x==\"\"}"));

        [Test] public void MultiWordStringCompare()
            => Assert.AreEqual("1", With(("x", "foo bar")).Eval("expr {$x==\"foo bar\"}"));

        [Test] public void NumericVarArithmetic()
            => Assert.AreEqual("6", With(("x", "5")).Eval("expr {$x+1}"));

        [Test] public void VarLessThanVar()
            => Assert.AreEqual("1", With(("a", "3"), ("b", "4")).Eval("expr {$a<$b}"));

        [Test] public void StringVarSwitchValue()  // the current_plan=="work" idiom
            => Assert.AreEqual("1", With(("plan", "work")).Eval("expr {$plan==\"work\"}"));

        [Test] public void CommandSubstitutionAsOperand()
            => Assert.AreEqual("7", new TclInterpreter().Eval("expr {[set tmp 7] + 0}"));

        [Test] public void EmptyVarInArithmeticDoesNotThrow()
        {
            var i = With(("x", ""));
            Assert.AreEqual("1", i.Eval("expr {$x+1}"));   // empty coerces to 0
        }

        // ---- Tcl 8.3 INTEGER division/modulo (floored, % takes the divisor's sign) ----
        // Division always going double made z_spare_talk.tcl:574's
        // `incr talk_current_length [expr {(...)/10+1}]` receive "4.7" →
        // "[work-chain BROKE] … expected integer but got 4.7".

        [Test] public void IntDivisionStaysInteger()
            => Assert.AreEqual("4", new TclInterpreter().Eval("expr {(28+9)/10+1}"));

        [Test] public void IntDivisionFloorsTowardNegInfinity()
            => Assert.AreEqual("-4", new TclInterpreter().Eval("expr {-7/2}"));

        [Test] public void IntModuloTakesDivisorSign()
        {
            var i = new TclInterpreter();
            Assert.AreEqual("2", i.Eval("expr {-7%3}"));
            Assert.AreEqual("-2", i.Eval("expr {7%-3}"));
        }

        [Test] public void MixedDivisionStaysDouble()
            => Assert.AreEqual("3.5", new TclInterpreter().Eval("expr {7/2.0}"));

        [Test] public void IncrWithIntDivisionResult()
        {
            var i = With(("n", "3"));
            i.Eval("incr n [expr {37/10+1}]");
            Assert.AreEqual("7", i.Eval("set n"));
        }
    }
}
