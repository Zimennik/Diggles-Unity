#nullable enable

namespace Diggles.Tests.Editor
{
    using Diggles.Runtime.Stats;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;

    // The stat/attribute system: def_attrib declares range+default+category; values
    // are per-entity, clamped, and read as their declared default when unset (lazy —
    // a freshly spawned dwarf already "has" Hitpoints 1.0, so it doesn't insta-die).
    public class StatSystemTests
    {
        // ---- pure C# layer ----

        [Test]
        public void LazyDefault_ClampAndCategory()
        {
            var svc = new StatService();
            svc.Define("atr_Hitpoints", 0, 1, 1, "Gesundheit");
            svc.Define("exp_Holz", 0, 1, 0, "Holz");
            var v = new StatValues();

            Assert.AreEqual(1.0, svc.Get(v, "atr_Hitpoints"), "unset reads the declared default");
            Assert.AreEqual(1.0, svc.Set(v, "atr_Hitpoints", 5), "set clamps to max");
            Assert.AreEqual(0.0, svc.Add(v, "atr_Hitpoints", -5), "add clamps to min");

            Assert.AreEqual(StatCategory.Vital, StatDef.Classify("atr_Hitpoints"));
            Assert.AreEqual(StatCategory.Skill, StatDef.Classify("exp_Holz"));
            Assert.AreEqual(StatCategory.Generic, StatDef.Classify("GnomeAge"));
            Assert.IsInstanceOf<SkillSystem>(svc.SystemFor("exp_Holz"));
            Assert.IsInstanceOf<AttributeSystem>(svc.SystemFor("atr_Hitpoints"));
        }

        // ---- through the Tcl bridge (the path the campaign actually uses) ----

        [Test]
        public void TclBridge_DefDefaultClamp()
        {
            var i = new TclInterpreter();
            DigglesTclEngine.Register(i);
            i.Eval("def_attrib atr_Hitpoints 0 1 1 Gesundheit");
            i.Eval(@"def_class Critter none thing 0 {} { }");
            var h = i.Eval("new Critter \"\" \"0 0 0\" \"0 0 0\"").Trim();

            // Never set → reads the declared default (this is what stops insta-death).
            Assert.AreEqual("1", i.Eval($"get_attrib {h} atr_Hitpoints"));
            // set above max clamps down …
            i.Eval($"set_attrib {h} atr_Hitpoints 5");
            Assert.AreEqual("1", i.Eval($"get_attrib {h} atr_Hitpoints"));
            // … add reduces and stays clamped at the floor.
            i.Eval($"add_attrib {h} atr_Hitpoints -0.3");
            Assert.AreEqual("0.7", i.Eval($"get_attrib {h} atr_Hitpoints"));
            i.Eval($"add_attrib {h} atr_Hitpoints -5");
            Assert.AreEqual("0", i.Eval($"get_attrib {h} atr_Hitpoints"));
        }
    }
}
