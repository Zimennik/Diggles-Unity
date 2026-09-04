#nullable enable

namespace Diggles.Tests
{
    using System;
    using System.IO;
    using Diggles.Runtime.UI;
    using NUnit.Framework;

    public sealed class SaveGamePanelTests
    {
        private string _directory = "";

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(
                Path.GetTempPath(),
                $"diggles-panel-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }

        [Test]
        public void ClassifiesEntryKindsByPrefix()
        {
            Assert.That(SaveGamePanelController.ClassifyEntry("quick_Quicksave_20260717_0102.sav"),
                Is.EqualTo(SaveGamePanelModel.EntryKind.Quick));
            Assert.That(SaveGamePanelController.ClassifyEntry("auto_Autosave_20260717_0102.sav"),
                Is.EqualTo(SaveGamePanelModel.EntryKind.Auto));
            Assert.That(SaveGamePanelController.ClassifyEntry("save_MyGame.sav"),
                Is.EqualTo(SaveGamePanelModel.EntryKind.Manual));
            Assert.That(SaveGamePanelController.ClassifyEntry("Custom.sav"),
                Is.EqualTo(SaveGamePanelModel.EntryKind.Manual));
        }

        [Test]
        public void ScansNewestFirstWithSizesAndDisplayNames()
        {
            WriteFile("save_old.sav", 10, minutesAgo: 30);
            WriteFile("quick_new.sav", 20, minutesAgo: 1);
            WriteFile("auto_mid.sav", 30, minutesAgo: 10);
            WriteFile("notasave.txt", 5, minutesAgo: 0);

            var model = new SaveGamePanelModel();
            SaveGamePanelController.ScanInto(model, _directory);

            Assert.That(model.Entries.Count, Is.EqualTo(3));
            Assert.That(model.Entries[0].FileName, Is.EqualTo("quick_new.sav"));
            Assert.That(model.Entries[1].FileName, Is.EqualTo("auto_mid.sav"));
            Assert.That(model.Entries[2].FileName, Is.EqualTo("save_old.sav"));
            Assert.That(model.Entries[0].SizeBytes, Is.EqualTo(20));
            Assert.That(model.Entries[0].DisplayName, Is.EqualTo("quick_new"));
            Assert.That(model.Entries[0].Kind, Is.EqualTo(SaveGamePanelModel.EntryKind.Quick));
        }

        [Test]
        public void ScanOfMissingDirectoryLeavesModelEmpty()
        {
            var model = new SaveGamePanelModel();
            SaveGamePanelController.ScanInto(
                model,
                Path.Combine(_directory, "does-not-exist"));
            Assert.That(model.Entries, Is.Empty);
        }

        private void WriteFile(string name, int size, int minutesAgo)
        {
            string path = Path.Combine(_directory, name);
            File.WriteAllBytes(path, new byte[size]);
            File.SetLastWriteTime(path, DateTime.Now.AddMinutes(-minutesAgo));
        }
    }
}
