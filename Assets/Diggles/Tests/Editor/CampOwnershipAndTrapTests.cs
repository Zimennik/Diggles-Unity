#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;
    using UnityEngine;

    // Two bugs rooted in missing owner/diplomacy law:
    //
    // 1) voodoo camp takeover — after discovering the herbalist camp the player's gnomes
    // "move in": eat at the voodoo Feuerstelle and sleep in the voodoo Zelte, and a
    // dispatcher repair on any own building turns into a conquer.
    // a. The native sparetime place query filters by owner + object flags:
    // FUN_005f4d00:75-76 (queryrect) / FUN_005f4b20:75-80 (queryrange) return an
    // announced place only when `place+0x36c == asker+0x36c` (same owner) and
    // `(flags & 0x1404002) == 0x1400000` — built(0x400000) + active(0x1000000) set,
    // boxed(0x4000) + contained(0x2) clear. The port returned every announced place
    // of the kind — the discovered voodoo camp (template objects `set_owner 1`,
    // templates/tcl/urw_unq_vodo_*.tcl) entered the player gnomes' eat/sleep/home
    // searches (z_spare_procs.tcl:133/:405, z_spare_fun.tcl:435/:517/:708).
    // b. get/set_prod_ownerstrength were "0" stubs. Native: per-prodplace float at
    // record+0x68 (SetProdOwnerStrength FUN_00693c20:87 / GetProdOwnerStrength
    // FUN_00693a40), ctor default = 1.0f (FUN_005c9fa0:219). The Tcl branches on it:
    // prod_autorepair (z_work_common.tcl:313) `< 0.95` → conquer (else prod_repair),
    // and conquer_callback (:613-657) walks it down/up by ±offset. Stub 0 = every
    // repair became a conquer and every conquer completed instantly (change_owner).
    //
    // 2) traps hit everyone — trap victim scans are `obj_query... -owner enemy`
    // (fallen.tcl:87/:123/:268/:299). The port's OwnerMatches passed all diplomacy
    // words ("don't over-filter"), so own gnomes petrified in their own Medusa traps.
    // Native law:
    // • word → mask (FUN_005e2330): world→0x10000, player→0x20000,
    // enemy→(Σ 1<<i where dip[self][i]==4) | 0x10000 (world counts as hostile),
    // friend→rel 2, ally→rel 1, neutral→rel 3, any→0xffffffff, allplayers→0xff,
    // own→1<<self, other→(0xff & ~(1<<self)) | 0x10000, numeric n→1<<n, -1→0x10000;
    // subject owner -1 → 0xffffffff for the relation words.
    // • object → bits (FUN_005e0e50:66-80): owner<0 → 0x10000, else
    // (owner==subject ? 0x20000: 0x40000) | 1<<owner. Match = (bits & mask) != 0.
    // • diplomacy table (8×8 at game+0xa420): init = all neutral(3), diagonal own(0)
    // (FUN_0055a060:218-233); set_diplomacy (FUN_00669360) is one-directional and
    // rejects world(-1)/self-pairs; get_diplomacy (FUN_00669000) maps
    // 0→own 1→ally 2→friend 3→neutral 4→enemy, and returns "enemy" when either
    // side is the world (-1).
    public sealed class CampOwnershipAndTrapTests
    {
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
            ctx.TolerateMissingEntities = true;
            ctx.TolerateMissingClass = true;
            ctx.TolerateMissingFiles = true;
            var sim = new DigglesGameSim(ctx);
            return (interp, ctx, sim);
        }

        static DigglesTclEntity Spawn(TclInterpreter interp, DigglesTclContext ctx, string cls,
            string pos = "0 0 0", int owner = 0)
        {
            var e = ctx.GetEntity(interp.Eval($"new {cls} \"\" \"{pos}\" \"0 0 0\"").Trim())!;
            interp.Eval($"set_owner {e.Handle} {owner}");
            return e;
        }

        // ------------------------------------------------------------------
        // 1a) sparetime query — owner + flag filter (the "move in" vector)
        // ------------------------------------------------------------------

        // Gnome owner 0; own fire + foreign (voodoo, owner 1) fire/tent nearby. The native
        // returns only the same-owner places (FUN_005f4d00:75 / FUN_005f4b20:75).
        [Test]
        public void SparetimeQuery_ExcludesForeignOwnerPlaces()
        {
            var (interp, ctx, _) = Host();
            interp.Eval("def_class F none production 0 {} {}");
            interp.Eval("def_class G none gnome 0 {} {}");

            var g = Spawn(interp, ctx, "G", "10 100 8", owner: 0);
            var ownFire = Spawn(interp, ctx, "F", "12 100 8", owner: 0);
            var vodoFire = Spawn(interp, ctx, "F", "14 100 8", owner: 1);
            var vodoTent = Spawn(interp, ctx, "F", "16 100 8", owner: 1);
            foreach (var b in new[] { ownFire, vodoFire, vodoTent })
                interp.Eval($"set_buildupstate {b.Handle} 1");   // genericprod.tcl:451 — real buildings are built
            interp.Eval($"sparetime {ownFire.Handle} announce eat");    // feuerstelle.tcl:326
            interp.Eval($"sparetime {vodoFire.Handle} announce eat");
            interp.Eval($"sparetime {vodoTent.Handle} announce sleep"); // zelt.tcl:47

            Assert.AreEqual(ownFire.Handle.ToString(),
                interp.Eval($"sparetime {g.Handle} queryrect eat -160 -80 160 80").Trim(),
                "queryrect must return only SAME-OWNER places (FUN_005f4d00:75) — the voodoo fire must not feed the player camp");
            Assert.AreEqual("",
                interp.Eval($"sparetime {g.Handle} queryrange sleep 40").Trim(),
                "queryrange must exclude the foreign tent (FUN_005f4b20:75) — gnomes must not sleep in the voodoo camp");
        }

        // The flag gate (flags & 0x1404002) == 0x1400000: built set; boxed/contained clear.
        [Test]
        public void SparetimeQuery_ExcludesUnbuiltBoxedAndContained()
        {
            var (interp, ctx, _) = Host();
            interp.Eval("def_class F none production 0 {} {}");
            interp.Eval("def_class G none gnome 0 {} {}");
            var g = Spawn(interp, ctx, "G", "10 100 8", owner: 0);

            // unbuilt (spawn default, CObj ctor bit 0x400000 clear) → excluded
            var raw = Spawn(interp, ctx, "F", "12 100 8", owner: 0);
            interp.Eval($"sparetime {raw.Handle} announce eat");
            Assert.AreEqual("", interp.Eval($"sparetime {g.Handle} queryrect eat -160 -80 160 80").Trim(),
                "an UNBUILT announced place must not be offered (built bit 0x400000 required)");

            // built → included
            interp.Eval($"set_buildupstate {raw.Handle} 1");
            Assert.AreEqual(raw.Handle.ToString(),
                interp.Eval($"sparetime {g.Handle} queryrect eat -160 -80 160 80").Trim());

            // boxed → excluded (set_boxed also clears built — FUN_0067dc80:157-161)
            interp.Eval($"set_boxed {raw.Handle} 1");
            Assert.AreEqual("", interp.Eval($"sparetime {g.Handle} queryrect eat -160 -80 160 80").Trim(),
                "a BOXED place must not be offered (boxed bit 0x4000 must be clear)");

            // contained → excluded
            interp.Eval($"set_boxed {raw.Handle} 0");
            interp.Eval($"set_buildupstate {raw.Handle} 1");
            var barrel = Spawn(interp, ctx, "F", "12 100 8", owner: 0);
            interp.Eval($"inv_add {barrel.Handle} {raw.Handle}");
            Assert.AreEqual("", interp.Eval($"sparetime {g.Handle} queryrect eat -160 -80 160 80").Trim(),
                "a CONTAINED place must not be offered (contained bit 0x2 must be clear)");
        }

        // ------------------------------------------------------------------
        // 1b) prod_ownerstrength — repair vs conquer branch
        // ------------------------------------------------------------------

        // Native default = 1.0 (prod record ctor FUN_005c9fa0:219 writes 1.0f at rec+0x68).
        // z_work_common.tcl:313 branches `< 0.95` → conquer; a healthy own building must
        // take the prod_repair branch. The old "0" stub made every repair a conquer.
        [Test]
        public void ProdOwnerstrength_DefaultFullStrength_TakesRepairBranch()
        {
            var (interp, ctx, _) = Host();
            interp.Eval("def_class F none production 0 {} {}");
            var b = Spawn(interp, ctx, "F", "0 0 8", owner: 0);

            Assert.AreEqual(1.0, double.Parse(
                    interp.Eval($"get_prod_ownerstrength {b.Handle}").Trim(),
                    System.Globalization.CultureInfo.InvariantCulture), 1e-6,
                "spawn default must be FULL strength 1.0 (FUN_005c9fa0:219), not the stub 0");
            Assert.AreEqual("0",
                interp.Eval($"expr {{[get_prod_ownerstrength {b.Handle}] < 0.95}}").Trim(),
                "prod_autorepair's branch (z_work_common.tcl:313) must pick prod_repair, not conquer");
        }

        // SetProdOwnerStrength FUN_00693c20:75-87 — raw float write (no clamp; the Tcl caps
        // at 1.0 itself, z_work_common.tcl:645); unparsable → 0.0.
        [Test]
        public void ProdOwnerstrength_SetGet_RoundTrip()
        {
            var (interp, ctx, _) = Host();
            interp.Eval("def_class F none production 0 {} {}");
            var b = Spawn(interp, ctx, "F", "0 0 8", owner: 1);

            interp.Eval($"set_prod_ownerstrength {b.Handle} 0.4");
            Assert.AreEqual(0.4, double.Parse(
                    interp.Eval($"get_prod_ownerstrength {b.Handle}").Trim(),
                    System.Globalization.CultureInfo.InvariantCulture), 1e-6);
            // the conquer rope-pull read-modify-write pattern (z_work_common.tcl:619/:643)
            interp.Eval($"set_prod_ownerstrength {b.Handle} [expr {{[get_prod_ownerstrength {b.Handle}] - 0.1}}]");
            Assert.AreEqual(0.3, double.Parse(
                    interp.Eval($"get_prod_ownerstrength {b.Handle}").Trim(),
                    System.Globalization.CultureInfo.InvariantCulture), 1e-6,
                "read-modify-write must progress (the stub pair read 0 forever)");
        }

        // ------------------------------------------------------------------
        // 2) obj_query -owner enemy — the trap trigger law
        // ------------------------------------------------------------------

        // Mirrors the Medusa trap victim scan (fallen.tcl:299, -owner enemy -boundingbox).
        // Own gnomes and neutral (voodoo) gnomes must not match; world critters (owner -1,
        // the wild Riesenhamster/Spinne) and declared enemies must.
        [Test]
        public void ObjQuery_OwnerEnemy_SkipsOwnAndNeutral_MatchesWorldAndDeclaredEnemies()
        {
            var (interp, ctx, _) = Host();
            interp.Eval("def_class Trap none protection 0 {} {}");
            interp.Eval("def_class Zwerg none gnome 0 {} {}");

            var trap = Spawn(interp, ctx, "Trap", "0 0 8", owner: 0);
            var ownGnome = Spawn(interp, ctx, "Zwerg", "0 0 8", owner: 0);
            string Q() => interp.Eval(
                $"obj_query {trap.Handle} \"-class Zwerg -owner enemy -boundingbox \\{{-0.5 -1.0 -5.0 0.5 1.0 5.0\\}}\"").Trim();

            Assert.AreEqual("0", Q(),
                "an OWN gnome must never trigger the owner's trap (dip[0][0] = own(0), not enemy(4))");

            var vodo = Spawn(interp, ctx, "Zwerg", "0.2 0 8", owner: 1);
            Assert.AreEqual("0", Q(),
                "a NEUTRAL foreign gnome must not trigger the trap (table default = neutral(3), FUN_0055a060:224)");

            var wild = Spawn(interp, ctx, "Zwerg", "0.1 0.5 8", owner: -1);
            Assert.AreEqual(wild.Handle.ToString(), Q(),
                "a WORLD-owned critter must match — the enemy mask ORs the 0x10000 world bit (FUN_005e2330:151)");

            interp.Eval($"del {wild.Handle}");
            interp.Eval("set_diplomacy 0 1 enemy");
            Assert.AreEqual(vodo.Handle.ToString(), Q(),
                "after set_diplomacy 0 1 enemy the owner-1 gnome must match (rel 4 bit)");
        }

        // The table semantics behind it: defaults, world, one-directional writes,
        // rejected self/world writes (FUN_00669000 / FUN_00669360 / FUN_0055a060).
        [Test]
        public void Diplomacy_TableSemantics_Native()
        {
            var (interp, _, _) = Host();

            Assert.AreEqual("neutral", interp.Eval("get_diplomacy 0 1").Trim(),
                "default off-diagonal relation = neutral(3) (FUN_0055a060:224)");
            Assert.AreEqual("own", interp.Eval("get_diplomacy 2 2").Trim(),
                "diagonal = own(0) (FUN_0055a060:230-233; FUN_00669000:131-135 prints 'own')");
            Assert.AreEqual("enemy", interp.Eval("get_diplomacy 0 -1").Trim(),
                "either side -1 = the world → 'enemy' (FUN_00669000:93-94)");

            interp.Eval("set_diplomacy 0 1 enemy");
            Assert.AreEqual("enemy", interp.Eval("get_diplomacy 0 1").Trim());
            Assert.AreEqual("neutral", interp.Eval("get_diplomacy 1 0").Trim(),
                "set_diplomacy is ONE-DIRECTIONAL (scripts set both directions, CampaignInit.tcl:4-13)");

            interp.Eval("set_diplomacy 3 3 enemy");   // "can't change own diplomacy state !" (FUN_00669360:103-105)
            Assert.AreEqual("own", interp.Eval("get_diplomacy 3 3").Trim());
            interp.Eval("set_diplomacy 0 -1 enemy");  // "can't change world diplomacy state" (:95-96)
            Assert.AreEqual("enemy", interp.Eval("get_diplomacy 0 -1").Trim());
            interp.Eval("set_diplomacy 1 2 friend");
            Assert.AreEqual("friend", interp.Eval("get_diplomacy 1 2").Trim());
        }

        // -owner own / numeric / -1 must keep their existing (already-correct) behavior.
        [Test]
        public void ObjQuery_OwnerWords_OwnNumericWorld_Unchanged()
        {
            var (interp, ctx, _) = Host();
            interp.Eval("def_class Zwerg none gnome 0 {} {}");
            var self = Spawn(interp, ctx, "Zwerg", "0 0 8", owner: 0);
            var mate = Spawn(interp, ctx, "Zwerg", "0.5 0 8", owner: 0);
            var vodo = Spawn(interp, ctx, "Zwerg", "0.6 0 8", owner: 1);
            var wild = Spawn(interp, ctx, "Zwerg", "0.7 0 8", owner: -1);

            Assert.AreEqual(mate.Handle.ToString(),
                interp.Eval($"obj_query {self.Handle} \"-class Zwerg -owner own -range 5\"").Trim());
            Assert.AreEqual(vodo.Handle.ToString(),
                interp.Eval($"obj_query {self.Handle} \"-class Zwerg -owner 1 -range 5\"").Trim());
            Assert.AreEqual(wild.Handle.ToString(),
                interp.Eval($"obj_query {self.Handle} \"-class Zwerg -owner -1 -range 5\"").Trim());
            // the lager.tcl:112 list form {own -1} — ORed word masks
            var both = TclList.Parse(interp.Eval(
                $"obj_query {self.Handle} \"-class Zwerg -owner \\{{own -1\\}} -range 5\"").Trim());
            CollectionAssert.AreEquivalent(
                new[] { mate.Handle.ToString(), wild.Handle.ToString() }, both);
        }
    }
}
