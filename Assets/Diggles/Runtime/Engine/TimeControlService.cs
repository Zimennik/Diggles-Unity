#nullable enable

namespace Diggles.Runtime
{
    using Diggles.Runtime.Tcl.Engine;
    using UnityEngine;
    using VContainer;
    using VContainer.Unity;

    // Pure, Unity-free logic core of the fast-forward control (testable in isolation). Holds the
    // selected level and the gating rule; the service feeds it live input + context flags.
    public sealed class TimeControlModel
    {
        public static readonly int[] Levels = { 1, 2, 4, 6, 8, 10 };

        public int Index { get; private set; }
        public int Speed => Levels[Index];

        // The multiplier to actually apply this frame: the selected level only in gameplay and
        // outside a cutscene; 1 otherwise (menu backdrop / any active sequence).
        public int EffectiveSpeed(bool inGame, bool cutscene) => (inGame && !cutscene) ? Levels[Index] : 1;

        // "+"/"−" one step, clamped to the ends. Returns true if the level actually changed.
        public bool TryBump(bool up)
        {
            int ni = Index + (up ? 1 : -1);
            if (ni < 0) ni = 0;
            if (ni > Levels.Length - 1) ni = Levels.Length - 1;
            if (ni == Index) return false;
            Index = ni;
            return true;
        }

        public bool Reset()   // → x1; true if it wasn't already there
        {
            if (Index == 0) return false;
            Index = 0;
            return true;
        }
    }

    // Real-time fast-forward for testing: "+" speeds the game up, "−" slows it back down, cycling
    // x1 · x2 · x4 · x6 · x8 · x10.
    //
    // This is deliberately different from DigglesGameSim.TimeFactor (the game-time scale used for
    // cutscene slow-mo / pause — `gametime factor`). TimeFactor makes each sim Step a bigger game-time
    // chunk, which is correct for slow-mo (<1) but coarsens the sim when >1 (movement overshoots,
    // the 10 Hz logic-tick drops backlog → under-sampled AI). To fast-forward without losing fidelity
    // we instead run more fixed steps per real frame (EngineTickService multiplies its accumulator +
    // catch-up cap by EffectiveSpeed) and scale the render-side animator clock (GlobalTimeScale) by
    // the same factor. Every system that advances off the sim step — Tcl gametime/timers/state-think,
    // actions, movement executors, particles, sound, the work dispatcher — then advances faithfully
    // N× faster at the native per-step granularity; animations play N× faster in lock-step. (The
    // cinematic camera stays on real time, as it does natively.)
    //
    // Gating: only in gameplay (ctx.IsStoryMgr — the campaign world; the menu backdrop is not a
    // StoryMgr world) and not during a cutscene (ctx.SequenceActive). EffectiveSpeed folds both in,
    // so the sim is forced back to x1 the instant a sequence starts; a cutscene start also resets
    // the selected level to x1 so it stays x1 afterwards.
    public sealed class TimeControlService : ITickable, IStartable, System.IDisposable
    {
        readonly IActiveGameSession _sessions;
        readonly TimeControlModel _model = new();
        RuntimeGameSession? _observedSession;
        bool _prevSeq;
        TimeControlOverlay? _overlay;

        [Preserve]
        [Inject]
        public TimeControlService(IActiveGameSession sessions) { _sessions = sessions; }

        // The level the player has selected (x1..x10), regardless of gating.
        public int Speed => _model.Speed;

        DigglesTclContext CurrentContext => _sessions.Current.Engine.Ctx;
        bool InGame => CurrentContext.IsStoryMgr;
        bool InCutscene => CurrentContext.SequenceActive;

        // True while fast-forward is actually in effect (reads the live context flags, so the moment a
        // sequence starts EffectiveSpeed drops to 1 with no frame lag).
        public bool Active => InGame && !InCutscene;

        // The multiplier the sim driver applies THIS frame — the selected level in gameplay, else 1.
        public int EffectiveSpeed => _model.EffectiveSpeed(InGame, InCutscene);

        void IStartable.Start()
        {
            var go = new GameObject("TimeControlOverlay") { hideFlags = HideFlags.HideAndDontSave };
            _overlay = go.AddComponent<TimeControlOverlay>();
            _overlay.Service = this;
        }

        void ITickable.Tick()
        {
            RuntimeGameSession session = _sessions.Current;
            var ctx = session.Engine.Ctx;
            if (!ReferenceEquals(_observedSession, session))
            {
                bool replacedSession = _observedSession != null;
                _observedSession = session;
                _prevSeq = ctx.SequenceActive;
                // Fast-forward is a port-owned testing control, not native saved state. A committed
                // load starts at x1 and does not inherit an input selection from the retired world.
                if (replacedSession && _model.Reset()) Log();
            }

            // A cutscene start (rising edge of the sequence flag) resets the level to x1. EffectiveSpeed
            // already forces the sim to x1 while SequenceActive is up; this makes the selection stay
            // x1 after the sequence ends (no leftover fast-forward).
            bool seq = ctx.SequenceActive;
            if (seq && !_prevSeq && _model.Reset()) Log();
            _prevSeq = seq;

            // "+"/"−" only change the level in actual gameplay (never in the menu or a cutscene).
            if (!ctx.IsStoryMgr || seq) return;
            if (PlusPressed())       { if (_model.TryBump(true))  Log(); }
            else if (MinusPressed()) { if (_model.TryBump(false)) Log(); }
        }

        void System.IDisposable.Dispose()
        {
            if (_overlay != null) Object.Destroy(_overlay.gameObject);
            _overlay = null;
        }

        // Main-row "+" is Shift+"=", so accept "=" as well; plus the numpad keys.
        static bool PlusPressed()
            => Input.GetKeyDown(KeyCode.Equals) || Input.GetKeyDown(KeyCode.Plus) || Input.GetKeyDown(KeyCode.KeypadPlus);
        static bool MinusPressed()
            => Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.KeypadMinus);

        void Log() => Debug.Log($"[TimeControl] game speed → x{_model.Speed}");
    }

    // Minimal on-screen indicator (dev tool, immediate-mode GUI — same lightweight style as
    // TclDebugConsole). Shows ">> x4" top-centre only while fast-forward is in effect.
    public sealed class TimeControlOverlay : MonoBehaviour
    {
        public TimeControlService? Service;
        GUIStyle? _style;
        string _txt = "";
        int _txtSpeed = -1;   // ">> xN" cached per speed — this interpolated per OnGUI pass (×2/frame) while fast-forwarding

        void OnGUI()
        {
            var s = Service;
            if (s == null || !s.Active || s.Speed <= 1) return;
            _style ??= new GUIStyle(GUI.skin.label)
            { fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            if (_txtSpeed != s.Speed) { _txtSpeed = s.Speed; _txt = $">> x{s.Speed}"; }
            string txt = _txt;
            var r = new Rect(Screen.width * 0.5f - 70f, 6f, 140f, 30f);
            _style.normal.textColor = Color.black;
            GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), txt, _style);   // shadow
            _style.normal.textColor = new Color(1f, 0.9f, 0.35f);                       // amber
            GUI.Label(r, txt, _style);
        }
    }
}
