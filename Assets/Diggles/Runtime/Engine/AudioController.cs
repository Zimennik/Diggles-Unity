#nullable enable

namespace Diggles.Runtime
{
    using System.Collections;
    using System.Collections.Generic;
    using System.IO;
    using Diggles.Runtime.Tcl.Engine;
    using UnityEngine;
    using UnityEngine.Audio;
    using UnityEngine.Networking;
    using VContainer;
    using VContainer.Unity;

    // The single audio authority for the game. Extracted from DigglesCutscenePresenter so audio is
    // owned by one injected component instead of the cutscene renderer.
    //
    // Responsibilities:
    // - 3 channels (voice / music / sfx), each routed to an AudioMixer group when a mixer asset
    // is present (else AudioSource.volume fallback);
    // - volumes driven by SettingsService, persisted there;
    // - the cutscene AudioState (DigglesTclContext.Audio) playback: one-shot SFX, the voice stream
    // (a [start,stop] segment of one long MP3, stopped on the sim clock), adaptive music themes —
    // moved verbatim from the presenter so cutscene audio behaviour is unchanged;
    // - a small public API (PlaySfx / PlayMusic / StopMusic) for future gameplay (non-cutscene) audio.
    //
    // Created + injected by DigglesGameInstaller. Registered as IStartable so the container guarantees
    // instantiation (its Start applies the initial volumes + subscribes to settings). Not gated on
    // SequenceActive — audio is driven by explicit commands and applies always, same as before.
    //
    // The AudioMixer is optional: load it from Resources/<MixerResourceName>. Create it in Unity with
    // groups Master -> {Music, Sfx, Voice} and exposed float params MasterVolume / MusicVolume /
    // SfxVolume / VoiceVolume (see Docs). Without it, volumes fall back to AudioSource.volume.
    public sealed class AudioController : MonoBehaviour, IStartable, IRuntimeSessionPresentationParticipant
    {
        const string MasterParam = "MasterVolume";
        const string MusicParam = "MusicVolume";
        const string SfxParam = "SfxVolume";
        const string VoiceParam = "VoiceVolume";

        [Header("Mixing")]
        [Tooltip("Optional AudioMixer asset, wired in the Inspector. Groups Master/Music/Sfx/Voice with " +
                 "exposed params MasterVolume/MusicVolume/SfxVolume/VoiceVolume (see Docs/AUDIO_SETUP.md). " +
                 "Without it, channel volumes fall back to AudioSource.volume.")]
        [SerializeField] AudioMixer? audioMixer;

        DigglesTclContext? _ctx;
        SettingsService? _settings;
        AudioMixer? _mixer;

        AudioSource? _voice, _music, _sfx;
        int _lastVoicePlay = -1;
        string? _voiceLoadedFor, _musicLoadedFor;
        bool _musicLoading;   // async stream load in flight (guards the end-of-track cycler)
        readonly System.Random _musicRng = new();
        readonly Dictionary<string, AudioClip?> _clipCache = new(System.StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string?> _audioPathCache = new(System.StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _clipLoadsInFlight = new(System.StringComparer.OrdinalIgnoreCase);
        string? _voicePrewarmFor;

        [UnityEngine.Scripting.Preserve]
        [Inject]
        public void Construct(IActiveGameSession sessions, SettingsService settings)
        {
            _settings = settings;
            BindSession(sessions.Current);
        }

        public void BindSession(RuntimeGameSession session)
        {
            // Any asynchronous decode belongs to the context that requested it. Cancelling here
            // prevents its completion callback from marking voice state in a newly committed world.
            StopAllCoroutines();
            _voice?.Stop();
            _music?.Stop();
            _sfx?.Stop();
            _ctx = session.Engine.Ctx;
            _lastVoicePlay = -1;
            _voiceLoadedFor = null;
            _musicLoadedFor = null;
            _musicLoading = false;
            _voicePrewarmFor = null;
            _clipLoadsInFlight.Clear();
            _audioPathCache.Clear();
        }

        void Awake() => BuildAudio();

        // VContainer entry point — runs once after the container is built and injection is done.
        void IStartable.Start()
        {
            _mixer = audioMixer;
            if (_mixer != null) RouteSourcesToMixer();
            else Debug.Log("[AudioController] no AudioMixer wired (Inspector field) — using AudioSource.volume " +
                           "fallback (assign the DigglesAudio mixer, see Docs/AUDIO_SETUP.md, to enable mixing).");
            if (_settings != null) _settings.Changed += ApplyVolumes;
            ApplyVolumes();
        }

        void OnDestroy()
        {
            if (_settings != null) _settings.Changed -= ApplyVolumes;
        }

        // ----- volume / mixer -----

        void RouteSourcesToMixer()
        {
            if (_voice != null) _voice.outputAudioMixerGroup = FindGroup(VoiceParam, "Voice");
            if (_music != null) _music.outputAudioMixerGroup = FindGroup(MusicParam, "Music");
            if (_sfx != null) _sfx.outputAudioMixerGroup = FindGroup(SfxParam, "Sfx");
        }

        AudioMixerGroup? FindGroup(string _, string groupName)
        {
            var groups = _mixer!.FindMatchingGroups(groupName);
            return groups is { Length: > 0 } ? groups[0] : null;
        }

        // Re-apply the current settings to the mixer (dB) or, with no mixer, fold channel × master
        // into each source's volume. Subscribed to SettingsService.Changed.
        void ApplyVolumes()
        {
            if (_settings == null) return;
            float master = _settings.Muted ? 0f : _settings.MasterVolume;
            if (_mixer != null)
            {
                _mixer.SetFloat(MasterParam, LinearToDb(master));
                _mixer.SetFloat(MusicParam, LinearToDb(_settings.MusicVolume));
                _mixer.SetFloat(SfxParam, LinearToDb(_settings.SfxVolume));
                _mixer.SetFloat(VoiceParam, LinearToDb(_settings.VoiceVolume));
            }
            else
            {
                // No mixer: the group hierarchy isn't there, so multiply channel × master per source.
                if (_music != null) _music.volume = master * _settings.MusicVolume;
                if (_sfx != null) _sfx.volume = master * _settings.SfxVolume;
                if (_voice != null) _voice.volume = master * _settings.VoiceVolume;
            }
        }

        static float LinearToDb(float linear) => linear <= 0.0001f ? -80f : Mathf.Log10(linear) * 20f;

        // ----- public API (future gameplay audio) -----

        public void PlaySfx(string file, float volume = 1f)
        {
            var clip = ResolveClip(file);
            if (clip != null && _sfx != null) _sfx.PlayOneShot(clip, Mathf.Clamp01(volume));
        }

        public void PlayMusic(string file)
        {
            if (_music != null) StartCoroutine(LoadAndPlay(file, _music));
        }

        public void StopMusic()
        {
            if (_music != null) _music.Stop();
        }

        // ----- sources -----

        void BuildAudio()
        {
            _voice = NewSource("Voice", false);
            // Music does not loop a single stream: the adaptive layer cycles random standard
            // streams when a track ends (UpdateAudio) — a looping source never "ends".
            _music = NewSource("Music", false);
            _sfx = NewSource("Sfx", false);
        }

        AudioSource NewSource(string sourceName, bool loop)
        {
            var go = new GameObject(sourceName);
            go.transform.SetParent(transform, false);
            var s = go.AddComponent<AudioSource>();
            s.playOnAwake = false; s.loop = loop; s.spatialBlend = 0f;
            return s;
        }

        // ----- cutscene playback (driven by DigglesTclContext.Audio; moved verbatim) -----

        void LateUpdate() => UpdateAudio();

        void UpdateAudio()
        {
            var au = _ctx?.Audio;
            if (au == null) return;
            if (au.VoiceFile == null) _voicePrewarmFor = null;
            if (au.VoiceFile != null
                && !string.Equals(_voicePrewarmFor, au.VoiceFile, System.StringComparison.OrdinalIgnoreCase)
                && (!_clipCache.TryGetValue(au.VoiceFile, out AudioClip? prewarmed) || prewarmed == null))
            {
                _voicePrewarmFor = au.VoiceFile;
                StartCoroutine(PrewarmVoice(au.VoiceFile));
            }
            // One-shot SFX (sound play/playglobal) moved to WorldSoundService: it drains
            // AudioState.PendingOneShots with the native positional falloff (FUN_004d19a0) and
            // never misses multiple shots per frame. This controller keeps voice + music only.
            //
            // Cutscene voice stream. Native seq_audiostream stamps the audio player's absolute stop
            // and start positions before Run (FUN_00667160:160-180); neither the subtitle clock nor
            // a talk animation stops the sample. Drive the presented window from the decompressed
            // clip's media position and mirror its remaining time into the bound subtitle. AudioState
            // keeps a sim-clock countdown only until playback starts, so headless/failed loads finish.
            if (au.VoicePlaying && au.VoiceFile != null && au.VoicePlayCount != _lastVoicePlay)
            {
                _lastVoicePlay = au.VoicePlayCount;
                _voiceLoadedFor = au.VoiceFile;
                CutsceneTrace.Log($"voice play: file={au.VoiceFile} start={au.VoiceStart:F3} " +
                    $"stop={au.VoiceStop:F3} dur={(au.VoiceStop - au.VoiceStart):F3}");
                StartCoroutine(LoadSeekPlay(
                    au.VoiceFile,
                    au.VoiceStart,
                    au.VoiceStop,
                    au.VoicePlayCount,
                    _voice));
            }

            if (_voice != null && au.VoiceStarted && au.VoicePlayCount == _lastVoicePlay)
            {
                if (_voice.isPlaying)
                    au.UpdateVoicePlaybackPosition(PlaybackPosition(_voice));
                else
                    au.CompleteVoicePlayback();
            }

            if (_voice != null && _voice.isPlaying && (!au.VoicePlaying || au.VoiceFile == null))
            {
                // Media window elapsed, or stop/close/sequence teardown interrupted it.
                _voice.Stop();
            }
            // Adaptive music (adaptive_sound, native dispatcher FUN_00642d70): the base layer of
            // the original adaptive engine — on a theme change play its intro stream (fight
            // stream when in fight mode), then cycle random standard streams when a track ends.
            // Partial by design: the native engine also crossfades between streams, switches
            // themes from position markers near the camera (marker/markerenable, radius-gated),
            // and reacts to autofight combat triggers (FUN_004d0360). Those need the
            // marker-proximity tick + a fade mixer (not yet implemented). Registration data for
            // all of it is already faithful in AudioState.
            string? wantTheme = au.MusicPlaying ? (au.CurrentTheme ?? au.PrimaryTheme) : null;
            if (wantTheme != null && _musicLoadedFor != wantTheme
                && au.Themes.TryGetValue(wantTheme, out var theme))
            {
                _musicLoadedFor = wantTheme;
                string? stream = au.Mode == "fight" && theme.Fight != null
                    ? theme.Fight
                    : theme.Intro ?? (theme.Standard.Count > 0 ? theme.Standard[0] : null);
                if (stream != null) StartCoroutine(LoadAndPlayMusic(stream));
            }
            else if (wantTheme != null && _music != null && !_music.isPlaying && !_musicLoading
                     && au.Themes.TryGetValue(wantTheme, out var t2) && t2.Standard.Count > 0)
            {
                // track ended → next random standard stream of the current theme
                StartCoroutine(LoadAndPlayMusic(t2.Standard[_musicRng.Next(t2.Standard.Count)]));
            }
            else if (wantTheme == null && _music != null && _music.isPlaying && _musicLoadedFor != null)
            {
                // adaptive_sound stop
                _music.Stop();
                _musicLoadedFor = null;
            }
            // volfact/basevol duck (0..100 → 0..1): scripts duck the music during cutscene
            // dialogue (`adaptive_sound volfact 40` …). Folded into the source volume each frame
            // on top of the settings channel volume (mixer path leaves source volume at 1).
            if (_music != null && _settings != null)
            {
                float chan = _mixer != null ? 1f : (_settings.Muted ? 0f : _settings.MasterVolume) * _settings.MusicVolume;
                _music.volume = chan * au.MusicBaseVolume;
            }
        }

        IEnumerator LoadAndPlayMusic(string file)
        {
            _musicLoading = true;
            yield return LoadAndPlay(file, _music);
            _musicLoading = false;
        }

        // ----- audio file loading (WAV via WavLoader, MP3 via UnityWebRequest) -----

        AudioClip? ResolveClip(string file)
        {
            if (string.IsNullOrEmpty(file)) return null;
            if (_clipCache.TryGetValue(file, out var cached)) return cached;
            string? path = FindAudioFile(file);
            AudioClip? clip = null;
            if (path != null && path.EndsWith(".wav", System.StringComparison.OrdinalIgnoreCase))
            {
                // Synchronous main-thread file read + decode — hitch candidate on first use.
                using var _perf = Diggles.Runtime.Diagnostics.PerfTrace.Section("audio.wavload", 50);
                try { clip = WavLoader.ToAudioClip(WavLoader.ParseWav(File.ReadAllBytes(path)), Path.GetFileNameWithoutExtension(path)); }
                catch (System.Exception e) { Debug.LogWarning($"[cutscene] WAV load failed {path}: {e.Message}"); }
            }
            _clipCache[file] = clip;   // MP3 resolves async via LoadAndPlay; cache WAV here
            return clip;
        }

        // Like LoadAndPlay but seeks to the marker's absolute start and schedules the absolute stop.
        // `playCount` tags this line: if a newer line superseded it during async decode, abandon.
        IEnumerator LoadSeekPlay(
            string file,
            float startSec,
            float stopSec,
            int playCount,
            AudioSource? src)
        {
            if (src == null) yield break;
            long readyStart = Diggles.Runtime.Diagnostics.HitchFamilyTimingDiag.Start();
            yield return EnsureVoiceClipLoaded(file);
            Diggles.Runtime.Diagnostics.HitchFamilyTimingDiag.Record("voice.play.wait", readyStart);
            if (!_clipCache.TryGetValue(file, out AudioClip? clip) || clip == null) yield break;
            // A newer line, a failed-load fallback expiry, or skip teardown may have superseded this
            // request during decode. Never resurrect its audio after its logical window closed.
            AudioState? audio = _ctx?.Audio;
            if (playCount != _lastVoicePlay || audio == null || audio.VoicePlayCount != playCount
                || !audio.VoicePlaying || audio.VoiceFile == null)
                yield break;
            // Stop first: setting AudioSource.time on an actively-playing source is ignored (the seek
            // silently resets to 0). When a long line was still playing as the next line fired (e.g.
            // the 11s elf "Die" overlapping the gnome "Wir"), the re-seek to 19.8s landed at 0 → the
            // clip played from its start = the wrong/"laughter" audio. Stopping makes the seek stick.
            src.Stop();
            src.clip = clip;
            src.time = Mathf.Clamp(startSec, 0f, Mathf.Max(0f, clip.length - 0.01f));
            src.Play();
            float playbackPosition = PlaybackPosition(src);
            if (!audio.MarkVoiceStarted(playbackPosition))
            {
                src.Stop();
                yield break;
            }

            // SetScheduledEndTime gives the mixer a sample-accurate real-time boundary; the position
            // poll in UpdateAudio mirrors the same boundary into logical/headless state and subtitles.
            float remaining = AudioState.VoiceWindowRemaining(startSec, stopSec, playbackPosition);
            src.SetScheduledEndTime(AudioSettings.dspTime + remaining);
            CutsceneTrace.Log(
                $"voice seek: file={file} -> src.time={src.time:F3} " +
                $"(req {startSec:F3}) cliplen={clip.length:F2}");
        }

        private static float PlaybackPosition(AudioSource source)
        {
            AudioClip? clip = source.clip;
            return clip != null && clip.frequency > 0
                ? source.timeSamples / (float)clip.frequency
                : source.time;
        }

        IEnumerator PrewarmVoice(string file)
        {
            long started = Diggles.Runtime.Diagnostics.HitchFamilyTimingDiag.Start();
            yield return EnsureVoiceClipLoaded(file);
            Diggles.Runtime.Diagnostics.HitchFamilyTimingDiag.Record("voice.prewarm", started);
        }

        IEnumerator EnsureVoiceClipLoaded(string file)
        {
            if (_clipCache.TryGetValue(file, out AudioClip? cached) && cached != null) yield break;
            if (!_clipLoadsInFlight.Add(file))
            {
                while (_clipLoadsInFlight.Contains(file)) yield return null;
                yield break;
            }

            string? path = FindAudioFile(file);
            if (path == null)
            {
                _clipCache[file] = null;
                _clipLoadsInFlight.Remove(file);
                Debug.LogWarning($"[cutscene] voice not found: {file}");
                yield break;
            }
            if (path.EndsWith(".wav", System.StringComparison.OrdinalIgnoreCase))
            {
                ResolveClip(file);
                _clipLoadsInFlight.Remove(file);
                yield break;
            }

            using var req = UnityWebRequestMultimedia.GetAudioClip("file://" + path, AudioType.MPEG);
            if (req.downloadHandler is DownloadHandlerAudioClip handler) handler.streamAudio = false;
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                _clipCache[file] = null;
                Debug.LogWarning($"[cutscene] voice MP3 load failed {path}: {req.error}");
            }
            else
            {
                try
                {
                    using var _decode = Diggles.Runtime.Diagnostics.PerfTrace.Section("audio.voice.decode", 20);
                    _clipCache[file] = DownloadHandlerAudioClip.GetContent(req);
                }
                catch (System.Exception e)
                {
                    _clipCache[file] = null;
                    Debug.LogWarning($"[cutscene] voice MP3 decode failed {path}: {e.Message}");
                }
            }
            _clipLoadsInFlight.Remove(file);
        }

        IEnumerator LoadAndPlay(string file, AudioSource? src)
        {
            if (src == null) yield break;
            string? path = FindAudioFile(file);
            if (path == null) { Debug.LogWarning($"[cutscene] audio not found: {file}"); yield break; }
            if (path.EndsWith(".wav", System.StringComparison.OrdinalIgnoreCase))
            {
                var clip = ResolveClip(file);
                if (clip != null) { src.clip = clip; src.Play(); }
                yield break;
            }
            using var req = UnityWebRequestMultimedia.GetAudioClip("file://" + path, AudioType.MPEG);
            yield return req.SendWebRequest();
            if (req.result == UnityWebRequest.Result.Success)
            {
                src.clip = DownloadHandlerAudioClip.GetContent(req);
                src.Play();
            }
            else Debug.LogWarning($"[cutscene] MP3 load failed {path}: {req.error}");
        }

        // Locate an audio file under data/Sound/** or data/Music/** by name (with/without ext).
        string? FindAudioFile(string file)
        {
            if (_audioPathCache.TryGetValue(file, out string? cachedPath)) return cachedPath;
            var dataRoot = _ctx?.DataRoot;
            if (string.IsNullOrEmpty(dataRoot))
            {
                _audioPathCache[file] = null;
                return null;
            }
            foreach (var sub in new[] { "Sound", "Music" })
            {
                string root = Path.Combine(dataRoot, sub);
                if (!Directory.Exists(root)) continue;
                foreach (var ext in new[] { "", ".wav", ".mp3" })
                {
                    string fname = file.EndsWith(ext, System.StringComparison.OrdinalIgnoreCase) || ext == "" ? file : file + ext;
                    var hits = Directory.GetFiles(root, Path.GetFileName(fname), SearchOption.AllDirectories);
                    if (hits.Length > 0)
                    {
                        _audioPathCache[file] = hits[0];
                        return hits[0];
                    }
                }
            }
            _audioPathCache[file] = null;
            return null;
        }
    }
}
