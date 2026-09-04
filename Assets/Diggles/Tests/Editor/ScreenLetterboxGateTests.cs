#nullable enable

namespace Diggles.Tests
{
    using Diggles.Runtime;
    using NUnit.Framework;

    // S02-008: the cinematic letterbox bars must NOT appear over the main-menu backdrop's ambient
    // cutscenes. ScreenManager.ShouldShowLetterbox is the pure gate the LateUpdate driver calls; it
    // mirrors DigglesWorldRuntime's canonical menu signal (_atMenu = OriginalSaveState==null && !IsStoryMgr).
    public sealed class ScreenLetterboxGateTests
    {
        [Test]
        public void MenuBackdropCutscene_NoBars()
            // Menu backdrop: an ambient cutscene runs (sequenceActive) but it is neither a StoryMgr
            // world nor a loaded save → the only case that must suppress the bars.
            => Assert.IsFalse(ScreenManager.ShouldShowLetterbox(
                sequenceActive: true, isStoryMgr: false, hasOriginalSave: false));

        [Test]
        public void CampaignCutscene_ShowsBars()
            // Real campaign cutscene → StoryMgr world.
            => Assert.IsTrue(ScreenManager.ShouldShowLetterbox(
                sequenceActive: true, isStoryMgr: true, hasOriginalSave: false));

        [Test]
        public void LoadedSaveCutscene_ShowsBars()
            // Tutorial / any world loaded from a.sav is real gameplay even when IsStoryMgr is false.
            => Assert.IsTrue(ScreenManager.ShouldShowLetterbox(
                sequenceActive: true, isStoryMgr: false, hasOriginalSave: true));

        [Test]
        public void NoCutscene_NeverShowsBars(
            [Values(false, true)] bool isStoryMgr,
            [Values(false, true)] bool hasOriginalSave)
            // No active sequence → never any bars, regardless of world kind.
            => Assert.IsFalse(ScreenManager.ShouldShowLetterbox(
                sequenceActive: false, isStoryMgr, hasOriginalSave));
    }
}
