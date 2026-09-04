#nullable enable

namespace Diggles.Runtime.Tcl.Engine
{
    using System;
    using Diggles.Runtime;
    using UnityEngine;

    /// <summary>
    /// Holds a restored native Walk action without planning or mutating the owner until the first
    /// ordinary post-commit simulation tick.
    /// </summary>
    public sealed class RestoredWalkAction : ISaveableEntityAction, IWaterReactiveWalk
    {
        private readonly DigglesTclContext _context;
        private readonly DigglesTclEntity _owner;
        private readonly WalkActionSaveState _state;
        private IEntityAction? _inner;
        private DigglesAnimController? _controller;
        private int _tickCounter;
        private int _dryAnimationSet;
        private bool _waterMovement;

        public RestoredWalkAction(
            DigglesTclContext context,
            DigglesTclEntity owner,
            WalkActionSaveState state,
            IEntityAction? preparedInner = null)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _inner = preparedInner;
            _tickCounter = state.TickCounter;
            _dryAnimationSet = state.WalkAnimationSet;
        }

        public bool Tick(double dt)
        {
            _inner ??= DigglesTclEngine.BuildMetaWalkFromSaveState(_context, _owner, _state);
            _tickCounter++;
            bool running = _inner.Tick(dt);
            if (!running)
                ClearWaterMovement(replayWalkLoop: false);
            else if (_waterMovement)
                ApplyWaterMovement();
            return running;
        }

        public void SetWaterMovement(bool active)
        {
            DigglesAnimController? controller = Controller();
            int waterAnimationSet = _state.WaterAnimationSet;
            if (controller == null || waterAnimationSet < 0) return;

            if (active)
            {
                _waterMovement = true;
                ApplyWaterMovement();
                return;
            }

            ClearWaterMovement(replayWalkLoop: true);
        }

        public RuntimeEntityActionSaveState CaptureSaveState() =>
            _state.WithTickCounter(_tickCounter);

        private DigglesAnimController? Controller()
        {
            if (_controller == null && _owner.GameObject != null)
                _controller = _owner.GameObject.GetComponentInChildren<DigglesAnimController>();
            return _controller;
        }

        private void ApplyWaterMovement()
        {
            DigglesAnimController? controller = Controller();
            int waterAnimationSet = _state.WaterAnimationSet;
            if (controller == null || waterAnimationSet < 0 || controller.ActiveAnimset == waterAnimationSet)
                return;

            if (controller.ActiveAnimset >= 0)
                _dryAnimationSet = controller.ActiveAnimset;
            controller.SetActiveAnimset(waterAnimationSet);
            controller.PlayState("walk_loop", DigglesAnimator.AnimMode.Loop);
        }

        private void ClearWaterMovement(bool replayWalkLoop)
        {
            DigglesAnimController? controller = Controller();
            if (!_waterMovement || controller == null) return;
            controller.SetActiveAnimset(_dryAnimationSet);
            if (replayWalkLoop)
                controller.PlayState("walk_loop", DigglesAnimator.AnimMode.Loop);
            _waterMovement = false;
        }
    }
}
