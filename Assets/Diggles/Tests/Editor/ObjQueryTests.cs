#nullable enable

namespace Diggles.Tests
{
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;

    // obj_query fixes, verified against the native CObjQuery RE
    // (FUN_005de500 option parser / FUN_005e0e50 filter / FUN_005e12a0 execute):
    // • -flagneg excludes ONLY entities with the flag SET — an earlier revision defaulted
    // unknown flags to TRUE, so `-flagneg locked` (etc.) excluded EVERYTHING and
    // zeroed the gnome food search / Lager store loop / takeitems.
    // • -flagpos requires ALL listed flags set ((flags & mask) == mask).
    // • -visibility is the FOG-OF-WAR filter (REAL — the Lager auto-store
    // lager.tcl:99/112 and sparetime searches hauled loot out of unexplored fog while
    // it was a no-op): parser masks FUN_005de500:897-1041, candidate bits from the 8
    // per-player max-fow bytes at threshold 0x80 (FUN_005e0e50:84-105). No fog wired
    // (these fixtures) ⇒ no constraint — it must not bail the query to "0".
    // • -water uses the live fluid point query; -alloc remains neutral without a class table.
    // • unknown options are SKIPPED, never a bail (FUN_005de500 strcmp fall-through).
    // • default sorting = distance ascending; -limit N returns the NEAREST N
    // (FUN_005e12a0:557-601 sorts by dist² then copies the first N).
    // • cloaked objects are excluded unless -cloaked 1
    // (FUN_005de500:LAB_005e0bd3 flagneg |= 0x8000000 when -cloaked absent).
    // Each test cites the original-data call shape it protects.
    public sealed class ObjQueryTests
    {
        static (TclInterpreter interp, DigglesTclContext ctx) BuildHost()
        {
            var interp = new TclInterpreter();
            var ctx = DigglesTclEngine.Register(interp);
            return (interp, ctx);
        }

        // Subject + three food items on a line; flags via the real setter commands.
        static (TclInterpreter interp, DigglesTclContext ctx, string g, string p1, string p2, string p3)
            FoodWorld()
        {
            var (interp, ctx) = BuildHost();
            interp.Eval(@"
                def_class Zwerg flesh gnome 0 {} {}
                def_class Pilz food material 0 {} {}
                set g  [new Zwerg """" ""100 100 8"" ""0 0 0""]
                set p1 [new Pilz  """" ""101 100 8"" ""0 0 0""]
                set p2 [new Pilz  """" ""104 100 8"" ""0 0 0""]
                set p3 [new Pilz  """" ""108 100 8"" ""0 0 0""]
            ");
            return (interp, ctx,
                interp.Eval("set g"), interp.Eval("set p1"),
                interp.Eval("set p2"), interp.Eval("set p3"));
        }

        // ---- -flagneg: exclude ONLY the flagged ----

        [Test]
        public void FlagNeg_Locked_ExcludesOnlyTheLockedItem()
        {
            var (interp, _, g, p1, p2, p3) = FoodWorld();
            interp.Eval($"set_lock {p2} 1");
            string r = interp.Eval($"obj_query {g} \"-class Pilz -flagneg locked\"");
            StringAssert.DoesNotContain(p2, r, "-flagneg locked must exclude the locked item");
            StringAssert.Contains(p1, r);
            StringAssert.Contains(p3, r);
        }

        // The exact Lager store-loop shape (lager.tcl:112): list-form flagneg + -alloc -1
        // + -visibility playervisible. Used to return "0" unconditionally.
        [Test]
        public void FlagNeg_List_LagerStoreShape_DoesNotNukeResults()
        {
            var (interp, _, g, p1, p2, p3) = FoodWorld();
            interp.Eval($"set_storable {p1} 1");
            interp.Eval($"set_storable {p2} 1");
            interp.Eval($"set_storable {p3} 1");
            interp.Eval($"set_lock {p3} 1");
            string r = interp.Eval(
                $"obj_query {g} \"-flagpos storable -flagneg {{contained locked instore}} " +
                "-range 50 -class {Pilz} -visibility playervisible -owner {0 -1} -alloc -1\"");
            Assert.AreNotEqual("0", r, "the lager store query must not bail");
            StringAssert.Contains(p1, r);
            StringAssert.Contains(p2, r);
            StringAssert.DoesNotContain(p3, r, "locked item must be excluded");
        }

        [Test]
        public void FlagNeg_UnknownFlagWord_IsNeutral()
        {
            // An unknown word ORs no bit in the native parser (FUN_005de500:LAB_005dfb2e) —
            // it must not exclude anything (the old `_ => true` default excluded EVERYTHING).
            var (interp, _, g, p1, p2, p3) = FoodWorld();
            string r = interp.Eval($"obj_query {g} \"-class Pilz -flagneg frobnicated\"");
            StringAssert.Contains(p1, r);
            StringAssert.Contains(p2, r);
            StringAssert.Contains(p3, r);
        }

        // ---- -flagpos: ALL listed flags must be set ----

        [Test]
        public void FlagPos_Boxed_MatchesOnlyTheBoxedItem()
        {
            var (interp, _, g, p1, p2, p3) = FoodWorld();
            interp.Eval($"set_boxed {p2} 1");
            string r = interp.Eval($"obj_query {g} \"-class Pilz -flagpos boxed\"");
            Assert.AreEqual(p2, r.Trim(), "-flagpos boxed must match exactly the boxed item");
        }

        [Test]
        public void FlagPos_Visible_DefaultsTrue_UntilSetVisibilityZero()
        {
            // Food items never call set_visibility, yet z_spare_procs.tcl:72 finds them
            // with `-flagpos visible` — the engine default flags include visible.
            var (interp, _, g, p1, p2, p3) = FoodWorld();
            interp.Eval($"set_visibility {p3} 0");
            string r = interp.Eval($"obj_query {g} \"-class Pilz -flagpos visible\"");
            StringAssert.Contains(p1, r);
            StringAssert.Contains(p2, r);
            StringAssert.DoesNotContain(p3, r, "set_visibility 0 must clear the visible flag");
        }

        [Test]
        public void FlagPos_List_RequiresAllFlags()
        {
            // z_spare_procs.tcl:81 `-flagpos {visible instore}` — instore-only food path.
            var (interp, ctx, g, p1, p2, p3) = FoodWorld();
            interp.Eval($"set_instore {p2} 1");
            string r = interp.Eval($"obj_query {g} \"-class Pilz -flagpos {{visible instore}}\"");
            Assert.AreEqual(p2, r.Trim(), "only the instore item carries BOTH flags");
        }

        [Test]
        public void FlagPos_MaleFemale_StillWork()
        {
            var (interp, _) = BuildHost();
            interp.Eval(@"
                def_class Zwerg flesh gnome 0 {} {}
                set g [new Zwerg """" ""0 0 0"" ""0 0 0""]
                set m [new Zwerg """" ""1 0 0"" ""0 0 0""]
                set f [new Zwerg """" ""2 0 0"" ""0 0 0""]
                set_objgender $m male
                set_objgender $f female
            ");
            string m = interp.Eval("set m"), f = interp.Eval("set f");
            Assert.AreEqual(m, interp.Eval("obj_query $g \"-class Zwerg -flagpos male\"").Trim());
            Assert.AreEqual(f, interp.Eval("obj_query $g \"-class Zwerg -flagpos female\"").Trim());
            // -flagneg male keeps the female AND the genderless (neutral) subject's peers.
            string r = interp.Eval("obj_query $g \"-class Zwerg -flagneg male\"");
            StringAssert.DoesNotContain(m, r);
            StringAssert.Contains(f, r);
        }

        // ---- -visibility / dry-default -water / -alloc / unknown options: never a bail ----

        [Test]
        public void VisibilityAndWater_DoNotNukeResults_FoodSearchShape()
        {
            // The exact gnome eat-search shape (z_spare_procs.tcl:72): -visibility own +
            // -owner {own -1} + -water 0 + a doubled -limit. Used to return "0" for the
            // -visibility/-water options alone.
            var (interp, _, g, p1, _, _) = FoodWorld();
            string r = interp.Eval(
                $"obj_query {g} \"-class {{Pilz}} -boundingbox {{-20 -20 -16 20 20 16}} " +
                "-visibility own -owner {own -1} -limit 10 -flagpos visible " +
                "-flagneg {contained locked} -water 0 -limit 1\"");
            Assert.AreNotEqual("0", r, "-visibility and a dry -water 0 filter must not bail the query");
            Assert.AreEqual(p1, r.Trim(), "-limit 1 returns the nearest item");
        }

        [Test]
        public void UnknownOption_IsSkipped_NotABail()
        {
            var (interp, _, g, p1, p2, p3) = FoodWorld();
            string r = interp.Eval($"obj_query {g} \"-class Pilz -bogusopt 42\"");
            Assert.AreNotEqual("0", r, "an unknown option must be skipped (FUN_005de500 fall-through)");
            StringAssert.Contains(p1, r);
        }

        // ---- -visibility WITH fog wired: the real fog filter ----

        // The Lager store loop (lager.tcl:99/112 `-visibility playervisible`) and the
        // sparetime food search (z_spare_procs.tcl:72 `-visibility own`) must not pull
        // items out of unexplored fog. Law: candidate passes iff its max-fow byte on the
        // requested lane ≥ 0x80 (FUN_005e0e50:84-105; "own" = 1<<self.Owner lane bit,
        // playervisible = the subject-owner aggregate bit 0x10000). Queries WITHOUT
        // -visibility are never fog-gated (the native default mask is -1).
        [Test]
        public void Visibility_FogWired_FiltersUnexploredItems()
        {
            var (interp, ctx) = BuildHost();
            ctx.LocalPlayer = 0;
            interp.Eval(@"
                def_class Zwerg flesh gnome 0 {} {}
                def_class Pilz food material 0 {} {}
                set g  [new Zwerg """" ""10 10 8"" ""0 0 0""]
                set pn [new Pilz  """" ""12 10 8"" ""0 0 0""]
                set pf [new Pilz  """" ""40 40 8"" ""0 0 0""]
            ");
            string g = interp.Eval("set g"), pn = interp.Eval("set pn"), pf = interp.Eval("set pf");
            ctx.Entities[int.Parse(g)].Owner = 0;

            // No fog wired → -visibility is unconstrained (headless/menu — mirrors FowEligible).
            string all = interp.Eval($"obj_query {g} \"-class Pilz -visibility playervisible\"");
            StringAssert.Contains(pf, all, "no fog wired -> -visibility must not constrain");

            // All-black fog; reveal ±2 tiles around the NEAR item only.
            ctx.FogOfWar = new Diggles.Runtime.World.FogOfWarState(64, 64, (x, y) => 0);
            ctx.FogOfWar.RemoveFogBoxNoCollision(12 * 8, 10 * 8, 16, 16, 0);
            ctx.FogOfWar.EndUpdate();

            string r = interp.Eval($"obj_query {g} \"-class Pilz -visibility playervisible\"");
            StringAssert.Contains(pn, r, "revealed item passes playervisible (lane byte >= 0x80)");
            StringAssert.DoesNotContain(pf, r, "unexplored item must be fog-filtered (the Lager-store leak)");

            string rOwn = interp.Eval($"obj_query {g} \"-class Pilz -visibility own\"");
            StringAssert.Contains(pn, rOwn, "own = the subject-owner lane bit, same threshold");
            StringAssert.DoesNotContain(pf, rOwn, "the sparetime eat-search leak");

            string rNone = interp.Eval($"obj_query {g} \"-class Pilz\"");
            StringAssert.Contains(pf, rNone, "no -visibility -> no fog gate (native mask -1)");
        }

        // ---- distance sort + -limit = nearest N ----

        [Test]
        public void Limit_ReturnsNearestN_SortedByDistance()
        {
            var (interp, _) = BuildHost();
            interp.Eval(@"
                def_class Zwerg flesh gnome 0 {} {}
                def_class Pilz food material 0 {} {}
                set g  [new Zwerg """" ""100 100 8"" ""0 0 0""]
                set far  [new Pilz """" ""120 100 8"" ""0 0 0""]
                set near [new Pilz """" ""101 100 8"" ""0 0 0""]
                set mid  [new Pilz """" ""107 100 8"" ""0 0 0""]
            ");
            string near = interp.Eval("set near"), mid = interp.Eval("set mid");
            string r = interp.Eval("obj_query $g \"-class Pilz -limit 2\"").Trim();
            Assert.AreEqual($"{near} {mid}", r,
                "-limit 2 must return the two NEAREST, nearest first (FUN_005e12a0 sorts by dist² before the copy)");
        }

        [Test]
        public void NoLimit_ResultIsDistanceSorted()
        {
            var (interp, _) = BuildHost();
            interp.Eval(@"
                def_class Zwerg flesh gnome 0 {} {}
                def_class Pilz food material 0 {} {}
                set g [new Zwerg """" ""0 0 0"" ""0 0 0""]
                set c [new Pilz """" ""9 0 0"" ""0 0 0""]
                set a [new Pilz """" ""1 0 0"" ""0 0 0""]
                set b [new Pilz """" ""5 0 0"" ""0 0 0""]
            ");
            string a = interp.Eval("set a"), b = interp.Eval("set b"), c = interp.Eval("set c");
            Assert.AreEqual($"{a} {b} {c}", interp.Eval("obj_query $g \"-class Pilz\"").Trim(),
                "default sorting (=1) is distance ascending even without -limit");
        }

        // ---- cloaked-by-default exclusion ----

        [Test]
        public void Cloaked_ExcludedByDefault_IncludedWithCloakedOne()
        {
            var (interp, _, g, p1, p2, p3) = FoodWorld();
            interp.Eval($"set_cloaked {p2} 1");
            string r = interp.Eval($"obj_query {g} \"-class Pilz\"");
            StringAssert.DoesNotContain(p2, r, "cloaked objects are excluded unless -cloaked 1");
            string r2 = interp.Eval($"obj_query {g} \"-class Pilz -cloaked 1\"");
            StringAssert.Contains(p2, r2, "-cloaked 1 lifts the implicit cloaked flagneg");
        }

        // ---- contained items ----

        [Test]
        public void Contained_HiddenFromSearches_UnlessFlagPosContained()
        {
            var (interp, ctx, g, p1, p2, p3) = FoodWorld();
            // Pack p2 into the gnome (inv_add convention: ContainedIn set).
            var e2 = ctx.GetEntity(p2)!;
            e2.ContainedIn = int.Parse(g);
            string r = interp.Eval($"obj_query {g} \"-class Pilz\"");
            StringAssert.DoesNotContain(p2, r, "a packed item is out of the world grid");
            // Trigger_Tournament.tcl:307 counts the contents with -flagpos contained.
            string rc = interp.Eval($"obj_query {g} \"-class Pilz -flagpos contained\"");
            Assert.AreEqual(p2, rc.Trim());
        }

        [Test]
        public void NoMatches_ReturnsZero()
        {
            var (interp, _, g, _, _, _) = FoodWorld();
            Assert.AreEqual("0", interp.Eval($"obj_query {g} \"-class Drache\"").Trim(),
                "no matches → \"0\" (the scripts test `== 0`)");
        }
    }

    // Same defect cluster against the REAL class chain (WorkLoopTests.RealHost pattern:
    // txtinit/animinit/classinit → ~680 classes). Heavy — run on demand.
    [Explicit]
    public sealed class ObjQueryRealClassTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

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

        [Test, Explicit]
        public void RealClasses_FoodSearch_FindsThePilz()
        {
            var (interp, ctx, _) = RealHost();
            Assert.IsTrue(ctx.Classes.ContainsKey("Zwerg"), "Zwerg class not loaded");
            Assert.IsTrue(ctx.Classes.ContainsKey("Pilz"), "Pilz class not loaded");
            string g = interp.Eval("new Zwerg \"\" \"100 100 8\" \"0 0 0\"").Trim();
            string p = interp.Eval("new Pilz \"\" \"102 100 8\" \"0 0 0\"").Trim();
            // The z_spare_procs.tcl:72 eat-search shape against the real Pilz class. A FRESH Pilz
            // is LOCKED (pilz.tcl obj_init: set_lock this 1, cleared only at ripeness/agephase 3),
            // so the search must reject it first — the ripeness gate working end-to-end.
            string foodSearch =
                $"obj_query {g} \"-class {{Pilz}} -boundingbox {{-20 -20 -16 20 20 16}} " +
                "-visibility own -owner {own -1} -limit 10 -flagpos visible " +
                "-flagneg {contained locked} -water 0 -limit 1\"";
            string unripe = interp.Eval(foodSearch);
            TestContext.WriteLine($"food search (unripe) → '{unripe}'");
            Assert.AreEqual("0", unripe.Trim(), "an unripe (locked) Pilz must NOT be found");

            interp.Eval($"set_lock {p} 0");   // ripen: pilz.tcl clears the lock at agephase 3
            string r = interp.Eval(foodSearch);
            TestContext.WriteLine($"food search (ripe) → '{r}'");
            Assert.AreEqual(p, r.Trim(), "the gnome food search must find the ripe Pilz");
        }

        [Test, Explicit]
        public void RealClasses_FlagNegLocked_KeepsUnlockedItems()
        {
            var (interp, ctx, _) = RealHost();
            string g = interp.Eval("new Zwerg \"\" \"100 100 8\" \"0 0 0\"").Trim();
            string p1 = interp.Eval("new Pilz \"\" \"101 100 8\" \"0 0 0\"").Trim();
            string p2 = interp.Eval("new Pilz \"\" \"103 100 8\" \"0 0 0\"").Trim();
            interp.Eval($"set_lock {p1} 0");   // fresh Pilze spawn LOCKED (pilz.tcl obj_init) — ripen p1
            interp.Eval($"set_lock {p2} 1");
            string r = interp.Eval($"obj_query {g} \"-class Pilz -flagneg {{contained locked}}\"");
            TestContext.WriteLine($"flagneg search → '{r}'");
            StringAssert.Contains(p1, r);
            StringAssert.DoesNotContain(p2, r);
        }
    }
}
