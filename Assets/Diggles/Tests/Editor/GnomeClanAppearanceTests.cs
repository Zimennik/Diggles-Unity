#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;

    // Per-clan gnome appearance — the original's owner→race→clothing/hat law, end to end.
    //
    // Ground truth (unmodified scripts):
    // • zwerg.tcl:40 — every dwarf constructs with `set hatcolor -1`.
    // • z_methods.tcl init (the t=3 timer):
    //:194-199 campaign (is_storymgr) → is_campaign=1, hatcolor=0;
    //:200-204 is_human = ([get_objownertype this] != 2) — 2 = AI player
    // (ENGINE_TCL_API.txt:518 "0=none 1=human 2=ai");
    //:292 clan = [get_objrace this] — the race of the object's owner
    // (no set_objrace exists; set_ownerrace <owner> <race> feeds it —
    // CampaignInit.tcl:15-19 / Skirmish.tcl:50-54 map owners 0..4 → races 0..4);
    //:294 clanname = lindex {"" Voodoo_ Knocker_ Brain_ Vampir_} clan;
    //:297 hatcolor==-1 (i.e. NOT campaign/tutorial) → [get_player_color own]+1;
    //:299-348 the ${gender}${clan} switch — clothing variant lists per clan
    // (male0 {00 11 22 33 44}, male1/female1 {55 66}, male2 {77 88},
    // female2 {77 87}, male3 {99 9A}, male4 {AB} …) + Voodoo hair props;
    //:415-419 set_textureanimation channels 0/1 = the clothing hex nibbles,
    // channel 2 = haircolor.
    // • z_work_common.tcl:780-832 get_nameofmuetze_proc — the hat law: a non-human
    // (:789-797 `![im_a_human]`, z_procs.tcl:32-35 reads is_human) always wears
    // Dummy_<clanname>Muetze_<a|b>; only human-owned dwarves get the work-category hats
    // (wood → Dummy_Muetze_holz_<a|b>,:804/:825).
    // • AI clan gnomes spawn as plain `new Zwerg` + `set_owner $objhandle 1` in the level
    // templates (Templates/urw_unq_vodo_002_a.tcl:191-192) — appearance is 100% this init.
    //
    // Port defects these pin: get_objrace was a hardcoded "0" stub, get_objownertype was
    // unregistered (unknown → "0" → everyone human), and set_ownerrace was a RegisterEntityProp
    // that misread the owner index as an entity handle — so every AI clan dressed as clan 0
    // with player work hats.
    public sealed class GnomeClanAppearanceTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

        static (TclInterpreter interp, DigglesTclContext ctx, DigglesGameSim sim) BuildHost(bool campaign, int seed)
        {
            UnityEngine.Random.InitState(seed);
            var interp = new TclInterpreter { TolerateExprErrors = true, TolerateMissingVars = true };
            interp.UnknownCommandHandler = (i, a) => "0";
            interp.RegisterCommand("puts", (i, a) => "");
            var ctx = DigglesTclEngine.Register(interp);
            ctx.DataRoot = DataRoot;
            ctx.TolerateMissingEntities = true;
            ctx.TolerateMissingClass = true;    // Dummy_* hair props synthesize as placeholders
            ctx.TolerateMissingFiles = true;
            ctx.IsStoryMgr = campaign;
            var sim = new DigglesGameSim(ctx);
            interp.Eval("call scripts/classes/zwerg/zwerg.tcl");   // the real dwarf script
            // The campaign/skirmish boot race rows (CampaignInit.tcl:15-19 = Skirmish.tcl:50-54).
            interp.Eval("set_ownerrace 0 0\nset_ownerrace 1 1\nset_ownerrace 2 2\nset_ownerrace 3 3\nset_ownerrace 4 4");
            return (interp, ctx, sim);
        }

        static DigglesTclEntity Spawn(TclInterpreter interp, DigglesTclContext ctx, string pos, int owner)
        {
            var e = ctx.GetEntity(interp.Eval($"new Zwerg \"\" \"{pos}\" \"0 0 0\"").Trim())!;
            // the template spawn law: new Zwerg + set_owner (urw_unq_vodo_002_a.tcl:191-192)
            interp.Eval($"set_owner {e.Handle} {owner}");
            return e;
        }

        static string Var(DigglesTclEntity e, string name)
            => e.VarsScope.Vars.TryGetValue(name, out var v) ? v : "<none>";

        // Records set_textureanimation VALUES per (handle, submesh) — headless has no renderer,
        // so the clothing nibbles are asserted at the command surface (z_methods.tcl:415-419).
        sealed class TexRecorder
        {
            public readonly Dictionary<(int handle, int submesh), int> Writes = new();
            public void Attach(TclInterpreter interp, DigglesTclContext ctx) =>
                interp.CommandTrace = (name, args, result) =>
                {
                    if (name != "set_textureanimation" || args.Length < 4) return;
                    int h = args[1] == "this" ? (ctx.This?.Handle ?? -1)
                          : int.TryParse(args[1], out var ph) ? ph : -1;
                    if (h >= 0 && int.TryParse(args[2], out var sm) && int.TryParse(args[3], out var v))
                        Writes[(h, sm)] = v;
                };
        }

        static void RunPastInit(DigglesGameSim sim)
        {
            for (int i = 0; i < 50; i++) sim.Step(0.1);   // the t=3 init timer (zwerg.tcl:183)
        }

        // ---------------- the primitive law (no data files) ----------------

        [Test]
        public void RacePrimitives_ResolveThroughTheOwner()
        {
            var interp = new TclInterpreter();
            var ctx = DigglesTclEngine.Register(interp);
            interp.Eval("def_class G none gnome 0 {} {}");
            var g = ctx.GetEntity(interp.Eval("new G \"\" \"0 0 0\" \"0 0 0\"").Trim())!;

            // set_ownerrace takes an owner index (ENGINE_TCL_API.txt:1305) — never an obj ref.
            interp.Eval("set_ownerrace 1 1");
            interp.Eval("set_ownerrace 2 4");
            Assert.AreEqual("1", interp.Eval("get_ownerrace 1").Trim(), "owner 1 → race 1");
            Assert.AreEqual("4", interp.Eval("get_ownerrace 2").Trim(), "owner 2 → race 4");
            Assert.AreEqual("-1", interp.Eval("get_ownerrace 9").Trim(), "invalid owner → -1");

            // get_objrace = the owner's race (z_methods.tcl:292 `set clan [get_objrace this]`).
            interp.Eval($"set_owner {g.Handle} 1");
            Assert.AreEqual("1", interp.Eval($"get_objrace {g.Handle}").Trim());
            interp.Eval($"set_owner {g.Handle} 2");
            Assert.AreEqual("4", interp.Eval($"get_objrace {g.Handle}").Trim());

            // Tournament spectators: set_owner -1 (obw_wettkampf.tcl:1699) must not read as a
            // valid clan — the Zuschauer default branch (z_methods.tcl:349-372) depends on it.
            interp.Eval($"set_owner {g.Handle} -1");
            Assert.AreEqual("-1", interp.Eval($"get_objrace {g.Handle}").Trim());

            // get_objownertype: 0=none 1=human 2=ai (ENGINE_TCL_API.txt:518).
            Assert.AreEqual("0", interp.Eval($"get_objownertype {g.Handle}").Trim(), "owner -1 → none");
            interp.Eval($"set_owner {g.Handle} 0");
            Assert.AreEqual("1", interp.Eval($"get_objownertype {g.Handle}").Trim(), "local player → human");
            interp.Eval($"set_owner {g.Handle} 3");
            Assert.AreEqual("2", interp.Eval($"get_objownertype {g.Handle}").Trim(), "AI owner → ai");
        }

        // ---------------- the full dwarf init, real scripts ----------------

        [Test]
        public void CampaignInit_DressesEachClanByOwnerRace()
        {
            Assume.That(System.IO.Directory.Exists(DataRoot), "original data dir not present");
            var (interp, ctx, sim) = BuildHost(campaign: true, seed: 20260711);
            var rec = new TexRecorder();
            rec.Attach(interp, ctx);

            var player  = Spawn(interp, ctx, "30 40 8", owner: 0);
            var voodoo  = Spawn(interp, ctx, "60 40 8", owner: 1);
            var knocker = Spawn(interp, ctx, "90 40 8", owner: 2);
            RunPastInit(sim);
            foreach (var d in new[] { player, voodoo, knocker })
                Assert.AreEqual("1", Var(d, "gnome_initialized"), $"#{d.Handle}: init must have run");

            // --- is_human: derived from get_objownertype==2 (z_methods.tcl:200-204) ---
            Assert.AreEqual("1", Var(player, "is_human"), "owner 0 = the human player");
            Assert.AreEqual("0", Var(voodoo, "is_human"), "owner 1 = an AI player");
            Assert.AreEqual("0", Var(knocker, "is_human"), "owner 2 = an AI player");

            // --- clanname: lindex {"" Voodoo_ Knocker_ Brain_ Vampir_} [get_objrace] (:292-294) ---
            Assert.AreEqual("", Var(player, "clanname"));
            Assert.AreEqual("Voodoo_", Var(voodoo, "clanname"));
            Assert.AreEqual("Knocker_", Var(knocker, "clanname"));

            // --- campaign hatcolor: is_storymgr → 0 for every owner (:194-196) ---
            foreach (var d in new[] { player, voodoo, knocker })
                Assert.AreEqual("0", Var(d, "hatcolor"), $"#{d.Handle}: campaign hatcolor is 0");

            // --- clothing: the ${gender}${clan} switch lists (:299-348), written to texture
            // channels 0/1 as hex nibbles (:415-417) ---
            void AssertClothing(DigglesTclEntity d, string[] maleList, string[] femaleList, string who)
            {
                string clothing = Var(d, "clothing");
                var list = d.Gender == "female" ? femaleList : maleList;
                CollectionAssert.Contains(list, clothing,
                    $"{who} #{d.Handle} ({d.Gender}): clothing '{clothing}' must come from the clan list");
                Assert.IsTrue(rec.Writes.TryGetValue((d.Handle, 0), out var ch0), $"{who}: channel 0 written");
                Assert.IsTrue(rec.Writes.TryGetValue((d.Handle, 1), out var ch1), $"{who}: channel 1 written");
                Assert.AreEqual(System.Convert.ToInt32(clothing[0].ToString(), 16), ch0,
                    $"{who}: channel 0 = the first clothing nibble (z_methods.tcl:415)");
                Assert.AreEqual(System.Convert.ToInt32(clothing[1].ToString(), 16), ch1,
                    $"{who}: channel 1 = the second clothing nibble (z_methods.tcl:416)");
                Assert.IsTrue(rec.Writes.ContainsKey((d.Handle, 2)), $"{who}: haircolor channel 2 written (:419)");
            }
            AssertClothing(player,
                new[] { "00", "11", "22", "33", "44" },
                new[] { "00","01","02","03","04","10","11","12","13","14","20","21","22","23","24",
                        "30","31","32","33","34","40","41","42","43","44" }, "player");
            AssertClothing(voodoo,  new[] { "55", "66" }, new[] { "55", "66" }, "voodoo");
            AssertClothing(knocker, new[] { "77", "88" }, new[] { "77", "87" }, "knocker");

            // --- the hat law (z_work_common.tcl:789-797): an AI-owned dwarf always wears the
            // clan hat, whatever the work category; a human-owned one gets the category hat ---
            string Hat(DigglesTclEntity d) => interp.Eval($"call_method {d.Handle} get_nameofmuetze wood").Trim();
            string End(DigglesTclEntity d) => d.Gender == "female" ? "b" : "a";
            Assert.AreEqual($"Dummy_Muetze_holz_{End(player)}", Hat(player),
                "human + wood → the wood work hat (z_work_common.tcl:804)");
            Assert.AreEqual($"Dummy_Voodoo_Muetze_{End(voodoo)}", Hat(voodoo),
                "AI voodoo → ALWAYS Dummy_Voodoo_Muetze_* (z_work_common.tcl:792-796)");
            Assert.AreEqual($"Dummy_Knocker_Muetze_{End(knocker)}", Hat(knocker),
                "AI knocker → ALWAYS Dummy_Knocker_Muetze_*");
        }

        [Test]
        public void SkirmishInit_HatcolorIsPlayerColorPlusOne()
        {
            Assume.That(System.IO.Directory.Exists(DataRoot), "original data dir not present");
            var (interp, ctx, sim) = BuildHost(campaign: false, seed: 20260712);

            var p0 = Spawn(interp, ctx, "30 40 8", owner: 0);
            var p1 = Spawn(interp, ctx, "60 40 8", owner: 1);
            RunPastInit(sim);
            Assert.AreEqual("1", Var(p0, "gnome_initialized"));
            Assert.AreEqual("1", Var(p1, "gnome_initialized"));

            // No storymgr, no tutorial trigger → hatcolor stays -1 until z_methods.tcl:297:
            // hatcolor = [get_player_color own] + 1.
            Assert.AreEqual("0", Var(p0, "is_campaign"), "not a campaign world");
            Assert.AreEqual("1", Var(p0, "hatcolor"), "owner 0: player color 0 + 1");
            Assert.AreEqual("2", Var(p1, "hatcolor"), "owner 1: player color 1 + 1");
        }
    }
}
