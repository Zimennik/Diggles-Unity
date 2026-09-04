#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using Diggles.Runtime.Save;
    using NUnit.Framework;

    public sealed class OriginalSavePlayerDocumentTests
    {
        [Test]
        public void ReadsEveryAvailableCurrentSaveIntoTypedPlayerState()
        {
            string root = Path.Combine(OriginalDataTestFixture.DataRoot, "Gamesave");
            Assume.That(Directory.Exists(root),
                $"Original save fixture directory was not found at '{root}'.");
            string[] paths = Directory.GetFiles(root, "*.sav", SearchOption.AllDirectories);
            Assume.That(paths, Is.Not.Empty);

            var reserved = new Dictionary<int, int>();
            var reservedPositions = new Dictionary<string, int>();
            var slotStates = new Dictionary<int, int>();
            var playerOwnerIds = new Dictionary<string, int>();
            var clanOwnerIds = new Dictionary<string, int>();
            var diplomacy = new Dictionary<int, int>();
            var playerNames = new Dictionary<string, int>();
            var secondaryNames = new Dictionary<string, int>();
            var codec = new OriginalSaveArchiveCodec();
            foreach (string path in paths)
            {
                OriginalSaveArchiveDocument document = codec.Read(path);
                OriginalSavePlayerDocument players = document.Players ??
                    throw new AssertionException($"{path} has no typed player document.");
                Assert.That(players.ReservedFraming, Has.Count.EqualTo(32), path);
                Assert.That(players.Slots, Has.Count.EqualTo(8), path);
                OriginalSaveRuntimeSnapshot runtime = new OriginalSaveRuntimeStager().Stage(
                    new OriginalSaveLoadPlanner().Prepare(document));
                Assert.That(runtime.Players.ReservedFraming,
                    Is.EqualTo(players.ReservedFraming), path);
                for (int index = 0; index < players.ReservedFraming.Count; index++)
                {
                    int value = players.ReservedFraming[index];
                    Increment(reserved, value);
                    if (value != 0) Increment(reservedPositions, $"{index}:{value}");
                }

                for (int owner = 0; owner < players.Slots.Count; owner++)
                {
                    OriginalSavePlayerSlotDocument slot = players.Slots[owner];
                    Assert.That(slot.PlayerName.CopyBytesIncludingTerminator()[^1], Is.Zero, path);
                    Assert.That(slot.SecondaryName.CopyBytesIncludingTerminator()[^1], Is.Zero, path);
                    Assert.That(slot.Diplomacy, Has.Count.EqualTo(8), path);
                    Increment(slotStates, slot.SlotState);
                    Increment(playerOwnerIds, $"{owner}:{slot.PlayerOwnerId}");
                    Increment(clanOwnerIds, $"{owner}:{slot.ClanOwnerId}");
                    Increment(playerNames, slot.PlayerName.Text);
                    Increment(secondaryNames, slot.SecondaryName.Text);
                    foreach (int relation in slot.Diplomacy) Increment(diplomacy, relation);
                    OriginalSaveRuntimePlayerSlotSnapshot staged = runtime.Players.Slots[owner];
                    Assert.That(staged.PlayerName, Is.EqualTo(slot.PlayerName.Text), path);
                    Assert.That(staged.SlotState, Is.EqualTo(slot.SlotState), path);
                    Assert.That(staged.PlayerOwnerId, Is.EqualTo(slot.PlayerOwnerId), path);
                    Assert.That(staged.ClanOwnerId, Is.EqualTo(slot.ClanOwnerId), path);
                    Assert.That(staged.SecondaryName, Is.EqualTo(slot.SecondaryName.Text), path);
                    Assert.That(staged.Diplomacy, Is.EqualTo(slot.Diplomacy), path);
                }
            }

            Print("reserved", reserved);
            Print("reservedPosition", reservedPositions);
            Print("slotState", slotStates);
            Print("diplomacy", diplomacy);
            Print("playerOwner", playerOwnerIds);
            Print("clanOwner", clanOwnerIds);
            Print("playerName", playerNames);
            Print("secondaryName", secondaryNames);
            Assert.That(paths.Length, Is.GreaterThanOrEqualTo(98));
        }

        private static void Increment<TKey>(Dictionary<TKey, int> values, TKey key)
            where TKey : notnull
        {
            values.TryGetValue(key, out int count);
            values[key] = count + 1;
        }

        private static void Print<TKey>(string label, Dictionary<TKey, int> values)
            where TKey : notnull
        {
            foreach (KeyValuePair<TKey, int> pair in
                values.OrderBy(pair => pair.Key.ToString(), System.StringComparer.Ordinal))
            {
                TestContext.Out.WriteLine($"{label}='{pair.Key}' count={pair.Value}");
            }
        }
    }
}
