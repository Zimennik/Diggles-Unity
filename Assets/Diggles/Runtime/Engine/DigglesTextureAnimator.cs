#nullable enable

namespace Diggles.Runtime
{
    using System;
    using System.Collections.Generic;
    using UnityEngine;

    // Per-submesh texture-variant flipbook — the runtime side of the Diggles
    // `set_textureanimation` command. Swaps the `_BaseMap` of one submesh's material through
    // a list of variant textures at 10 FPS. Drives facial expression / eyes / visemes / blink
    // (dwarf submesh 3 = face, 4 = eyes) and any texture variation (clothing/hair/hat color, …).
    //
    // Lives on the inner "Model" GameObject next to DigglesAnimator + MeshRenderer. The
    // renderer's VAT MaterialPropertyBlock (set by DigglesAnimator) still morphs the mesh — we
    // only override the per-submesh texture, via a per-instance material clone in that renderer's
    // material slot, so morph + variant compose without touching the shared material (other
    // entities of the same model are unaffected). DigglesAnimator.SetAnim reassigns the renderer
    // materials (overwriting our clone); we re-assert it when its MaterialVersion changes.
    [RequireComponent(typeof(MeshRenderer))]
    public sealed class DigglesTextureAnimator : MonoBehaviour
    {
        const float Fps = 10f;
        static readonly int IdBaseMap = Shader.PropertyToID("_BaseMap");
        static readonly int IdMainTex = Shader.PropertyToID("_MainTex");

        sealed class Channel
        {
            public Texture2D?[] Frames = Array.Empty<Texture2D?>();
            public int[] FrameIds = Array.Empty<int>();
            public int AnimType = 2;
            public int CurrentFrameValue;
            public float Timer;
            public int Cur;
            public int LastApplied = -1;
            public Material? Clone;   // per-instance material for this submesh slot
            // End-of-talk law (voice lip-sync only): when this play-once flipbook completes,
            // drop the channel and restore the submesh's base shared material instead of the
            // generic freeze. The generic type-1 end is the native CObj_Tick FUN_005cc600 law
            // (cursor = (cursor+1) % count; a wrap of a type-1 sets type 0 → freeze at frame
            // 0), which scripted fanims rely on (sq_actor pose kicks). The talk mouth is the
            // native exception by composition, not by tick law: its finished frame-0 freeze is
            // immediately repainted by the face-mood layer (z_faceanim.tcl set_fanim →
            // set_textureanimation this 3 <resting> — enable_auto_fanim in gameplay,
            // free_face_anim → reset_fanim_feeling per actor in cutscenes, sequencer.tcl:543),
            // and a cleared/absent channel renders the base texture (render sync FUN_005f5ef0:
            // cursor-past-count → variant 0 = base). The port has no always-on mood repaint on
            // frozen cutscene actors, so the talk channel closes itself to base at completion.
            public bool RevertToBaseOnComplete;
            public Material? BaseShared;   // the shared material the clone was made from
        }

        MeshRenderer? _r;
        DigglesAnimator? _anim;
        int _lastMatVersion;
        readonly Dictionary<int, Channel> _channels = new();

        internal readonly struct NativeChannelState
        {
            internal NativeChannelState(
                int submesh,
                int[] frameIds,
                int cursor,
                int animType,
                int currentFrameValue)
            {
                Submesh = submesh;
                FrameIds = frameIds;
                Cursor = cursor;
                AnimType = animType;
                CurrentFrameValue = currentFrameValue;
            }

            internal int Submesh { get; }
            internal int[] FrameIds { get; }
            internal int Cursor { get; }
            internal int AnimType { get; }
            internal int CurrentFrameValue { get; }
        }

        void Awake()
        {
            _r = GetComponent<MeshRenderer>();
            _anim = GetComponent<DigglesAnimator>();
            _lastMatVersion = _anim != null ? _anim.MaterialVersion : 0;
        }

        // Drive `submesh` through `frames` (resolved variant textures) at 10 FPS. One frame =
        // a static swap; multiple = an animated loop (animType 1 = play-once, else loop). Nulls
        // in `frames` are skipped (keep the previous texture).
        public void SetTextureAnimation(
            int submesh,
            Texture2D?[] frames,
            IReadOnlyList<int> frameIds,
            int startFrame,
            int animType,
            bool revertToBaseOnComplete = false)
        {
            if (frames == null) throw new ArgumentNullException(nameof(frames));
            if (frameIds == null) throw new ArgumentNullException(nameof(frameIds));
            if (frames.Length != frameIds.Count)
                throw new ArgumentException("Texture frames and native frame ids must have equal lengths.");
            if (frames.Length == 0 || submesh < 0) return;
            int currentFrameValue = startFrame >= 0 && startFrame < frameIds.Count
                ? frameIds[startFrame]
                : 0;
            SetChannel(submesh, frames, frameIds, startFrame, animType, currentFrameValue,
                revertToBaseOnComplete);
        }

        internal void RestoreNativeChannel(
            int submesh,
            Texture2D?[] frames,
            IReadOnlyList<int> frameIds,
            int cursor,
            int animType,
            int currentFrameValue)
        {
            if (frames == null) throw new ArgumentNullException(nameof(frames));
            if (frameIds == null) throw new ArgumentNullException(nameof(frameIds));
            if (frames.Length != frameIds.Count)
                throw new ArgumentException("Texture frames and native frame ids must have equal lengths.");
            if (submesh < 0) throw new ArgumentOutOfRangeException(nameof(submesh));
            SetChannel(submesh, frames, frameIds, cursor, animType, currentFrameValue);
        }

        // Save-capture mutation tracking: any channel install or playback advance bumps the
        // revision; the session stager marks the baseline once staging is complete. A retained
        // entity whose table is still at the baseline round-trips its archived table verbatim
        // (the port arms extra channels — e.g. face animation — during staging that the native
        // save legitimately lacks; they only become serializable once the table really mutates
        // in play).
        internal long NativeTableRevision { get; private set; }
        internal long NativeBaselineRevision { get; private set; } = -1;
        internal void MarkNativeBaseline() => NativeBaselineRevision = NativeTableRevision;
        internal bool MutatedSinceNativeBaseline =>
            NativeBaselineRevision < 0 || NativeTableRevision != NativeBaselineRevision;

        internal NativeChannelState[] CaptureNativeChannels()
        {
            var result = new NativeChannelState[_channels.Count];
            int index = 0;
            foreach (KeyValuePair<int, Channel> pair in _channels)
            {
                Channel channel = pair.Value;
                result[index++] = new NativeChannelState(
                    pair.Key,
                    (int[])channel.FrameIds.Clone(),
                    channel.Cur,
                    channel.AnimType,
                    channel.CurrentFrameValue);
            }
            Array.Sort(result, (left, right) => left.Submesh.CompareTo(right.Submesh));
            return result;
        }

        private void SetChannel(
            int submesh,
            Texture2D?[] frames,
            IReadOnlyList<int> frameIds,
            int cursor,
            int animType,
            int currentFrameValue,
            bool revertToBaseOnComplete = false)
        {
            if (_r == null) _r = GetComponent<MeshRenderer>();
            if (_anim == null) _anim = GetComponent<DigglesAnimator>();
            if (_r == null) return;
            if (!_channels.TryGetValue(submesh, out Channel? ch))
            {
                ch = new Channel();
                _channels[submesh] = ch;
            }
            ch.Frames = (Texture2D?[])frames.Clone();
            ch.FrameIds = new int[frameIds.Count];
            for (int index = 0; index < frameIds.Count; index++)
                ch.FrameIds[index] = frameIds[index];
            ch.AnimType = animType;
            ch.CurrentFrameValue = currentFrameValue;
            ch.Cur = cursor;
            ch.Timer = 0f;
            ch.LastApplied = -1;
            ch.RevertToBaseOnComplete = revertToBaseOnComplete;
            if (frames.Length != 0) EnsureClones();
            if (_anim != null) _lastMatVersion = _anim.MaterialVersion;
            NativeTableRevision++;
            ApplyTexture(ch, ch.Cur);
        }

        void OnEnable() => DigglesFrameDriver.Register(this);    // central late tick
        void OnDisable() => DigglesFrameDriver.Unregister(this);

        // Called by DigglesFrameDriver.RunLatePhase (was a per-instance LateUpdate).
        internal void TickLate() => Step(Time.deltaTime);

        // The tick body, with an explicit delta so EditMode tests can drive the flipbook
        // (Time.deltaTime is 0 outside Play). Play always enters via TickLate.
        public void Step(float deltaSeconds)
        {
            if (_r == null || _channels.Count == 0) return;
            // Re-assert clones if DigglesAnimator swapped the renderer materials (anim change).
            if (_anim != null && _anim.MaterialVersion != _lastMatVersion)
            {
                EnsureClones();
                _lastMatVersion = _anim.MaterialVersion;
            }
            List<int>? completedToBase = null;
            foreach (var kv in _channels)
            {
                if (Advance(kv.Value, deltaSeconds))
                    (completedToBase ??= new List<int>()).Add(kv.Key);
            }
            if (completedToBase != null)
                foreach (int submesh in completedToBase)
                    RevertChannelToBase(submesh);
        }

        // Make sure each channel's submesh slot holds our per-instance clone (created lazily
        // from the shared material so it keeps the right shader/alpha config).
        void EnsureClones()
        {
            if (_r == null) return;
            var mats = _r.sharedMaterials;
            bool changed = false;
            foreach (var kv in _channels)
            {
                int sm = kv.Key; var ch = kv.Value;
                if (ch.Frames.Length == 0) continue;
                if (sm < 0 || sm >= mats.Length || mats[sm] == null) continue;
                // Remember the shared material we override — the talk-channel completion
                // restores it (RevertChannelToBase). Re-captured on every re-assert: an anim
                // change may have handed this slot a different shared material.
                if (!ReferenceEquals(mats[sm], ch.Clone)) ch.BaseShared = mats[sm];
                if (ch.Clone == null)
                    ch.Clone = new Material(mats[sm]) { name = mats[sm].name + " (texanim)" };
                if (!ReferenceEquals(mats[sm], ch.Clone)) { mats[sm] = ch.Clone; changed = true; }
                ch.LastApplied = -1;   // force re-apply onto the (possibly new) clone
            }
            if (changed) _r.sharedMaterials = mats;
        }

        // Returns true when a RevertToBaseOnComplete play-once just finished — the caller
        // drops the channel and restores the base shared material (end-of-talk mouth close).
        bool Advance(Channel ch, float deltaSeconds)
        {
            int n = ch.Frames.Length;
            if (n <= 0)
            {
                ch.CurrentFrameValue = 0;
                return false;
            }
            if (ch.AnimType == 0)
            {
                SynchronizeCurrentFrame(ch);
                ApplyTexture(ch, ch.Cur);
                return false;
            }
            ch.Timer += deltaSeconds * Fps;
            while (ch.Timer >= 1f)
            {
                ch.Timer -= 1f;
                int next = ch.Cur + 1;
                if (next >= n)
                {
                    // Voice lip-sync end: the finished talk flipbook clears to the base face
                    // (see the Channel.RevertToBaseOnComplete note for the native chain).
                    if (ch.AnimType == 1 && ch.RevertToBaseOnComplete) return true;
                    next = 0;
                    if (ch.AnimType == 1)
                        ch.AnimType = 0;
                }
                ch.Cur = next;
                NativeTableRevision++;
                if (ch.AnimType == 0) break;
            }
            SynchronizeCurrentFrame(ch);
            ApplyTexture(ch, ch.Cur);
            return false;
        }

        // Drop a completed talk channel: the submesh returns to its base shared material —
        // the same render state a cleared native texture-anim yields (FUN_005f5ef0's
        // cursor-past-count branch selects variant 0 = the base texture).
        void RevertChannelToBase(int submesh)
        {
            if (!_channels.TryGetValue(submesh, out Channel? ch)) return;
            _channels.Remove(submesh);
            NativeTableRevision++;
            if (_r != null && ch.Clone != null)
            {
                var mats = _r.sharedMaterials;
                if (submesh >= 0 && submesh < mats.Length
                    && ReferenceEquals(mats[submesh], ch.Clone) && ch.BaseShared != null)
                {
                    mats[submesh] = ch.BaseShared;
                    _r.sharedMaterials = mats;
                }
            }
            if (ch.Clone != null) DestroySafe(ch.Clone);
        }

        static void SynchronizeCurrentFrame(Channel ch)
        {
            ch.CurrentFrameValue = ch.Cur >= 0 && ch.Cur < ch.FrameIds.Length
                ? ch.FrameIds[ch.Cur]
                : 0;
        }

        void ApplyTexture(Channel ch, int idx)
        {
            if (ch.Clone == null || idx == ch.LastApplied) return;
            ch.LastApplied = idx;
            var t = (idx >= 0 && idx < ch.Frames.Length) ? ch.Frames[idx] : null;
            if (t == null) return;   // null variant → keep current texture
            if (ch.Clone.HasProperty(IdBaseMap)) ch.Clone.SetTexture(IdBaseMap, t);
            else if (ch.Clone.HasProperty(IdMainTex)) ch.Clone.SetTexture(IdMainTex, t);
        }

        // Drop all texture-variant overrides (destroy the per-submesh clones, clear channels)
        // so the renderer falls back to its current shared materials' base textures. Called
        // when the model is re-skinned (mann→frau): the old channels hold the previous db's
        // variant textures (e.g. ZMann_ face), which must not paint onto the new (frau) mesh.
        public void ClearChannels()
        {
            foreach (var ch in _channels.Values)
                if (ch.Clone != null) DestroySafe(ch.Clone);
            _channels.Clear();
        }

        void OnDestroy()
        {
            foreach (var ch in _channels.Values)
                if (ch.Clone != null) DestroySafe(ch.Clone);
        }

        // The headless EditMode tests boot the real campaign (auto_choose_gender re-skins dwarves
        // → set_alternateanimdb → ClearChannels) — Destroy outside Play logs "Destroy may not
        // be called from edit mode!" and fails the test on the unhandled error. Same guard
        // pattern as RuntimeWorldSpawner.Destroy.
        static void DestroySafe(UnityEngine.Object o)
        {
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }
    }
}
