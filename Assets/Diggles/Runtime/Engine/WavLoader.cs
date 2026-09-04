#nullable enable

namespace Diggles.Runtime
{
    using System;
    using UnityEngine;

    // Runtime loader for the original game's audio: standard RIFF/WAVE PCM.wav (data/Sound/**,
    // mostly mono 22050 Hz, 8- or 16-bit). ParseWav is pure (no Unity) so it's verifiable
    // headless; ToAudioClip wraps it into an AudioClip for a Play-mode AudioSource. Unknown
    // RIFF chunks (e.g. a stray "PAD ") are skipped. MP3 (music / cutscene voice) is decoded
    // at Play-time via UnityWebRequestMultimedia (no headless decode). See Docs/CUTSCENE_SYSTEM.md §6.
    public static class WavLoader
    {
        public struct WavData
        {
            public int Channels;
            public int SampleRate;
            public float[] Samples;     // interleaved, normalised to [-1, 1]
            public int FrameCount => Channels > 0 ? Samples.Length / Channels : 0;
            public float Duration => SampleRate > 0 ? (float)FrameCount / SampleRate : 0f;
        }

        // Parse a RIFF/WAVE PCM byte buffer. Throws on a non-WAVE / unsupported (non-PCM,
        // non-8/16/24/32-bit) buffer.
        public static WavData ParseWav(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 44)
                throw new Exception("WAV too short");
            if (bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F'
                || bytes[8] != 'W' || bytes[9] != 'A' || bytes[10] != 'V' || bytes[11] != 'E')
                throw new Exception("not a RIFF/WAVE file");

            int channels = 1, sampleRate = 22050, bits = 16, fmt = 1;
            int dataOff = -1, dataLen = 0;

            int p = 12;   // after "RIFF"<size>"WAVE"
            while (p + 8 <= bytes.Length)
            {
                string id = $"{(char)bytes[p]}{(char)bytes[p + 1]}{(char)bytes[p + 2]}{(char)bytes[p + 3]}";
                int len = bytes[p + 4] | (bytes[p + 5] << 8) | (bytes[p + 6] << 16) | (bytes[p + 7] << 24);
                int body = p + 8;
                if (id == "fmt ")
                {
                    fmt = bytes[body] | (bytes[body + 1] << 8);
                    channels = bytes[body + 2] | (bytes[body + 3] << 8);
                    sampleRate = bytes[body + 4] | (bytes[body + 5] << 8) | (bytes[body + 6] << 16) | (bytes[body + 7] << 24);
                    bits = bytes[body + 14] | (bytes[body + 15] << 8);
                }
                else if (id == "data")
                {
                    dataOff = body;
                    dataLen = Math.Min(len, bytes.Length - body);
                }
                p = body + len + (len & 1);   // chunks are word-aligned
            }

            if (fmt != 1 && fmt != 0xFFFE) throw new Exception($"unsupported WAV format {fmt} (only PCM)");
            if (dataOff < 0) throw new Exception("no data chunk");
            if (channels < 1) channels = 1;

            int bytesPerSample = Math.Max(1, bits / 8);
            int n = dataLen / bytesPerSample;
            var samples = new float[n];
            for (int s = 0; s < n; s++)
            {
                int o = dataOff + s * bytesPerSample;
                switch (bits)
                {
                    case 8:  samples[s] = (bytes[o] - 128) / 128f; break;                       // unsigned
                    case 16: samples[s] = (short)(bytes[o] | (bytes[o + 1] << 8)) / 32768f; break;
                    case 24:
                    {
                        int v = bytes[o] | (bytes[o + 1] << 8) | (bytes[o + 2] << 16);
                        if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
                        samples[s] = v / 8388608f; break;
                    }
                    case 32: samples[s] = (int)(bytes[o] | (bytes[o + 1] << 8) | (bytes[o + 2] << 16) | (bytes[o + 3] << 24)) / 2147483648f; break;
                    default: throw new Exception($"unsupported bit depth {bits}");
                }
            }
            return new WavData { Channels = channels, SampleRate = sampleRate, Samples = samples };
        }

        public static AudioClip ToAudioClip(WavData w, string name)
        {
            var clip = AudioClip.Create(name, Mathf.Max(1, w.FrameCount), Mathf.Max(1, w.Channels), Mathf.Max(8000, w.SampleRate), false);
            clip.SetData(w.Samples, 0);
            return clip;
        }
    }
}
