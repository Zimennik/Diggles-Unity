#nullable enable

namespace Diggles.Runtime
{
    using UnityEngine;

    // Lightweight on/off trace for debugging cutscenes in Play: the native leaf commands log
    // what they were ASKED to do (camera focus, speech text, sound, fade, …) and the presenter
    // logs what it RENDERED — so we can cross-check "what should happen" against what's on screen.
    // Enabled by the CutsceneTester / PlayCutscene. See Docs/CUTSCENE_SYSTEM.md.
    public static class CutsceneTrace
    {
        public static bool Enabled;
        public static void Log(string msg)
        {
            if (!Enabled) return;
            Debug.Log("[cutscene] " + msg);
            // Also mirror into the dumpable file log if one is open (DigglesDebugLog.Begin), so a
            // single diggles_cutscene.log captures the whole run for the player to send over.
            Diggles.Runtime.Diagnostics.DigglesDebugLog.Log("[cutscene] " + msg);
        }
    }
}
