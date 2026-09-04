#nullable enable

namespace Diggles.Runtime.Tcl.Engine
{
    using System;
    using UnityEngine.Scripting;

    public interface IRuntimeSaveableActionFactory
    {
        /// <summary>
        /// Creates an action without ticking it, evaluating Tcl, or invoking completion callbacks.
        /// </summary>
        IEntityAction Create(
            RuntimeEntityActionSaveState state,
            DigglesTclEntity owner,
            DigglesTclContext context);
    }

    public sealed class RuntimeSaveableActionFactory : IRuntimeSaveableActionFactory
    {
        [Preserve]
        public RuntimeSaveableActionFactory() { }

        public IEntityAction Create(
            RuntimeEntityActionSaveState state,
            DigglesTclEntity owner,
            DigglesTclContext context)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            if (context == null) throw new ArgumentNullException(nameof(context));

            switch (state)
            {
                case WaitActionSaveState wait:
                    return new WaitAction(wait);

                case MoveActionSaveState move:
                    return new MoveAction(context, owner, move);

                case AnimActionSaveState animation:
                    DigglesAnimController? controller =
                        owner.GameObject?.GetComponentInChildren<DigglesAnimController>();
                    return new AnimAction(controller, animation);

                case RotateActionSaveState rotate:
                {
                    DigglesObject? digglesObject = owner.GameObject?.GetComponent<DigglesObject>();
                    DigglesAnimController? rotateController =
                        owner.GameObject?.GetComponentInChildren<DigglesAnimController>();
                    return new TurnToFaceAction(digglesObject, rotateController, rotate);
                }

                case FlyActionSaveState fly:
                    return new FlyAction(owner.GameObject?.transform, fly);

                case WalkActionSaveState walk:
                    return new RestoredWalkAction(context, owner, walk);

                case NativeAttackActionSaveState attack:
                    return new RestoredNativeAttackAction(context, owner, attack);

                case NativeCrawlActionSaveState crawl:
                    return new RestoredNativeCrawlAction(crawl);

                case NativeAttackBallisticActionSaveState ballistic:
                    return new RestoredNativeAttackBallisticAction(ballistic);

                case NativeLoreActionSaveState lore:
                    return new RestoredNativeLoreAction(lore);

                case TimedImpactActionSaveState timedImpact:
                    if (timedImpact.PendingImpact.AttackerId != owner.Handle)
                    {
                        throw new InvalidOperationException(
                            $"Pending combat impact attacker {timedImpact.PendingImpact.AttackerId} does not match action owner {owner.Handle}.");
                    }
                    IEntityAction inner = Create(timedImpact.InnerAction, owner, context);
                    return new TimedImpactAction(inner, context, timedImpact.PendingImpact);

                default:
                    throw new NotSupportedException(
                        $"Runtime action state '{state.GetType().Name}' is not supported.");
            }
        }
    }
}
