#nullable enable

namespace Diggles.Runtime.Tcl.Engine
{
    using System;

    // Wraps a regular engine action with one structured combat impact. The native melee resolver
    // schedules evt_fight_get_hurt at a fixed delay, independent of the selected clip's duration, so
    // an early animation terminal is held until that deadline while a longer animation keeps running.
    public sealed class TimedImpactAction : ISaveableEntityAction
    {
        private readonly IEntityAction _inner;
        private readonly DigglesTclContext _context;
        private readonly PendingCombatImpactState _pendingImpact;
        private bool _innerFinished;

        public bool DrivesTransform => _inner.DrivesTransform;

        // The staged impact while it has not yet been claimed, else null. Read by
        // FightResolver.StartAnimation when a combat animation replaces this wrapper: native
        // damage is a queued defender event (FUN_005bf660:699-706) that action replacement
        // cannot recall, so the unapplied impact is re-armed on the replacement action.
        internal PendingCombatImpactState? UnappliedImpact =>
            _pendingImpact.Applied ? null : _pendingImpact;

        public TimedImpactAction(
            IEntityAction inner,
            DigglesTclContext context,
            PendingCombatImpactState pendingImpact)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _pendingImpact = pendingImpact?.Copy() ??
                throw new ArgumentNullException(nameof(pendingImpact));
        }

        public RuntimeEntityActionSaveState CaptureSaveState()
        {
            if (_inner is not ISaveableEntityAction saveableInner)
            {
                throw new NotSupportedException(
                    $"Combat impact inner action '{_inner.GetType().Name}' is not saveable.");
            }
            return new TimedImpactActionSaveState(
                saveableInner.CaptureSaveState(),
                _pendingImpact);
        }

        public bool Tick(double dt)
        {
            if (!_innerFinished)
                _innerFinished = !_inner.Tick(dt);
            if (_pendingImpact.AdvanceAndClaim(dt))
                FightResolver.ApplyPendingCombatImpact(_context, _pendingImpact);
            return !_innerFinished || !_pendingImpact.Applied;
        }
    }
}
