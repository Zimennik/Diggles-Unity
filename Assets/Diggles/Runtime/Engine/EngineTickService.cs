#nullable enable

namespace Diggles.Runtime
{
    using UnityEngine;
    using VContainer;
    using VContainer.Unity;

    // Advances the game simulation at a FIXED 40 Hz (25 ms), independent of render rate. A VContainer
    // entry point (ITickable runs in the Update PlayerLoop) — extracted from DigglesWorldRuntime.Update
    // so the sim tick is owned by the DI scope, not the world loader.
    //
    // A fixed accumulator is faithful (the original ticks logic at 40 Hz) + cheaper than per-frame +
    // deterministic; animation/rendering stay at render rate (DigglesAnimator.Update).
    public sealed class EngineTickService : ITickable, System.IDisposable
    {
        const float SimStep = 0.025f;

        readonly IActiveGameSession sessions;
        readonly IGameMutationGate mutationGate;
        readonly TimeControlService time;
        RuntimeGameSession? observedSession;
        float _accum;

        [Preserve]
        [Inject]
        public EngineTickService(
            IActiveGameSession sessions,
            IGameMutationGate mutationGate,
            TimeControlService time)
        {
            this.sessions = sessions;
            this.mutationGate = mutationGate;
            this.time = time;
        }

        void ITickable.Tick()
        {
            // Activation is synchronous today, but the explicit gate keeps the frame driver correct
            // if candidate binding later spans callbacks or another PlayerLoop phase.
            if (this.mutationGate.IsMutationInProgress) return;

            RuntimeGameSession session = this.sessions.Current;
            if (!ReferenceEquals(this.observedSession, session))
            {
                // A partial fixed-step remainder belongs to the old simulation cadence. Carrying it
                // into the newly committed world would advance the loaded session by unsaved time.
                this.observedSession = session;
                this._accum = 0f;
            }

            var sim = session.Engine.Sim;
            if (sim == null) return;
            // FAST-FORWARD (TimeControlService, +/- x1..x10): speed the game up by running the fixed
            // sim step `speed`× more often per real frame (fine granularity kept — see the service),
            // and scaling the render-side animator clock by the same factor so clips stay in lock-step.
            // 1 in the menu / a cutscene (the service gates it), so those play at normal speed.
            int speed = this.time.EffectiveSpeed;
            // Publish the ACTIVE sim's clock scale to the render-side anim clocks (gametime
            // factor/stop → clips slow/freeze, faithful to the scaled SrvDoThink anim stepping;
            // × the fast-forward factor). This service is the SINGLE owner of
            // DigglesAnimator.GlobalTimeScale: it lives in the live scene's scope and reads the
            // active session's CURRENT sim every frame — sims stepped anywhere else (EditMode tests, tools,
            // a stale scope) never touch the global, so an inactive sim can't freeze the animators.
            DigglesAnimator.PublishTimeScale((sim.ClockRunning ? sim.TimeFactor : 0.0) * speed);
            this._accum += Time.deltaTime * speed;
            int steps = 0;
            int cap = 4 * speed;                          // proportional catch-up cap (still bounded)
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            while (this._accum >= SimStep && steps < cap)
            {
                sim.Step(SimStep);
                this._accum -= SimStep;
                steps++;
            }
            if (this._accum > SimStep) this._accum = 0f;   // drop backlog (no spiral)
            // PerfHud diagnostics: the fixed-step sim's wall cost this
            // frame — lets Play attribute frame time to "sim CPU" vs render without a profiler.
            Diggles.Runtime.Tools.PerfHud.SimMs =
                (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            Diggles.Runtime.Tools.PerfHud.SimSteps = steps;
            Diggles.Runtime.Tools.PerfHud.SimEntities = sim.Ctx.Entities.Count;
        }

        // Scope teardown (scene unload / ReloadAndPlay): drop a mid-cutscene 0.3/0 scale so the
        // next scene's animators run at 1× until its own scope's service publishes its sim.
        void System.IDisposable.Dispose() => DigglesAnimator.PublishTimeScale(1.0);
    }
}
