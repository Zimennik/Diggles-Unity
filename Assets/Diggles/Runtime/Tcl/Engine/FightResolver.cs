#nullable enable

namespace Diggles.Runtime.Tcl.Engine
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using UnityEngine;

    // Runtime half of CFightActionMgr. The original Tcl state machines remain responsible for
    // approach, turn-taking, target notification and fight exit; this class selects configured
    // actions, owns their engine lifecycle, evaluates damage and produces the result protocol
    // consumed by genericfight.tcl.
    public static class FightResolver
    {
        const string Hitpoints = "atr_Hitpoints";
        const float DamageScale = 0.6f; // DAT_0073709c in DoObjActions.
        const double ImpactDelaySeconds = 0.3d; // DAT_00737218 in melee DoObjActions.
        const float NativeHeightScale = 0.5f; // DAT_0073708c in fight geometry.
        const float MeleeFacingTolerance = Mathf.PI / 12f; // _DAT_00737208: 15 degrees.
        const float BallisticFacingTolerance = Mathf.PI / 6f; // _DAT_00737240: 30 degrees.
        const float FrontDirectionLimit = Mathf.PI / 4f;
        const float SideDirectionLimit = Mathf.PI * 3f / 4f;

        static readonly HashSet<string> ProductionKinds = new(StringComparer.Ordinal)
        {
            "production", "elevator", "store", "energy", "protection",
        };

        public static string Melee(DigglesTclContext ctx, FightSystem fight, string[] args)
        {
            RequireArgs(args, 5, 6,
                "fight_setactions attacker defender attackerfinish defenderfinish ?force?");
            var attacker = ctx.ResolveEntity(args[1]);
            var defender = ctx.ResolveEntity(args[2]);
            bool force = args.Length > 5 && ParseBool(args[5]);
            return ResolveAttack(ctx, fight, attacker, defender, args[3], args[4], false, force);
        }

        public static string Ballistic(DigglesTclContext ctx, FightSystem fight, string[] args)
        {
            RequireArgs(args, 4, 4,
                "fight_setactions_ballistic attacker defender attackerfinish");
            var attacker = ctx.ResolveEntity(args[1]);
            var defender = ctx.ResolveEntity(args[2]);
            return ResolveAttack(ctx, fight, attacker, defender, args[3], "", true, false);
        }

        public static string Training(DigglesTclContext ctx, FightSystem fight, string[] args)
        {
            RequireArgs(args, 4, 4, "fight_setactions_training object attack callback");
            var entity = ctx.ResolveEntity(args[1]);
            bool attack = ParseBool(args[2]);
            string pose = WeaponPose(fight, entity, false);
            bool wall = IsWallMounted(ctx, entity);
            var action = PickAction(ctx, fight, entity, pose,
                attack ? new[] { "beat", "stab", "throw" } : new[] { "ward", "dodge" },
                wall,
                '\0',
                attack ? ExperienceForPose(pose) : "exp_F_Defense");
            if (action == null) return attack ? "attack" : "defend";
            StartAnimation(ctx, fight, entity, action.Animation, args[3], null);
            return attack ? "attack" : "defend";
        }

        public static string StrikeBack(DigglesTclContext ctx, FightSystem fight, string[] args)
        {
            RequireArgs(args, 2, 5,
                "fight_setactions_strikeback object ?range? ?externalhit? ?position?");
            var attacker = ctx.ResolveEntity(args[1]);
            float range = args.Length > 2 ? Math.Max(0f, ParseFloat(args[2])) : 1.2f;
            bool externalHit = args.Length > 3 && ParseBool(args[3]);
            Vector3 centre = args.Length > 4 ? ParseVector(args[4]) : attacker.Position;
            string pose = WeaponPose(fight, attacker, false);
            bool attackerWall = IsWallMounted(ctx, attacker);
            var attackAction = PickAction(ctx, fight, attacker, pose,
                new[] { "beat", "stab", "throw" },
                attackerWall,
                '\0',
                ExperienceForPose(pose));
            if (attackAction == null) return "miss";

            var targets = new List<DigglesTclEntity>();
            foreach (var candidate in ctx.Entities.Values)
            {
                if (ReferenceEquals(candidate, attacker) || IsDead(ctx, candidate) ||
                    candidate.AttackInProgress) continue;
                if (!SupportsStrikeBackTarget(candidate)) continue;
                if (CombatDistance(centre, candidate.Position) > range) continue;
                targets.Add(candidate);
            }
            if (targets.Count == 0) return "no_free_defender";

            foreach (var target in targets)
            {
                float damage = CalculateDamage(ctx, fight, attacker, target, attackAction, null);
                bool targetWall = IsWallMounted(ctx, target);
                char targetDirection = HorizontalDirection(target, attacker, targetWall);
                var reaction = PickHitReaction(
                    ctx, fight, target, attackAction, targetWall, targetDirection, damage);
                // Same participant gate as the melee path (FUN_005bf660:699-706): a
                // non-participant only gets the hurt event, never a forced reaction clip.
                if (reaction != null && IsFightParticipant(target))
                    StartAnimation(ctx, fight, target, reaction.Animation,
                        $"state_enable {target.Handle}", null);
                ApplyDamage(ctx, attacker.Handle, target, damage);
            }

            if (!externalHit)
                StartAnimation(ctx, fight, attacker, attackAction.Animation, "", null);
            return "attack";
        }

        static string ResolveAttack(
            DigglesTclContext ctx,
            FightSystem fight,
            DigglesTclEntity attacker,
            DigglesTclEntity defender,
            string attackerFinish,
            string defenderFinish,
            bool ballistic,
            bool force)
        {
            bool production = IsProduction(defender);
            if (IsDead(ctx, defender))
                return Result(production ? "production" : null, "defender_is_dead");
            if (defender.AttackInProgress)
            {
                // FUN_005bf300: a busy defender is not a dead end — the native searches a
                // ±1.2 (DAT_00737160) per-axis box around the attacker for a substitute
                // (not the attacker's owner, alive, not busy (+0x2b5), with a coordinated
                // action pair) and returns the `new_defender {h}` protocol word that
                // genericfight.tcl:197-201 already consumes. Only a genuinely empty box
                // yields no_free_defender — which the dispatch dictionary does not match,
                // so returning it eagerly would leave group fights as standing gnomes
                // re-dispatching forever (regression, now fixed by the substitute search).
                DigglesTclEntity? substitute =
                    FindSubstituteDefender(ctx, fight, attacker, defender);
                if (substitute != null)
                    return Result(TclList.Format(new[]
                    {
                        "new_defender",
                        substitute.Handle.ToString(CultureInfo.InvariantCulture),
                    }));
                return Result("no_free_defender");
            }

            var weapon = fight.GetWeapon(attacker.WeaponClassId) ?? fight.GetWeapon(0);
            float distance = CombatDistance(attacker.Position, defender.Position);
            float reach = ballistic
                ? Math.Max(0f, weapon?.Range ?? 0f)
                : Math.Max(attacker.Class.FightDistance, defender.Class.FightDistance) + 0.2f;
            if (distance > reach)
            {
                attacker.BallisticResult = 0;
                if (!ballistic && distance <= reach + 2f)
                    return Result(TclList.Format(new[] { "reapproach", Format(reach) }));
                return Result("out_of_range");
            }

            bool attackerWall = IsWallMounted(ctx, attacker);
            bool defenderWall = IsWallMounted(ctx, defender);
            if (!attackerWall && !force && FacingError(attacker, defender) >
                (ballistic ? BallisticFacingTolerance : MeleeFacingTolerance))
            {
                attacker.BallisticResult = 0;
                return Result(ballistic ? "rotate" : "miss");
            }

            string pose = WeaponPose(fight, attacker, ballistic);
            char attackerDirection = HorizontalDirection(attacker, defender, attackerWall);
            var attackAction = PickAction(ctx, fight, attacker, pose,
                ballistic ? new[] { "throw" } : new[] { "beat", "stab" },
                attackerWall,
                attackerDirection,
                ExperienceForPose(pose));
            if (attackAction == null)
            {
                attacker.BallisticResult = 0;
                return Result("miss");
            }

            char defenderDirection = HorizontalDirection(defender, attacker, defenderWall);
            FightSystem.ActionDef? response = production
                ? null
                : PickResponse(
                    ctx, fight, defender, attackAction, defenderWall, defenderDirection);
            bool dodged = response?.Type == "dodge";
            FightSystem.ActionDef? defenseAction = response?.Type == "ward" ? response : null;
            float damage = dodged
                ? 0f
                : CalculateDamage(ctx, fight, attacker, defender, attackAction, defenseAction);
            attacker.BallisticResult = damage > 0f ? 1 : 0;

            FightSystem.ActionDef? reaction = response;
            bool lethal = EffectiveHitpoints(ctx, defender) - damage < 0.01;
            if (!production && lethal)
            {
                reaction = PickHitReaction(
                    ctx, fight, defender, attackAction, defenderWall, defenderDirection, damage);
            }

            PendingCombatImpactState impact = CreatePendingCombatImpact(
                fight,
                attacker,
                defender,
                damage,
                ImpactDelaySeconds);
            StartAnimation(ctx, fight, attacker, attackAction.Animation, attackerFinish, impact);
            // Participant gate (FUN_005bf660:493-553/:699-706): the defender gets a reaction
            // action only when they are a fight participant (their state machine sits in
            // fight_dispatch; the native trapped branch is participant-like too). A working /
            // climbing / walking non-participant natively receives only the delayed
            // evt_fight_get_hurt (queued by the impact path) — regression: forcing a reaction
            // clip onto them replaced their real action mid-flight (a victim's ClimbAction was
            // getting yanked mid-wall).
            if (!production && IsFightParticipant(defender))
            {
                if (reaction != null)
                    StartAnimation(ctx, fight, defender, reaction.Animation, defenderFinish, null);
                else if (defender.ActiveAction != null)
                {
                    // No matching response row, but a swung-at participant is busy natively
                    // (the defender action is created in the participant branch:719-750
                    // regardless) — without busy, his own dispatch swings the same tick and
                    // both sides play one attack pool. Approximation pending further RE of the
                    // empty-response case: tie busy to his current action.
                    defender.AttackCarrier = defender.ActiveAction;
                    defender.AttackInProgress = true;
                }
            }

            // No diplomacy write here. Native fight_setactions (CFightActionMgr::DoObjActions,
            // FUN_005bf660) contains zero diplomacy calls; war escalation is script-side only:
            // - fight_startfight writes attacker→victim one-directionally
            // (genericfight.tcl:1236-1241 `set_diplomacy $oowner $eowner enemy`);
            // - the reverse direction comes solely from the victim's own chain —
            // fight_notifytarget (genericfight.tcl:705-728) → evt_task_defend →
            // evt_task_defend_proc (z_events.tcl) → the victim's fight_startfight —
            // which natively withholds it for trapped victims and mid-user-walk victims;
            // - the production reverse write lives in fight_dispatch
            // (genericfight.tcl:183-187).
            // A resolver-side forced bilateral war fired in strictly more cases than the
            // original (e.g. a swing at a trapped neutral gnome) and was removed as an
            // uncited invention.

            return Result(
                production ? "production" : null,
                TclList.Format(new[]
                {
                    "attack", defender.Handle.ToString(CultureInfo.InvariantCulture),
                }));
        }

        static FightSystem.ActionDef? PickResponse(
            DigglesTclContext ctx,
            FightSystem fight,
            DigglesTclEntity defender,
            FightSystem.ActionDef attackAction,
            bool wall,
            char horizontalDirection)
        {
            string pose = WeaponPose(fight, defender, false, true);
            var candidates = FindActions(
                ctx,
                fight,
                defender,
                pose,
                new[] { "ward", "dodge", "hit" },
                wall,
                horizontalDirection,
                attackAction.VerticalDirections,
                "exp_F_Defense",
                action => action.Attack >= 0f);
            return Choose(ctx, candidates);
        }

        static FightSystem.ActionDef? PickHitReaction(
            DigglesTclContext ctx,
            FightSystem fight,
            DigglesTclEntity defender,
            FightSystem.ActionDef attackAction,
            bool wall,
            char horizontalDirection,
            float damage)
        {
            string pose = WeaponPose(fight, defender, false);
            bool lethal = EffectiveHitpoints(ctx, defender) - damage < 0.01;
            var preferred = FindActions(
                ctx,
                fight,
                defender,
                pose,
                new[] { "hit" },
                wall,
                horizontalDirection,
                attackAction.VerticalDirections,
                "exp_F_Defense",
                action => lethal ? action.Attack < 0f : action.Attack >= 0f);
            if (preferred.Count == 0)
            {
                preferred = FindActions(
                    ctx,
                    fight,
                    defender,
                    pose,
                    new[] { "hit" },
                    wall,
                    horizontalDirection,
                    attackAction.VerticalDirections,
                    "exp_F_Defense",
                    null);
            }
            return Choose(ctx, preferred);
        }

        static FightSystem.ActionDef? PickAction(
            DigglesTclContext ctx,
            FightSystem fight,
            DigglesTclEntity entity,
            string pose,
            string[] types,
            bool wall,
            char horizontalDirection,
            string experience)
        {
            var candidates = FindActions(
                ctx,
                fight,
                entity,
                pose,
                types,
                wall,
                horizontalDirection,
                "",
                experience,
                null);
            return Choose(ctx, candidates);
        }

        static List<FightSystem.ActionDef> FindActions(
            DigglesTclContext ctx,
            FightSystem fight,
            DigglesTclEntity entity,
            string pose,
            string[] types,
            bool wall,
            char horizontalDirection,
            string verticalDirections,
            string experience,
            Predicate<FightSystem.ActionDef>? extra)
        {
            var result = new List<FightSystem.ActionDef>();
            double skill = experience.Length > 0 ? ctx.Stats.Get(entity.Stats, experience) : 1.0;
            foreach (var action in fight.Actions)
            {
                if (!string.Equals(action.UserClass, entity.Class.Name, StringComparison.Ordinal)) continue;
                if (!string.Equals(action.Pose, pose, StringComparison.Ordinal)) continue;
                if (Array.IndexOf(types, action.Type) < 0) continue;
                if (IsWallAction(action) != wall) continue;
                if (horizontalDirection != '\0' &&
                    action.HorizontalDirections.IndexOf(horizontalDirection) < 0) continue;
                if (verticalDirections.Length > 0 &&
                    !DirectionsIntersect(action.VerticalDirections, verticalDirections)) continue;
                if (action.MinimumExperience > skill + 1e-6) continue;
                if (extra != null && !extra(action)) continue;
                result.Add(action);
            }
            return result;
        }

        static FightSystem.ActionDef? Choose(
            DigglesTclContext ctx,
            List<FightSystem.ActionDef> candidates) =>
            candidates.Count == 0 ? null : candidates[ctx.StoryRng.Next(candidates.Count)];

        static float CalculateDamage(
            DigglesTclContext ctx,
            FightSystem fight,
            DigglesTclEntity attacker,
            DigglesTclEntity defender,
            FightSystem.ActionDef attackAction,
            FightSystem.ActionDef? defenseAction)
        {
            var attackWeapon = fight.GetWeapon(attacker.WeaponClassId) ?? fight.GetWeapon(0);
            var defenseWeapon = fight.GetWeapon(defender.WeaponClassId) ?? fight.GetWeapon(0);
            var shield = fight.GetWeapon(defender.ShieldClassId);
            float attackCombination = attackWeapon == null
                ? 1f
                : fight.GetCombinationMultiplier(ctx, attacker, attackWeapon.ClassName);
            float defenseCombination = defenseWeapon == null
                ? 1f
                : fight.GetCombinationMultiplier(ctx, defender, defenseWeapon.ClassName);
            float attackValue = Math.Max(0.01f,
                (attackWeapon?.Attack ?? 0.2f) * attackCombination);
            float defenseWeaponValue = Math.Max(0.01f,
                (defenseWeapon?.Attack ?? 0.2f) * defenseCombination);
            float weaponRatio = Mathf.Sqrt(attackValue / defenseWeaponValue);
            float guard = Math.Max(0f, defenseAction?.Attack ?? 0f) +
                Math.Max(0f, shield?.Defense ?? 0f);
            guard = Mathf.Clamp(guard, 0f, 0.9f);

            string attackExperience = attackWeapon?.ExperienceAttribute ?? "exp_F_Kungfu";
            float attackerSkill = (float)ctx.Stats.Get(attacker.Stats, attackExperience);
            float defenderSkill = (float)ctx.Stats.Get(defender.Stats, "exp_F_Defense");
            float skillFactor = Mathf.Clamp(1f + attackerSkill - defenderSkill * 0.75f, 0.25f, 2f);
            float randomFactor = 0.85f + (float)ctx.StoryRng.NextDouble() * 0.3f;
            float damage = Math.Max(0f, attackAction.Attack) * (1f - guard) * weaponRatio *
                0.2f * skillFactor * randomFactor * DamageScale;
            var hpRule = fight.GetHpRule(defender.Class.Name, defender.Owner);
            if (hpRule.Ratio > 0f) damage /= hpRule.Ratio;
            if (IsProduction(defender)) damage *= 0.1f;
            return damage;
        }

        // The hp a new blow will land on: the stat minus hurt already in flight. The stat is
        // only subtracted when evt_fight_get_hurt applies (evt_fight_get_hurt_proc's
        // add_attrib, genericfight.tcl:421-428), and a rapid exchange can queue two
        // sub-lethal blows whose sum kills — the native death truth follows the
        // post-application hp (fight_checkhp reads `< 0.01` after the event lands,
        // genericfight.tcl:430-441), never a pre-queue snapshot. Both the lethal-reaction
        // pick (fall vs hurt) and the DiedInFight flag must judge against this value.
        static double EffectiveHitpoints(DigglesTclContext ctx, DigglesTclEntity defender)
        {
            double hp = ctx.Stats.Get(defender.Stats, Hitpoints);
            foreach (var pending in defender.PendingEvents)
                if (pending.Name == "evt_fight_get_hurt" &&
                    pending.Payload != null &&
                    pending.Payload.TryGetValue("-num1", out var queued))
                    hp -= ParseFloat(queued);
            return hp;
        }

        static void ApplyDamage(
            DigglesTclContext ctx,
            int attackerHandle,
            DigglesTclEntity defender,
            float damage)
        {
            if (damage <= 0f || IsDead(ctx, defender)) return;
            if (EffectiveHitpoints(ctx, defender) - damage < 0.01) defender.DiedInFight = true;
            if (!IsProduction(defender) &&
                ctx.FindEventHandler(defender.Class, "evt_fight_get_hurt") != null)
            {
                defender.EnqueueEvent(new EntityEvent("evt_fight_get_hurt",
                    new Dictionary<string, string>
                    {
                        ["-target"] = defender.Handle.ToString(CultureInfo.InvariantCulture),
                        ["-num1"] = Format(damage),
                        ["-subject1"] = attackerHandle.ToString(CultureInfo.InvariantCulture),
                    }), ctx.Sim?.GameTime ?? 0d);
            }
            else
            {
                ctx.Stats.Add(defender.Stats, Hitpoints, -damage);
            }
        }

        static void TrainAttackSkill(
            DigglesTclContext ctx,
            DigglesTclEntity attacker,
            string trainingSkill,
            float trainingAmount)
        {
            if (trainingSkill.Length == 0 || trainingAmount <= 0f) return;
            // The native fight path advances the configured combat attribute in small continuous
            // steps. Route through add_expattrib so the talent cap and exp_Kampf aggregate stay
            // synchronized exactly like every script-authored skill gain.
            ctx.Interp.Eval(TclList.Format(new[]
            {
                "add_expattrib",
                attacker.Handle.ToString(CultureInfo.InvariantCulture),
                trainingSkill,
                Format(trainingAmount),
            }));
        }

        internal static void ApplyPendingCombatImpact(
            DigglesTclContext ctx,
            PendingCombatImpactState impact)
        {
            // FUN_005bf660:699-706 publishes a landed swing as
            // `set_event %i evt_fight_get_hurt -target %i -delay DAT_00737218 -num1 dmg*0.6
            // -subject1 %i`
            // — a timed event queued on the defender with the amount baked at swing
            // evaluation. Nothing that happens to the attacker afterwards (death, action
            // replacement, even deletion) recalls it; the only thing that drops a queued
            // event is the defender object no longer existing at delivery (the native event
            // queue GCs entries for deleted objects; port mirror:
            // DigglesGameSim.OnEventMatured). So: no attacker-liveness gate here.
            if (ctx.Entities.TryGetValue(impact.DefenderId, out DigglesTclEntity? defender))
                ApplyDamage(ctx, impact.AttackerId, defender, impact.Damage);
            // Training placement is a port choice (the native site is unreferenced in the
            // decompile); it follows the same no-cancellation rule and requires only that the
            // attacker still exists to receive the stat write.
            if (ctx.Entities.TryGetValue(impact.AttackerId, out DigglesTclEntity? attacker))
                TrainAttackSkill(ctx, attacker, impact.TrainingSkill, impact.TrainingAmount);
        }

        static PendingCombatImpactState CreatePendingCombatImpact(
            FightSystem fight,
            DigglesTclEntity attacker,
            DigglesTclEntity defender,
            float damage,
            double delay)
        {
            FightSystem.WeaponDef? weapon =
                fight.GetWeapon(attacker.WeaponClassId) ?? fight.GetWeapon(0);
            string trainingSkill = weapon?.ExperienceAttribute ?? "";
            float trainingAmount = weapon == null || trainingSkill.Length == 0
                ? 0f
                : Math.Max(0.0001f, weapon.AttributeValue * 0.0005f);
            return new PendingCombatImpactState(
                attacker.Handle,
                defender.Handle,
                damage,
                trainingSkill,
                trainingAmount,
                delay);
        }

        // Native +0x2b5 lifetime — the substitute-search box (FUN_005bf300:55-73).
        const float SubstituteSearchBox = 1.2f;   // DAT_00737160

        // A fight participant sits in the genericfight dispatch state — the native gate is a
        // strcmp against "fight_dispatch" (FUN_005bf660:493-553; the trapped branch:521-553 is
        // participant-like and not yet modeled).
        static bool IsFightParticipant(DigglesTclEntity entity)
            => string.Equals(entity.CurrentState, "fight_dispatch", StringComparison.Ordinal);

        // FUN_005bf300: scan a ±1.2 per-axis box around the attacker for a substitute defender:
        // not the attacker's owner (:49-51 owner mask), alive (:18 atr_Hitpoints > 0),
        // not busy (:92 +0x2b5), with a valid attack row for this attacker (the FUN_005beaf0
        // re-pair check — approximated by PickAction returning a row). Productions and
        // entities being deleted never substitute.
        static DigglesTclEntity? FindSubstituteDefender(
            DigglesTclContext ctx,
            FightSystem fight,
            DigglesTclEntity attacker,
            DigglesTclEntity busyDefender)
        {
            bool attackerWall = IsWallMounted(ctx, attacker);
            string pose = WeaponPose(fight, attacker, false);
            foreach (var pair in ctx.Entities)
            {
                DigglesTclEntity cand = pair.Value;
                if (ReferenceEquals(cand, attacker) || ReferenceEquals(cand, busyDefender))
                    continue;
                if (cand.Owner == attacker.Owner) continue;
                if (cand.AttackInProgress || cand.DeleteInProgress || cand.DiedInFight) continue;
                if (IsProduction(cand)) continue;
                var p = cand.Position;
                var a = attacker.Position;
                if (Math.Abs(p.x - a.x) > SubstituteSearchBox
                    || Math.Abs(p.y - a.y) > SubstituteSearchBox
                    || Math.Abs(p.z - a.z) > SubstituteSearchBox) continue;
                if (IsDead(ctx, cand) || EffectiveHitpoints(ctx, cand) <= 0d) continue;
                char direction = HorizontalDirection(attacker, cand, attackerWall);
                if (PickAction(ctx, fight, attacker, pose, new[] { "beat", "stab" },
                        attackerWall, direction, ExperienceForPose(pose)) == null) continue;
                return cand;
            }
            return null;
        }

        static void StartAnimation(
            DigglesTclContext ctx,
            FightSystem fight,
            DigglesTclEntity entity,
            string animation,
            string callback,
            PendingCombatImpactState? impact)
        {
            // Preserve an already-staged, still-unapplied impact across the action
            // replacement. FUN_005bf660:699-706 queues the swing's damage on the defender via
            // the game event queue — replacing the striker's animation cannot recall it. The
            // external action replacement must re-arm the staged impact with its remaining
            // native delay instead of discarding it.
            PendingCombatImpactState? inherited =
                (entity.ActiveAction as TimedImpactAction)?.UnappliedImpact;
            string resolvedAnimation = fight.ResolveFightAnimation(entity, animation);
            string clear = $"set_attackinprogress {entity.Handle} 0";
            string completion = string.IsNullOrWhiteSpace(callback)
                ? clear
                : clear + "; " + callback;
            string command = TclList.Format(new[]
            {
                "action",
                entity.Handle.ToString(CultureInfo.InvariantCulture),
                "anim",
                resolvedAnimation,
                completion,
                completion,
            });
            ctx.Interp.Eval(command);
            // Ordering invariant: Eval(action …) first runs the old
            // action's breakcode, whose state chain can synchronously install a different
            // action — arming the busy flag blindly after Eval stranded it with no carrier
            // (its clear-string lived on the displaced action). Native +0x2b5 is CAct_Attack's
            // lifetime: verify our action owns the slot (the completion string is unique to
            // this swing) before arming, and register the final slot content as the carrier —
            // the DigglesTclEntity.ActiveAction setter clears the flag on any transition away.
            bool installed = string.Equals(entity.ActionFinish, completion, StringComparison.Ordinal);
            if (inherited != null)
            {
                if (entity.ActiveAction != null)
                {
                    entity.ActiveAction =
                        new TimedImpactAction(entity.ActiveAction, ctx, inherited);
                }
                else if (inherited.AdvanceAndClaim(double.MaxValue))
                {
                    // No action slot survived to carry the staged event; the native queue can
                    // never lose it, so publish now rather than drop it.
                    ApplyPendingCombatImpact(ctx, inherited);
                }
            }
            if (impact != null && entity.ActiveAction != null)
                entity.ActiveAction = new TimedImpactAction(entity.ActiveAction, ctx, impact);
            if (installed && entity.ActiveAction != null)
            {
                entity.AttackCarrier = entity.ActiveAction;
                entity.AttackInProgress = true;
            }
            entity.ActiveAction?.Tick(0d);
        }

        static string WeaponPose(
            FightSystem fight,
            DigglesTclEntity entity,
            bool ballistic,
            bool preferShield = false)
        {
            if (preferShield && entity.ShieldClassId > 0)
            {
                string shieldPose = fight.GetWeapon(entity.ShieldClassId)?.Pose ?? "";
                if (shieldPose.Length > 0) return shieldPose;
            }
            var weapon = fight.GetWeapon(entity.WeaponClassId) ?? fight.GetWeapon(0);
            if (ballistic && weapon != null && weapon.Range <= 1f) return "ballist";
            return weapon?.Pose ?? "kungfu";
        }

        static string ExperienceForPose(string pose) => pose switch
        {
            "kungfu" => "exp_F_Kungfu",
            "sword" => "exp_F_Sword",
            "twohand" => "exp_F_Twohanded",
            "ballist" => "exp_F_Ballistic",
            "shield" => "exp_F_Defense",
            _ => "",
        };

        static bool DirectionsIntersect(string first, string second)
        {
            foreach (char direction in first)
                if (second.IndexOf(direction) >= 0) return true;
            return false;
        }

        static bool IsWallAction(FightSystem.ActionDef action) =>
            action.VerticalDirections.IndexOf('w') >= 0;

        static bool IsWallMounted(DigglesTclContext ctx, DigglesTclEntity entity)
        {
            var nav = ctx.Terrain?.Nav;
            return nav != null && !FightSystem.IsFloorMounted(nav, entity.Position);
        }

        static char HorizontalDirection(
            DigglesTclEntity subject,
            DigglesTclEntity target,
            bool wall)
        {
            if (wall)
            {
                float deltaX = target.Position.x - subject.Position.x;
                float deltaY = target.Position.y - subject.Position.y;
                if (Mathf.Abs(deltaX) > Mathf.Abs(deltaY)) return deltaX < 0f ? 'l' : 'r';
                return deltaY < 0f ? 'f' : 'b';
            }

            float relative = Mathf.DeltaAngle(
                CurrentYaw(subject) * Mathf.Rad2Deg,
                TargetYaw(subject, target) * Mathf.Rad2Deg) * Mathf.Deg2Rad;
            float absolute = Mathf.Abs(relative);
            if (absolute < FrontDirectionLimit) return 'f';
            if (absolute < SideDirectionLimit) return relative < 0f ? 'r' : 'l';
            return 'b';
        }

        // StrikeBack is an explicit area-action primitive, not an autonomous target search. The
        // native implementation never reads owner/diplomacy while enumerating its spatial query;
        // this is required for player-owned training dummies to hit their player-owned trainees.
        static bool SupportsStrikeBackTarget(DigglesTclEntity candidate) =>
            candidate.Class.Kind is "gnome" or "monster";

        static bool IsProduction(DigglesTclEntity entity) =>
            ProductionKinds.Contains(entity.Class.Kind);

        static bool IsDead(DigglesTclContext ctx, DigglesTclEntity entity) =>
            entity.DiedInFight || ctx.Stats.Get(entity.Stats, Hitpoints) < 0.01;

        static float CombatDistance(Vector3 first, Vector3 second)
        {
            float dx = first.x - second.x;
            float dy = first.y - second.y;
            float dz = (first.z - second.z) * NativeHeightScale;
            return Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        static float FacingError(DigglesTclEntity attacker, DigglesTclEntity defender)
        {
            return Mathf.Abs(Mathf.DeltaAngle(
                CurrentYaw(attacker) * Mathf.Rad2Deg,
                TargetYaw(attacker, defender) * Mathf.Rad2Deg)) * Mathf.Deg2Rad;
        }

        static float CurrentYaw(DigglesTclEntity entity)
        {
            GameObject? gameObject = entity.GameObject;
            var digglesObject = gameObject != null
                ? gameObject.GetComponent<Diggles.Runtime.DigglesObject>()
                : null;
            return digglesObject != null ? digglesObject.CurrentNativeYaw() : entity.Rotation.y;
        }

        static float TargetYaw(DigglesTclEntity subject, DigglesTclEntity target)
        {
            float yaw = Mathf.Atan2(
                subject.Position.x - target.Position.x,
                (target.Position.z - subject.Position.z) * NativeHeightScale);
            return yaw < 0f ? yaw + Mathf.PI * 2f : yaw;
        }

        static string Result(params string?[] items)
        {
            var values = new List<string>();
            foreach (string? item in items)
                if (!string.IsNullOrEmpty(item)) values.Add(item!);
            return TclList.Format(values);
        }

        static Vector3 ParseVector(string value)
        {
            var values = TclList.Parse(value);
            return new Vector3(
                values.Count > 0 ? ParseFloat(values[0]) : 0f,
                values.Count > 1 ? ParseFloat(values[1]) : 0f,
                values.Count > 2 ? ParseFloat(values[2]) : 0f);
        }

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
