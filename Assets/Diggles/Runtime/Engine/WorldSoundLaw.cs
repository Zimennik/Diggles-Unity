#nullable enable

namespace Diggles.Runtime
{
    using UnityEngine;
    using Diggles.Runtime.World;

    // Pure, headless-testable native sound laws (positional falloff + the voice-limit laws),
    // extracted from the shipped Diggles.exe so the Play-mode WorldSoundService is a thin shell around them.
    // Every falloff constant is byte-verified from Diggles.exe.rdata — full RE + citations in
    // Docs/SOUND_SYSTEM_RE.md. No Unity scene dependency beyond Mathf and the plain-C#
    // FogOfWarState, so EditMode tests pin the laws directly (WorldSoundLawTests).
    public static class WorldSoundLaw
    {
        // ---- FUN_004d19a0 positional falloff constants (all confirmed vs.rdata) ----
        public const float ZoomGain = 0.30f;   // DAT_00737060 — zoom slope AND att offset
        public const float DistK    = 0.25f;   // DAT_00737058
        public const float PanPerDx = 0.03f;   // DAT_00737428=3.0 → DSound ±10000 → panStereo ±1
        public const float GainOne  = 1.00f;   // DAT_00737138 — gain numerator / zf base

        // ---- volume gates ----
        public const float MinPlayGain = 0.10f;  // FUN_004d19a0:43 (10 < round(vol*gain*100))
        public const float MinAnimVol  = 0.20f;  // FUN_005d0820:26 (0x14 < round(vol*100))

        // ---- limit laws ----
        // Native refuses a new sample when the active-voice list exceeds 5 (FUN_00454f40:19,
        // `5 < *(dev+0x18) -> return 0`) → at most ~6 concurrent sample voices, and it never steals.
        public const int MaxVoices = 6;
        // Positional plays require the local player's fog byte >= 100 at the sound tile
        // (FUN_004d19a0:44 -> FUN_005ab220, gate `99 < result`).
        public const int FogAudibleThreshold = 100;

        // (gain, pan) for a sound at native ground (nx,ny) heard from the listener = camera focus +
        // zoom. FUN_004d19a0:22-42. Ground-plane only — native ignores z/depth. Returns the raw
        // gain multiplier (caller does vol*gain and the >MinPlayGain gate) and panStereo in [-1,1].
        public static void Attenuate(float nx, float ny, float focusX, float focusY, float zoom,
                                     out float gain, out float pan)
        {
            float dx = nx - focusX;
            float dy = ny - focusY;
            float zf = (zoom - GainOne) * ZoomGain + GainOne;
            float dist = Mathf.Sqrt(dx * dx + dy * dy);                 // DAT_00736f78 add = 0.0
            float att = Mathf.Max(0f, dist * DistK * zf - ZoomGain);
            gain = (GainOne / (att + GainOne)) / zf;
            pan = Mathf.Clamp(dx * PanPerDx, -1f, 1f);
        }

        // Native tile-visibility gate for a positional play (FUN_004d19a0:44 -> FUN_005ab220 >= 100):
        // the MAX fog byte over the 2x2 tile corner at the sound position must clear the threshold.
        // Out-of-map samples read 0 (FogAt clamps), matching the native bounds `return 0`. The lane
        // is the local player (native `*(world+0x64)`); a tile fades below the threshold ~15 s after
        // the last revealer, so off-screen/unexplored environment SFX go silent.
        public static bool FogAudible(FogOfWarState fog, int player, float nx, float ny)
        {
            int ix = Mathf.RoundToInt(nx), iy = Mathf.RoundToInt(ny);
            int m = fog.FogAt(ix, iy, player);
            int b = fog.FogAt(ix, iy + 1, player);     if (b > m) m = b;
            b = fog.FogAt(ix + 1, iy, player);         if (b > m) m = b;
            b = fog.FogAt(ix + 1, iy + 1, player);     if (b > m) m = b;
            return m >= FogAudibleThreshold;
        }

        public enum VoiceAcquire { ReuseFree, CreateNew, Drop }

        // Voice-pool allocation policy (FUN_00454f40:19 + the FUN_004cfcb0 pool): reuse an idle
        // source; else grow to the cap; else drop the new sound (native `return 0` — never steals).
        public static VoiceAcquire Acquire(int freeCount, int poolCount, int maxVoices)
            => freeCount > 0 ? VoiceAcquire.ReuseFree
             : poolCount < maxVoices ? VoiceAcquire.CreateNew
             : VoiceAcquire.Drop;
    }
}
