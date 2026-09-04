#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text;
    using Diggles.Runtime;
    using Diggles.Runtime.Navigation;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class FightSystemTests
    {
        static (TclInterpreter Interp, DigglesTclContext Context) Host()
        {
            var interp = new TclInterpreter
            {
                TolerateExprErrors = true,
                TolerateMissingVars = true,
            };
            interp.UnknownCommandHandler = (_, _) => "0";
            var context = DigglesTclEngine.Register(interp);
            context.TolerateMissingClass = true;
            context.TolerateMissingEntities = true;
            context.TolerateMissingFiles = true;
            return (interp, context);
        }

        static DigglesTclEntity Spawn(TclInterpreter interp, DigglesTclContext context,
            string className, string position = "0 0 0") =>
            context.GetEntity(interp.Eval($"new {className} \"\" \"{position}\" \"0 0 0\"").Trim())!;

        static float Number(string value) =>
            float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);

        static (NavWorld Nav, uint[] BlockIndices) AttackNavigation(
            System.Action<byte[]> configure)
        {
            const int width = 32;
            const int height = 32;
            var blockIndices = new uint[width * height];
            System.Array.Fill(blockIndices, 1u);
            var auxiliary = new byte[BdbModel.AuxStride * 2];
            for (int index = BdbModel.AuxStride; index < auxiliary.Length; index++)
                auxiliary[index] = 10;
            var bdb = new BdbModel
            {
                Header = new BdbHeader { NBlocks = 2 },
                PerBlockAux = auxiliary,
            };
            var subGrid = new byte[width * height * 64];
            configure(subGrid);
            return (new NavWorld(width, height, blockIndices, subGrid, bdb), blockIndices);
        }

        static void SetSubCell(byte[] subGrid, int subX, int subY, byte value)
        {
            const int width = 32;
            int tile = (subY >> 3) * width + (subX >> 3);
            subGrid[tile * 64 + ((subY & 7) << 3) + (subX & 7)] = value;
        }

        static GameObject AttachTerrain(
            DigglesTclContext context,
            NavWorld nav,
            uint[] blockIndices,
            string name)
        {
            var terrainObject = new GameObject(name);
            var terrain = terrainObject.AddComponent<DiggableTerrain>();
            terrain.Width = nav.Width;
            terrain.Height = nav.Height;
            terrain.BlockIdx = blockIndices;
            terrain.Nav = nav;
            context.Terrain = terrain;
            return terrainObject;
        }

        static void DefineCombatFixture(TclInterpreter interp)
        {
            interp.Eval("def_attrib atr_Hitpoints 0 10 10");
            interp.Eval("def_attrib atr_ExpMax 0 10 10");
            interp.Eval("def_attrib exp_F_Kungfu 0 1 0.5");
            interp.Eval("def_attrib exp_F_Ballistic 0 1 0.5");
            interp.Eval("def_attrib exp_F_Defense 0 1 0");
            interp.Eval("def_attrib exp_Kampf 0 10 0");
            interp.Eval("def_attrib atr_Kampf 0 10 0");
            interp.Eval("def_class Fighter none gnome 0 {} {class_fightdist 1}");
            interp.Eval("def_class Target none monster 0 {} {class_fightdist 1; " +
                "def_event evt_fight_get_hurt; " +
                "handle_event evt_fight_get_hurt {" +
                "add_attrib this atr_Hitpoints -[event_get this -num1]}}");
            interp.Eval("def_class Building none production 0 {} {class_fightdist 1}");
            interp.Eval("fight_weapon none 1 0 1 kungfu exp_F_Kungfu 2 0");
            interp.Eval("fight_weapon Bow 1 0 9 ballist exp_F_Ballistic 2 0");
            interp.Eval("fight_action Punch Fighter kungfu beat s f 0 punch 1 0");
            interp.Eval("fight_action Shot Fighter ballist throw s f 0 shot 1 0");
            interp.Eval("fight_action Hurt Target kungfu hit s flrb 0 hurt 0 0");
            interp.Eval("fight_action Fall Target kungfu hit s flrb 0 fall -1 0");
        }

        static string[] ResultItems(string value)
        {
            var parsed = TclList.Parse(value);
            var result = new string[parsed.Count];
            for (int index = 0; index < parsed.Count; index++) result[index] = parsed[index];
            return result;
        }

        [Test]
        public void Definitions_PreserveActionsWeaponsMapsAndFrames()
        {
            var (interp, context) = Host();

            interp.Eval("fight_action Slash Zwerg sword beat usd flr 2 slash_anim 0.7 0.2");
            interp.Eval("fight_weapon none 0.2 0.2 1 kungfu exp_F_Kungfu 2.5 0");
            interp.Eval("fight_weapon Bow 0.75 0 9 ballist exp_F_Ballistic 1.5 0.04 bow {9 13}");
            interp.Eval("fight_weapon_kombi {Bow Amulet} {2 0}");
            interp.Eval("fight_map_obj2db Zwerg mann");
            interp.Eval("fight_map_class2db Troll troll");

            Assert.AreEqual(1, context.Fight.Actions.Count);
            Assert.AreEqual("slash_anim", context.Fight.Actions[0].Animation);
            Assert.IsTrue(context.Fight.Actions[0].Collision,
                "the native collision field treats every non-zero integer, including 2, as set");
            Assert.AreEqual(0.2f, context.Fight.Actions[0].MinimumExperience, 1e-6f);
            Assert.AreEqual(2, context.Fight.Weapons.Count);
            Assert.AreEqual(1, context.Fight.Weapons[1].Id);
            Assert.AreEqual(new[] { 9, 13 }, context.Fight.Weapons[1].Frames);
            Assert.AreEqual("mann", context.Fight.ObjectDatabaseMap["Zwerg"]);
            Assert.AreEqual("troll", context.Fight.ClassDatabaseMap["Troll"]);
            Assert.AreEqual(2f, context.Fight.Combinations[0].Multipliers[0], 1e-6f);
        }

        [Test]
        public void ShippedFightTable_LoadsCompletely()
        {
            var (interp, context) = Host();
            string path = Path.Combine(OriginalDataTestFixture.DataRoot, "Scripts", "init", "fight.tcl");
            interp.Eval(File.ReadAllText(path, Encoding.GetEncoding("ISO-8859-1")));

            Assert.AreEqual(377, context.Fight.Actions.Count);
            Assert.AreEqual(52, context.Fight.Weapons.Count);
            Assert.AreEqual(18, context.Fight.ObjectDatabaseMap.Count);
            Assert.AreEqual(5, context.Fight.Combinations.Count);
            Assert.AreEqual("kungfu", context.Fight.Weapons[0].Pose);
            Assert.AreEqual("ballist", context.Fight.Weapons[1].Pose);
            Assert.AreEqual(0.5f, context.Fight.GetHpRule("Fresspflanze", 7).Ratio, 1e-6f);
            Assert.AreEqual(0.1f, context.Fight.GetHpRule("Zwerg", 4).Escape, 1e-6f,
                "omitted escape values use the documented native default");
            Assert.AreEqual(0.1f, context.Fight.GetHpRule("Unknown", 0).Escape, 1e-6f,
                "fight.tcl documents the native default escape threshold as 0.1");
        }

        [Test]
        public void FirstCombatConsumer_LazilyLoadsTheShippedFightTable()
        {
            var (interp, context) = Host();
            context.DataRoot = OriginalDataTestFixture.DataRoot;

            Assert.That(context.Fight.Actions, Is.Empty,
                "the normal runtime definition boot does not source init/fight.tcl");
            Assert.That(context.Fight.Weapons, Is.Empty);

            Assert.That(interp.Eval("get_weapon_pose 0").Trim(), Is.EqualTo("kungfu"));
            Assert.That(context.Fight.Actions, Has.Count.EqualTo(377));
            Assert.That(context.Fight.Weapons, Has.Count.EqualTo(52));

            _ = interp.Eval("get_weapon_range 0");
            Assert.That(context.Fight.Actions, Has.Count.EqualTo(377),
                "subsequent combat consumers must not duplicate the definition table");
        }

        [Test]
        public void LazyFightTableHydration_DoesNotLeakLoaderVarsIntoTheConsumingScope()
        {
            // The original executes init/fight.tcl in the system interpreter at boot; its
            // top-level loader helpers (`set sw` fight.tcl:249, `set DrB`:412, `set RH`
            //:435) never exist in any object interpreter — and the per-object interp var
            // table is exactly what the save system captures. Hydration triggered from
            // inside an entity body must therefore not deposit them into that entity.
            var (interp, context) = Host();
            context.DataRoot = OriginalDataTestFixture.DataRoot;
            interp.Eval("def_class ScopeProbe none gnome 0 {} {" +
                "def_event evt_probe; " +
                "handle_event evt_probe {set probe_pose [get_weapon_pose 0]}}");
            var probe = Spawn(interp, context, "ScopeProbe");

            DigglesTclEngine.DispatchEvent(context, probe,
                new Diggles.Runtime.Sim.EntityEvent("evt_probe", null));

            Assert.AreEqual("kungfu", probe.VarsScope.Vars["probe_pose"],
                "hydration triggered from inside an entity body still serves the table");
            Assert.That(context.Fight.Actions, Has.Count.EqualTo(377));
            Assert.IsFalse(probe.VarsScope.Vars.ContainsKey("sw"),
                "fight.tcl loader helpers must not enter the entity's saved var table");
            Assert.IsFalse(probe.VarsScope.Vars.ContainsKey("DrB"));
            Assert.IsFalse(probe.VarsScope.Vars.ContainsKey("RH"));
            Assert.IsFalse(interp.GlobalScope.Vars.ContainsKey("sw"),
                "the hydration scope is isolated — no leak into the shared global either");
        }

        [Test]
        public void FightTableLoadFailure_DoesNotLatchAPartialCatalogue()
        {
            var (interp, context) = Host();
            string root = Path.Combine(Path.GetTempPath(),
                "diggles_fight_partial_" + System.Guid.NewGuid().ToString("N"));
            string initDir = Path.Combine(root, "Scripts", "init");
            Directory.CreateDirectory(initDir);
            string file = Path.Combine(initDir, "fight.tcl");
            const string GoodRows =
                "fight_action Punch Fighter kungfu beat s f 0 punch 1 0\n" +
                "fight_weapon none 1 0 1 kungfu exp_F_Kungfu 2 0\n";
            File.WriteAllText(file, GoodRows + "fight_weapon broken\n");
            context.DataRoot = root;
            try
            {
                Assert.That(() => interp.Eval("get_weapon_pose 0"),
                    Throws.InstanceOf<TclException>(),
                    "a mid-file loader failure must surface, not be swallowed");
                Assert.That(context.Fight.Actions, Is.Empty,
                    "a failed load must not strand a truncated catalogue that the " +
                    "fixture-preservation guard would latch as final");
                Assert.That(context.Fight.Weapons, Is.Empty);

                File.WriteAllText(file, GoodRows);
                context.FileCache.Clear();
                Assert.AreEqual("kungfu", interp.Eval("get_weapon_pose 0").Trim(),
                    "the next combat consumer must retry the full load");
                Assert.That(context.Fight.Actions, Has.Count.EqualTo(1));
                Assert.That(context.Fight.Weapons, Has.Count.EqualTo(1));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Test]
        public void ShippedFightTable_DrivesARealZwergVersusWukerAttack()
        {
            var (interp, context) = Host();
            interp.Eval("def_attrib atr_Hitpoints 0 10 10");
            interp.Eval("def_attrib atr_ExpMax 0 10 10");
            interp.Eval("def_attrib exp_F_Kungfu 0 1 0.5");
            interp.Eval("def_attrib exp_F_Defense 0 1 0");
            interp.Eval("def_attrib exp_Kampf 0 10 0");
            interp.Eval("def_attrib atr_Kampf 0 10 0");
            interp.Eval("def_class Zwerg none gnome 0 {} {class_fightdist 1}");
            interp.Eval("def_class Wuker none monster 0 {} {class_fightdist 1}");
            string path = Path.Combine(OriginalDataTestFixture.DataRoot, "Scripts", "init", "fight.tcl");
            interp.Eval(File.ReadAllText(path, Encoding.GetEncoding("ISO-8859-1")));
            context.StoryRng = new System.Random(23);
            var sim = new Diggles.Runtime.Sim.DigglesGameSim(context);
            var attacker = Spawn(interp, context, "Zwerg", "0 0 0");
            var defender = Spawn(interp, context, "Wuker", "0 0 2");
            // A swung-at entity only receives a reaction action as a fight participant
            // (FUN_005bf660:493-553/:699 — the participant gate).
            defender.CurrentState = "fight_dispatch";

            var result = ResultItems(interp.Eval(
                $"fight_setactions {attacker.Handle} {defender.Handle} {{}} {{}}"));
            Assert.IsNotNull(attacker.ActiveAction);
            var attackerState = (TimedImpactActionSaveState)
                ((ISaveableEntityAction)attacker.ActiveAction!).CaptureSaveState();
            var attackerAnimation = (AnimActionSaveState)attackerState.InnerAction;
            var defenderAnimation = (AnimActionSaveState)
                ((ISaveableEntityAction)defender.ActiveAction!).CaptureSaveState();
            Assert.That(attackerAnimation.Clip, Does.StartWith("mann."));
            Assert.That(defenderAnimation.Clip, Does.StartWith("wuker."));
            Assert.That(attackerAnimation.Clip, Does.Not.Contain("kletter"));
            Assert.That(defenderAnimation.Clip, Does.Not.Contain("kletter"),
                "floor combat cannot select the wall-only climbing rows");
            sim.Step(0.4);

            CollectionAssert.AreEqual(new[] { "attack", defender.Handle.ToString() },
                TclList.Parse(result[0]));
            Assert.Less(context.Stats.Get(defender.Stats, "atr_Hitpoints"), 10f,
                "the resolver must consume the original action and HP-ratio tables directly");
        }

        [Test]
        public void WeaponQueries_RespectTypeExperienceBallisticFilterAndCombination()
        {
            var (interp, context) = Host();
            interp.Eval("def_attrib exp_F_Kungfu 0 1 0.5");
            interp.Eval("def_attrib exp_F_Sword 0 1 0.1");
            interp.Eval("def_attrib exp_F_Ballistic 0 1 0.1");
            interp.Eval("def_attrib exp_F_Defense 0 1 0.1");
            interp.Eval("def_class Fighter none gnome 0 {} {}");
            interp.Eval("def_class Sword none tool 0 {} {}");
            interp.Eval("def_class Bow none tool 0 {} {}");
            interp.Eval("def_class Shield none tool 0 {} {}");
            interp.Eval("def_class Amulet none tool 0 {} {}");
            interp.Eval("fight_weapon none 0.2 0.2 1 kungfu exp_F_Kungfu 2.5 0");
            interp.Eval("fight_weapon Sword 1 0 1 sword exp_F_Sword 2 0.05");
            interp.Eval("fight_weapon Bow 1.5 0 9 ballist exp_F_Ballistic 1 0.2");
            interp.Eval("fight_weapon Shield 0 0.4 0 shield exp_F_Defense 2 0");
            interp.Eval("fight_weapon_kombi {Sword Amulet} {2 0}");

            var fighter = Spawn(interp, context, "Fighter");
            var sword = Spawn(interp, context, "Sword");
            var bow = Spawn(interp, context, "Bow");
            var shield = Spawn(interp, context, "Shield");
            var amulet = Spawn(interp, context, "Amulet");
            foreach (var item in new[] { sword, bow, shield, amulet })
                interp.Eval($"inv_add {fighter.Handle} {item.Handle}");

            Assert.AreEqual("1", interp.Eval($"get_weapon_id {sword.Handle} true").Trim());
            Assert.AreEqual("3", interp.Eval($"get_weapon_id {shield.Handle} false").Trim());
            Assert.AreEqual("1", interp.Eval($"chack_weapon_exp {fighter.Handle} 1").Trim());
            Assert.AreEqual("0", interp.Eval($"chack_weapon_exp {fighter.Handle} 2").Trim());

            var melee = TclList.Parse(interp.Eval($"get_best_weapon {fighter.Handle} 0"));
            Assert.AreEqual(sword.Handle.ToString(CultureInfo.InvariantCulture), melee[0]);
            Assert.AreEqual(shield.Handle.ToString(CultureInfo.InvariantCulture), melee[1]);
            Assert.AreEqual(Mathf.Sqrt(2f), Number(melee[2]), 1e-6f,
                "native attack evaluation is sqrt(attack times combination multiplier)");
            Assert.AreEqual(1f, Number(melee[3]), 1e-6f,
                "the native result exposes the selected shield's combination multiplier");

            var ballistic = TclList.Parse(interp.Eval($"get_best_weapon {fighter.Handle} 1"));
            Assert.AreEqual("-1", ballistic[0],
                "a ballistic weapon above the minimum experience must not be selected");
            interp.Eval($"set_attrib {fighter.Handle} exp_F_Ballistic 0.2");
            ballistic = TclList.Parse(interp.Eval($"get_best_weapon {fighter.Handle} 1"));
            Assert.AreEqual(bow.Handle.ToString(CultureInfo.InvariantCulture), ballistic[0]);
            Assert.AreEqual(Mathf.Sqrt(1.5f), Number(ballistic[2]), 1e-6f);
        }

        [Test]
        public void BestWeapon_TwoHandedSelectionSuppressesShield()
        {
            var (interp, context) = Host();
            interp.Eval("def_attrib exp_F_Kungfu 0 1 0.5");
            interp.Eval("def_attrib exp_F_Twohanded 0 1 0.1");
            interp.Eval("def_attrib exp_F_Defense 0 1 0.1");
            interp.Eval("def_class Fighter none gnome 0 {} {}");
            interp.Eval("def_class Axe none tool 0 {} {}");
            interp.Eval("def_class Shield none tool 0 {} {}");
            interp.Eval("fight_weapon none 0.2 0.2 1 kungfu exp_F_Kungfu 2.5 0");
            interp.Eval("fight_weapon Axe 1.4 0 1 twohand exp_F_Twohanded 2 0");
            interp.Eval("fight_weapon Shield 0 0.4 0 shield exp_F_Defense 2 0");
            var fighter = Spawn(interp, context, "Fighter");
            var axe = Spawn(interp, context, "Axe");
            var shield = Spawn(interp, context, "Shield");
            interp.Eval($"inv_add {fighter.Handle} {axe.Handle}");
            interp.Eval($"inv_add {fighter.Handle} {shield.Handle}");

            var best = TclList.Parse(interp.Eval($"get_best_weapon {fighter.Handle} 0"));

            Assert.AreEqual(axe.Handle.ToString(CultureInfo.InvariantCulture), best[0]);
            Assert.AreEqual("-1", best[1]);
            Assert.AreEqual(0f, Number(best[3]), 1e-6f);
        }

        [Test]
        public void HpRules_UseExactOwnerThenAllOwnersFallback()
        {
            var (interp, context) = Host();
            interp.Eval("def_hp_ratio Zwerg all 2.5 0.1");
            interp.Eval("def_hp_ratio Zwerg 3 1.5 0.45");

            Assert.AreEqual(0.45f, Number(interp.Eval("get_escape_value Zwerg 3")), 1e-6f);
            Assert.AreEqual(0.1f, Number(interp.Eval("get_escape_value Zwerg 2")), 1e-6f);
            Assert.AreEqual(1.5f, context.Fight.GetHpRule("Zwerg", 3).Ratio, 1e-6f);
            Assert.AreEqual(2.5f, context.Fight.GetHpRule("Zwerg", 7).Ratio, 1e-6f);
            Assert.AreEqual(1f, context.Fight.GetHpRule("Unknown", 0).Ratio, 1e-6f);
        }

        [Test]
        public void ObjectFightFields_RoundTripAndVelocityComponentIsLive()
        {
            var (interp, context) = Host();
            interp.Eval("def_class Fighter none gnome 0 {} {}");
            var fighter = Spawn(interp, context, "Fighter");

            interp.Eval($"set_weapon_class {fighter.Handle} 7");
            interp.Eval($"set_shield_class {fighter.Handle} 9");
            interp.Eval($"set_attackinprogress {fighter.Handle} true");
            interp.Eval($"set_diedinfight {fighter.Handle} 1");
            interp.Eval($"set_climbability {fighter.Handle} 0");
            interp.Eval($"set_vel {fighter.Handle} \"1.25 -2 3.5\"");

            Assert.AreEqual("7", interp.Eval($"get_weapon_class {fighter.Handle}").Trim());
            Assert.AreEqual("9", interp.Eval($"get_shield_class {fighter.Handle}").Trim());
            Assert.AreEqual("1", interp.Eval($"get_attackinprogress {fighter.Handle}").Trim());
            Assert.AreEqual("1", interp.Eval($"get_diedinfight {fighter.Handle}").Trim());
            Assert.AreEqual("0", interp.Eval($"get_climbability {fighter.Handle}").Trim());
            CollectionAssert.AreEqual(new[] { "1.25", "-2", "3.5" },
                TclList.Parse(interp.Eval($"get_velcomp {fighter.Handle}")));
        }

        [Test]
        public void AttackPosition_UsesLargerClassDistanceAndDefenderHeight()
        {
            var (interp, context) = Host();
            interp.Eval("def_class Attacker none gnome 0 {} {class_fightdist 1.25}");
            interp.Eval("def_class Defender none production 0 {} {class_fightdist 3}");
            var attacker = Spawn(interp, context, "Attacker", "10 10 2");
            var defender = Spawn(interp, context, "Defender", "6 10 8");

            var position = TclList.Parse(interp.Eval(
                $"get_attack_pos {attacker.Handle} {defender.Handle}"));
            Assert.AreEqual(9f, Number(position[0]), 1e-6f);
            Assert.AreEqual(10f, Number(position[1]), 1e-6f);
            Assert.AreEqual(8f, Number(position[2]), 1e-6f);

            position = TclList.Parse(interp.Eval(
                $"get_attack_pos {attacker.Handle} {defender.Handle} 1.5"));
            Assert.AreEqual(7.5f, Number(position[0]), 1e-6f);
        }

        [Test]
        public void AttackPosition_FloorUsesNativePlacementEnvelopeAndReachableCell()
        {
            var (interp, context) = Host();
            interp.Eval("def_class FloorAttacker none gnome 0 {} {class_fightdist 1.2}");
            interp.Eval("def_class FloorDefender none production 0 {} {class_fightdist 1.2}");
            var attacker = Spawn(interp, context, "FloorAttacker", "18 12 12");
            var defender = Spawn(interp, context, "FloorDefender", "14 12 12");
            var (nav, blockIndices) = AttackNavigation(subGrid =>
            {
                for (int subX = 5 * 8; subX <= 27 * 8; subX++)
                    SetSubCell(subGrid, subX, 12 * 8, NavWorld.SubWalk | NavWorld.SubFloor);
            });
            GameObject terrainObject = AttachTerrain(
                context, nav, blockIndices, "FightSystemTestsFloorTerrain");
            try
            {
                string result = interp.Eval(
                    $"get_attack_pos {attacker.Handle} {defender.Handle}").Trim();
                Assert.AreNotEqual("0", result);
                var position = TclList.Parse(result);
                Assert.That(position, Has.Count.EqualTo(3));
                float x = Number(position[0]);
                float y = Number(position[1]);
                float distance = Vector2.Distance(
                    new Vector2(x, y),
                    new Vector2(defender.Position.x, defender.Position.y));
                Assert.That(distance, Is.InRange(1.2f, 1.4f));
                Assert.That(x, Is.GreaterThan(defender.Position.x));
                Assert.IsTrue(nav.SubWalkable(
                    Mathf.RoundToInt(x * 8f),
                    Mathf.RoundToInt(y * 8f)));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(terrainObject);
            }
        }

        [Test]
        public void AttackPosition_FloorPlacementFailureReturnsZero()
        {
            var (interp, context) = Host();
            interp.Eval("def_class IsolatedAttacker none gnome 0 {} {class_fightdist 1.2}");
            interp.Eval("def_class IsolatedDefender none production 0 {} {class_fightdist 1.2}");
            var attacker = Spawn(interp, context, "IsolatedAttacker", "18 12 12");
            var defender = Spawn(interp, context, "IsolatedDefender", "14 12 12");
            var (nav, blockIndices) = AttackNavigation(subGrid =>
            {
                SetSubCell(subGrid, 18 * 8, 12 * 8, NavWorld.SubWalk | NavWorld.SubFloor);
                SetSubCell(subGrid, 14 * 8, 12 * 8, NavWorld.SubWalk | NavWorld.SubFloor);
            });
            GameObject terrainObject = AttachTerrain(
                context, nav, blockIndices, "FightSystemTestsIsolatedFloorTerrain");
            try
            {
                Assert.AreEqual("0", interp.Eval(
                    $"get_attack_pos {attacker.Handle} {defender.Handle}").Trim());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(terrainObject);
            }
        }

        [Test]
        public void AttackPosition_ReachableWallUsesNativeSideOffset()
        {
            var (interp, context) = Host();
            interp.Eval("def_class ClimbingAttacker none monster 0 {} {class_fightdist 1.2}");
            interp.Eval("def_class ClimbingDefender none gnome 0 {} {class_fightdist 1.2}");
            var attacker = Spawn(interp, context, "ClimbingAttacker", "10 12 12");
            var defender = Spawn(interp, context, "ClimbingDefender", "10 10 12");
            var (nav, blockIndices) = AttackNavigation(subGrid =>
            {
                for (int subY = 70; subY <= 12 * 8; subY++)
                    SetSubCell(subGrid, 10 * 8, subY, NavWorld.SubWalk | NavWorld.SubClimb);
                SetSubCell(subGrid, 10 * 8, 12 * 8, NavWorld.SubWalk | NavWorld.SubFloor);
            });
            GameObject terrainObject = AttachTerrain(
                context, nav, blockIndices, "FightSystemTestsReachableWallTerrain");
            try
            {
                var position = TclList.Parse(interp.Eval(
                    $"get_attack_pos {attacker.Handle} {defender.Handle}"));
                Assert.That(position, Has.Count.EqualTo(3));
                Assert.AreEqual(10f, Number(position[0]), 1e-6f);
                Assert.AreEqual(8.8f, Number(position[1]), 1e-6f);
                Assert.AreEqual(12f, Number(position[2]), 1e-6f);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(terrainObject);
            }
        }

        [Test]
        public void AttackPosition_UnreachableWallSideReturnsZero()
        {
            var (interp, context) = Host();
            interp.Eval("def_class WallAttacker none monster 0 {} {class_fightdist 1.2}");
            interp.Eval("def_class WallDefender none gnome 0 {} {class_fightdist 1.2}");
            var attacker = Spawn(interp, context, "WallAttacker", "10 12 12");
            var defender = Spawn(interp, context, "WallDefender", "10 10 12");
            var (nav, blockIndices) = AttackNavigation(subGrid =>
            {
                for (int subX = 5 * 8; subX <= 15 * 8; subX++)
                    SetSubCell(subGrid, subX, 12 * 8, NavWorld.SubWalk | NavWorld.SubFloor);
                SetSubCell(subGrid, 10 * 8, 10 * 8, NavWorld.SubWalk | NavWorld.SubClimb);
            });
            GameObject terrainObject = AttachTerrain(
                context, nav, blockIndices, "FightSystemTestsBlockedWallTerrain");
            try
            {
                Assert.AreEqual("0", interp.Eval(
                    $"get_attack_pos {attacker.Handle} {defender.Handle}").Trim());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(terrainObject);
            }
        }

        [Test]
        public void WukerEnemyScan_DoesNotStartBlindWalkToBlockedWallSide()
        {
            var (interp, context) = Host();
            context.DataRoot = OriginalDataTestFixture.DataRoot;
            interp.Eval("def_attrib atr_Hitpoints 0 1 1");
            interp.Eval("def_class WallScanTarget none gnome 0 {} {class_fightdist 1.2}");
            interp.Eval("def_class WallScanWuker none monster 0 {} {" +
                "class_fightdist 1.2; " +
                "obj_init {set scan_range 7; set sniff_range 13; " +
                "set enemy_classes {WallScanTarget}; set attack_behaviour none; " +
                "set attack_item 0; set approach 0; " +
                "call scripts/classes/characters/w_procs.tcl; " +
                "call scripts/misc/genericfight.tcl}; " +
                "method scan {} {return [find_enemy]}}");
            var wuker = Spawn(interp, context, "WallScanWuker", "10 12 12");
            var target = Spawn(interp, context, "WallScanTarget", "10 10 12");
            context.Stats.Set(target.Stats, "atr_Hitpoints", 1.0);
            target.VarsScope.Vars["_hoverable"] = "1";
            var (nav, blockIndices) = AttackNavigation(subGrid =>
            {
                for (int subX = 5 * 8; subX <= 15 * 8; subX++)
                    SetSubCell(subGrid, subX, 12 * 8, NavWorld.SubWalk | NavWorld.SubFloor);
                SetSubCell(subGrid, 10 * 8, 10 * 8, NavWorld.SubWalk | NavWorld.SubClimb);
            });
            GameObject terrainObject = AttachTerrain(
                context, nav, blockIndices, "FightSystemTestsWukerWallTerrain");
            try
            {
                Assert.AreEqual("2", interp.Eval($"call_method {wuker.Handle} scan").Trim());
                Assert.IsNull(wuker.ActiveAction);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(terrainObject);
            }
        }

        [Test]
        public void MeleeAttack_SelectsActionsDelaysDamageAndCompletesCallbacks()
        {
            var (interp, context) = Host();
            DefineCombatFixture(interp);
            context.StoryRng = new System.Random(7);
            var sim = new Diggles.Runtime.Sim.DigglesGameSim(context);
            var attacker = Spawn(interp, context, "Fighter", "0 0 0");
            var defender = Spawn(interp, context, "Target", "0 0 2");
            defender.CurrentState = "fight_dispatch";   // participant gate (FUN_005bf660:493/:699)

            string[] result = ResultItems(interp.Eval($"fight_setactions " +
                $"{attacker.Handle} {defender.Handle} {{set attacker_done 1}} " +
                "{set defender_done 1}"));

            Assert.AreEqual(1, result.Length);
            CollectionAssert.AreEqual(new[] { "attack", defender.Handle.ToString() },
                TclList.Parse(result[0]));
            Assert.IsTrue(attacker.AttackInProgress);
            Assert.IsTrue(defender.AttackInProgress);
            Assert.AreEqual(10f, context.Stats.Get(defender.Stats, "atr_Hitpoints"), 1e-6f,
                "damage is tied to the animation impact instead of command evaluation");

            sim.Step(0.299);
            Assert.AreEqual(10f, context.Stats.Get(defender.Stats, "atr_Hitpoints"), 1e-6f);
            Assert.That(defender.PendingEvents, Is.Empty,
                "the native damage event is not due before the 0.3-second impact timer");

            sim.Step(0.002);
            Assert.AreEqual(1, defender.PendingEvents.Count,
                "crossing 0.3 seconds enqueues the shipped evt_fight_get_hurt contract");
            Assert.AreEqual(10f, context.Stats.Get(defender.Stats, "atr_Hitpoints"), 1e-6f,
                "the target consumes the queued event on the next event-dispatch phase");
            sim.Step(0.01);

            Assert.Less(context.Stats.Get(defender.Stats, "atr_Hitpoints"), 10f);
            Assert.IsFalse(attacker.AttackInProgress);
            Assert.IsFalse(defender.AttackInProgress);
            Assert.AreEqual("1", attacker.VarsScope.Vars["attacker_done"]);
            Assert.AreEqual("1", defender.VarsScope.Vars["defender_done"]);
            Assert.Greater(context.Stats.Get(attacker.Stats, "exp_Kampf"), 0,
                "combat progression must also update the aggregate skill");
        }

        [Test]
        public void MeleeAttack_WritesNoDiplomacyItself()
        {
            // Native fight_setactions (CFightActionMgr::DoObjActions, FUN_005bf660) contains
            // zero diplomacy calls; war escalation is script-side only (fight_startfight,
            // genericfight.tcl:1236-1241, plus the victim's defend chain and the production
            // branch genericfight.tcl:183-187). A landed swing at a neutral clan's gnome must
            // therefore leave the diplomacy table untouched at the resolver level.
            var (interp, context) = Host();
            DefineCombatFixture(interp);
            interp.Eval("def_class ClanGnome none gnome 0 {} {class_fightdist 1}");
            var attacker = Spawn(interp, context, "Fighter", "0 0 0");
            var defender = Spawn(interp, context, "ClanGnome", "0 0 2");
            interp.Eval($"set_owner {attacker.Handle} 0");
            interp.Eval($"set_owner {defender.Handle} 1");
            Assert.AreEqual("neutral", interp.Eval("get_diplomacy 0 1").Trim());
            Assert.AreEqual("neutral", interp.Eval("get_diplomacy 1 0").Trim());

            string[] result = ResultItems(interp.Eval(
                $"fight_setactions {attacker.Handle} {defender.Handle} {{}} {{}}"));

            Assert.That(result, Has.Length.EqualTo(1));
            CollectionAssert.AreEqual(new[] { "attack", defender.Handle.ToString() },
                TclList.Parse(result[0]));
            Assert.AreEqual("neutral", interp.Eval("get_diplomacy 0 1").Trim(),
                "FUN_005bf660 never writes diplomacy — that is fight_startfight's job");
            Assert.AreEqual("neutral", interp.Eval("get_diplomacy 1 0").Trim());
        }

        [Test]
        public void ShippedFightStartfight_DeclaresWarAttackerToVictimOnly()
        {
            // The native escalation chain: fight_startfight writes attacker→victim
            // one-directionally (genericfight.tcl:1236-1241 `set_diplomacy $oowner $eowner
            // enemy`); the reverse direction is written only when the victim's own defend
            // path (fight_notifytarget genericfight.tcl:705-728 → evt_task_defend →
            // z_events.tcl evt_task_defend_proc) reaches its own fight_startfight.
            var (interp, context) = Host();
            context.DataRoot = OriginalDataTestFixture.DataRoot;
            interp.Eval("def_attrib fight_Counter 0 1000 0");
            interp.Eval("def_class ChainAttacker none gnome 0 {} {class_fightdist 1; " +
                "obj_init {set attack_item 0; set attack_behaviour none; " +
                "call scripts/misc/genericfight.tcl; proc set_weapons {} {}}; " +
                "method startfight {target} {global attack_item; " +
                "set attack_item $target; fight_startfight}}");
            interp.Eval("def_class ChainVictim none gnome 0 {} {class_fightdist 1}");
            var attacker = Spawn(interp, context, "ChainAttacker", "0 0 0");
            var defender = Spawn(interp, context, "ChainVictim", "0 0 2");
            interp.Eval($"set_owner {attacker.Handle} 0");
            interp.Eval($"set_owner {defender.Handle} 1");

            interp.Eval($"call_method {attacker.Handle} startfight {defender.Handle}");

            Assert.AreEqual("enemy", interp.Eval("get_diplomacy 0 1").Trim(),
                "fight_startfight declares war attacker→victim (genericfight.tcl:1236-1241)");
            Assert.AreEqual("neutral", interp.Eval("get_diplomacy 1 0").Trim(),
                "the reverse write belongs solely to the victim's own defend-path " +
                "fight_startfight, which has not run here");
        }

        [Test]
        public void PendingCombatImpact_CapturesRestoresAndAppliesExactlyOnce()
        {
            var (interp, context) = Host();
            DefineCombatFixture(interp);
            context.StoryRng = new System.Random(7);
            var attacker = Spawn(interp, context, "Fighter", "0 0 0");
            var defender = Spawn(interp, context, "Target", "0 0 2");

            _ = interp.Eval(
                $"fight_setactions {attacker.Handle} {defender.Handle} {{}} {{}}");
            var live = attacker.ActiveAction as TimedImpactAction;
            Assert.That(live, Is.Not.Null);
            var saved = live!.CaptureSaveState() as TimedImpactActionSaveState;
            Assert.That(saved, Is.Not.Null);
            Assert.That(saved!.PendingImpact.AttackerId, Is.EqualTo(attacker.Handle));
            Assert.That(saved.PendingImpact.DefenderId, Is.EqualTo(defender.Handle));
            Assert.That(saved.PendingImpact.Damage, Is.GreaterThan(0f));
            Assert.That(saved.PendingImpact.TrainingSkill, Is.EqualTo("exp_F_Kungfu"));
            Assert.That(saved.PendingImpact.RemainingDelay, Is.EqualTo(0.3d));

            IEntityAction restored = new RuntimeSaveableActionFactory().Create(
                saved,
                attacker,
                context);
            Assert.That(defender.PendingEvents, Is.Empty,
                "side-effect-free construction must not apply the staged impact");
            Assert.That(restored.Tick(0.05d), Is.True);
            Assert.That(defender.PendingEvents, Is.Empty);

            Assert.That(restored.Tick(0.06d), Is.True,
                "an early animation terminal must not pull the native impact timer forward");
            Assert.That(defender.PendingEvents, Is.Empty);
            Assert.That(restored.Tick(0.18d), Is.True);
            Assert.That(defender.PendingEvents, Is.Empty);

            double kungfuBefore = context.Stats.Get(attacker.Stats, "exp_F_Kungfu");
            double ballisticBefore = context.Stats.Get(attacker.Stats, "exp_F_Ballistic");
            interp.Eval($"set_weapon_class {attacker.Handle} 1");
            Assert.That(restored.Tick(0.02d), Is.False,
                "the saved impact becomes terminal only after its 0.3-second deadline");
            Assert.That(defender.PendingEvents, Has.Count.EqualTo(1));
            Assert.That(context.Stats.Get(attacker.Stats, "exp_F_Kungfu"),
                Is.GreaterThan(kungfuBefore),
                "the training attribute is captured when the swing starts");
            Assert.That(context.Stats.Get(attacker.Stats, "exp_F_Ballistic"),
                Is.EqualTo(ballisticBefore),
                "changing weapons mid-swing must not redirect the staged training gain");
            double trained = context.Stats.Get(attacker.Stats, "exp_Kampf");
            Assert.That(trained, Is.GreaterThan(0d));

            Assert.That(restored.Tick(1d), Is.False);
            Assert.That(defender.PendingEvents, Has.Count.EqualTo(1));
            Assert.That(context.Stats.Get(attacker.Stats, "exp_Kampf"), Is.EqualTo(trained),
                "a restored impact must remain claimed after its first application");
        }

        [Test]
        public void PendingCombatImpact_AttackerDeathMidSwingStillLandsTheQueuedHit()
        {
            // FUN_005bf660:699-706: the landed swing is `set_event <defender>
            // evt_fight_get_hurt … -delay <DAT_00737218> -num1 dmg*0.6` — a timed event
            // queued on the defender with the damage baked at swing evaluation. The native
            // queue has no attacker-liveness gate at delivery: the attacker dying inside the
            // 0.3-second window cannot recall the event.
            var (interp, context) = Host();
            DefineCombatFixture(interp);
            context.StoryRng = new System.Random(7);
            var attacker = Spawn(interp, context, "Fighter", "0 0 0");
            var defender = Spawn(interp, context, "Target", "0 0 2");

            _ = interp.Eval(
                $"fight_setactions {attacker.Handle} {defender.Handle} {{}} {{}}");
            var live = attacker.ActiveAction as TimedImpactAction;
            Assert.That(live, Is.Not.Null);

            attacker.DiedInFight = true;
            Assert.That(live!.Tick(0.31d), Is.False);

            Assert.That(defender.PendingEvents, Has.Count.EqualTo(1),
                "the queued native damage event is immune to the attacker's death");
            // Training gain: the native site is uncited, so only the damage event is pinned
            // here; training simply follows the same no-cancellation delivery.
        }

        [Test]
        public void MeleeAttack_BusyDefenderKeepsFullReactionChainAndReportsNoFreeDefender()
        {
            var (interp, context) = Host();
            DefineCombatFixture(interp);
            interp.Eval("fight_action FHurt Fighter kungfu hit s flrb 0 hurt 0 0");
            context.StoryRng = new System.Random(7);
            var sim = new Diggles.Runtime.Sim.DigglesGameSim(context);
            var alpha = Spawn(interp, context, "Fighter", "0 0 0");
            var beta = Spawn(interp, context, "Fighter", "0 0 2");
            beta.Rotation = new Vector3(0f, Mathf.PI, 0f);

            string[] first = ResultItems(interp.Eval(
                $"fight_setactions {alpha.Handle} {beta.Handle} {{}} {{}}"));
            CollectionAssert.AreEqual(new[] { "attack", beta.Handle.ToString() },
                TclList.Parse(first[0]));
            Assert.That(alpha.ActiveAction, Is.TypeOf<TimedImpactAction>());
            IEntityAction alphaSwing = alpha.ActiveAction!;
            IEntityAction betaReaction = beta.ActiveAction!;

            string[] second = ResultItems(interp.Eval(
                $"fight_setactions {beta.Handle} {alpha.Handle} {{}} {{}}"));
            CollectionAssert.AreEqual(new[] { "no_free_defender" }, second);
            Assert.AreSame(alphaSwing, alpha.ActiveAction,
                "a counter-swing cannot replace the defender's in-flight attack action");
            Assert.AreSame(betaReaction, beta.ActiveAction,
                "the down/hurt/get-up action must remain one uninterrupted reaction");

            sim.Step(0.2);
            Assert.AreEqual(10f, context.Stats.Get(alpha.Stats, "atr_Hitpoints"), 1e-6f);
            Assert.AreEqual(10f, context.Stats.Get(beta.Stats, "atr_Hitpoints"), 1e-6f);

            sim.Step(0.2);
            Assert.Less(context.Stats.Get(beta.Stats, "atr_Hitpoints"), 10f,
                "the original staged impact still lands after the uninterrupted reaction");
            Assert.AreEqual(10f, context.Stats.Get(alpha.Stats, "atr_Hitpoints"), 1e-6f,
                "the rejected counter-swing must not stage damage");
        }

        [Test]
        public void FightReaction_UsesLiteralFightBankAndHoldsDownGetUpWithoutIdleInsert()
        {
            var (interp, context) = Host();
            DefineCombatFixture(interp);
            interp.Eval("def_class ChainTarget none monster 0 {} {class_fightdist 1}");
            interp.Eval(
                "fight_action ChainDown ChainTarget kungfu hit s flrb 0 down_getup 0 0");
            interp.Eval(
                "fight_action ChainDeath ChainTarget kungfu hit s flrb 0 down_getup -1 0");
            interp.Eval("fight_map_obj2db ChainTarget targetdb");
            var sim = new Diggles.Runtime.Sim.DigglesGameSim(context);
            var attacker = Spawn(interp, context, "Fighter", "0 0 0");
            var secondAttacker = Spawn(interp, context, "Fighter", "0 0 0");
            var defender = Spawn(interp, context, "ChainTarget", "0 0 2");
            defender.CurrentState = "fight_dispatch";   // participant gate (FUN_005bf660:493/:699)
            var presentation = new GameObject("FightSystemTestsChainTarget");
            try
            {
                presentation.AddComponent<MeshFilter>();
                presentation.AddComponent<MeshRenderer>();
                var animator = presentation.AddComponent<DigglesAnimator>();
                animator.Interpolate = false;
                animator.AddClip(new DigglesAnimClip
                {
                    Name = "down_getup",
                    Frames = new Mesh[20],
                });
                animator.AddClip(new DigglesAnimClip
                {
                    Name = "kletter_climb",
                    Frames = new Mesh[4],
                });
                animator.AddClip(new DigglesAnimClip
                {
                    Name = "standard",
                    Frames = new Mesh[2],
                });
                var controller = presentation.AddComponent<DigglesAnimController>();
                controller.Animator = animator;
                controller.ObjName = "targetdb";
                controller.SetTables(
                    new Dictionary<string, string>
                    {
                        ["down_getup"] = "targetdb.kletter_climb",
                    },
                    null);
                defender.GameObject = presentation;

                _ = interp.Eval($"fight_setactions {attacker.Handle} {defender.Handle} " +
                    "{} {set chain_done 1}");
                IEntityAction reaction = defender.ActiveAction!;
                sim.Step(0.01);

                Assert.AreEqual("down_getup", animator.CurrentAnimName,
                    "a qualified fight clip must bypass the logical movement-state map");
                Assert.AreEqual(DigglesAnimator.AnimMode.Once, animator.CurrentMode);
                int restartCount = animator.RestartCount;

                CollectionAssert.AreEqual(new[] { "no_free_defender" }, ResultItems(interp.Eval(
                    $"fight_setactions {secondAttacker.Handle} {defender.Handle} {{}} {{}}")));
                Assert.AreSame(reaction, defender.ActiveAction);
                Assert.AreEqual(restartCount, animator.RestartCount,
                    "a busy fight reaction cannot be restarted or replaced");

                animator.TickClock(3f);
                sim.Step(2.1);

                Assert.AreEqual("1", defender.VarsScope.Vars["chain_done"]);
                Assert.IsNull(defender.ActiveAction);
                Assert.AreEqual("down_getup", animator.CurrentAnimName);
                Assert.AreEqual(DigglesAnimator.AnimMode.Still, animator.CurrentMode,
                    "completion holds the authored get-up endpoint without an idle clip between segments");
            }
            finally
            {
                defender.GameObject = null;
                UnityEngine.Object.DestroyImmediate(presentation);
            }
        }

        [Test]
        public void ExpiredRestoredCombatImpact_WaitsForFirstOrdinaryTick()
        {
            var (interp, context) = Host();
            DefineCombatFixture(interp);
            var attacker = Spawn(interp, context, "Fighter", "0 0 0");
            var defender = Spawn(interp, context, "Target", "0 0 2");
            var saved = new TimedImpactActionSaveState(
                new AnimActionSaveState("punch", true, 1d),
                new PendingCombatImpactState(
                    attacker.Handle,
                    defender.Handle,
                    1f,
                    "",
                    0f,
                    0d));

            IEntityAction restored = new RuntimeSaveableActionFactory().Create(
                saved,
                attacker,
                context);

            Assert.That(defender.PendingEvents, Is.Empty,
                "staging and factory construction must not apply an expired impact");
            Assert.That(restored.Tick(0.01d), Is.True);
            Assert.That(defender.PendingEvents, Has.Count.EqualTo(1),
                "an expired impact must apply on the first normal post-commit tick");
        }

        [Test]
        public void MeleeAttack_ReturnsNativeRangeResultsWithoutStartingActions()
        {
            var (interp, context) = Host();
            DefineCombatFixture(interp);
            var attacker = Spawn(interp, context, "Fighter", "0 0 0");
            var near = Spawn(interp, context, "Target", "0 0 6");
            var far = Spawn(interp, context, "Target", "0 0 40");

            var reapproach = ResultItems(interp.Eval(
                $"fight_setactions {attacker.Handle} {near.Handle} {{}} {{}}"));
            var outOfRange = ResultItems(interp.Eval(
                $"fight_setactions {attacker.Handle} {far.Handle} {{}} {{}}"));

            Assert.AreEqual("reapproach", TclList.Parse(reapproach[0])[0]);
            CollectionAssert.AreEqual(new[] { "out_of_range" }, outOfRange);
            Assert.IsNull(attacker.ActiveAction);
        }

        [Test]
        public void BallisticAttack_UsesWeaponRangeAndPublishesHitResult()
        {
            var (interp, context) = Host();
            DefineCombatFixture(interp);
            context.StoryRng = new System.Random(11);
            var sim = new Diggles.Runtime.Sim.DigglesGameSim(context);
            var attacker = Spawn(interp, context, "Fighter", "0 0 0");
            var defender = Spawn(interp, context, "Target", "0 0 14");
            interp.Eval($"set_weapon_class {attacker.Handle} 1");

            var result = ResultItems(interp.Eval($"fight_setactions_ballistic " +
                $"{attacker.Handle} {defender.Handle} {{set shot_done 1}}"));

            CollectionAssert.AreEqual(new[] { "attack", defender.Handle.ToString() },
                TclList.Parse(result[0]));
            Assert.AreEqual("1", interp.Eval($"get_ballisticresult {attacker.Handle}").Trim());
            sim.Step(0.4);
            sim.Step(0.2);
            Assert.Less(context.Stats.Get(defender.Stats, "atr_Hitpoints"), 10f);
            Assert.AreEqual("1", attacker.VarsScope.Vars["shot_done"]);
        }

        [Test]
        public void ProductionAttack_ReportsProductionAndAppliesScaledDirectDamage()
        {
            var (interp, context) = Host();
            DefineCombatFixture(interp);
            context.StoryRng = new System.Random(13);
            var sim = new Diggles.Runtime.Sim.DigglesGameSim(context);
            var attacker = Spawn(interp, context, "Fighter", "0 0 0");
            var building = Spawn(interp, context, "Building", "0 0 2");

            var result = ResultItems(interp.Eval(
                $"fight_setactions {attacker.Handle} {building.Handle} {{}} {{}}"));
            sim.Step(0.4);

            Assert.AreEqual("production", result[0]);
            CollectionAssert.AreEqual(new[] { "attack", building.Handle.ToString() },
                TclList.Parse(result[1]));
            Assert.Less(context.Stats.Get(building.Stats, "atr_Hitpoints"), 10f);
            Assert.Greater(context.Stats.Get(building.Stats, "atr_Hitpoints"), 9.9f,
                "production damage uses the native ten-percent scale");
            Assert.AreEqual(0, building.PendingEvents.Count);
        }

        [Test]
        public void Training_StartsConfiguredActionAndRunsCallback()
        {
            var (interp, context) = Host();
            DefineCombatFixture(interp);
            var sim = new Diggles.Runtime.Sim.DigglesGameSim(context);
            var fighter = Spawn(interp, context, "Fighter");
            var sparringPartner = Spawn(interp, context, "Target", "0 0 0");
            interp.Eval($"set_attrib {sparringPartner.Handle} atr_Hitpoints 0.02");

            Assert.AreEqual("attack", interp.Eval($"fight_setactions_training " +
                $"{fighter.Handle} true {{set training_done 1}}").Trim());
            Assert.IsNotNull(fighter.ActiveAction);
            Assert.IsTrue(fighter.AttackInProgress);
            sim.Step(0.1);
            sim.Step(0.2);

            Assert.IsFalse(fighter.AttackInProgress);
            Assert.AreEqual("1", fighter.VarsScope.Vars["training_done"]);
            Assert.That(context.Stats.Get(sparringPartner.Stats, "atr_Hitpoints"),
                Is.EqualTo(0.02d).Within(1e-6d),
                "training actions never publish a combat impact, even beside a fragile target");
        }

        [Test]
        public void StrikeBack_HitsLivingTargetsInsideRadiusRegardlessOfOwner()
        {
            var (interp, context) = Host();
            DefineCombatFixture(interp);
            context.StoryRng = new System.Random(17);
            var sim = new Diggles.Runtime.Sim.DigglesGameSim(context);
            var attacker = Spawn(interp, context, "Fighter", "0 0 0");
            var hostile = Spawn(interp, context, "Target", "1 0 0");
            var friendly = Spawn(interp, context, "Target", "1 0 0");
            var distant = Spawn(interp, context, "Target", "6 0 0");
            interp.Eval($"set_owner {attacker.Handle} 0");
            interp.Eval($"set_owner {hostile.Handle} 1");
            interp.Eval($"set_owner {friendly.Handle} 0");
            interp.Eval($"set_owner {distant.Handle} 1");
            interp.Eval("set_diplomacy 0 1 enemy");

            Assert.AreEqual("attack", interp.Eval(
                $"fight_setactions_strikeback {attacker.Handle} 2.2 true").Trim());
            Assert.AreEqual(1, hostile.PendingEvents.Count);
            sim.Step(0.1);

            Assert.Less(context.Stats.Get(hostile.Stats, "atr_Hitpoints"), 10f);
            Assert.Less(context.Stats.Get(friendly.Stats, "atr_Hitpoints"), 10f,
                "native strikeback is an explicit area action and does not read diplomacy");
            Assert.AreEqual(10f, context.Stats.Get(distant.Stats, "atr_Hitpoints"), 1e-6f);
            Assert.IsNull(attacker.ActiveAction,
                "external-hit mode is already driven by the caller's authored animation");
        }

        [TestCase(0d, 1.6d, 0d)]
        [TestCase(-0.8d, 0d, 90d)]
        [TestCase(0d, -1.6d, 180d)]
        [TestCase(0.8d, 0d, 270d)]
        public void FightRotateTowards_AuthoredCardinalTableWritesNativeYaw(
            double targetX,
            double targetZ,
            double expectedYawDegrees)
        {
            var (interp, context) = Host();
            context.DataRoot = OriginalDataTestFixture.DataRoot;
            // The `call` must sit inside obj_init, not the class body: genericfight.tcl is
            // dual-loaded (`if {[in_class_def]} {states/methods} else {procs}`), and the procs
            // (fight_rotate_towards among them) only register on the instance replay with
            // in_class_def false — the real chain is zwerg.tcl:18 `obj_init {` →:162 `call
            // scripts/misc/genericfight.tcl`. A class-body call runs the if-branch only and
            // leaves every fight proc unknown ("0").
            interp.Eval("def_class FacingProbe none gnome 0 {} {" +
                "obj_init {set attack_item 0; call scripts/misc/genericfight.tcl}; " +
                "method rotate_for_test {target} {global attack_item; " +
                "set attack_item $target; fight_rotate_towards}}");
            interp.Eval("def_class FacingTarget none monster 0 {} {}");
            var fighter = Spawn(interp, context, "FacingProbe", "0 0 0");
            var target = Spawn(interp, context, "FacingTarget", "0 0 0");
            target.Position = new Vector3((float)targetX, 0f, (float)targetZ);
            fighter.Rotation = new Vector3(0f, 0.37f, 0f);

            _ = interp.Eval($"call_method {fighter.Handle} rotate_for_test {target.Handle}");

            Assert.That(Mathf.Abs(Mathf.DeltaAngle(
                fighter.Rotation.y * Mathf.Rad2Deg,
                (float)expectedYawDegrees)), Is.LessThan(0.35f));
        }

        [TestCase(0d, 1.6d, 0d, 0d, true)]
        [TestCase(-0.8d, 0d, 90d, 0d, true)]
        [TestCase(0d, -1.6d, 180d, 0d, true)]
        [TestCase(0.8d, 0d, 270d, 0d, true)]
        [TestCase(0d, 1.6d, 0d, 14.99d, true)]
        [TestCase(0d, 1.6d, 0d, -14.99d, true)]
        [TestCase(0d, 1.6d, 0d, 15.01d, false)]
        [TestCase(0d, 1.6d, 0d, -15.01d, false)]
        public void MeleeFacing_AuthoredAngleTableUsesNativeHeadingAndFifteenDegreeGate(
            double targetX,
            double targetZ,
            double targetYawDegrees,
            double errorDegrees,
            bool expectedAttack)
        {
            var (interp, context) = Host();
            DefineCombatFixture(interp);
            var attacker = Spawn(interp, context, "Fighter", "0 0 0");
            var defender = Spawn(interp, context, "Target", "0 0 0");
            defender.Position = new Vector3((float)targetX, 0f, (float)targetZ);
            attacker.Rotation = new Vector3(
                0f,
                (float)(targetYawDegrees + errorDegrees) * Mathf.Deg2Rad,
                0f);

            string[] result = ResultItems(interp.Eval(
                $"fight_setactions {attacker.Handle} {defender.Handle} {{}} {{}}"));

            Assert.AreEqual(
                expectedAttack ? "attack" : "miss",
                TclList.Parse(result[0])[0]);
            Assert.AreEqual(expectedAttack, attacker.ActiveAction != null);
        }

        [Test]
        public void ActionResolvers_ReturnNativeFacingResultsBeforeStartingAnimations()
        {
            var (interp, context) = Host();
            DefineCombatFixture(interp);
            var attacker = Spawn(interp, context, "Fighter", "0 0 0");
            var defender = Spawn(interp, context, "Target", "1 0 0");

            CollectionAssert.AreEqual(new[] { "miss" }, ResultItems(interp.Eval(
                $"fight_setactions {attacker.Handle} {defender.Handle} {{}} {{}}")));
            interp.Eval($"set_weapon_class {attacker.Handle} 1");
            CollectionAssert.AreEqual(new[] { "rotate" }, ResultItems(interp.Eval(
                $"fight_setactions_ballistic {attacker.Handle} {defender.Handle} {{}}")));
            Assert.IsNull(attacker.ActiveAction);
            Assert.AreEqual(0, attacker.BallisticResult);
        }

        [Test]
        public void MeleeFacing_UsesTheScriptsHalfScaledHeightCoordinate()
        {
            var (interp, context) = Host();
            DefineCombatFixture(interp);
            context.StoryRng = new System.Random(19);
            // Same 1:2 x:z ratio (the half-height law turns it into a 45-degree facing), but
            // inside the melee reach: fightdist 1 + 0.2 => 1.2, and sqrt(0.8^2 + (1.6*0.5)^2)
            // = 1.13.
            var attacker = Spawn(interp, context, "Fighter", "0 0 0");
            var defender = Spawn(interp, context, "Target", "0.8 0 1.6");
            attacker.Rotation = new Vector3(0f, Mathf.PI * 1.75f, 0f);

            CollectionAssert.AreEqual(
                new[] { "attack", defender.Handle.ToString(CultureInfo.InvariantCulture) },
                TclList.Parse(ResultItems(interp.Eval(
                    $"fight_setactions {attacker.Handle} {defender.Handle} {{}} {{}}"))[0]));
            Assert.That(attacker.ActiveAction, Is.TypeOf<TimedImpactAction>(),
                "raw height would disagree with fight_rotate_towards by more than the 15-degree gate");
        }

        [Test]
        public void AttackOrderEligibility_MatchesDiplomacyShiftAndOwnerlessTargetTable()
        {
            var (interp, context) = Host();
            context.DataRoot = OriginalDataTestFixture.DataRoot;
            interp.Eval("call scripts/init/eventgen.tcl");
            interp.Eval("def_class EligibilityOwner none gnome 0 {} {}");
            interp.Eval("def_class EligibilityGnome none gnome 0 {} {}");
            interp.Eval("def_class EligibilityMonster none monster 0 {} {}");
            interp.Eval("def_class EligibilityProtection none protection 0 {} {}");
            interp.Eval("def_class EligibilityProduction none production 0 {} {}");
            interp.Eval("def_class Geisel none gnome 0 {} {}");
            var owner = Spawn(interp, context, "EligibilityOwner");
            var sameOwner = Spawn(interp, context, "EligibilityGnome");
            var enemy = Spawn(interp, context, "EligibilityGnome");
            var neutral = Spawn(interp, context, "EligibilityGnome");
            var friend = Spawn(interp, context, "EligibilityGnome");
            var hostage = Spawn(interp, context, "Geisel");
            var wildMonster = Spawn(interp, context, "EligibilityMonster");
            var wildProtection = Spawn(interp, context, "EligibilityProtection");
            var wildProduction = Spawn(interp, context, "EligibilityProduction");
            interp.Eval($"set_owner {owner.Handle} 0");
            interp.Eval($"set_owner {sameOwner.Handle} 0");
            interp.Eval($"set_owner {hostage.Handle} 0");
            interp.Eval($"set_owner {enemy.Handle} 1");
            interp.Eval($"set_owner {neutral.Handle} 2");
            interp.Eval($"set_owner {friend.Handle} 3");
            interp.Eval("set_diplomacy 0 1 enemy");
            interp.Eval("set_diplomacy 0 3 friend");

            var cases = new[]
            {
                ("same owner", sameOwner, 0, "0"),
                ("enemy", enemy, 0, "1"),
                ("enemy with shift", enemy, 1, "2"),
                ("neutral", neutral, 0, "0"),
                ("neutral with shift", neutral, 1, "1"),
                ("friend", friend, 0, "0"),
                ("friend with shift", friend, 1, "1"),
                ("hostage", hostage, 0, "1"),
                ("hostage with shift", hostage, 1, "0"),
                ("ownerless monster", wildMonster, 0, "1"),
                ("ownerless protection", wildProtection, 0, "1"),
                ("ownerless production", wildProduction, 0, "0"),
            };
            foreach (var entry in cases)
            {
                Assert.AreEqual(
                    entry.Item4,
                    interp.Eval(
                        $"is_attackable {owner.Handle} {entry.Item2.Handle} {entry.Item3}").Trim(),
                    entry.Item1);
            }
        }

        [Test]
        public void OrderedAttack_ApproachesSwingsKillsAndExitsTheFightLoop()
        {
            var (interp, context) = Host();
            context.DataRoot = OriginalDataTestFixture.DataRoot;
            interp.Eval("call scripts/classes/zwerg/zwerg.tcl");
            interp.Eval("def_class OrderedTarget none monster 0 {} {" +
                "class_fightdist 1.2; " +
                "call scripts/misc/genericfight.tcl; " +
                "handle_event evt_task_defend {}}");
            context.StoryRng = new System.Random(31);
            var sim = new Diggles.Runtime.Sim.DigglesGameSim(context);
            var attacker = Spawn(interp, context, "Zwerg", "0 0 0");
            var defender = Spawn(interp, context, "OrderedTarget", "4 0 0");
            interp.Eval($"set_owner {attacker.Handle} 0");
            interp.Eval($"set_owner {defender.Handle} 1");
            interp.Eval("set_diplomacy 0 1 enemy");
            interp.Eval($"set_attrib {attacker.Handle} atr_Hitpoints 1");
            interp.Eval($"set_attrib {attacker.Handle} exp_F_Kungfu 0.5");
            interp.Eval($"set_attrib {defender.Handle} atr_Hitpoints 0.011");

            Assert.That(context.Fight.Actions, Is.Empty,
                "the ordered event starts from the same definition state as the live runtime");
            interp.Eval($"set_event {attacker.Handle} evt_task_attack " +
                $"-target {attacker.Handle} -subject1 {defender.Handle} -text1 userevent");

            for (int step = 0; step < 10 && attacker.WalkResult != 2; step++)
                sim.Step(0.1);
            Assert.That(attacker.WalkResult, Is.EqualTo(2),
                "evt_task_attack must enter fight_doapproach and start the walk action");
            Assert.That(context.Fight.Actions, Has.Count.EqualTo(377),
                "set_weapons must hydrate the shipped combat table before choosing a weapon");

            var attackPosition = TclList.Parse(interp.Eval(
                $"get_attack_pos {attacker.Handle} {defender.Handle}"));
            attacker.Position = new Vector3(
                Number(attackPosition[0]), Number(attackPosition[1]), Number(attackPosition[2]));
            attacker.Rotation = new Vector3(0f, Mathf.PI * 1.5f, 0f);

            for (int step = 0; step < 10 && attacker.ActiveAction is not TimedImpactAction; step++)
                sim.Step(0.1);
            Assert.That(attacker.ActiveAction, Is.TypeOf<TimedImpactAction>(),
                "arrival must advance fight_dispatch into the animation-coupled swing");
            double hpBefore = context.Stats.Get(defender.Stats, "atr_Hitpoints");

            sim.Step(0.299);
            Assert.That(context.Stats.Get(defender.Stats, "atr_Hitpoints"), Is.EqualTo(hpBefore));
            Assert.That(defender.PendingEvents, Is.Empty);

            sim.Step(0.002);
            Assert.That(defender.PendingEvents, Has.Count.EqualTo(1));
            Assert.That(defender.DiedInFight, Is.True,
                "the lethal result is visible to fight_dispatch as soon as impact is published");
            sim.Step(0.1);
            Assert.That(context.Stats.Get(defender.Stats, "atr_Hitpoints"), Is.LessThan(0.01d));

            for (int step = 0; step < 20 &&
                interp.Eval($"state_get {attacker.Handle}").Trim() == "fight_dispatch"; step++)
            {
                sim.Step(0.1);
            }

            Assert.That(interp.Eval($"state_get {attacker.Handle}").Trim(),
                Is.Not.EqualTo("fight_dispatch"));
            Assert.That(attacker.AttackInProgress, Is.False);
            Assert.That(attacker.VarsScope.Vars["current_fightmode"], Is.EqualTo("-1"),
                "defender_is_dead must take the normal fight_cancel exit");
        }
    }
}
