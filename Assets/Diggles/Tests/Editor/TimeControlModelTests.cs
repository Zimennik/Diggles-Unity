#nullable enable

namespace Diggles.Tests
{
    using Diggles.Runtime;
    using NUnit.Framework;

    // The fast-forward control's pure logic (TimeControlModel). The service wiring (input, the
    // ctx.IsStoryMgr/SequenceActive gates, the animator + accumulator scaling) is Play-verified.
    public sealed class TimeControlModelTests
    {
        [Test]
        public void Levels_AreTheRequestedLadder()
            => CollectionAssert.AreEqual(new[] { 1, 2, 4, 6, 8, 10 }, TimeControlModel.Levels);

        [Test]
        public void Bump_CyclesTheLadderAndClampsBothEnds()
        {
            var m = new TimeControlModel();
            Assert.AreEqual(1, m.Speed);
            Assert.IsFalse(m.TryBump(false), "at x1 already — cannot go lower");
            foreach (var e in new[] { 2, 4, 6, 8, 10 })
            {
                Assert.IsTrue(m.TryBump(true));
                Assert.AreEqual(e, m.Speed);
            }
            Assert.IsFalse(m.TryBump(true), "at x10 already — cannot go higher");
            Assert.AreEqual(10, m.Speed);
            Assert.IsTrue(m.TryBump(false));
            Assert.AreEqual(8, m.Speed);
        }

        [Test]
        public void EffectiveSpeed_AppliesOnlyInGameplayOutsideCutscenes()
        {
            var m = new TimeControlModel();
            m.TryBump(true); m.TryBump(true);   // → x4
            Assert.AreEqual(4, m.EffectiveSpeed(inGame: true, cutscene: false), "gameplay → the selected level");
            Assert.AreEqual(1, m.EffectiveSpeed(inGame: false, cutscene: false), "menu backdrop → x1");
            Assert.AreEqual(1, m.EffectiveSpeed(inGame: true, cutscene: true), "cutscene → x1 (auto-reset before it)");
            Assert.AreEqual(1, m.EffectiveSpeed(inGame: false, cutscene: true), "menu cutscene → x1");
            Assert.AreEqual(4, m.Speed, "gating must NOT mutate the stored selection");
        }

        [Test]
        public void Reset_DropsToX1_ReportsWhetherItChanged()
        {
            var m = new TimeControlModel();
            Assert.IsFalse(m.Reset(), "already x1 — no change");
            m.TryBump(true);
            Assert.AreEqual(2, m.Speed);
            Assert.IsTrue(m.Reset(), "cutscene start resets the level");
            Assert.AreEqual(1, m.Speed);
        }
    }
}
