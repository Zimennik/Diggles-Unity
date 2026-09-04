#nullable enable

namespace Diggles.Tests
{
    using Diggles.Runtime.Tcl;
    using NUnit.Framework;

    // builtin fixes, each grounded in an original-data call site:
    // lsort -index (+ -real/-integer/-decreasing) → sparetime/occupation engine
    // linsert → schreinerei/bar prod action lists
    // format (real C-style sprintf, %4i / %010o) → std_ai log + prodfill serial
    // string repeat → owner dig-layer attrib names
    // lsearch default mode = -glob → Skirmish.tcl enummap lookup
    // lreplace multi-element replace / tail delete → schreinerei / std_ai
    public sealed class TclBuiltinsTests
    {
        static TclInterpreter MakeInterp() => new TclInterpreter();

        // ----- lsort -index (sparetime engine) -----

        // Exact call shape of z_spare_main.tcl:422 — the occupation-emergency sort
        // that runs every sparetime tick. Default compare is ASCII (real Tcl),
        // which the script relies on because all attribs are "0.xx"-shaped.
        [Test]
        public void Lsort_Index_Ascii_SparetimeEmergencies_ExactCallShape()
        {
            var i = MakeInterp();
            i.Eval("set at_Nu 0.43");
            i.Eval("set at_Al 0.81");
            i.Eval("set at_Fun 0.27");
            string r = i.Eval("lsort -index 1 [list \"eat $at_Nu\" \"slp $at_Al\" \"fun $at_Fun\"]");
            Assert.AreEqual("{fun 0.27} {eat 0.43} {slp 0.81}", r);
        }

        // z_spare_fun.tcl:508 — pick the best-rated sparetime place:
        // return [lindex [lsort -index 1 -real $qplist] 0]
        [Test]
        public void Lsort_Index_Real_PicksNumericBest()
        {
            var i = MakeInterp();
            i.Eval("set qplist {}");
            i.Eval("lappend qplist [list place1 10.5 dorf 0 4]");
            i.Eval("lappend qplist [list place2 9.25 dorf 0 4]");
            i.Eval("lappend qplist [list place3 11.0 dorf 0 4]");
            // ASCII would put "10.5" before "9.25"; -real must not.
            Assert.AreEqual("place2 9.25 dorf 0 4", i.Eval("lindex [lsort -index 1 -real $qplist] 0"));
        }

        // z_spare_talk.tcl:147 flag order: -index 1 -decreasing -real
        [Test]
        public void Lsort_Index_Decreasing_Real_FlagOrderA()
        {
            var i = MakeInterp();
            Assert.AreEqual("{g2 1.5} {g1 0.5} {g3 0.25}",
                i.Eval("lsort -index 1 -decreasing -real {{g1 0.5} {g2 1.5} {g3 0.25}}"));
        }

        // std_ai.tcl:2061 flag order: -real -decreasing -index 1
        [Test]
        public void Lsort_Real_Decreasing_Index_FlagOrderB()
        {
            var i = MakeInterp();
            Assert.AreEqual("{s1 12.0} {s2 9.5} {s3 2.0}",
                i.Eval("lsort -real -decreasing -index 1 {{s3 2.0} {s1 12.0} {s2 9.5}}"));
        }

        // std_ai.tcl:2023: set allowed [lsort -integer -index 1 $allowed]
        [Test]
        public void Lsort_Integer_Index()
        {
            var i = MakeInterp();
            Assert.AreEqual("{c 2} {b 9} {a 10}",
                i.Eval("lsort -integer -index 1 {{a 10} {b 9} {c 2}}"));
        }

        // std_ai.tcl:1506 / brut.tcl:339 sort on index 2.
        [Test]
        public void Lsort_Index2_Real_Decreasing()
        {
            var i = MakeInterp();
            Assert.AreEqual("{m2 x 1.5} {m1 x 0.5} {m3 x 0.25}",
                i.Eval("lsort -index 2 -real -decreasing {{m1 x 0.5} {m2 x 1.5} {m3 x 0.25}}"));
        }

        // The Tcl gotcha the emergency sort depends on: without -real/-integer
        // the keys compare as ASCII strings, so "10" sorts before "9".
        [Test]
        public void Lsort_Index_DefaultIsAscii_NotNumeric()
        {
            var i = MakeInterp();
            Assert.AreEqual("{a 10} {b 9}", i.Eval("lsort -index 1 {{b 9} {a 10}}"));
        }

        // Trigger_Tutorial.tcl:1246 writes "-int" — Tcl resolves unique option prefixes.
        [Test]
        public void Lsort_IntegerOptionPrefixAbbreviation()
        {
            var i = MakeInterp();
            Assert.AreEqual("1 2 3 10", i.Eval("lsort -int {3 1 10 2}"));
        }

        // Tcl lsort is a stable mergesort: equal keys keep input order. Matters for
        // equal-rated candidates in the sparetime/dispatcher lists.
        [Test]
        public void Lsort_Stable_EqualKeysKeepInputOrder()
        {
            var i = MakeInterp();
            Assert.AreEqual("{c 0.5} {a 1.0} {b 1.0}",
                i.Eval("lsort -index 1 -real {{a 1.0} {b 1.0} {c 0.5}}"));
            Assert.AreEqual("{a 1.0} {b 1.0} {c 0.5}",
                i.Eval("lsort -index 1 -real -decreasing {{a 1.0} {b 1.0} {c 0.5}}"));
        }

        [Test]
        public void Lsort_Index_MissingElement_Throws()
        {
            var i = MakeInterp();
            Assert.Throws<TclException>(() => i.Eval("lsort -index 3 {{a b}}"));
        }

        // ----- linsert (schreinerei / bar action lists) -----

        [Test]
        public void LInsert_Middle()
        {
            var i = MakeInterp();
            Assert.AreEqual("a b X c d", i.Eval("linsert {a b c d} 2 X"));
        }

        [Test]
        public void LInsert_MultipleElements()
        {
            var i = MakeInterp();
            Assert.AreEqual("a X Y b", i.Eval("linsert {a b} 1 X Y"));
        }

        [Test]
        public void LInsert_EndAndEndRelative()
        {
            var i = MakeInterp();
            Assert.AreEqual("a b c X", i.Eval("linsert {a b c} end X"));
            Assert.AreEqual("a b X c", i.Eval("linsert {a b c} end-1 X"));
        }

        [Test]
        public void LInsert_OutOfRangeClamps()
        {
            var i = MakeInterp();
            Assert.AreEqual("a b X", i.Eval("linsert {a b} 10 X"));
            Assert.AreEqual("X a b", i.Eval("linsert {a b} -1 X"));
        }

        // bar.tcl:312-313 — splice the chair-order relief + bore anim into the
        // sitchairloop run.
        [Test]
        public void LInsert_BarSitchairActionList()
        {
            var i = MakeInterp();
            i.Eval("set rlst {}");
            i.Eval("for {set k 0} {$k<5} {incr k} { lappend rlst \"play_anim sitchairloop\" }");
            i.Eval("set rlst [linsert $rlst 4 \"sparetime_place_relief sitchairorder 0.05\"]");
            i.Eval("set rlst [linsert $rlst 2 \"play_anim sitchairbore\"]");
            Assert.AreEqual("7", i.Eval("llength $rlst"));
            Assert.AreEqual("play_anim sitchairbore", i.Eval("lindex $rlst 2"));
            Assert.AreEqual("sparetime_place_relief sitchairorder 0.05", i.Eval("lindex $rlst 5"));
            Assert.AreEqual("play_anim sitchairloop", i.Eval("lindex $rlst 6"));
        }

        // schreinerei.tcl:341 — set rlst [linsert $rlst $where "prod_anim hammeraccidenta"]
        [Test]
        public void LInsert_SchreinereiHammerAccident()
        {
            var i = MakeInterp();
            i.Eval("set rlst [list \"prod_anim hammerstarta\" \"prod_anim hammerloopmetall\" \"prod_anim hammerend\"]");
            i.Eval("set where 1");
            i.Eval("set rlst [linsert $rlst $where \"prod_anim hammeraccidenta\"]");
            Assert.AreEqual("4", i.Eval("llength $rlst"));
            Assert.AreEqual("prod_anim hammeraccidenta", i.Eval("lindex $rlst 1"));
            Assert.AreEqual("prod_anim hammerend", i.Eval("lindex $rlst 3"));
        }

        // ----- format -----

        // std_ai.tcl:3066: append nextline [format "%4i" $entry]
        [Test]
        public void Format_WidthInteger_StdAiTable()
        {
            var i = MakeInterp();
            Assert.AreEqual("   7", i.Eval("format \"%4i\" 7"));
            Assert.AreEqual("12345", i.Eval("format \"%4i\" 12345"));
            Assert.AreEqual(" -42", i.Eval("format \"%4i\" -42"));
        }

        // z_work_prodfill.tcl:210: set rnd [format "%010o" [irandom 1073741824]]
        [Test]
        public void Format_OctalZeroPad_ProdfillSerial()
        {
            var i = MakeInterp();
            Assert.AreEqual("0000000123", i.Eval("format \"%010o\" 83"));
            Assert.AreEqual("7777777777", i.Eval("format \"%010o\" 1073741823"));
            Assert.AreEqual("0000000000", i.Eval("format \"%010o\" 0"));
        }

        [Test]
        public void Format_CommonConversions()
        {
            var i = MakeInterp();
            Assert.AreEqual("hi 5", i.Eval("format \"%s %d\" hi 5"));
            Assert.AreEqual("ff", i.Eval("format %x 255"));
            Assert.AreEqual("FF", i.Eval("format %X 255"));
            Assert.AreEqual("A", i.Eval("format %c 65"));
            Assert.AreEqual("50%", i.Eval("format \"%d%%\" 50"));
            Assert.AreEqual("3.14", i.Eval("format \"%.2f\" 3.14159"));
            Assert.AreEqual("  3.1", i.Eval("format \"%5.1f\" 3.14159"));
            Assert.AreEqual("7   |", i.Eval("format \"%-4d|\" 7"));
            Assert.AreEqual("007", i.Eval("format \"%03d\" 7"));
            Assert.AreEqual("ab", i.Eval("format \"%.2s\" abcdef"));
        }

        [Test]
        public void Format_NotEnoughArgs_Throws()
        {
            var i = MakeInterp();
            Assert.Throws<TclException>(() => i.Eval("format %d"));
        }

        // ----- string repeat -----

        // z_work_common.tcl:13 — per-owner dig-layer attrib names: ${type}[string repeat "_" $i]
        [Test]
        public void StringRepeat_OwnerDigLayerNames()
        {
            var i = MakeInterp();
            i.Eval("set type wand");
            i.Eval("set names {}");
            i.Eval("for {set k 0} {$k<3} {incr k} { lappend names ${type}[string repeat \"_\" $k] }");
            Assert.AreEqual("wand wand_ wand__", i.Eval("set names"));
        }

        // std_ai.tcl:1275 / 2559 — map seeding with "0 " runs.
        [Test]
        public void StringRepeat_MapSeed()
        {
            var i = MakeInterp();
            Assert.AreEqual("0 0 0 0", i.Eval("string trim [string repeat \"0 \" 4]"));
            Assert.AreEqual("", i.Eval("string repeat ab 0"));
            Assert.AreEqual("ababab", i.Eval("string repeat ab 3"));
        }

        // ----- lsearch default mode = glob -----

        // Skirmish.tcl:212: set newindex [lsearch $enummap "$eName *"] — relies on
        // real Tcl's default -glob matching.
        [Test]
        public void LSearch_DefaultGlob_SkirmishEnummap()
        {
            var i = MakeInterp();
            i.Eval("set enummap {}");
            i.Eval("lappend enummap [list eAct {ACT_A 0 ACT_B 1}]");
            i.Eval("lappend enummap [list eDir {N 0 S 1}]");
            i.Eval("set eName eDir");
            Assert.AreEqual("1", i.Eval("lsearch $enummap \"$eName *\""));
            Assert.AreEqual("-1", i.Eval("lsearch $enummap \"eXyz *\""));
        }

        [Test]
        public void LSearch_ExactFlagStillLiteral()
        {
            var i = MakeInterp();
            Assert.AreEqual("1", i.Eval("lsearch -exact {ab a* b} a*"));
            Assert.AreEqual("0", i.Eval("lsearch {ab a* b} a*"));   // default glob matches "ab"
        }

        // ----- lreplace -----

        // schreinerei.tcl:336 — replace one slot with three elements.
        [Test]
        public void LReplace_MultiElementReplace()
        {
            var i = MakeInterp();
            Assert.AreEqual("h1 s1 s2 s3 h3", i.Eval("lreplace {h1 h2 h3} 1 1 s1 s2 s3"));
        }

        // std_ai.tcl:3232 (lreplace $entry 1 4 — tail delete) and
        // Trigger_Tutorial.tcl:221 (lreplace $tocheck end end — pop last).
        [Test]
        public void LReplace_DeleteRanges()
        {
            var i = MakeInterp();
            Assert.AreEqual("Holz 5", i.Eval("lreplace {Holz 1 2 3 4 5} 1 4"));
            Assert.AreEqual("a b", i.Eval("lreplace {a b c} end end"));
            Assert.AreEqual("a c", i.Eval("lreplace {a b c} 1 1"));
        }
    }
}
