#nullable enable

namespace Diggles.Tests.Editor
{
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;

    // The object-scope model: each entity's per-instance variables behave like its
    // own interpreter globals — `global x` inside obj_init / method / state binds to
    // THAT entity's scope, so two instances don't clobber each other. Genuine shared
    // interpreter-globals stay readable inside bodies (two-tier fallback), and
    // ref_get/ref_set reach another object's per-instance vars.
    public class TclObjectScopeTests
    {
        static (TclInterpreter, DigglesTclContext) Host()
        {
            var i = new TclInterpreter();
            var ctx = DigglesTclEngine.Register(i);
            return (i, ctx);
        }

        static string New(TclInterpreter i, string cls)
            => i.Eval($"new {cls} \"\" \"0 0 0\" \"0 0 0\"").Trim();

        [Test]
        public void GlobalInMethodIsPerInstance_NotShared()
        {
            var (i, _) = Host();
            i.Eval(@"def_class Thing none thing 0 {} {
                obj_init { global n; set n 0 }
                method bump {} { global n; set n [expr {$n + 1}]; return $n }
                method getn {} { global n; return $n }
            }");
            var a = New(i, "Thing");
            var b = New(i, "Thing");

            i.Eval($"call_method {a} bump");
            i.Eval($"call_method {a} bump");   // a.n = 2
            i.Eval($"call_method {b} bump");   // b.n = 1

            // Each instance keeps its OWN n — `global` did not write a shared slot.
            Assert.AreEqual("2", i.Eval($"call_method {a} getn"));
            Assert.AreEqual("1", i.Eval($"call_method {b} getn"));
        }

        [Test]
        public void BareSetInObjInitIsPerInstance()
        {
            var (i, _) = Host();
            i.Eval(@"def_class T2 none thing 0 {} {
                obj_init { set v 0 }
                method setv {x} { set v $x }
                method getv {} { return $v }
            }");
            var a = New(i, "T2");
            var b = New(i, "T2");
            i.Eval($"call_method {a} setv 7");
            i.Eval($"call_method {b} setv 9");
            Assert.AreEqual("7", i.Eval($"call_method {a} getv"));
            Assert.AreEqual("9", i.Eval($"call_method {b} getv"));
        }

        [Test]
        public void GenuineSharedGlobalStaysReadableInsideBody()
        {
            var (i, _) = Host();
            i.Eval("set shared_const 42");   // top-level → real interpreter global
            i.Eval(@"def_class Reader none thing 0 {} {
                method rd {} { return $shared_const }
            }");
            var a = New(i, "Reader");
            Assert.AreEqual("42", i.Eval($"call_method {a} rd"));
        }

        [Test]
        public void RefGetSet_ReachAnotherObjectsVars()
        {
            var (i, _) = Host();
            i.Eval(@"def_class R none thing 0 {} { obj_init { set hp 100 } }");
            var a = New(i, "R");
            Assert.AreEqual("100", i.Eval($"ref_get {a} hp"));
            i.Eval($"ref_set {a} hp 55");
            Assert.AreEqual("55", i.Eval($"ref_get {a} hp"));
        }
    }
}
