#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    // cook fidelity: a built Feuerstelle's cook order must
    // run the FAITHFUL production work chain, not the old invented prod_cook shortcut. The cook
    // click now (1) schedules a Grillpilz slot and (2) raises evt_autoprod_workat on the gnome —
    // the SAME event the autonomous ProdDispatcher raises — which drives:
    // evt_autoprod_workat_proc (z_events.tcl:1127): tasklist_clear; stop_prod; current_worktask
    // "work"; prod_change_muetze [get_class_category Grillpilz]="food" → state_triggerfresh
    // work_auto
    // state_work_auto_proc (z_work_states.tcl:120-124): prod_autobuild Grillpilz <fire>
    // prod_autobuild (z_work_common.tcl:268): current_worklist = get_itemtasklist → prod_getitemtask
    // (genericprod.tcl:699; returns "" once slot==0,:723) → Feuerstelle.prod_item_actions
    // (feuerstelle.tcl:11) → prod_actions_Pilzhut "Grillpilz" branch (:74-101): prod_goworkdummy +
    // prod_turnback facing, prod_machineanim feuerstelle.pilz/brennt + prod_blowright particles,
    // prod_createproduct_rndrot Grillpilz output, then prod_finishitem → finish_itemprod →
    // info_end_prod (slot−1) + stop_production.
    //
    // These headless tests load the REAL Zwerg + Feuerstelle classes (RealHost, the WorkLoopTests
    // pattern) and drive evt_autoprod_workat — the click path's exact trigger. No GameObjects in
    // EditMode, so walks degrade to brief WaitActions, but the LOGIC (hat, facing via the immediate
    // action-rotate write, output spawn, slot decrement, interrupt) advances and is verifiable.
    [Explicit]   // loads ~300 real classes from the data dir; run on demand (batch)
    public sealed class CookWorkChainTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

        readonly List<GameObject> _spawnedGos = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _spawnedGos) if (go != null) Object.DestroyImmediate(go);
            _spawnedGos.Clear();
        }

        static (TclInterpreter interp, DigglesTclContext ctx, DigglesGameSim sim) RealHost()
        {
            var interp = new TclInterpreter { TolerateExprErrors = true, TolerateMissingVars = true };
            interp.UnknownCommandHandler = (i, a) => "0";
            var ctx = DigglesTclEngine.Register(interp);
            ctx.DataRoot = DataRoot;
            ctx.TolerateMissingEntities = true;
            ctx.TolerateMissingClass = true;
            ctx.TolerateMissingFiles = true;
            var sim = new DigglesGameSim(ctx);
            foreach (var s in new[] { "scripts/init/txtinit.tcl", "scripts/init/animinit.tcl", "scripts/init/classinit.tcl" })
                try { interp.Eval("call " + s); }
                catch (System.Exception e) { TestContext.WriteLine($"[init] {s}: {e.Message}"); }
            return (interp, ctx, sim);
        }

        DiggableTerrain MakeOpenTerrain(int w, int h)
        {
            var go = new GameObject("DiggableTerrain");
            _spawnedGos.Add(go);
            var t = go.AddComponent<DiggableTerrain>();
            var block = new uint[w * h];
            for (int k = 0; k < block.Length; k++) block[k] = 59;   // 59 = air ⇒ open ground
            var bdb = BdbParser.Parse(DataRoot + "/BlockDB2.BDB");
            t.Init(new BdbPmpWorldBuilder.TclWorldResult { Width = w, Height = h, BlockIdx = block, Terrain = new byte[w * h] }, bdb, "", () => { });
            return t;
        }

        static DigglesTclEntity Spawn(TclInterpreter interp, DigglesTclContext ctx, string cls, string pos)
            => ctx.GetEntity(interp.Eval($"new {cls} \"\" \"{pos}\" \"0 0 0\""))!;

        // Stand up a built, scheduled campfire + a gnome and raise the cook order exactly as the
        // ClickToMoveService cook click does. Returns (gnome, fire). Steps the sim `steps` ticks.
        (DigglesTclEntity g, DigglesTclEntity fire) StartCook(
            TclInterpreter interp, DigglesTclContext ctx, DigglesGameSim sim, int slot, int steps)
        {
            ctx.LocalPlayer = 0;
            ctx.Terrain = MakeOpenTerrain(64, 64);

            var fire = Spawn(interp, ctx, "Feuerstelle", "35 30 8");
            fire.Owner = 0;
            fire.BuildupComplete = true;

            var g = Spawn(interp, ctx, "Zwerg", "33 30 8");
            g.Owner = 0;
            interp.Eval($"set_worktime {g.Handle} 0 12");
            for (int i = 0; i < 10; i++) sim.Step(0.1);   // settle to idle

            // The click path: schedule the product + raise the faithful work event.
            interp.Eval($"set_prod_slot_cnt {fire.Handle} Grillpilz {slot}");
            ctx.This = g;
            interp.Eval($"set_event this evt_autoprod_workat -subject1 {fire.Handle} -text1 Grillpilz");
            ctx.This = null;

            for (int i = 0; i < steps; i++) sim.Step(0.1);
            return (g, fire);
        }

        int CountClass(DigglesTclContext ctx, string cls)
        {
            int n = 0;
            foreach (var e in ctx.Entities.Values) if (e.Class?.Name == cls) n++;
            return n;
        }

        // get_class_category must resolve a CLASS NAME (every shipped caller passes one). Grillpilz
        // is `def_class Grillpilz food …` → "food" → the cook hat. The old impl resolved it as an
        // entity handle and returned "" → the default warning hat.
        [Test]
        public void GetClassCategory_ResolvesClassName()
        {
            var (interp, _, _) = RealHost();
            Assert.AreEqual("food", interp.Eval("get_class_category Grillpilz").Trim(),
                "Grillpilz category must be 'food' (def_class Grillpilz food …)");
        }

        // BUG 1 — the cook hat. evt_autoprod_workat_proc calls prod_change_muetze food → muetze_auf
        // Dummy_Muetze_nahrung_a → create_muetze sets current_muetze_name. The food cook hat must be
        // on, not the default warning hat (the prod_cook shortcut never changed the hat at all).
        [Test, Explicit]
        public void Cook_ChangesToFoodHat()
        {
            var (interp, ctx, sim) = RealHost();
            var (g, _) = StartCook(interp, ctx, sim, slot: 1, steps: 60);
            g.VarsScope.Vars.TryGetValue("current_muetze_name", out var hat);
            TestContext.WriteLine($"RESULT hat: current_muetze_name='{hat}'");
            Assert.IsNotNull(hat, "no hat set — prod_change_muetze never ran");
            StringAssert.Contains("nahrung", hat ?? "",
                "cook hat must be the food hat (Dummy_Muetze_nahrung_*), not the default");
        }

        // BUG 2 — the cook facing. The feuerstelle Grillpilz chain turns the gnome to face the fire
        // with prod_turnback (z_work_prod.tcl:159 → rotate_toback → action this rotate 3.14, an
        // ABSOLUTE π facing, the workatfire pose) — interleaved with prod_turnfront/turnright for
        // the hungry/blow beats. The action-rotate verb writes roty IMMEDIATELY (headless-
        // observable). Sample roty across the chain and assert it REACHES the π workplace facing:
        // proof the gnome faces the fire to cook, vs the prod_cook shortcut that never rotated
        // (roty stuck at the spawn 0).
        [Test, Explicit]
        public void Cook_FacesWorkplace_Back()
        {
            var (interp, ctx, sim) = RealHost();
            var (g, _) = StartCook(interp, ctx, sim, slot: 1, steps: 0);
            bool facedBack = false; float maxRoty = 0f;
            for (int i = 0; i < 300; i++)
            {
                sim.Step(0.1);
                float r = g.Rotation.y;
                maxRoty = Mathf.Max(maxRoty, r);
                if (Mathf.Abs(Mathf.DeltaAngle(r * Mathf.Rad2Deg, 180f)) < 12f) facedBack = true;
            }
            TestContext.WriteLine($"RESULT facing: facedBack(≈π)={facedBack} maxRoty={maxRoty:0.000}");
            Assert.IsTrue(facedBack,
                "gnome never turned to face the fire (prod_turnback π); the shortcut left it at 0");
        }

        // BUG 4 + 5 — output placed AND production terminates. With slot=1 the chain produces
        // number2produce_Grillpilz = 2 Grillpilz (techtreetunes.tcl:513) via prod_createproduct_rndrot,
        // then prod_finishitem → info_end_prod decrements the slot to 0 → prod_getitemtask returns ""
        // → the gnome stops cooking (no forever loop).
        [Test, Explicit]
        public void Cook_ProducesOutput_AndTerminates()
        {
            var (interp, ctx, sim) = RealHost();
            var (g, fire) = StartCook(interp, ctx, sim, slot: 1, steps: 0);
            int grill0 = CountClass(ctx, "Grillpilz");

            bool everCooked = false;
            for (int i = 0; i < 600; i++)
            {
                sim.Step(0.1);
                g.VarsScope.Vars.TryGetValue("current_worktask", out var wt);
                if (wt == "work") everCooked = true;
                if (i % 60 == 0)
                    TestContext.WriteLine($"step {i}: state={g.CurrentState} wt={wt} slot={interp.Eval($"get_prod_slot_cnt {fire.Handle} Grillpilz").Trim()} grillpilz={CountClass(ctx, "Grillpilz")}");
            }
            int grill1 = CountClass(ctx, "Grillpilz");
            string slot = interp.Eval($"get_prod_slot_cnt {fire.Handle} Grillpilz").Trim();
            TestContext.WriteLine($"RESULT produce/terminate: everCooked={everCooked} grillpilz {grill0}->{grill1} slot={slot} finalState={g.CurrentState}");
            Assert.IsTrue(everCooked, "gnome never entered the cook (work) flow");
            Assert.Greater(grill1, grill0, "no Grillpilz was produced (prod_createproduct_rndrot never ran)");
            // Termination: the slot must fall to 0 (info_end_prod decrement → prod_getitemtask
            // returns "" → no re-cook). The decrement MECHANISM is proven in isolation by
            // InfoEndProd_DecrementsCurrentItemSlot; this end-to-end check needs the whole anim
            // chain to drain headless within the step budget.
            Assert.AreEqual("0", slot, "the Grillpilz slot never decremented to 0 — production loops forever (info_end_prod still stubbed?)");
        }

        // info_end_prod must decrement the building's current_itemtype slot (the native
        // FUN_0069e210 demand decrement; was a "0" stub → forever loop). Direct unit check of the
        // primitive, independent of the full chain timing.
        [Test, Explicit]
        public void InfoEndProd_DecrementsCurrentItemSlot()
        {
            var (interp, ctx, sim) = RealHost();
            var fire = Spawn(interp, ctx, "Feuerstelle", "35 30 8");
            fire.Owner = 0;
            interp.Eval($"set_prod_slot_cnt {fire.Handle} Grillpilz 3");
            fire.VarsScope.Vars["current_itemtype"] = "Grillpilz";   // what get_itemtasklist stores

            interp.Eval($"info_end_prod {fire.Handle}");
            Assert.AreEqual("2", interp.Eval($"get_prod_slot_cnt {fire.Handle} Grillpilz").Trim(), "first finish");
            interp.Eval($"info_end_prod {fire.Handle}");
            interp.Eval($"info_end_prod {fire.Handle}");
            Assert.AreEqual("0", interp.Eval($"get_prod_slot_cnt {fire.Handle} Grillpilz").Trim(), "reaches 0");
            // 10 = infinite — never decrements
            interp.Eval($"set_prod_slot_cnt {fire.Handle} Grillpilz 10");
            interp.Eval($"info_end_prod {fire.Handle}");
            Assert.AreEqual("10", interp.Eval($"get_prod_slot_cnt {fire.Handle} Grillpilz").Trim(), "infinite slot is never reduced");
        }

        // BUG 6 — interruptible. Mid-cook, a NEW user order (evt_task_walk) must stop the cook:
        // evt_task_walk_proc runs gnome_failed_work + tasklist_clear + stop_prod at its head
        // (z_events.tcl:346-349) and switches to the walk task. The shortcut had a queued
        // walk+wait+prod_make finishchain that ignored a new order.
        [Test, Explicit]
        public void Cook_InterruptedByNewOrder()
        {
            var (interp, ctx, sim) = RealHost();
            var (g, _) = StartCook(interp, ctx, sim, slot: 10, steps: 40);   // slot 10 = would cook forever
            g.VarsScope.Vars.TryGetValue("current_worktask", out var wtMid);
            TestContext.WriteLine($"mid-cook: state={g.CurrentState} wt={wtMid}");

            // A new WALK order — exactly what a click on the ground raises.
            ctx.This = g;
            interp.Eval("set_event this evt_task_walk -pos1 {50 30 8}");
            ctx.This = null;

            bool sawTask = false;
            for (int i = 0; i < 40; i++)
            {
                sim.Step(0.1);
                if (g.CurrentState == "task") sawTask = true;
            }
            g.VarsScope.Vars.TryGetValue("current_worktask", out var wtAfter);
            g.VarsScope.Vars.TryGetValue("last_event", out var le);
            TestContext.WriteLine($"RESULT interrupt: wtMid={wtMid} sawTask={sawTask} state={g.CurrentState} lastEvent='{le}'");
            Assert.AreEqual("work", wtMid, "precondition: gnome must have been cooking before the interrupt");
            Assert.IsTrue(sawTask || (le != null && le.Contains("walk")),
                "a new walk order did not interrupt the cook (evt_task_walk must clear the work chain)");
        }
    }
}
