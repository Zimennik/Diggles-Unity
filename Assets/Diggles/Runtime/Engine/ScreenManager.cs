#nullable enable

namespace Diggles.Runtime
{
    using Diggles.Runtime.Tcl.Engine;
    using UnityEngine;
    using VContainer;
    using VContainer.Unity;

    // Coordinator for the cutscene screen layer, and the only injected component in it: reads the
    // cutscene state (DigglesTclContext.ScreenFx /.Dialogue /.SequenceActive) and drives three
    // dumb view components it references — FadeView, SubtitleView, LetterboxView — each of which
    // knows only how to display, not the game state. ScreenManager owns the cross-cutting
    // coordination (the fade-clear edge on cutscene end); the views own only their own animation.
    //
    // Scene-placed on the DigglesControllers object; found + injected via RegisterComponentInHierarchy.
    // Wire the three view refs in the Inspector (each view in turn references its own UI). Any unset
    // view is simply skipped.
    public sealed class ScreenManager : MonoBehaviour, IStartable, IRuntimeSessionPresentationParticipant
    {
        [Header("Views (each references its own UI in the Inspector)")]
        [SerializeField] FadeView? fade;
        [SerializeField] SubtitleView? subtitle;
        [SerializeField] LetterboxView? letterbox;

        DigglesTclContext? _ctx;
        RuntimeGameSession? _session;   // read live for OriginalSaveState (menu-vs-gameplay signal)

        [UnityEngine.Scripting.Preserve]
        [Inject]
        public void Construct(IActiveGameSession sessions) => BindSession(sessions.Current);

        bool _prevSeqActive;   // fade-clear edge

        public void BindSession(RuntimeGameSession session)
        {
            _ctx = session.Engine.Ctx;
            _session = session;
            _prevSeqActive = _ctx.SequenceActive;
        }

        // The cinematic letterbox belongs to in-game cutscenes only; the main-menu backdrop cycles
        // ambient cutscenes (SequenceActive true) that must stay full-frame. Mirrors DigglesWorldRuntime's
        // menu signal (BindSession: _atMenu = OriginalSaveState == null && !IsStoryMgr): a session is
        // "at the menu" only when it is neither a StoryMgr/campaign world nor staged from a.sav (the
        // tutorial world is IsStoryMgr==false yet real gameplay, reached via a save load). So gameplay —
        // campaign (IsStoryMgr) or any loaded save (hasOriginalSave) — always keeps the bars.
        public static bool ShouldShowLetterbox(bool sequenceActive, bool isStoryMgr, bool hasOriginalSave)
            => sequenceActive && (isStoryMgr || hasOriginalSave);

        // Entry point — forces the container to resolve (hence find + inject) this scene component.
        void IStartable.Start() { }

        void LateUpdate()
        {
            if (_ctx == null) return;
            var fx = _ctx.ScreenFx;
            bool active = _ctx.SequenceActive;

            if (active && !_prevSeqActive) subtitle?.PrewarmLayout();
            // Coordination: on the cutscene-end (active→inactive) edge, clear any trailing fade so a
            // fade-to-black at the end of the cutscene doesn't persist into gameplay.
            if (!active && _prevSeqActive) fx?.ClearFade();
            _prevSeqActive = active;

            // Feed each view its slice of state. (fade only when there's a fade state, matching the
            // original — leave the overlay untouched otherwise.)
            if (fx != null) fade?.SetAlpha(fx.FadeAlpha);
            subtitle?.Render(_ctx.Dialogue);
            // Gate the bars on gameplay: never during the menu backdrop's ambient cutscenes.
            letterbox?.SetShown(ShouldShowLetterbox(active, _ctx.IsStoryMgr, _session?.OriginalSaveState != null));
        }
    }
}
