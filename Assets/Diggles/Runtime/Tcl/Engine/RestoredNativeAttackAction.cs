#nullable enable

namespace Diggles.Runtime.Tcl.Engine
{
    using System;
    using UnityEngine;

    // Resumes the native CAct_Attack timing/frame shell without replaying combat resolution. Native
    // saves do not serialize a defender or damage inside this action; port-owned pending damage is
    // persisted separately by TimedImpactAction. A serialized chained action is dispatched only on
    // the ordinary terminal simulation tick, never during candidate construction or installation.
    public sealed class RestoredNativeAttackAction : ISaveableEntityAction
    {
        private readonly DigglesTclContext _context;
        private readonly DigglesTclEntity _owner;
        private readonly string _chainedActionVerb;
        private readonly string _chainedActionArguments;
        private readonly int[] _leadingRawBits;
        private readonly byte[] _deadlineFlags;
        private readonly byte[] _frameFlags;
        private readonly int[] _impactAndTrailingRawBits;
        private readonly byte _trailingFlag;
        private double _remainingSeconds;
        private double _frameAccumulator;
        private int _currentFrameOrTick;
        private bool _completionAttempted;

        public RestoredNativeAttackAction(
            DigglesTclContext context,
            DigglesTclEntity owner,
            NativeAttackActionSaveState state)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            if (state == null) throw new ArgumentNullException(nameof(state));
            _chainedActionVerb = state.ChainedActionVerb;
            _chainedActionArguments = state.ChainedActionArguments;
            _leadingRawBits = Copy(state.LeadingRawBits);
            _remainingSeconds = state.RemainingSeconds;
            _deadlineFlags = Copy(state.DeadlineFlags);
            _currentFrameOrTick = state.CurrentFrameOrTick;
            _frameFlags = Copy(state.FrameFlags);
            _impactAndTrailingRawBits = Copy(state.ImpactAndTrailingRawBits);
            _trailingFlag = state.TrailingFlag;
        }

        public bool DrivesTransform => false;

        public RuntimeEntityActionSaveState CaptureSaveState() =>
            new NativeAttackActionSaveState(
                _chainedActionVerb,
                _chainedActionArguments,
                _leadingRawBits,
                _remainingSeconds,
                _deadlineFlags,
                _currentFrameOrTick,
                _frameFlags,
                _impactAndTrailingRawBits,
                _trailingFlag);

        public bool Tick(double dt)
        {
            if (_completionAttempted) return false;

            double elapsed = Math.Max(0d, dt);
            if (_frameFlags[1] != 0)
            {
                _remainingSeconds = 0d;
                _completionAttempted = true;
                return false;
            }

            if (_frameFlags[0] == 0 && _remainingSeconds > 0d)
            {
                _remainingSeconds = Math.Max(0d, _remainingSeconds - elapsed);
                _frameAccumulator += elapsed;
                while (_frameAccumulator >= 0.1d)
                {
                    _frameAccumulator -= 0.1d;
                    _currentFrameOrTick++;
                }
                if (_remainingSeconds > 0d) return true;
            }

            _completionAttempted = true;
            if (string.IsNullOrWhiteSpace(_chainedActionVerb)) return false;
            if (!SafeVerb(_chainedActionVerb))
            {
                Debug.LogWarning(
                    $"Native Attack action for owner {_owner.Handle} has an invalid chained verb.");
                return false;
            }

            try
            {
                string command = $"action {_owner.Handle} {_chainedActionVerb}";
                if (!string.IsNullOrWhiteSpace(_chainedActionArguments))
                    command += " " + _chainedActionArguments;
                _context.Interp?.Eval(command);
                // CmdAction replaces ActiveAction synchronously. Returning true prevents the outer
                // simulation loop from clearing that replacement as if it were this terminal shell.
                return _owner.ActiveAction != null &&
                    !ReferenceEquals(_owner.ActiveAction, this);
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    $"Native Attack chained action failed for owner {_owner.Handle}: {exception.Message}");
                return false;
            }
        }

        private static bool SafeVerb(string value)
        {
            foreach (char character in value)
            {
                if (char.IsLetterOrDigit(character) || character is '_' or ':' or '-') continue;
                return false;
            }
            return value.Length > 0;
        }

        private static int[] Copy(System.Collections.Generic.IReadOnlyList<int> source)
        {
            var result = new int[source.Count];
            for (int index = 0; index < result.Length; index++) result[index] = source[index];
            return result;
        }

        private static byte[] Copy(System.Collections.Generic.IReadOnlyList<byte> source)
        {
            var result = new byte[source.Count];
            for (int index = 0; index < result.Length; index++) result[index] = source[index];
            return result;
        }
    }
}
