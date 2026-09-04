#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;
    using UnityEngine;

    // GNOME VITALS + SKILL drivers — pins the ORIGINAL's stat economy laws running through the
    // port's Tcl engine. Ground truth (all Tcl-side in the original; there is NO native decay):
    //
    // • The 1 Hz driver: zwerg.tcl:184 arms `timer_event this evt_zwerg_attribupdate -repeat -1
    // -interval 1` at obj_init; the handler (z_events.tcl:288→1695 evt_zwerg_attribupdate_proc)
    // does ALL decay/coupling/death each game-second.
    // • Base decay per fire (z_events.tcl:1850-1860): occupation "work" → Alertness -0.00022,
    // Nutrition -0.00025, Mood -0.00012; anything else → -0.00017 / -0.000195 / -0.00004.
    // AI-owned gnomes halve all three (![im_a_human],:1871-1875).
    // • Coupling (:1822): Nu<0.2 → HP += (Nu-0.2)*0.004; Al<0.1 → HP += (Al-0.1)*0.002;
    // both fine → HP regen (min(Nu,0.6)-0.2)*0.0006+(min(Al,0.5)-0.1)*0.0004 (≤ +0.0004/s);
    // Nu<0.4 → Mood += (max(Nu,0.2)-0.4)*0.0006; Al<0.4 → Mood += (max(Al,0.2)-0.4)*0.0003.
    // • Death (:1766-1770): HP < 0.01 → set_event evt_zwerg_die → evt_zwerg_die_proc (:1946)
    // sets is_dying and leaves a Zipfelmuetze cap carrying atr_ExpMax + [get_expattrib] values.
    // • Feeds: eating = 4 bites × stt_eatgain_<food>*0.25 Nutrition (+ stt_fungain Mood),
    // z_spare_procs.tcl:337-382, table sparetimetunes.tcl:43-70 (Grillpilz 0.19/0.01);
    // sleep = stt_slpgain_<place>×gf Alertness per 2 s (z_spare_procs.tcl:598).
    // • Skills: prod_exp (z_work_prod.tcl:587-598) grants tttgain × clan_exp_factor × part per
    // completed production step while exp < gain×100; clan_exp_factor = clan table (default 1)
    // × multi_exp_factor = 2.0 outside the campaign (z_procs.tcl:37-56).
    public sealed class StatsDriverTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

        readonly List<GameObject> _gos = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _gos) if (go != null) Object.DestroyImmediate(go);
            _gos.Clear();
        }

        static (TclInterpreter interp, DigglesTclContext ctx, DigglesGameSim sim) Host()
        {
            var interp = new TclInterpreter { TolerateExprErrors = true, TolerateMissingVars = true };
            interp.UnknownCommandHandler = (i, a) => "0";
            var ctx = DigglesTclEngine.Register(interp);
            ctx.DataRoot = DataRoot;
            ctx.TolerateMissingEntities = true;
            ctx.TolerateMissingClass = true;
            ctx.TolerateMissingFiles = true;
            var sim = new DigglesGameSim(ctx);
            return (interp, ctx, sim);
        }

        // Full real-class host (WorkLoopTests.RealHost pattern) for the integration pins.
        static (TclInterpreter interp, DigglesTclContext ctx, DigglesGameSim sim) RealHost()
        {
            var (interp, ctx, sim) = Host();
            foreach (var s in new[] { "scripts/init/txtinit.tcl", "scripts/init/animinit.tcl", "scripts/init/classinit.tcl" })
                try { interp.Eval("call " + s); }
                catch (System.Exception e) { TestContext.WriteLine($"[init] {s}: {e.Message}"); }
            return (interp, ctx, sim);
        }

        static DigglesTclEntity Spawn(TclInterpreter interp, DigglesTclContext ctx, string cls, string pos = "100 100 8")
            => ctx.GetEntity(interp.Eval($"new {cls} \"\" \"{pos}\" \"0 0 0\"").Trim())!;

        // A minimal gnome class that runs the REAL z_events.tcl (the whole vitals driver) with the
        // globals zwerg.tcl's obj_init would provide (zwerg.tcl:18-188) — an exact-law harness
        // without the 300-class load. The helper procs mirror z_procs.tcl (im_a_human:32,
        // calc_age:1780) and z_faceanim/z_spare stubs that the driver only touches cosmetically.
        static DigglesTclEntity SpawnLawGnome(TclInterpreter interp, DigglesTclContext ctx)
        {
            // z_events.tcl (the whole vitals driver + its handle_event registrations) is called at
            // CLASS BODY level exactly like zwerg.tcl:191 — handle_event registers on the class
            // being defined (ctx.CurrentClass); an obj_init-time call would land on the tolerant
            // scratch class and the handlers would never fire.
            interp.Eval(@"def_class StatGnome none gnome 0 {} {
                call scripts/misc/genattribs.tcl
                call scripts/classes/zwerg/z_events.tcl
                obj_init {
                    set is_burning 0
                    set is_sleeping 0
                    set is_dying 0
                    set is_old 0
                    set is_underwater 0
                    set is_wearing_divingbell 0
                    set is_wearing_divingbell_by_usercommand 0
                    set MAX_AIR_UNDERWATER 15
                    set remainingair 15
                    set out_of_water_timer 0
                    set trap_mode 0
                    set spt_fun_items 0
                    set spt_disappointment 0.0
                    set current_occupation ""idle""
                    set auto_fanim_state 0
                    set ntNutrMessage -1
                    set ntHitMessage -1
                    set ntAltMessage -1
                    set tll_fl_common 0
                    set tll_fl_hunger 0
                    set tll_fl_tired 0
                    set is_human 1
                    set is_campaign 0
                    set birthtime [gettime]
                    set clan """"
                    set myhairs 0
                    set myglasses 0
                    set gnome_gender ""male""
                    proc im_a_human {} {global is_human; return $is_human}
                    proc calc_age {} {global birthtime; return [expr {[gettime]-$birthtime}]}
                    proc fun_log {m} {}
                    proc sparetime_react_towater {} {}
                    proc set_fanim_feeling {m} {}
                    proc random_fanim_sequence {} {}
                    proc hide_tools {} {}
                    proc kill_all_ghosts {} {}
                    proc stop_prod {} {}
                    call scripts/classes/zwerg/z_events.tcl
                    timer_event this evt_zwerg_attribupdate -repeat -1 -interval 1 -userid 2
                }
            }");
            return Spawn(interp, ctx, "StatGnome");
        }

        static double Stat(DigglesTclContext ctx, DigglesTclEntity e, string name) => ctx.Stats.Get(e.Stats, name);

        // ------------------------------------------------------------------
        // 1) The exact per-second decay law, idle vs work occupation
        // ------------------------------------------------------------------

        [Test]
        public void AttribUpdate_IdleDecayLaw()
        {
            var (interp, ctx, sim) = Host();
            var g = SpawnLawGnome(interp, ctx);

            double nu0 = Stat(ctx, g, "atr_Nutrition"), al0 = Stat(ctx, g, "atr_Alertness"),
                   mo0 = Stat(ctx, g, "atr_Mood"), hi0 = Stat(ctx, g, "atr_Hitpoints");
            Assert.AreEqual(0.9, nu0, 1e-9, "genattribs.tcl:21 default");
            Assert.AreEqual(0.9, al0, 1e-9, "genattribs.tcl:22 default");
            Assert.AreEqual(0.8, mo0, 1e-9, "genattribs.tcl:23 default");
            Assert.AreEqual(1.0, hi0, 1e-9, "genattribs.tcl:20 default");

            for (int i = 0; i < 100; i++) sim.Step(0.1);   // ~10 game-seconds → ~10 fires

            double dNu = nu0 - Stat(ctx, g, "atr_Nutrition");
            double dAl = al0 - Stat(ctx, g, "atr_Alertness");
            double dMo = mo0 - Stat(ctx, g, "atr_Mood");
            Assert.Greater(dNu, 0, "the 1 Hz attribupdate driver never fired (evt_zwerg_attribupdate)");

            // Derive the fire count from Nutrition and cross-pin the other two rates against it —
            // exact law check independent of timer phase (idle: Nu 0.000195, Al 0.00017, Mo 0.00004).
            int fires = (int)System.Math.Round(dNu / 0.000195);
            Assert.That(fires, Is.InRange(9, 11), "≈1 fire per game-second expected");
            Assert.AreEqual(fires * 0.000195, dNu, 1e-7, "idle Nutrition rate (z_events.tcl:1857)");
            Assert.AreEqual(fires * 0.00017, dAl, 1e-7, "idle Alertness rate (z_events.tcl:1856)");
            Assert.AreEqual(fires * 0.00004, dMo, 1e-7, "idle Mood rate (z_events.tcl:1858)");
            Assert.AreEqual(1.0, Stat(ctx, g, "atr_Hitpoints"), 1e-9,
                "well-fed regen (z_events.tcl:1812-1814) clamps HP at the 1.0 max");
        }

        [Test]
        public void AttribUpdate_WorkDecayLaw()
        {
            var (interp, ctx, sim) = Host();
            var g = SpawnLawGnome(interp, ctx);
            g.VarsScope.Vars["current_occupation"] = "work";   // z_work_states.tcl:85/161

            double nu0 = Stat(ctx, g, "atr_Nutrition"), al0 = Stat(ctx, g, "atr_Alertness"), mo0 = Stat(ctx, g, "atr_Mood");
            for (int i = 0; i < 100; i++) sim.Step(0.1);

            double dNu = nu0 - Stat(ctx, g, "atr_Nutrition");
            int fires = (int)System.Math.Round(dNu / 0.00025);
            Assert.That(fires, Is.InRange(9, 11), "≈1 fire per game-second expected");
            Assert.AreEqual(fires * 0.00025, dNu, 1e-7, "work Nutrition rate (z_events.tcl:1852)");
            Assert.AreEqual(fires * 0.00022, al0 - Stat(ctx, g, "atr_Alertness"), 1e-7, "work Alertness rate (:1851)");
            Assert.AreEqual(fires * 0.00012, mo0 - Stat(ctx, g, "atr_Mood"), 1e-7, "work Mood rate (:1853)");
        }

        // ------------------------------------------------------------------
        // 2) Hunger → HP drain coupling + starvation is INDIRECT (via HP)
        // ------------------------------------------------------------------

        [Test]
        public void AttribUpdate_HungerDrainsHitpoints()
        {
            var (interp, ctx, sim) = Host();
            var g = SpawnLawGnome(interp, ctx);
            ctx.This = g;
            interp.Eval($"set_attrib {g.Handle} atr_Nutrition 0.1");
            interp.Eval($"set_attrib {g.Handle} atr_Alertness 0.5");
            ctx.This = null;

            // exactly one fire: first tick is at spawn+1 s
            for (int i = 0; i < 10; i++) sim.Step(0.1);

            // sub_Hi = (0.1-0.2)*0.004 = -0.0004 (z_events.tcl:1808); no regen branch (Nu<0.2)
            Assert.AreEqual(1.0 - 0.0004, Stat(ctx, g, "atr_Hitpoints"), 1e-7,
                "starving gnome loses (Nu-0.2)*0.004 HP per second");
            // Mood coupling: Nu<0.4 → (max(Nu,0.2)-0.4)*0.0006 = -0.00012, plus idle -0.00004
            Assert.AreEqual(0.8 - 0.00012 - 0.00004, Stat(ctx, g, "atr_Mood"), 1e-7,
                "hunger mood penalty (z_events.tcl:1815-1818) + idle mood decay");
        }

        [Test]
        public void AttribUpdate_HitpointsBelow001_FiresDeathEvent()
        {
            var (interp, ctx, sim) = Host();
            var g = SpawnLawGnome(interp, ctx);
            ctx.This = g;
            interp.Eval($"set_attrib {g.Handle} atr_Hitpoints 0.005");
            ctx.This = null;

            for (int i = 0; i < 30; i++) sim.Step(0.1);   // fire + evt_zwerg_die delivery

            g.VarsScope.Vars.TryGetValue("is_dying", out var dying);
            Assert.IsNotNull(dying, "evt_zwerg_die_proc never ran");
            Assert.AreNotEqual("0", dying, "HP<0.01 must raise evt_zwerg_die (z_events.tcl:1766-1770) " +
                "and the death proc must mark is_dying (:1955)");
        }

        // Invulnerability: a NEGATIVE atr_Hitpoints delta is DROPPED while the object's
        // 0x10000000 flag is set (native add_attrib FUN_00674580:117-146; set via
        // set_invulnerable — the Unverwundbarkeitstrank, z_methods.tcl:551-580).
        [Test]
        public void AddAttrib_InvulnerableGnome_IgnoresHitpointLoss()
        {
            var (interp, ctx, _) = Host();
            interp.Eval("def_class G none gnome 0 {} { call scripts/misc/genattribs.tcl }");
            var g = Spawn(interp, ctx, "G");
            interp.Eval($"set_invulnerable {g.Handle} 1");
            interp.Eval($"add_attrib {g.Handle} atr_Hitpoints -0.5");
            Assert.AreEqual(1.0, Stat(ctx, g, "atr_Hitpoints"), 1e-9, "negative HP delta must be dropped");
            interp.Eval($"add_attrib {g.Handle} atr_Nutrition -0.1");
            Assert.AreEqual(0.8, Stat(ctx, g, "atr_Nutrition"), 1e-9, "other attribs still decay");
            interp.Eval($"set_invulnerable {g.Handle} 0");
            interp.Eval($"add_attrib {g.Handle} atr_Hitpoints -0.5");
            Assert.AreEqual(0.5, Stat(ctx, g, "atr_Hitpoints"), 1e-9, "flag cleared → delta applies");
        }

        // get_expattrib / get_bestexp — the de-stubbed skill-list commands (FUN_00644de0 /
        // FUN_0068bff0; literals "exp_"/"exp_F_" read from Diggles.exe @0x721330/0x721338).
        [Test]
        public void GetExpAttrib_Modes_And_GetBestExp()
        {
            var (interp, ctx, _) = Host();
            interp.Eval("def_class G none gnome 0 {} { call scripts/misc/genattribs.tcl }");
            var g = Spawn(interp, ctx, "G");

            Assert.AreEqual("exp_Nahrung exp_Holz exp_Stein exp_Metall exp_Transport exp_Energie exp_Service exp_Kampf",
                interp.Eval("get_expattrib 0").Trim(),
                "mode 0 = exp_* minus exp_F_* in registration order (genattribs.tcl:5-12)");
            Assert.AreEqual("exp_F_Sword exp_F_Twohanded exp_F_Defense exp_F_Ballistic exp_F_Kungfu",
                interp.Eval("get_expattrib 1").Trim(), "mode 1 = fight skills only (genattribs.tcl:13-17)");
            StringAssert.StartsWith("exp_Nahrung", interp.Eval("get_expattrib").Trim());
            StringAssert.EndsWith("exp_F_Kungfu", interp.Eval("get_expattrib").Trim());

            Assert.AreEqual("", interp.Eval($"get_bestexp {g.Handle}").Trim(),
                "all-zero skills → empty (native strict-max seeded at 0)");
            interp.Eval($"set_attrib {g.Handle} exp_Stein 0.3");
            interp.Eval($"set_attrib {g.Handle} exp_Holz 0.2");
            Assert.AreEqual("exp_Stein", interp.Eval($"get_bestexp {g.Handle}").Trim());
        }

        // ------------------------------------------------------------------
        // 3) Integration — the REAL Zwerg class chain decays live
        // ------------------------------------------------------------------

        [Test]
        public void RealZwerg_VitalsDecay_And_TunablesLoaded()
        {
            var (interp, ctx, sim) = RealHost();
            Assert.IsTrue(ctx.Classes.ContainsKey("Zwerg"), "Zwerg class not loaded");
            var g = Spawn(interp, ctx, "Zwerg");

            // the spare-time tunables must be loaded per-gnome (zwerg.tcl:144-145)
            g.VarsScope.Vars.TryGetValue("stt_eatgain_Grillpilz", out var eatgain);
            Assert.AreEqual("0.19", eatgain?.Trim(), "sparetimetunes.tcl:43 stt_eatgain_Grillpilz");
            g.VarsScope.Vars.TryGetValue("stt_slpgain_0", out var slpgain);
            Assert.AreEqual("0.004", slpgain?.Trim(), "sparetimetunes.tcl:18 stt_slpgain_0");

            for (int i = 0; i < 50; i++) sim.Step(0.1);    // settle into idle, timers armed
            double nu0 = Stat(ctx, g, "atr_Nutrition");
            double al0 = Stat(ctx, g, "atr_Alertness");

            for (int i = 0; i < 600; i++) sim.Step(0.1);   // 60 game-seconds

            double dNu = nu0 - Stat(ctx, g, "atr_Nutrition");
            double dAl = al0 - Stat(ctx, g, "atr_Alertness");
            // 60 fires × 0.000195 (idle) … × 0.00025 (work) — accept the whole occupancy band,
            // reject both "pinned" (no decay) and runaway (>2× work rate).
            Assert.That(dNu, Is.InRange(60 * 0.000195 * 0.8, 60 * 0.00025 * 2.0),
                $"live Zwerg Nutrition decay off-law (Δ={dNu})");
            Assert.That(dAl, Is.InRange(60 * 0.00017 * 0.8, 60 * 0.00022 * 2.0),
                $"live Zwerg Alertness decay off-law (Δ={dAl})");
            Assert.AreEqual(1.0, Stat(ctx, g, "atr_Hitpoints"), 1e-6, "healthy fed gnome stays at full HP");
        }

        // ------------------------------------------------------------------
        // 4) Feeds — eating restores per the native table
        // ------------------------------------------------------------------

        [Test]
        public void RealZwerg_EatingGrillpilz_RestoresNutrition()
        {
            var (interp, ctx, sim) = RealHost();
            var g = Spawn(interp, ctx, "Zwerg");
            var food = Spawn(interp, ctx, "Grillpilz", "100.5 100 8");
            for (int i = 0; i < 30; i++) sim.Step(0.1);

            interp.Eval($"set_attrib {g.Handle} atr_Nutrition 0.4");
            // Queue the real eat program (z_spare_procs.tcl:337-368 "ground") and drive the pump.
            // Must run in the gnome's OBJECT scope (RunInEntityContext) like the real sparetime
            // event bodies do: sparetime_eat's `global stt_eatgain_$food` (:340) binds the
            // PER-GNOME tunables loaded by zwerg.tcl:145 — a bare Eval would bind the shared
            // global scope and read an unset var (affect 0).
            DigglesTclEngine.RunInEntityContext(ctx, g,
                $"sparetime_eat {food.Handle} ground\nstate_triggerfresh this work_dispatch", "test");

            double nu0 = Stat(ctx, g, "atr_Nutrition");
            for (int i = 0; i < 600 && g.TaskList.Count > 0; i++) sim.Step(0.1);
            double gained = Stat(ctx, g, "atr_Nutrition") - nu0;

            // 4 bites × 0.19*0.25 = +0.19 total (z_spare_procs.tcl:341,359-362; sparetimetunes.tcl:43)
            // minus ≲0.015 decay while the anim chain drains.
            Assert.That(gained, Is.InRange(0.19 - 0.03, 0.19 + 0.001),
                $"eating a Grillpilz must restore ≈0.19 Nutrition (got Δ={gained})");
        }

        // ------------------------------------------------------------------
        // 5) Skills — prod_exp grants tttgain × clan_exp_factor × part
        // ------------------------------------------------------------------

        [Test]
        public void RealZwerg_ProdExp_GrantsExperience()
        {
            var (interp, ctx, sim) = RealHost();
            var g = Spawn(interp, ctx, "Zwerg");
            for (int i = 0; i < 30; i++) sim.Step(0.1);

            Assert.AreEqual(0.0, Stat(ctx, g, "exp_Nahrung"), 1e-12, "skills start at 0 (genattribs.tcl:5)");

            // entity scope: clan_exp_factor reads the per-gnome clanname/is_campaign globals
            DigglesTclEngine.RunInEntityContext(ctx, g, "prod_exp {{exp_Nahrung 0.004}} 1.0", "test");

            // clan_exp_factor = clan table (none → 1.0) × multi_exp_factor 2.0 outside the campaign
            // (z_procs.tcl:37-56) ⇒ 0.004 × 2 = 0.008.
            Assert.AreEqual(0.008, Stat(ctx, g, "exp_Nahrung"), 1e-9,
                "prod_exp must add tttgain × clan_exp_factor (z_work_prod.tcl:587-598)");

            // the soft cap: no growth once exp ≥ gain×100 (z_work_prod.tcl:592)
            interp.Eval($"set_attrib {g.Handle} exp_Nahrung 0.9");
            DigglesTclEngine.RunInEntityContext(ctx, g, "prod_exp {{exp_Nahrung 0.004}} 1.0", "test");
            Assert.AreEqual(0.9, Stat(ctx, g, "exp_Nahrung"), 1e-9,
                "exp ≥ gain×100 must stop growth (z_work_prod.tcl:592)");
        }
    }
}
