#nullable enable

namespace Diggles.Tests
{
    using System.IO;
    using Diggles.Runtime;
    using Diggles.Runtime.Localization;
    using Diggles.Runtime.UI;
    using NUnit.Framework;
    using TMPro;
    using UnityEngine;

    // The localization utility over the original data table (data/messages.txt: `## "Key"` +
    // per-locale lines): service lookups, live locale switching, the %1 placeholder law, the
    // TMP component, and the locale-token art resolution.
    public sealed class GameLocalizationTests
    {
        static string DataRoot => OriginalDataTestFixture.DataRoot;

        GameLocalization? _previous;
        string _previousLocale = "en";

        [SetUp]
        public void SetUp()
        {
            _previous = GameLocalization.Current;
            _previousLocale = DigglesText.Locale;
        }

        [TearDown]
        public void TearDown()
        {
            DigglesText.SetLocale(_previousLocale);
            GameLocalization.Bind(_previous);
        }

        // A deterministic table fixture: the real data root ships exactly one Steam locale's
        // lines (a Russian install's messages.txt has only `ru` rows), so table-law tests must
        // not assume which locales the data root carries.
        static string WriteSyntheticRoot()
        {
            var root = Path.Combine(Path.GetTempPath(), "diggles-loc-msg-test");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "messages.txt"),
                "## \"New Game\"\r\nen \"New game\"\r\nde \"Neues Spiel\"\r\n\r\n" +
                "## \"Only German\"\r\nde \"Nur Deutsch\"\r\n");
            return root;
        }

        [Test]
        public void Get_ResolvesTableKey_AndSwitchesLocaleLive()
        {
            var root = WriteSyntheticRoot();
            var localization = new GameLocalization(root);
            localization.SetLocale("en");
            Assert.That(localization.Get("New Game"), Is.EqualTo("New game"));
            Assert.That(localization.Get("Only German"), Is.EqualTo("Nur Deutsch"),
                "single-locale entries resolve through the first available locale (Steam ships one)");

            bool changed = false;
            GameLocalization.Changed += Mark;
            try
            {
                GameLocalization.Bind(localization);
                changed = false;
                localization.SetLocale("de");
                Assert.That(changed, Is.True, "SetLocale must raise Changed");
                Assert.That(localization.Get("New Game"), Is.EqualTo("Neues Spiel"));
                Assert.That(localization.Locale, Is.EqualTo("de"));
            }
            finally
            {
                GameLocalization.Changed -= Mark;
            }

            Assert.That(localization.Get("no such key anywhere"), Is.EqualTo("no such key anywhere"),
                "unknown keys pass through visibly");
            Assert.That(localization.Locales, Does.Contain("en").And.Contain("de"));
            void Mark() => changed = true;
        }

        [Test]
        public void RealData_ResolvesAKnownMenuKey_UnderTheDetectedLocale()
        {
            var localization = new GameLocalization(DataRoot);
            var detected = DigglesText.DetectLocale(DataRoot);
            if (!string.IsNullOrEmpty(detected)) localization.SetLocale(detected);
            Assert.That(localization.TryGet("New Game", out var text), Is.True,
                $"data root '{DataRoot}' (locale '{localization.Locale}') must carry the New Game entry");
            Assert.That(text, Is.Not.Empty.And.Not.EqualTo("New Game"),
                "the resolved line is a translation, not the key passthrough");
        }

        [Test]
        public void Format_SubstitutesNativePlaceholders()
        {
            var localization = new GameLocalization(DataRoot);
            Assert.That(localization.Format("takes %1 of %2", 3, "7"), Is.EqualTo("takes 3 of 7"));
        }

        [Test]
        public void LocalizedText_TracksKeyAndLocale()
        {
            var localization = new GameLocalization(WriteSyntheticRoot());
            localization.SetLocale("en");
            GameLocalization.Bind(localization);
            var go = new GameObject("label", typeof(TextMeshProUGUI));
            try
            {
                var label = go.GetComponent<TextMeshProUGUI>();
                label.text = "authored preview";
                var localized = go.AddComponent<LocalizedText>();
                localized.MessageKey = "New Game";
                Assert.That(label.text, Is.EqualTo("New game"));

                localization.SetLocale("de");
                Assert.That(label.text, Is.EqualTo("Neues Spiel"), "locale switch re-applies live");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void AssetLibrary_ResolvesLocaleToken_WithFallbacks()
        {
            var root = Path.Combine(Path.GetTempPath(), "diggles-loc-test");
            var gui = Path.Combine(root, "GUI");
            Directory.CreateDirectory(gui);
            var png = MinimalPng();
            File.WriteAllBytes(Path.Combine(gui, "logo_de.png"), png);
            File.WriteAllBytes(Path.Combine(gui, "logo.png"), png);
            try
            {
                var assets = new LocalizedAssetLibrary(root);

                DigglesText.SetLocale("de");
                Assert.That(assets.ResolvePath("GUI/logo_{locale}.png"),
                    Does.EndWith("logo_de.png"), "current-locale variant wins");

                DigglesText.SetLocale("fr");
                Assert.That(assets.ResolvePath("GUI/logo_{locale}.png"),
                    Does.EndWith("logo.png"), "no fr, no en → token-stripped base file");

                Assert.That(assets.ResolvePath("GUI/logo.png"), Does.EndWith("logo.png"),
                    "token-less pattern loads the exact path");
                Assert.That(assets.ResolvePath("GUI/absent_{locale}.png"), Is.Null);

                var sprite = assets.GetSprite("GUI/logo.png");
                Assert.That(sprite, Is.Not.Null, "PNG decodes through LoadImage");
                assets.Dispose();
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        static byte[] MinimalPng()
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            texture.SetPixels32(new[]
            {
                new Color32(255, 0, 0, 255), new Color32(0, 255, 0, 255),
                new Color32(0, 0, 255, 255), new Color32(255, 255, 255, 255),
            });
            texture.Apply();
            var bytes = texture.EncodeToPNG();
            Object.DestroyImmediate(texture);
            return bytes;
        }
    }
}
