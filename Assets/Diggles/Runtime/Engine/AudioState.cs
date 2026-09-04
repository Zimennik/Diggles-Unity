#nullable enable

namespace Diggles.Runtime
{
    using System;
    using System.Collections.Generic;

    // Headless-testable audio model driven by the engine's three audio command groups:
    // sound — sample SFX/speech (bind name→file, play one-shots, anim-frame SFX)
    // adaptive_sound — streaming theme music (intro/standard[]/fight playlists + markers)
    // seq_audiostream — one cutscene voice MP3 with a play window
    // It records WHAT should be audible (resolvable file, volume, theme, window) — verifiable
    // without an audio device. A Play-mode component loads the real WAV/MP3 (data/Sound, data/
    // Music) into AudioSources from this state. See Docs/CUTSCENE_SYSTEM.md §6.
    //
    // World-sound RE (native CTclGlobalMethods::Sound FUN_00643a60 + CSoundMgr):
    // sound bind <name> <file> — register sample (file checked, FUN_00643a60:250-272)
    // sound anim|animnoseq <db.anim> <frame> <name> [mat] [room] [vol] [freq] [volvar] [freqvar]
    // — per-anim-frame sound table (FUN_004cf770). mat/room are validated then discarded
    // (FUN_004cf770 takes no such params — dead args in the shipped exe). Entries are
    // value-deduped; a frame holds a list of entry indices and playback picks one at
    // random (FUN_005cb9a0:153-174 — how feuerstelle.brennt rotates 8 crackle samples).
    // animnoseq entries are suppressed while a cutscene runs (FUN_005cb9a0:171: skip if
    // global+0x8c sequence flag && entry.noseq).
    // sound play <name> <vol> [{x y z}] — positional one-shot (FUN_004d19a0 falloff)
    // sound playglobal <name> <vol> — non-positional one-shot (FUN_004d1bb0)
    // sound animsound [0|1] — global anim-SFX toggle (byte at global+0x95d8)
    // sound volume [v] — master sample volume
    public sealed class AudioState
    {
        // ---- sound: logical-name → file registry + last one-shot ----
        public readonly Dictionary<string, string> SoundBindings = new();
        public float MasterVolume = 1f;
        public bool AnimSoundEnabled = true;

        public struct OneShot { public string Name; public float Volume; public bool Positional; public UnityEngine.Vector3 Pos; }
        public OneShot? LastOneShot;          // most recent sound play / playglobal
        public int OneShotCount;              // total one-shots fired (test signal)

        // FIFO of pending one-shots for the Play-mode presenter (WorldSoundService). The old
        // LastOneShot polling dropped all but the newest shot when several fired in one render
        // frame (common: two gnomes stepping in the same think pass). LastOneShot/OneShotCount
        // stay maintained for tests/back-compat; the queue is the playback feed.
        public readonly Queue<OneShot> PendingOneShots = new();

        // ---- sound anim: per-anim-frame SFX table ----
        // Entry = the native 0x18-byte CSoundMgr record {sampleIdx, vol, freq, volvar, freqvar,
        // noseq} (FUN_004cf770:86-92; field order from the dedup compare:61-64). The port keys
        // samples by NAME (bind registry) instead of index.
        public struct AnimSoundEntry
        {
            public string Sound;      // bound sample name (entry[0] = sample idx in native)
            public float Volume;      // entry[1] — arg 7, e.g. 0.9
            public float Freq;        // entry[2] — arg 8 (pitch factor; ×22050 Hz, FUN_004d19a0:55)
            public float VolVar;      // entry[3] — arg 9 (random volume add, FUN_005d0820:17-19)
            public float FreqVar;     // entry[4] — arg 10 (random pitch add, symmetric to VolVar)
            public bool NoSeq;        // entry[5] — registered via `sound animnoseq`
        }
        // Value-deduped entry table (native CSoundMgr anim-sound array, FUN_004cf770:50-93).
        public readonly List<AnimSoundEntry> AnimSoundEntries = new();
        // "<db>.<anim>" (e.g. "mann.gehen_loop") → frame → entry indices. The native hangs the
        // frame table off the anim record itself (+0x14, FUN_004cf770:29-48); a flat dictionary
        // is equivalent because the port resolves anims by qualified name. Duplicate registrations
        // append again (native appends without a contains-check — doubles the random-pick weight).
        public readonly Dictionary<string, Dictionary<int, List<int>>> AnimSounds =
            new(System.StringComparer.OrdinalIgnoreCase);

        public void RegisterAnimSound(string anim, int frame, string sound,
            float vol, float freq, float volVar, float freqVar, bool noSeq)
        {
            // dedup identical entries (FUN_004cf770:50-69 compares all 5 values + the noseq byte)
            int idx = -1;
            for (int i = 0; i < AnimSoundEntries.Count; i++)
            {
                var e = AnimSoundEntries[i];
                if (e.Sound == sound && e.Volume == vol && e.Freq == freq
                    && e.VolVar == volVar && e.FreqVar == freqVar && e.NoSeq == noSeq)
                { idx = i; break; }
            }
            if (idx < 0)
            {
                AnimSoundEntries.Add(new AnimSoundEntry
                { Sound = sound, Volume = vol, Freq = freq, VolVar = volVar, FreqVar = freqVar, NoSeq = noSeq });
                idx = AnimSoundEntries.Count - 1;
            }
            if (!AnimSounds.TryGetValue(anim, out var frames))
                AnimSounds[anim] = frames = new Dictionary<int, List<int>>();
            if (!frames.TryGetValue(frame, out var list))
                frames[frame] = list = new List<int>();
            list.Add(idx);   // native appends unconditionally (FUN_004cf770:113-117)
        }

        // Pick the sound to fire when `anim` enters `frame` — one random entry from the frame's
        // list (FUN_005cb9a0:153-166: count==1 → it, else rand%count), with the noseq suppression
        // (FUN_005cb9a0:171: while a sequence runs, noseq entries don't play) and the random
        // volume/pitch variance applied (FUN_005d0820:16-23: vol += rand01 * volvar; rand01 =
        // rand%32768 × DAT_00736f94=1/32768). Returns false when nothing should play.
        public bool TryPickAnimSound(string anim, int frame, bool sequenceRunning,
            System.Random rng, out AnimSoundEntry picked, out float effVolume, out float effFreq)
        {
            picked = default; effVolume = 0f; effFreq = 1f;
            if (!AnimSoundEnabled) return false;   // sound animsound 0 (global+0x95d8, FUN_005cb9a0:138)
            if (!AnimSounds.TryGetValue(anim, out var frames)
                || !frames.TryGetValue(frame, out var list) || list.Count == 0)
                return false;
            int pick = list.Count == 1 ? 0 : rng.Next(list.Count);
            var e = AnimSoundEntries[list[pick]];
            if (sequenceRunning && e.NoSeq) return false;   // FUN_005cb9a0:171
            picked = e;
            effVolume = e.Volume + (e.VolVar != 0f ? (float)rng.NextDouble() * e.VolVar : 0f);
            effFreq = e.Freq + (e.FreqVar != 0f ? (float)rng.NextDouble() * e.FreqVar : 0f);
            return true;
        }

        // ---- adaptive_sound: themes + current playback ----
        public sealed class Theme
        {
            public string? Intro;
            public string? Fight;
            public readonly List<string> Standard = new();
        }
        public readonly Dictionary<string, Theme> Themes = new();
        public string? CurrentTheme;
        public string Mode = "normal";        // normal | fight
        public bool MusicPlaying;
        public float MusicBaseVolume = 1f;    // adaptive_sound volfact/basevol — percent/100
        // adaptive_sound primary <theme> — the fallback theme when no marker applies
        // (FUN_00642d70:272-281 → FUN_004d02f0; it takes a theme name, not a mode).
        public string? PrimaryTheme;
        // adaptive_sound autofight <theme> — the theme the engine switches to on combat
        // (FUN_00642d70:419-426 → FUN_004d0360). Stored for the fight-mode switch.
        public string? AutoFightTheme;

        public struct MusicMarker
        {
            public int ObjectId;
            public string Theme;
            public UnityEngine.Vector3 Pos;
            public float Radius;
        }
        public readonly List<MusicMarker> MusicMarkers = new();
        public bool MarkersEnabled = true;

        public MusicMarker[] CaptureMusicMarkers()
        {
            var result = new MusicMarker[MusicMarkers.Count];
            MusicMarkers.CopyTo(result);
            return result;
        }

        public void RestoreMusicMarkers(IReadOnlyList<MusicMarker> markers)
        {
            ValidateMusicMarkers(markers);
            MusicMarkers.Clear();
            for (int index = 0; index < markers.Count; index++)
                MusicMarkers.Add(markers[index]);
        }

        public static void ValidateMusicMarkers(IReadOnlyList<MusicMarker>? markers)
        {
            if (markers == null) throw new ArgumentNullException(nameof(markers));
            for (int index = 0; index < markers.Count; index++)
            {
                MusicMarker marker = markers[index];
                if (marker.ObjectId < -1 || marker.Theme == null ||
                    !Finite(marker.Pos.x) || !Finite(marker.Pos.y) || !Finite(marker.Pos.z) ||
                    !Finite(marker.Radius) || marker.Radius < 0f)
                {
                    throw new ArgumentException("An adaptive-sound marker contains invalid native state.", nameof(markers));
                }
            }
        }

        // ---- seq_audiostream: cutscene voice ----
        public string? VoiceFile;             // opened stream file (null = closed)
        public bool VoicePlaying;
        public float VoiceStart, VoiceStop, VoiceRemaining;
        public int VoicePlayCount;            // ++ per PlayVoice — lets the presenter restart
                                              // the same file at a new [start,stop] window
                                              // (each cutscene line is a segment of one MP3).
        private DialogueState? _voiceDialogue;
        private int _voiceDialoguePlayCount = -1;
        private bool _voiceDialogueBindingPending;

        static bool Finite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);

        Theme GetTheme(string name)
        {
            if (!Themes.TryGetValue(name, out var t)) { t = new Theme(); Themes[name] = t; }
            return t;
        }

        // ----- sound -----
        public void Bind(string logic, string file) => SoundBindings[logic] = file;
        public void PlayOneShot(string name, float vol, bool positional, UnityEngine.Vector3 pos)
        {
            var shot = new OneShot { Name = name, Volume = vol, Positional = positional, Pos = pos };
            LastOneShot = shot;
            OneShotCount++;
            PendingOneShots.Enqueue(shot);
            if (PendingOneShots.Count > 64) PendingOneShots.Dequeue();   // headless safety (no drainer)
        }
        public string? ResolveSound(string name) => SoundBindings.TryGetValue(name, out var f) ? f : null;

        // ----- adaptive_sound -----
        public void SetIntroStream(string theme, string file)  => GetTheme(theme).Intro = file;
        public void SetFightStream(string theme, string file)  => GetTheme(theme).Fight = file;
        public void AddStandardStream(string theme, string file) => GetTheme(theme).Standard.Add(file);
        // Marker radius: optional 4th arg (FUN_00642d70:291-294); default 50.0 (the markerobj
        // path stamps 0x42480000 = 50.0f, FUN_00642d70:521).
        public void AddMarker(string theme, UnityEngine.Vector3 pos, float radius = 50f)
            => MusicMarkers.Add(new MusicMarker
            {
                ObjectId = -1,
                Theme = theme,
                Pos = pos,
                Radius = radius,
            });
        public void AddObjectMarker(string theme, int objectId)
            => MusicMarkers.Add(new MusicMarker
            {
                ObjectId = objectId,
                Theme = theme,
                Pos = UnityEngine.Vector3.zero,
                Radius = 50f,
            });
        // delmarker <pos> — removes the marker found at pos if within 1.0 (FUN_00642d70:303-323;
        // the 1.0 gate is DAT_00737138 at:317; distance is ground-plane x/y).
        public void DelMarker(UnityEngine.Vector3 pos)
        {
            int best = -1; float bestD = float.MaxValue;
            for (int i = 0; i < MusicMarkers.Count; i++)
            {
                float dx = MusicMarkers[i].Pos.x - pos.x, dy = MusicMarkers[i].Pos.y - pos.y;
                float d = UnityEngine.Mathf.Sqrt(dx * dx + dy * dy);
                if (d < bestD) { bestD = d; best = i; }
            }
            if (best >= 0 && bestD < 1f) MusicMarkers.RemoveAt(best);
        }
        public void DelObjectMarkers(int objectId) =>
            MusicMarkers.RemoveAll(marker => marker.ObjectId == objectId);

        // The native adaptive tick selects the nearest marker whose ground-plane distance is
        // strictly inside its radius (FUN_004d10f0:98-120), then sends that marker's theme to
        // the adaptive manager (FUN_004d1570:104-133). Save activation uses this one-shot subset
        // until the presenter's continuous marker-proximity tick is implemented.
        public bool ArmThemeFromMarkers(UnityEngine.Vector3 focus)
        {
            if (!MarkersEnabled) return false;

            int selectedIndex = -1;
            float selectedDistanceSquared = float.MaxValue;
            for (int index = 0; index < MusicMarkers.Count; index++)
            {
                MusicMarker marker = MusicMarkers[index];
                if (marker.Pos.x <= 0f || marker.Pos.y <= 0f || marker.Radius <= 0f) continue;

                float dx = marker.Pos.x - focus.x;
                float dy = marker.Pos.y - focus.y;
                float distanceSquared = dx * dx + dy * dy;
                float radiusSquared = marker.Radius * marker.Radius;
                if (distanceSquared >= radiusSquared ||
                    distanceSquared >= selectedDistanceSquared)
                {
                    continue;
                }

                selectedIndex = index;
                selectedDistanceSquared = distanceSquared;
            }

            if (selectedIndex < 0) return false;

            string selectedTheme = MusicMarkers[selectedIndex].Theme;
            bool currentThemeArmed = MusicPlaying && string.Equals(
                CurrentTheme,
                selectedTheme,
                StringComparison.OrdinalIgnoreCase);
            bool primaryThemeSelected = string.Equals(
                PrimaryTheme,
                selectedTheme,
                StringComparison.OrdinalIgnoreCase);
            if (currentThemeArmed && primaryThemeSelected) return false;

            PrimaryTheme = selectedTheme;
            if (currentThemeArmed) return false;

            ChangeTheme(selectedTheme);
            return true;
        }

        public void ChangeTheme(string theme) { CurrentTheme = theme; MusicPlaying = true; }

        // ----- seq_audiostream -----
        public bool OpenVoice(string file) { VoiceFile = file; return true; }
        // Native seq_audiostream sets the stream's absolute stop and start positions, then runs it
        // (FUN_00667160:160-180). Once a presenter starts, its media position therefore owns this
        // window. Before that, Tick remains a finite sim-clock fallback for headless runs and failed
        // loads. A marker-backed subtitle binds to the same remaining value so both end together.
        public bool VoiceStarted;

        public float VoiceDuration => VoiceWindowRemaining(VoiceStart, VoiceStop, VoiceStart);

        public static float VoiceWindowRemaining(float start, float stop, float playbackPosition)
        {
            if (!Finite(start) || !Finite(stop) || !Finite(playbackPosition) || stop <= start)
                return 0f;
            return UnityEngine.Mathf.Clamp(stop - playbackPosition, 0f, stop - start);
        }

        public void PlayVoice(float start, float stop)
        {
            ReleaseVoiceDialogue(true);
            VoiceStarted = false;
            VoiceStart = start;
            VoiceStop = stop;
            VoiceRemaining = VoiceWindowRemaining(start, stop, start);
            VoicePlaying = VoiceRemaining > 0f;
            VoicePlayCount++;
            _voiceDialogueBindingPending = VoicePlaying;
        }

        public bool BindVoiceDialogue(DialogueState dialogue, float duration)
        {
            if (dialogue == null) throw new ArgumentNullException(nameof(dialogue));
            if (!_voiceDialogueBindingPending) return false;
            _voiceDialogueBindingPending = false;
            ReleaseVoiceDialogue(false);
            if (!VoicePlaying || UnityEngine.Mathf.Abs(duration - VoiceDuration) > 0.001f)
                return false;

            _voiceDialogue = dialogue;
            _voiceDialoguePlayCount = VoicePlayCount;
            dialogue.SetVoiceDrivenSpeechRemaining(VoiceRemaining);
            return true;
        }

        public bool MarkVoiceStarted(float playbackPosition)
        {
            if (!VoicePlaying) return false;
            VoiceStarted = true;
            SetVoiceRemaining(VoiceWindowRemaining(VoiceStart, VoiceStop, playbackPosition));
            return VoicePlaying;
        }

        public void UpdateVoicePlaybackPosition(float playbackPosition)
        {
            if (!VoicePlaying || !VoiceStarted) return;
            SetVoiceRemaining(VoiceWindowRemaining(VoiceStart, VoiceStop, playbackPosition));
        }

        public void CompleteVoicePlayback() => SetVoiceRemaining(0f);

        public void StopVoice()
        {
            VoicePlaying = false;
            VoiceStarted = false;
            VoiceRemaining = 0f;
            _voiceDialogueBindingPending = false;
            ReleaseVoiceDialogue(true);
        }

        public void CloseVoice() { StopVoice(); VoiceFile = null; }

        public void Tick(double dt)
        {
            _voiceDialogueBindingPending = false;
            if (VoicePlaying && !VoiceStarted && VoiceRemaining > 0f)
            {
                SetVoiceRemaining(VoiceRemaining - (float)dt);
            }
        }

        private void SetVoiceRemaining(float remaining)
        {
            VoiceRemaining = UnityEngine.Mathf.Max(0f, remaining);
            if (_voiceDialogue != null && _voiceDialoguePlayCount == VoicePlayCount)
            {
                _voiceDialogue.SetVoiceDrivenSpeechRemaining(VoiceRemaining);
            }

            if (VoiceRemaining > 0f) return;
            VoicePlaying = false;
            VoiceStarted = false;
            ReleaseVoiceDialogue(false);
        }

        private void ReleaseVoiceDialogue(bool complete)
        {
            if (complete && _voiceDialogue != null) _voiceDialogue.CompleteSpeech();
            _voiceDialogue = null;
            _voiceDialoguePlayCount = -1;
        }
    }
}
