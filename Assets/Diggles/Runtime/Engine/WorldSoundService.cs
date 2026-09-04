#nullable enable

namespace Diggles.Runtime
{
    using System.Collections.Generic;
    using System.IO;
    using Diggles.Runtime.Tcl.Engine;
    using UnityEngine;
    using VContainer;
    using VContainer.Unity;

    // World SFX playback — the Play-mode half of the native CSoundMgr sample channel:
    // * drains AudioState.PendingOneShots (`sound play` / `sound playglobal`);
    // * subscribes to DigglesAnimator.AnimFrameAdvanced and fires the per-anim-frame
    // sounds registered by soundinit.tcl (`sound anim/animnoseq`), the way the native
    // per-object render tick does (FUN_005cb9a0);
    // * applies the native positional falloff + stereo pan (FUN_004d19a0), the fog-visibility
    // cull (FUN_005ab220) and the voice concurrency cap (FUN_00454f40:19) — all in WorldSoundLaw.
    //
    // The falloff math (FUN_004d19a0:22-42) and its.rdata-verified constants, the play gate
    // (>0.10), the fog cull (>=100) and the voice cap (~6, drop-new) all live in WorldSoundLaw and
    // are pinned by WorldSoundLawTests. Full RE + citations: Docs/SOUND_SYSTEM_RE.md.
    //
    // The listener is the camera state triple (at.x, at.y, zoom) = the set_view focus
    // (DigglesCameraController.FocusX/FocusY/Zoom). The falloff was already faithful, but the port
    // omitted the two native limit laws — the fog cull and the ~6-voice cap — so it stacked ~16
    // unculled voices into a wash. Both are now applied (fog cull fail-open — see PlayPositional).
    //
    // Anim-frame iteration (FUN_005cb9a0:136-178 + 179-220): on a frame change, entries fire
    // for every frame in (lastFrame, currentFrame) exclusive, then for currentFrame itself; a
    // loop wrap (current <= last) fires only the current frame. Per frame, one entry of the
    // registered list is picked at random; the pre-attenuation volume gate is > 20/100
    // (FUN_005d0820:26). All of that selection logic lives in AudioState.TryPickAnimSound —
    // this service is the Unity playback shell around it.
    public sealed class WorldSoundService : IStartable, ITickable, System.IDisposable
    {
        // Falloff constants + the limit-law thresholds live in WorldSoundLaw (pure, pinned by
        // WorldSoundLawTests). Kill switch for the fog cull (§3b) — flip to false if a not-yet-Play-
        // verified fog map ever silences live gameplay; it only adds culling, never plays more.
        public static bool FogCullEnabled = true;

        readonly System.Func<DigglesTclContext> contextAccessor;
        readonly SettingsService settings;
        DigglesTclContext? _observedContext;
        DigglesTclContext ctx => contextAccessor();

        GameObject? _root;
        readonly List<AudioSource> _pool = new();
        readonly Dictionary<string, AudioClip?> _clips = new(System.StringComparer.OrdinalIgnoreCase);
        readonly System.Random _rng = new();
        DigglesCameraController? _camera;
        float _nextCameraSearch;

        [Preserve]
        [Inject]
        public WorldSoundService(IActiveGameSession sessions, SettingsService settings)
        {
            contextAccessor = () => sessions.Current.Engine.Ctx;
            this.settings = settings;
        }

        void IStartable.Start()
        {
            DigglesAnimator.AnimFrameAdvanced += OnAnimFrame;
        }

        void System.IDisposable.Dispose()
        {
            DigglesAnimator.AnimFrameAdvanced -= OnAnimFrame;
            if (_root != null) Object.Destroy(_root);
            _pool.Clear();
        }

        void ITickable.Tick()
        {
            EnsureCurrentContext();
            var au = this.ctx.Audio;
            if (au == null) return;
            // `sound play <name> <vol> [{x y z}]` / `sound playglobal` — a queue, not a
            // last-shot poll, so several shots in one render frame all play.
            while (au.PendingOneShots.Count > 0)
            {
                var shot = au.PendingOneShots.Dequeue();
                if (shot.Positional) PlayPositional(shot.Name, shot.Volume, 1f, shot.Pos);
                else Play2d(shot.Name, shot.Volume, 1f, 0f);   // FUN_004d1bb0 — no falloff/pan
            }
        }

        // ---- anim-frame SFX (DigglesAnimator.AnimFrameAdvanced) ----

        void OnAnimFrame(DigglesAnimator animator, DigglesAnimClip clip, int prevFrame, int frame)
        {
            EnsureCurrentContext();
            var au = this.ctx.Audio;
            if (au == null || !au.AnimSoundEnabled) return;            // global+0x95d8 toggle
            if (string.IsNullOrEmpty(clip.SourceObjName)) return;      // no anim-DB identity
            string qualified = clip.QualifiedName;                     // cached — no per-frame-advance alloc
            if (!au.AnimSounds.ContainsKey(qualified)) return;          // fast reject (no alloc)
            // Native iteration: every frame in (prev, current) exclusive, then current
            // (FUN_005cb9a0:136 loop +:179 block). On a loop wrap only `frame` fires.
            if (frame > prevFrame + 1)
                for (int f = prevFrame + 1; f < frame; f++)
                    FireAnimFrame(au, qualified, f, animator);
            FireAnimFrame(au, qualified, frame, animator);
        }

        void EnsureCurrentContext()
        {
            DigglesTclContext current = ctx;
            if (ReferenceEquals(_observedContext, current)) return;
            _observedContext = current;
            _camera = null;
            _nextCameraSearch = 0f;
            foreach (AudioSource source in _pool)
                if (source != null) source.Stop();
            foreach (AudioClip? clip in _clips.Values)
                if (clip != null) Object.Destroy(clip);
            _clips.Clear();
        }

        void FireAnimFrame(AudioState au, string qualified, int frame, DigglesAnimator animator)
        {
            if (!au.TryPickAnimSound(qualified, frame, this.ctx.SequenceActive, _rng,
                    out var entry, out float vol, out float freq))
                return;
            if (vol <= WorldSoundLaw.MinAnimVol) return;                // FUN_005d0820:26
            PlayPositional(entry.Sound, vol, freq, WorldToNative(animator.transform.position));
        }

        // ---- playback ----

        void PlayPositional(string name, float vol, float freq, Vector3 nativePos)
        {
            float gain = 1f, pan = 0f;
            var cam = Camera();
            if (cam != null)
                WorldSoundLaw.Attenuate(nativePos.x, nativePos.y, cam.FocusX, cam.FocusY, cam.Zoom,
                    out gain, out pan);
            float v = vol * gain;
            if (v <= WorldSoundLaw.MinPlayGain) return;                 // FUN_004d19a0:43
            // Native fog-visibility cull (FUN_004d19a0:44 → FUN_005ab220 >= 100): a positional sound
            // on a dark/faded/unexplored tile is silent — this is why the original never washes the
            // map with off-screen SFX. Fail-open: skipped in cutscenes (authored, camera-focused
            // audio) and when no fog map exists (menus/tests), so it only adds culling.
            var fog = this.ctx.FogOfWar;
            if (FogCullEnabled && fog != null && !this.ctx.SequenceActive
                && !WorldSoundLaw.FogAudible(fog, this.ctx.LocalPlayer, nativePos.x, nativePos.y))
                return;
            Play2d(name, v, freq, pan);
        }

        void Play2d(string name, float vol, float freq, float pan)
        {
            if (this.settings.Muted) return;
            var clip = ResolveClip(name);
            if (clip == null) return;
            var src = FreeSource();
            if (src == null) return;                                    // all channels busy — drop
            src.clip = clip;
            src.volume = Mathf.Clamp01(vol) * this.settings.MasterVolume * this.settings.SfxVolume;
            src.pitch = freq > 0f ? freq : 1f;
            src.panStereo = pan;
            src.Play();
        }

        AudioSource? FreeSource()
        {
            if (_root == null)
            {
                _root = new GameObject("DigglesWorldSfx");
                Object.DontDestroyOnLoad(_root);
            }
            AudioSource? firstFree = null;
            int free = 0;
            foreach (var s in _pool)
                if (s != null && !s.isPlaying) { free++; firstFree ??= s; }
            // Native concurrency cap ≈ 6 with drop-new-when-full (FUN_00454f40:19 `return 0`).
            switch (WorldSoundLaw.Acquire(free, _pool.Count, WorldSoundLaw.MaxVoices))
            {
                case WorldSoundLaw.VoiceAcquire.ReuseFree:
                    return firstFree;
                case WorldSoundLaw.VoiceAcquire.CreateNew:
                    var src = _root.AddComponent<AudioSource>();
                    src.playOnAwake = false;
                    src.loop = false;
                    src.spatialBlend = 0f;   // 2D stereo with manual pan, not 3D HRTF
                    _pool.Add(src);
                    return src;
                default:
                    return null;             // all voices busy at the cap — drop
            }
        }

        // Sample name → AudioClip: prefer the `sound bind` registry path (data/sound/**.wav),
        // else fall back to a filename search under data/Sound|Music (the cutscene presenter's
        // historical behavior for file-named one-shots). WAVs load synchronously via WavLoader.
        AudioClip? ResolveClip(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_clips.TryGetValue(name, out var cached)) return cached;
            string? path = null;
            string bound = this.ctx.Audio?.ResolveSound(name) ?? name;
            path = DigglesTclEngine.ResolveDataFile(this.ctx, bound) ?? FindByFileName(bound);
            AudioClip? clip = null;
            if (path != null && path.EndsWith(".wav", System.StringComparison.OrdinalIgnoreCase))
            {
                try { clip = WavLoader.ToAudioClip(WavLoader.ParseWav(File.ReadAllBytes(path)), name); }
                catch (System.Exception e) { Debug.LogWarning($"[WorldSound] WAV load failed {path}: {e.Message}"); }
            }
            else if (path == null)
                Debug.LogWarning($"[WorldSound] sample not found: {name} (file '{bound}')");
            _clips[name] = clip;
            return clip;
        }

        string? FindByFileName(string file)
        {
            var dataRoot = this.ctx.DataRoot;
            if (string.IsNullOrEmpty(dataRoot)) return null;
            foreach (var sub in new[] { "Sound", "Music" })
            {
                string root = Path.Combine(dataRoot, sub);
                if (!Directory.Exists(root)) continue;
                foreach (var ext in new[] { "", ".wav" })
                {
                    string fname = Path.GetFileName(file) + ext;
                    var hits = Directory.GetFiles(root, fname, SearchOption.AllDirectories);
                    if (hits.Length > 0) return hits[0];
                }
            }
            return null;
        }

        Vector3 WorldToNative(Vector3 world)
        {
            var root = this.ctx.SpawnParent;
            return root != null
                ? RuntimeWorldSpawner.LocalToNative(root.InverseTransformPoint(world))
                : RuntimeWorldSpawner.LocalToNative(world);
        }

        DigglesCameraController? Camera()
        {
            if (_camera != null) return _camera;
            if (Time.unscaledTime < _nextCameraSearch) return null;
            _nextCameraSearch = Time.unscaledTime + 1f;
            _camera = Object.FindAnyObjectByType<DigglesCameraController>();
            return _camera;
        }
    }
}
