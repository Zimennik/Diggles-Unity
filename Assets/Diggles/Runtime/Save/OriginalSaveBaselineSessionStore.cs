#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using UnityEngine.Scripting;

    public sealed class OriginalSaveBaselineSessionStore
    {
        private RuntimeGameSession? _viewBeginOwner;
        private OriginalSaveViewBeginSessionStore? _viewBeginStore;

        [Preserve]
        public OriginalSaveBaselineSessionStore() { }

        public OriginalSaveRuntimeSnapshot? Baseline { get; private set; }
        public RuntimeGameSession? Owner { get; private set; }

        public void SetBaseline(
            RuntimeGameSession session,
            OriginalSaveRuntimeSnapshot snapshot)
        {
            Owner = session ?? throw new ArgumentNullException(nameof(session));
            Baseline = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        }

        public OriginalSaveViewBeginSessionStore GetOrCreateViewBeginStore(
            RuntimeGameSession session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (!ReferenceEquals(_viewBeginOwner, session) || _viewBeginStore == null)
            {
                _viewBeginOwner = session;
                _viewBeginStore = new OriginalSaveViewBeginSessionStore(
                    session.Engine.Ctx.IsStoryMgr);
            }
            return _viewBeginStore;
        }

        public void Clear()
        {
            Baseline = null;
            Owner = null;
            _viewBeginOwner = null;
            _viewBeginStore = null;
        }
    }
}
