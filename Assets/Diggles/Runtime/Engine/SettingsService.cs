#nullable enable

namespace Diggles.Runtime
{
    using System;
    using UnityEngine;

    // Central user-settings service. Audio volumes are the first category; graphics / gameplay
    // settings can be added here later behind the same load-on-construct / persist-on-change /
    // Changed-event contract. Backed by PlayerPrefs (each setter persists immediately).
    //
    // Plain C# (no MonoBehaviour) — registered as a DI singleton and injected into the
    // AudioController, which reads the volumes and re-applies them to the AudioMixer on Changed.
    // Volumes are linear 0..1 (the controller converts to dB for the mixer).
    public sealed class SettingsService
    {
        const string KeyMaster = "diggles.audio.master";
        const string KeyMusic = "diggles.audio.music";
        const string KeySfx = "diggles.audio.sfx";
        const string KeyVoice = "diggles.audio.voice";
        const string KeyMuted = "diggles.audio.muted";

        public float MasterVolume { get; private set; }
        public float MusicVolume { get; private set; }
        public float SfxVolume { get; private set; }
        public float VoiceVolume { get; private set; }
        public bool Muted { get; private set; }

        // Raised after any setting changes AND has been persisted. The AudioController subscribes
        // to re-apply volumes to the mixer.
        public event Action? Changed;

        public SettingsService() => Load();

        public void SetMasterVolume(float v) => MasterVolume = Persist(KeyMaster, v);
        public void SetMusicVolume(float v) => MusicVolume = Persist(KeyMusic, v);
        public void SetSfxVolume(float v) => SfxVolume = Persist(KeySfx, v);
        public void SetVoiceVolume(float v) => VoiceVolume = Persist(KeyVoice, v);

        public void SetMuted(bool muted)
        {
            if (this.Muted == muted) return;
            this.Muted = muted;
            PlayerPrefs.SetInt(KeyMuted, muted ? 1 : 0);
            PlayerPrefs.Save();
            this.Changed?.Invoke();
        }

        // Clamp, persist, notify; returns the stored value for assignment to the property.
        float Persist(string key, float value)
        {
            value = Mathf.Clamp01(value);
            PlayerPrefs.SetFloat(key, value);
            PlayerPrefs.Save();
            this.Changed?.Invoke();
            return value;
        }

        void Load()
        {
            this.MasterVolume = PlayerPrefs.GetFloat(KeyMaster, 1f);
            this.MusicVolume = PlayerPrefs.GetFloat(KeyMusic, 1f);
            this.SfxVolume = PlayerPrefs.GetFloat(KeySfx, 1f);
            this.VoiceVolume = PlayerPrefs.GetFloat(KeyVoice, 1f);
            this.Muted = PlayerPrefs.GetInt(KeyMuted, 0) != 0;
        }
    }
}
