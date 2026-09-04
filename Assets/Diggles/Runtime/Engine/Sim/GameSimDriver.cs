#nullable enable

namespace Diggles.Runtime.Sim
{
    using UnityEngine;

    // MonoBehaviour that runs DigglesGameSim at a fixed step. Custom
    // accumulator (not Unity FixedUpdate) so we don't have to touch
    // Time.fixedDeltaTime — that global is owned by physics and
    // changing it would knock the physics step out of sync.
    //
    // Replicates CGameThread::Cycle's pacing model
    // (Docs/ENGINE_ARCHITECTURE.md § 2.3): up to MaxCatchupSteps fixed
    // steps per render frame. Default step = 0.025 s = 40 Hz, matching
    // DAT_007371a8 in the original (best-guess; precise value awaits
    // dump inspection).
    //
    // [ExecuteAlways] would make MonoBehaviour.Update fire in Edit mode
    // too, but only when the editor decides to repaint (mouse move,
    // window focus, scene change). That's why a paused editor with the
    // sim toggled on doesn't actually tick. The fix is to subscribe to
    // EditorApplication.update (which ticks ~60 Hz regardless of GUI
    // events) and route it to the same step routine. Guarded by
    // #if UNITY_EDITOR so the runtime build stays editor-clean.
    [ExecuteAlways]
    public sealed class GameSimDriver : MonoBehaviour
    {
        [Tooltip("Seconds per simulation step. Default 0.025 s = 40 Hz.")]
        public float FixedStep = 0.025f;

        [Tooltip("Max sim steps per render frame (catch-up cap when frame lags).")]
        public int MaxCatchupSteps = 10;

        [Tooltip("If false, accumulator stops and sim is paused.")]
        public bool Running = true;

        public DigglesGameSim? Sim;

        double _accum;
        int _ticksThisFrame;

#if UNITY_EDITOR
        double _editorPrevTime;
#endif

        public int LastTicks => _ticksThisFrame;
        public double GameTime => Sim?.GameTime ?? 0.0;

        void OnEnable()
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                _editorPrevTime = UnityEditor.EditorApplication.timeSinceStartup;
                UnityEditor.EditorApplication.update += EditorTick;
            }
#endif
        }

        void OnDisable()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= EditorTick;
#endif
        }

        void Update()
        {
            // In Play mode Unity drives Update naturally. In Edit mode
            // Update only fires on editor repaint events — unreliable —
            // so the EditorTick path below handles that case.
            if (!Application.isPlaying) return;
            StepWith(Time.deltaTime);
        }

#if UNITY_EDITOR
        void EditorTick()
        {
            // Skip while playing (Update is the source of truth there).
            if (Application.isPlaying) return;
            double now = UnityEditor.EditorApplication.timeSinceStartup;
            float dt = (float)(now - _editorPrevTime);
            _editorPrevTime = now;
            // Cap huge dt jumps (window dragged, IDE alt-tabbed for minutes).
            if (dt > 0.25f) dt = 0.25f;
            StepWith(dt);
            // Force the SceneView to redraw so moved entities show their
            // new position without needing a mouse-over.
            if (_ticksThisFrame > 0) UnityEditor.SceneView.RepaintAll();
        }
#endif

        void StepWith(float dt)
        {
            _ticksThisFrame = 0;
            if (!Running || Sim == null || FixedStep <= 0f) return;
            // This driver is the frame loop of its host (TclSpawnTestWindow's editor scene — no
            // EngineTickService there), so it publishes its sim's clock scale to the animators.
            // One publisher per live host; DigglesGameSim.Step itself must never publish (a
            // stale/paused sim stepping elsewhere would stomp the global for every animator).
            Diggles.Runtime.DigglesAnimator.PublishTimeScale(
                Sim.ClockRunning ? Sim.TimeFactor : 0.0);
            _accum += dt;
            while (_accum >= FixedStep && _ticksThisFrame < MaxCatchupSteps)
            {
                Sim.Step(FixedStep);
                _accum -= FixedStep;
                _ticksThisFrame++;
            }
            // If we hit the cap, drop the remaining accumulated time
            // rather than letting it pile up indefinitely (the original
            // does the same — re/decompiled CGameThread::SrvDoThink
            // `local_24 = 10` loop bound).
            if (_ticksThisFrame >= MaxCatchupSteps) _accum = 0;
        }
    }
}
