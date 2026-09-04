#nullable enable

namespace Diggles.Runtime.Tcl.Engine
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using Diggles.Runtime.Navigation;
    using Diggles.Runtime.Tcl;
    using UnityEngine;

    // The data/query half of the original CFightActionMgr. Shipped init/fight.tcl declares the
    // action catalogue, weapon table, combinations and class/owner HP ratios through this API.
    // Actual fight turn resolution is layered on top of this registry by the combat checkpoint.
    public sealed class FightSystem
    {
        const string RuntimeDefinitionScript = "scripts/init/fight.tcl";
        const float AttackPositionPadding = 0.2f;
        const float AttackWallClearance = 0.6f;
        const float AttackRimClearance = 0.2f;
        const float WallAttackOffset = 1.2f;

        public sealed class ActionDef
        {
            public string Name = "";
            public string UserClass = "";
            public string Pose = "";
            public string Type = "";
            public string VerticalDirections = "";
            public string HorizontalDirections = "";
            public bool Collision;
            public string Animation = "";
            public float Attack;
            public float MinimumExperience;
        }

        public sealed class WeaponDef
        {
            public int Id;
            public string ClassName = "";
            public float Attack;
            public float Defense;
            public float Range;
            public string Pose = "";
            public string ExperienceAttribute = "";
            public float AttributeValue;
            public float MinimumExperience;
            public string AnimationIdentifier = "";
            public int[] Frames = Array.Empty<int>();

            public bool IsAttackWeapon => Attack > 0f;
            public bool IsDefenseWeapon => Defense > 0f;
        }

        public sealed class WeaponCombination
        {
            public string[] Classes = Array.Empty<string>();
            public float[] Multipliers = Array.Empty<float>();
        }

        public readonly struct HpRule
        {
            public readonly float Ratio;
            public readonly float Escape;

            public HpRule(float ratio, float escape)
            {
                Ratio = ratio;
                Escape = escape;
            }
        }

        public List<ActionDef> Actions { get; } = new();
        public List<WeaponDef> Weapons { get; } = new();
        public List<WeaponCombination> Combinations { get; } = new();
        public Dictionary<string, string> ObjectDatabaseMap { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> ClassDatabaseMap { get; } = new(StringComparer.Ordinal);

        readonly Dictionary<(string ClassName, int Owner), HpRule> _hpRules = new();
        NavWorld? _attackPathWorld;
        Calc1Pathfinder? _attackPathfinder;
        bool _definitionsReady;
        bool _definitionLoadInProgress;

        public static void RegisterCommands(DigglesTclContext ctx, TclInterpreter interp)
        {
            var fight = ctx.Fight;

            interp.RegisterCommand("fight_action", (_, args) => fight.DefineAction(args));
            interp.RegisterCommand("fight_weapon", (_, args) => fight.DefineWeapon(args));
            interp.RegisterCommand("fight_weapon_kombi", (_, args) => fight.DefineCombination(args));
            interp.RegisterCommand("fight_map_obj2db", (_, args) =>
                DefineMap(fight.ObjectDatabaseMap, args, "fight_map_obj2db"));
            interp.RegisterCommand("fight_map_class2db", (_, args) =>
                DefineMap(fight.ClassDatabaseMap, args, "fight_map_class2db"));
            interp.RegisterCommand("def_hp_ratio", (_, args) => fight.DefineHpRule(args));
            interp.RegisterCommand("fight_setactions", (_, args) =>
            {
                fight.EnsureDefinitionsLoaded(ctx);
                return FightResolver.Melee(ctx, fight, args);
            });
            interp.RegisterCommand("fight_setactions_ballistic", (_, args) =>
            {
                fight.EnsureDefinitionsLoaded(ctx);
                return FightResolver.Ballistic(ctx, fight, args);
            });
            interp.RegisterCommand("fight_setactions_strikeback", (_, args) =>
            {
                fight.EnsureDefinitionsLoaded(ctx);
                return FightResolver.StrikeBack(ctx, fight, args);
            });
            interp.RegisterCommand("fight_setactions_training", (_, args) =>
            {
                fight.EnsureDefinitionsLoaded(ctx);
                return FightResolver.Training(ctx, fight, args);
            });

            interp.RegisterCommand("get_best_weapon", (_, args) =>
            {
                RequireArgs(args, 2, 3, "get_best_weapon object ?ballistic?");
                fight.EnsureDefinitionsLoaded(ctx);
                var owner = ctx.ResolveEntity(args[1]);
                bool ballistic = args.Length > 2 && ParseBool(args[2]);
                return fight.GetBestWeapon(ctx, owner, ballistic);
            });
            interp.RegisterCommand("get_weapon_id", (_, args) =>
            {
                RequireArgs(args, 3, 3, "get_weapon_id object attack");
                fight.EnsureDefinitionsLoaded(ctx);
                var item = ctx.ResolveEntity(args[1]);
                return fight.GetWeaponId(item, ParseBool(args[2])).ToString(CultureInfo.InvariantCulture);
            });
            interp.RegisterCommand("get_weapon_pose", (_, args) =>
            {
                RequireArgs(args, 2, 2, "get_weapon_pose weaponid");
                fight.EnsureDefinitionsLoaded(ctx);
                var weapon = fight.GetWeapon(ParseInt(args[1]));
                return weapon?.Pose ?? "";
            });
            interp.RegisterCommand("get_weapon_range", (_, args) =>
            {
                RequireArgs(args, 2, 2, "get_weapon_range weaponid");
                fight.EnsureDefinitionsLoaded(ctx);
                return Format(fight.GetWeapon(ParseInt(args[1]))?.Range ?? 0f);
            });
            interp.RegisterCommand("chack_weapon_exp", (_, args) =>
            {
                RequireArgs(args, 3, 3, "chack_weapon_exp object weaponid");
                fight.EnsureDefinitionsLoaded(ctx);
                return fight.CanUseWeapon(ctx, ctx.ResolveEntity(args[1]), ParseInt(args[2])) ? "1" : "0";
            });
            interp.RegisterCommand("get_escape_value", (_, args) =>
            {
                RequireArgs(args, 3, 3, "get_escape_value class owner");
                fight.EnsureDefinitionsLoaded(ctx);
                return Format(fight.GetHpRule(args[1], ParseOwner(args[2])).Escape);
            });
            interp.RegisterCommand("get_attack_pos", (_, args) =>
            {
                RequireArgs(args, 3, 4, "get_attack_pos attacker defender ?distance?");
                var attacker = ctx.ResolveEntity(args[1]);
                var defender = ctx.ResolveEntity(args[2]);
                float distance = args.Length > 3
                    ? Math.Max(0f, ParseFloat(args[3]))
                    : Math.Max(attacker.Class.FightDistance, defender.Class.FightDistance);
                return fight.FindAttackPosition(ctx, attacker, defender, distance);
            });

            RegisterIntField(interp, ctx, "weapon_class", e => e.WeaponClassId,
                (e, value) => e.WeaponClassId = value);
            RegisterIntField(interp, ctx, "shield_class", e => e.ShieldClassId,
                (e, value) => e.ShieldClassId = value);
            RegisterBoolField(interp, ctx, "attackinprogress", e => e.AttackInProgress,
                (e, value) => e.AttackInProgress = value);
            RegisterBoolField(interp, ctx, "diedinfight", e => e.DiedInFight,
                (e, value) => e.DiedInFight = value);
            RegisterBoolField(interp, ctx, "climbability", e => e.ClimbAbility,
                (e, value) => e.ClimbAbility = value);

            // ntid_reset <obj> — CTclObjMethods::NTIDReset (FUN_0067c140:57-89): if the object
            // carries a live newsticker entry (obj+0x408 >= 0, the "under attack" news) the
            // command evals `newsticker delete <id>`, then stamps the slot -2. genericfight's
            // evt_timer_NTIDReset handler is its only data caller (genericfight.tcl:370) —
            // fights arm the timer so the attack news clears itself after the brawl.
            interp.RegisterCommand("ntid_reset", (_, args) =>
            {
                RequireArgs(args, 2, 2, "ntid_reset object");
                var e = ctx.ResolveEntity(args[1]);
                if (e.NewsTickerId >= 0)
                    ctx.Interp.Eval($"newsticker delete {e.NewsTickerId}");
                e.NewsTickerId = -2;
                return "";
            });

            interp.RegisterCommand("get_ballisticresult", (_, args) =>
            {
                RequireArgs(args, 2, 2, "get_ballisticresult object");
                return ctx.ResolveEntity(args[1]).BallisticResult.ToString(CultureInfo.InvariantCulture);
            });
            interp.RegisterCommand("get_velcomp", (_, args) =>
            {
                RequireArgs(args, 2, 2, "get_velcomp object");
                var v = ctx.ResolveEntity(args[1]).Velocity;
                return $"{Format(v.x)} {Format(v.y)} {Format(v.z)}";
            });
        }

        void EnsureDefinitionsLoaded(DigglesTclContext ctx)
        {
            if (_definitionsReady || _definitionLoadInProgress) return;

            // Unit fixtures may deliberately define a compact combat catalogue. Preserve it instead
            // of mixing it with the shipped IDs; the live runtime reaches this branch with both lists
            // empty because its definition boot does not source init/fight.tcl.
            if (Actions.Count > 0 || Weapons.Count > 0)
            {
                _definitionsReady = true;
                return;
            }
            if (string.IsNullOrWhiteSpace(ctx.DataRoot)) return;

            _definitionLoadInProgress = true;
            try
            {
                // The original executes init/fight.tcl in the system interpreter at boot; its
                // top-level loader helpers (`set sw` fight.tcl:249, `set DrB`:412, `set RH`
                //:435) never exist in any object interpreter — and per-object interp vars are
                // exactly what the save system captures. This lazy load can be triggered from
                // inside an entity body (ActiveGlobalOverride = that entity's scope), so the
                // hydration runs in an isolated throwaway scope: the C# fight_* table commands
                // are scope-free, and the loader helpers die with the scope instead of leaking
                // into the first fighter's saved var table.
                ctx.Interp.EvalInScope("call " + RuntimeDefinitionScript, new TclScope());
                if (Actions.Count == 0 || Weapons.Count == 0)
                {
                    throw new TclException(
                        $"combat definitions were not loaded from {RuntimeDefinitionScript}");
                }
            }
            catch
            {
                // A mid-file failure must not strand a truncated catalogue: the next combat
                // command would satisfy the fixture-preservation guard above
                // (Actions.Count > 0) and latch _definitionsReady over half a table. Reset to
                // the pristine empty state so the next consumer retries the full load.
                ClearDefinitions();
                throw;
            }
            finally
            {
                _definitionLoadInProgress = false;
            }
            _definitionsReady = true;
        }

        void ClearDefinitions()
        {
            Actions.Clear();
            Weapons.Clear();
            Combinations.Clear();
            ObjectDatabaseMap.Clear();
            ClassDatabaseMap.Clear();
            _hpRules.Clear();
        }

        string DefineAction(string[] args)
        {
            // Several shipped hit-action rows contain trailing control-byte debris after the
            // meaningful fields (fight.tcl:133-148). The native command consumes its fixed
            // prefix, so reject only a short row and ignore fields beyond optional minexp.
            if (args.Length < 10)
                throw new TclException("wrong # args: should be \"fight_action name user pose type vdir hdir ddir animation attack ?minexp?\"");
            Actions.Add(new ActionDef
            {
                Name = args[1],
                UserClass = args[2],
                Pose = args[3],
                Type = args[4],
                VerticalDirections = args[5],
                HorizontalDirections = args[6],
                Collision = ParseBool(args[7]),
                Animation = args[8],
                Attack = ParseFloat(args[9]),
                MinimumExperience = args.Length > 10 ? ParseFloat(args[10]) : 0f,
            });
            return "";
        }

        string DefineWeapon(string[] args)
        {
            RequireArgs(args, 9, 11,
                "fight_weapon class attack defense range pose expattrib attribvalue minexp ?anim? ?frames?");
            var weapon = new WeaponDef
            {
                Id = Weapons.Count,
                ClassName = args[1],
                Attack = ParseFloat(args[2]),
                Defense = ParseFloat(args[3]),
                Range = ParseFloat(args[4]),
                Pose = args[5],
                ExperienceAttribute = args[6],
                AttributeValue = ParseFloat(args[7]),
                MinimumExperience = ParseFloat(args[8]),
                AnimationIdentifier = args.Length > 9 ? args[9] : "",
                Frames = args.Length > 10 ? ParseIntList(args[10]) : Array.Empty<int>(),
            };
            Weapons.Add(weapon);
            return "";
        }

        string DefineCombination(string[] args)
        {
            RequireArgs(args, 3, 3, "fight_weapon_kombi classlist raisevallist");
            var classes = TclList.Parse(args[1]).ToArray();
            var raw = TclList.Parse(args[2]);
            var multipliers = new float[classes.Length];
            for (int index = 0; index < multipliers.Length; index++)
                multipliers[index] = index < raw.Count ? ParseFloat(raw[index]) : 1f;
            Combinations.Add(new WeaponCombination { Classes = classes, Multipliers = multipliers });
            return "";
        }

        string DefineHpRule(string[] args)
        {
            RequireArgs(args, 4, 5, "def_hp_ratio class owner ratio ?escape?");
            _hpRules[(args[1], ParseOwner(args[2]))] = new HpRule(
                ParseFloat(args[3]), args.Length > 4 ? ParseFloat(args[4]) : 0.1f);
            return "";
        }

        public WeaponDef? GetWeapon(int id) => id >= 0 && id < Weapons.Count ? Weapons[id] : null;

        internal string ResolveFightAnimation(DigglesTclEntity entity, string animation)
        {
            if (string.IsNullOrEmpty(animation) || animation.IndexOf('.') > 0) return animation;

            if (!ObjectDatabaseMap.TryGetValue(entity.Class.Name, out string database) &&
                !ClassDatabaseMap.TryGetValue(entity.Class.Name, out database))
            {
                GameObject? gameObject = entity.GameObject;
                database = gameObject != null
                    ? gameObject.GetComponentInChildren<Diggles.Runtime.DigglesAnimController>()?.ObjName ?? ""
                    : "";
            }
            if (database.Length == 0) database = entity.Class.Name.ToLowerInvariant();
            return database + "." + animation;
        }

        public HpRule GetHpRule(string className, int owner)
        {
            if (_hpRules.TryGetValue((className, owner), out var exact)) return exact;
            if (_hpRules.TryGetValue((className, -2), out var allOwners)) return allOwners;
            return new HpRule(1f, 0.1f);
        }

        public int GetWeaponId(DigglesTclEntity item, bool attack)
        {
            for (int index = 0; index < Weapons.Count; index++)
            {
                var weapon = Weapons[index];
                if (!string.Equals(weapon.ClassName, item.Class.Name, StringComparison.Ordinal)) continue;
                if (attack == weapon.IsAttackWeapon) return weapon.Id;
            }
            return 0;
        }

        public bool CanUseWeapon(DigglesTclContext ctx, DigglesTclEntity wielder, int weaponId)
        {
            var weapon = GetWeapon(weaponId);
            return weapon != null &&
                ctx.Stats.Get(wielder.Stats, weapon.ExperienceAttribute) + 1e-6 >= weapon.MinimumExperience;
        }

        public float GetCombinationMultiplier(
            DigglesTclContext ctx,
            DigglesTclEntity wielder,
            string className)
        {
            var inventoryClasses = new HashSet<string>(StringComparer.Ordinal);
            foreach (int handle in wielder.Inventory)
                if (ctx.Entities.TryGetValue(handle, out var item))
                    inventoryClasses.Add(item.Class.Name);
            return CombinationMultiplier(className, inventoryClasses);
        }

        string GetBestWeapon(DigglesTclContext ctx, DigglesTclEntity wielder, bool ballistic)
        {
            int attackHandle = -1;
            int attackWeaponId = -1;
            int shieldHandle = -1;
            float attackStrength = 0f;
            float defenseStrength = 0f;
            var inventoryClasses = new HashSet<string>(StringComparer.Ordinal);

            foreach (int handle in wielder.Inventory)
                if (ctx.Entities.TryGetValue(handle, out var item)) inventoryClasses.Add(item.Class.Name);

            foreach (int handle in wielder.Inventory)
            {
                if (!ctx.Entities.TryGetValue(handle, out var item)) continue;
                foreach (var weapon in Weapons)
                {
                    if (!string.Equals(weapon.ClassName, item.Class.Name, StringComparison.Ordinal)) continue;
                    if (!CanUseWeapon(ctx, wielder, weapon.Id)) continue;
                    float multiplier = CombinationMultiplier(weapon.ClassName, inventoryClasses);
                    if (weapon.IsAttackWeapon && ballistic == (weapon.Range > 1f))
                    {
                        float score = Mathf.Sqrt(Math.Max(0f, weapon.Attack * multiplier));
                        if (score > attackStrength)
                        {
                            attackStrength = score;
                            attackHandle = handle;
                            attackWeaponId = weapon.Id;
                        }
                    }
                }
            }

            // Weapon zero is the shipped "none" definition. Native code compares unarmed
            // strength only after finding an inventory weapon. If unarmed combat wins, the
            // handle is cleared but the candidate strength remains in the returned evaluation.
            if (!ballistic && attackHandle >= 0 && Weapons.Count > 0)
            {
                var unarmed = Weapons[0];
                float skill = (float)ctx.Stats.Get(wielder.Stats, unarmed.ExperienceAttribute);
                float score = Mathf.Sqrt(Math.Max(0f, unarmed.Attack)) * Math.Max(0.01f, skill);
                if (score > attackStrength)
                    attackHandle = -1;
            }

            // The native manager rebuilds a separate defense candidate list. Two-handed
            // weapons suppress that list, even when unarmed strength won the comparison above.
            bool allowsShield = attackWeaponId < 0 ||
                !string.Equals(GetWeapon(attackWeaponId)?.ExperienceAttribute,
                    "exp_F_Twohanded", StringComparison.Ordinal);
            if (allowsShield)
            {
                float bestDefenseScore = 0f;
                foreach (int handle in wielder.Inventory)
                {
                    if (!ctx.Entities.TryGetValue(handle, out var item)) continue;
                    foreach (var weapon in Weapons)
                    {
                        if (!weapon.IsDefenseWeapon ||
                            !string.Equals(weapon.ClassName, item.Class.Name, StringComparison.Ordinal) ||
                            !CanUseWeapon(ctx, wielder, weapon.Id)) continue;
                        float multiplier = CombinationMultiplier(weapon.ClassName, inventoryClasses);
                        float score = weapon.Defense * multiplier;
                        if (score <= bestDefenseScore) continue;
                        bestDefenseScore = score;
                        defenseStrength = multiplier;
                        shieldHandle = handle;
                    }
                }
            }

            return TclList.Format(new[]
            {
                attackHandle.ToString(CultureInfo.InvariantCulture),
                shieldHandle.ToString(CultureInfo.InvariantCulture),
                Format(attackStrength),
                Format(defenseStrength),
            });
        }

        float CombinationMultiplier(string className, HashSet<string> inventoryClasses)
        {
            float multiplier = 1f;
            foreach (var combination in Combinations)
            {
                int classIndex = Array.IndexOf(combination.Classes, className);
                if (classIndex < 0) continue;
                bool complete = true;
                foreach (string required in combination.Classes)
                    if (!inventoryClasses.Contains(required)) { complete = false; break; }
                if (!complete) continue;
                multiplier *= classIndex < combination.Multipliers.Length
                    ? combination.Multipliers[classIndex]
                    : 1f;
            }
            return multiplier;
        }

        string FindAttackPosition(
            DigglesTclContext context,
            DigglesTclEntity attacker,
            DigglesTclEntity defender,
            float distance)
        {
            var nav = context.Terrain?.Nav;
            if (nav == null) return FindGeometricAttackPosition(attacker, defender, distance);

            // Native FUN_005c3880 uses CPlaceFinder around floor-mounted defenders and a fixed
            // vertical side offset for wall-mounted defenders. A failed finder returns Tcl 0.
            Vector3 position;
            if (IsFloorMounted(nav, defender.Position))
            {
                string place = context.Interp.Eval(TclList.Format(new[]
                {
                    "get_place",
                    "-center", FormatPosition(defender.Position),
                    "-circle", Format(distance + AttackPositionPadding),
                    "-mindist", Format(distance),
                    "-nearpos", FormatPosition(attacker.Position),
                    "-except", defender.Handle.ToString(CultureInfo.InvariantCulture),
                    "-walldist", Format(AttackWallClearance),
                    "-rimdist", Format(AttackRimClearance),
                    "-materials", "false",
                }));
                if (!TryParsePosition(place, out position)) return "0";
            }
            else
            {
                position = defender.Position;
                position.y += defender.Position.y <= attacker.Position.y
                    ? -WallAttackOffset
                    : WallAttackOffset;
            }

            return IsReachable(nav, attacker.Position, position)
                ? FormatPosition(position)
                : "0";
        }

        static string FindGeometricAttackPosition(
            DigglesTclEntity attacker,
            DigglesTclEntity defender,
            float distance)
        {
            var delta = new Vector2(attacker.Position.x - defender.Position.x,
                attacker.Position.y - defender.Position.y);
            Vector2 direction = delta.sqrMagnitude > 0.000001f ? delta.normalized : Vector2.right;
            var position = new Vector3(
                defender.Position.x + direction.x * distance,
                defender.Position.y + direction.y * distance,
                defender.Position.z);
            return FormatPosition(position);
        }

        internal static bool IsFloorMounted(NavWorld nav, Vector3 position)
        {
            int x = Mathf.RoundToInt(position.x * 8f);
            int y = Mathf.RoundToInt(position.y * 8f);
            if (x <= 0 || y <= 0 || x >= nav.SubW - 1 || y >= nav.SubH - 1) return false;
            return nav.SubIsFloor(x, y + 1) ||
                   nav.SubIsFloor(x, y) ||
                   nav.SubIsFloor(x, y - 1);
        }

        bool IsReachable(NavWorld nav, Vector3 start, Vector3 target)
        {
            int startX = Mathf.RoundToInt(start.x * 8f);
            int startY = Mathf.RoundToInt(start.y * 8f);
            int targetX = Mathf.RoundToInt(target.x * 8f);
            int targetY = Mathf.RoundToInt(target.y * 8f);
            if (!nav.SubWalkable(targetX, targetY)) return false;
            if (!ReferenceEquals(_attackPathWorld, nav))
            {
                _attackPathWorld = nav;
                _attackPathfinder = new Calc1Pathfinder(nav);
            }
            var path = _attackPathfinder!.FindPath(
                startX,
                startY,
                targetX,
                targetY,
                canClimb: true,
                iOwner: 100);
            return path is { Count: > 0 };
        }

        static bool TryParsePosition(string value, out Vector3 position)
        {
            var values = TclList.Parse(value);
            if (values.Count >= 3 &&
                float.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture,
                    out float x) &&
                float.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture,
                    out float y) &&
                float.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture,
                    out float z) &&
                x >= 0f)
            {
                position = new Vector3(x, y, z);
                return true;
            }

            position = default;
            return false;
        }

        static string FormatPosition(Vector3 position) => TclList.Format(new[]
        {
            Format(position.x),
            Format(position.y),
            Format(position.z),
        });

        static void RegisterIntField(TclInterpreter interp, DigglesTclContext ctx, string suffix,
            Func<DigglesTclEntity, int> get, Action<DigglesTclEntity, int> set)
        {
            interp.RegisterCommand("get_" + suffix, (_, args) =>
            {
                RequireArgs(args, 2, 2, "get_" + suffix + " object");
                return get(ctx.ResolveEntity(args[1])).ToString(CultureInfo.InvariantCulture);
            });
            interp.RegisterCommand("set_" + suffix, (_, args) =>
            {
                RequireArgs(args, 3, 3, "set_" + suffix + " object value");
                set(ctx.ResolveEntity(args[1]), ParseInt(args[2]));
                return "";
            });
        }

        static void RegisterBoolField(TclInterpreter interp, DigglesTclContext ctx, string suffix,
            Func<DigglesTclEntity, bool> get, Action<DigglesTclEntity, bool> set)
        {
            interp.RegisterCommand("get_" + suffix, (_, args) =>
            {
                RequireArgs(args, 2, 2, "get_" + suffix + " object");
                return get(ctx.ResolveEntity(args[1])) ? "1" : "0";
            });
            interp.RegisterCommand("set_" + suffix, (_, args) =>
            {
                RequireArgs(args, 3, 3, "set_" + suffix + " object value");
                set(ctx.ResolveEntity(args[1]), ParseBool(args[2]));
                return "";
            });
        }

        static string DefineMap(Dictionary<string, string> map, string[] args, string command)
        {
            RequireArgs(args, 3, 3, command + " class database");
            map[args[1]] = args[2];
            return "";
        }

        static int[] ParseIntList(string value)
        {
            var values = TclList.Parse(value);
            var result = new int[values.Count];
            for (int index = 0; index < result.Length; index++) result[index] = ParseInt(values[index]);
            return result;
        }

        static int ParseOwner(string value) =>
            string.Equals(value, "all", StringComparison.OrdinalIgnoreCase) ? -2 : ParseInt(value);

        static int ParseInt(string value) =>
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
                ? result
                : 0;

        static float ParseFloat(string value) =>
            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float result)
                ? result
                : 0f;

        static bool ParseBool(string value)
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out int numeric))
            {
                return numeric != 0;
            }
            return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
        }

        static string Format(float value) => value.ToString("R", CultureInfo.InvariantCulture);

        static void RequireArgs(string[] args, int minimum, int maximum, string usage)
        {
            if (args.Length < minimum || args.Length > maximum)
                throw new TclException("wrong # args: should be \"" + usage + "\"");
        }
    }
}
