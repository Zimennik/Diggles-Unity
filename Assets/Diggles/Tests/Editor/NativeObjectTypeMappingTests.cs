#nullable enable

namespace Diggles.Tests
{
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;

    // Locks in the manual test- crash-root fix: a fresh `new <class>` must carry the
    // native object TYPE derived from the class kind, because the save format keys the
    // type-dependent body extensions (family/talkissue/planner/CProd) off it and the original's
    // loader allocates those sub-objects PER CLASS — a mismatched type crashes it.
    public sealed class NativeObjectTypeMappingTests
    {
        [TestCase("gnome", 1)]
        [TestCase("monster", 2)]
        [TestCase("material", 3)]
        [TestCase("tool", 4)]
        [TestCase("production", 5)]
        [TestCase("energy", 6)]
        [TestCase("protection", 7)]
        [TestCase("transport", 9)]
        [TestCase("elevator", 10)]
        [TestCase("baby", 11)]
        [TestCase("info", 12)]
        [TestCase("dummy", 0)]
        [TestCase("food", 0)]
        [TestCase("wood", 0)]
        [TestCase(null, 0)]
        [TestCase("", 0)]
        public void KindMapsToNativeObjectType(string? kind, int expected)
        {
            Assert.That(DigglesTclEngine.NativeObjectTypeFromKind(kind), Is.EqualTo(expected));
        }
    }
}
