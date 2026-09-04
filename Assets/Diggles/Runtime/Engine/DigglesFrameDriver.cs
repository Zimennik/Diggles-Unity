#nullable enable

namespace Diggles.Runtime
{
    using System;
    using System.Collections.Generic;
    using Unity.Profiling;
    using UnityEngine;

    // Central per-entity frame driver.
    //
    // Every spawned entity used to carry 3-5 individually-dispatched MonoBehaviour callbacks
    // (DigglesAnimator.Update, DigglesAnimController.Update, DigglesTextureAnimator.LateUpdate,
    // DigglesLinkAttachment.LateUpdate, NativePickBody.LateUpdate). At 1200+ live entities that
    // is ~3600+ native→managed dispatches per frame — pure overhead that scales with the dug-up
    // world's drop-item count (each dispatch also pays the editor's per-behaviour bookkeeping).
    //
    // This driver replaces them with one Update + one LateUpdate that iterate cached lists and
    // call the exact same per-component tick bodies. The component logic is byte-identical —
    // only the dispatch changed:
    // Update: all DigglesAnimator.TickClock(dt), then all DigglesAnimController.TickChain(dt)
    // (the spawner adds the animator before the controller on the same GO, so the
    // per-entity order matches the old per-GO dispatch order);
    // LateUpdate: all DigglesTextureAnimator.TickLate, then all DigglesLinkAttachment
    //.PlaceNow, then all NativePickBody.SyncNow (mutually independent).
    //
    // Components self-register in OnEnable / unregister in OnDisable, so Unity's own
    // enabled/activeInHierarchy semantics are preserved exactly (a disabled behaviour never
    // ticks). Registration is Play-mode-only: in EditMode nothing ever ticked these callbacks
    // anyway (headless tests drive the public tick seams directly).
    public sealed class DigglesFrameDriver : MonoBehaviour
    {
        // O(1) add/remove registry with stable iteration by index. Removal swap-fills from the
        // tail; mutation only happens on component enable/disable (never per frame).
        internal sealed class TickList<T> where T : Component
        {
            public readonly List<T> Items = new(256);
            readonly Dictionary<T, int> _index = new(256);

            public void Add(T item)
            {
                if (_index.ContainsKey(item)) return;
                _index[item] = Items.Count;
                Items.Add(item);
            }

            public void Remove(T item)
            {
                if (!_index.TryGetValue(item, out int i)) return;
                int last = Items.Count - 1;
                var tail = Items[last];
                Items[i] = tail;
                _index[tail] = i;
                Items.RemoveAt(last);
                _index.Remove(item);
            }

            public void Clear()
            {
                Items.Clear();
                _index.Clear();
            }
        }

        internal static readonly TickList<DigglesAnimator> Animators = new();
        internal static readonly TickList<DigglesAnimController> Controllers = new();
        internal static readonly TickList<DigglesTextureAnimator> TextureAnimators = new();
        internal static readonly TickList<DigglesLinkAttachment> LinkAttachments = new();
        internal static readonly TickList<NativePickBody> PickBodies = new();

        static readonly ProfilerMarker s_updateMarker = new("Diggles.FrameDriver.Update");
        static readonly ProfilerMarker s_lateMarker = new("Diggles.FrameDriver.LateUpdate");

        static DigglesFrameDriver? s_instance;

        // ---- registration (called from the components' OnEnable/OnDisable) -----------------

        public static void Register(DigglesAnimator a) { if (Arm()) Animators.Add(a); }
        public static void Unregister(DigglesAnimator a) => Animators.Remove(a);
        public static void Register(DigglesAnimController c) { if (Arm()) Controllers.Add(c); }
        public static void Unregister(DigglesAnimController c) => Controllers.Remove(c);
        public static void Register(DigglesTextureAnimator t) { if (Arm()) TextureAnimators.Add(t); }
        public static void Unregister(DigglesTextureAnimator t) => TextureAnimators.Remove(t);
        public static void Register(DigglesLinkAttachment l) { if (Arm()) LinkAttachments.Add(l); }
        public static void Unregister(DigglesLinkAttachment l) => LinkAttachments.Remove(l);
        public static void Register(NativePickBody p) { if (Arm()) PickBodies.Add(p); }
        public static void Unregister(NativePickBody p) => PickBodies.Remove(p);

        // Live registry sizes — PerfHud shows these so a Play capture can confirm the central
        // driver actually owns the army (and how big it is).
        public static (int anims, int ctrls, int tex, int links, int picks) Counts =>
            (Animators.Items.Count, Controllers.Items.Count, TextureAnimators.Items.Count,
             LinkAttachments.Items.Count, PickBodies.Items.Count);

        // Test seam (headless benches only): EditMode has no player loop, so registration is
        // normally refused there. The entity-army bench flips this to register real rigs and
        // drive RunUpdatePhase/RunLatePhase directly — it must call ClearAllForTests afterwards.
        public static bool RegisterInEditModeForTests;

        public static void ClearAllForTests()
        {
            Animators.Clear(); Controllers.Clear(); TextureAnimators.Clear();
            LinkAttachments.Clear(); PickBodies.Clear();
        }

        // Play-mode gate + lazy driver creation. EditMode registration is refused: no player
        // loop runs there (matching the old behavior — Update/LateUpdate never fired), and the
        // headless tests create thousands of short-lived rigs that must not pile up in statics.
        static bool Arm()
        {
            if (!Application.isPlaying) return RegisterInEditModeForTests;
            if (s_instance == null)
            {
                var go = new GameObject("~DigglesFrameDriver") { hideFlags = HideFlags.HideAndDontSave };
                DontDestroyOnLoad(go);
                s_instance = go.AddComponent<DigglesFrameDriver>();
            }
            return true;
        }

        // ---- the two phase bodies (public static so headless benches can run them 1:1) ------

        public static void RunUpdatePhase(float dt)
        {
            var anims = Animators.Items;
            for (int i = 0; i < anims.Count; i++)
            {
                var a = anims[i];
                if (a == null) continue;                    // destroyed mid-frame — swept by OnDisable
                try { a.TickClock(dt); }
                catch (Exception e) { Debug.LogException(e, a); }
            }
            var ctrls = Controllers.Items;
            for (int i = 0; i < ctrls.Count; i++)
            {
                var c = ctrls[i];
                if (c == null) continue;
                try { c.TickChain(dt); }
                catch (Exception e) { Debug.LogException(e, c); }
            }
        }

        public static void RunLatePhase()
        {
            var texes = TextureAnimators.Items;
            for (int i = 0; i < texes.Count; i++)
            {
                var t = texes[i];
                if (t == null) continue;
                try { t.TickLate(); }
                catch (Exception e) { Debug.LogException(e, t); }
            }
            var links = LinkAttachments.Items;
            for (int i = 0; i < links.Count; i++)
            {
                var l = links[i];
                if (l == null) continue;
                try { l.PlaceNow(); }
                catch (Exception e) { Debug.LogException(e, l); }
            }
            var picks = PickBodies.Items;
            for (int i = 0; i < picks.Count; i++)
            {
                var p = picks[i];
                if (p == null) continue;
                try { p.SyncNow(); }
                catch (Exception e) { Debug.LogException(e, p); }
            }
        }

        void Update()
        {
            using (s_updateMarker.Auto())
                RunUpdatePhase(Time.deltaTime);
        }

        void LateUpdate()
        {
            using (s_lateMarker.Auto())
                RunLatePhase();
        }

        void OnDestroy()
        {
            if (s_instance == this) s_instance = null;
        }
    }
}
