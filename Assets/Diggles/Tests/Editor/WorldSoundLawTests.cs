#nullable enable

namespace Diggles.Tests
{
    using Diggles.Runtime;
    using Diggles.Runtime.World;
    using NUnit.Framework;

    // Pure native SOUND laws (positional falloff + the LIMIT laws that fix manual test- items 20
    // & 21). Headless — no audio device. Full RE + citations: Docs/SOUND_SYSTEM_RE.md.
    //
    // * WorldSoundLaw.Attenuate = FUN_004d19a0 falloff (constants byte-verified vs.rdata) —
    // regression pins so the shape can never silently drift;
    // * WorldSoundLaw.Acquire = FUN_00454f40:19 voice cap (~6, drop-new) — item 21;
    // * WorldSoundLaw.FogAudible = FUN_005ab220 fog-visibility cull (2x2 MAX >= 100) — items 20/21.
    //
    // FAIL-FIRST: against the port's prior behaviour (no cull, pool of 16) the Fog_* and Acquire_*
    // asserts are RED — the port played sounds this law culls/drops.
    public sealed class WorldSoundLawTests
    {
        // ------------------------------------------------------------------
        // FUN_004d19a0 positional falloff — pinned at known distances
        // ------------------------------------------------------------------

        [Test]
        public void Attenuate_AtListener_FullGain_CenterPan()
        {
            // dx=dy=0 → att=0 → gain = 1/zf, zf=(1.35-1)*0.3+1 = 1.105
            WorldSoundLaw.Attenuate(100f, 50f, 100f, 50f, 1.35f, out float g, out float pan);
            Assert.AreEqual(1f / 1.105f, g, 1e-3f);
            Assert.AreEqual(0f, pan, 1e-4f);
        }

        [Test]
        public void Attenuate_FallsOffWithDistance_AndPansRight()
        {
            // 10 tiles east of focus at zoom 1.35
            WorldSoundLaw.Attenuate(110f, 50f, 100f, 50f, 1.35f, out float g, out float pan);
            const float zf = 1.105f;
            float att = 10f * 0.25f * zf - 0.30f;          // 2.4625
            float expect = (1f / (att + 1f)) / zf;         // ~0.262
            Assert.AreEqual(expect, g, 1e-3f);
            Assert.Less(g, 0.5f, "clearly attenuated at 10 tiles");
            Assert.AreEqual(0.3f, pan, 1e-3f);             // dx*0.03 = 10*0.03, east → right
        }

        [Test]
        public void Attenuate_PanClampsToUnit()
        {
            WorldSoundLaw.Attenuate(1000f, 50f, 100f, 50f, 1f, out _, out float panR);
            WorldSoundLaw.Attenuate(-1000f, 50f, 100f, 50f, 1f, out _, out float panL);
            Assert.AreEqual(1f, panR, 1e-4f);
            Assert.AreEqual(-1f, panL, 1e-4f);
        }

        [Test]
        public void Attenuate_ConstantsMatchBinary()
        {
            Assert.AreEqual(0.30f, WorldSoundLaw.ZoomGain, 1e-6f);   // DAT_00737060
            Assert.AreEqual(0.25f, WorldSoundLaw.DistK, 1e-6f);      // DAT_00737058
            Assert.AreEqual(0.03f, WorldSoundLaw.PanPerDx, 1e-6f);   // DAT_00737428=3.0 → /100
            Assert.AreEqual(0.10f, WorldSoundLaw.MinPlayGain, 1e-6f);// FUN_004d19a0:43
            Assert.AreEqual(0.20f, WorldSoundLaw.MinAnimVol, 1e-6f); // FUN_005d0820:26
        }

        // ------------------------------------------------------------------
        // FUN_00454f40:19 voice cap (the "too many simultaneous sounds" law)
        // ------------------------------------------------------------------

        [Test]
        public void Acquire_ReusesFree_ThenGrows_ThenDropsAtCap()
        {
            // an idle source exists → reuse it
            Assert.AreEqual(WorldSoundLaw.VoiceAcquire.ReuseFree, WorldSoundLaw.Acquire(1, 3, 6));
            // none idle, pool below cap → allocate one more
            Assert.AreEqual(WorldSoundLaw.VoiceAcquire.CreateNew, WorldSoundLaw.Acquire(0, 3, 6));
            // none idle, pool AT cap → DROP the new sound (native return 0 — never steals)
            Assert.AreEqual(WorldSoundLaw.VoiceAcquire.Drop, WorldSoundLaw.Acquire(0, 6, 6));
        }

        [Test]
        public void Cap_IsSix_MatchingNativeGate()
            => Assert.AreEqual(6, WorldSoundLaw.MaxVoices);   // FUN_00454f40:19 (5 < count → refuse)

        // ------------------------------------------------------------------
        // FUN_005ab220 fog-visibility cull — off-screen/faded → silent
        // ------------------------------------------------------------------

        static FogOfWarState OpenFog(int w = 40, int h = 40)
            => new FogOfWarState(w, h, (x, y) => 0);   // all-open terrain (fill nibble 0)

        [Test]
        public void Fog_UnseenTile_Culled()
        {
            var fog = OpenFog();
            // never revealed → byte 0 → below 100 → the native would not play it
            Assert.IsFalse(WorldSoundLaw.FogAudible(fog, 0, 10.2f, 8.4f));
        }

        [Test]
        public void Fog_RevealedTile_Audible()
        {
            var fog = OpenFog();
            fog.SetFowBegin(20, 0);                       // rows [5,20) permanently visible (0xFF)
            Assert.IsTrue(WorldSoundLaw.FogAudible(fog, 0, 10f, 8f), "inside the revealed band");
            Assert.IsFalse(WorldSoundLaw.FogAudible(fog, 0, 10f, 25f), "below the band → culled");
            // per-player: player 1 never saw it
            Assert.IsFalse(WorldSoundLaw.FogAudible(fog, 1, 10f, 8f));
        }

        [Test]
        public void Fog_SamplesMaxOver2x2Corner()
        {
            var fog = OpenFog();
            fog.SetFowBegin(20, 0);                       // rows [5,20) visible; row 4 is NOT
            // rounds to (10,4): 2x2 covers row 5 (visible) via the +1 neighbour → audible by MAX
            Assert.IsTrue(WorldSoundLaw.FogAudible(fog, 0, 10f, 4f));
            // rounds to (10,3): 2x2 = rows 3,4 — neither visible → culled
            Assert.IsFalse(WorldSoundLaw.FogAudible(fog, 0, 10f, 3f));
        }

        [Test]
        public void Fog_FadesToSilence_WhenNoRevealerStays()
        {
            var fog = OpenFog();
            // radial reveal a disc around tile (10,8) (fine units = tile*8) → centre goes 0xFF
            fog.RemoveFog(80, 64, 24, 24, 0, 0f, 1f);
            Assert.IsTrue(WorldSoundLaw.FogAudible(fog, 0, 10f, 8f), "just revealed → audible");
            // no revealer nearby: the fade list decays −4/update; after ~39 the byte drops below 100
            for (int i = 0; i < 45; i++) fog.BeginUpdate(0);
            Assert.IsFalse(WorldSoundLaw.FogAudible(fog, 0, 10f, 8f), "faded area goes silent");
        }
    }
}
