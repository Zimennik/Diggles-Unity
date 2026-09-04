#nullable enable

namespace Diggles.Runtime.Localization
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using UnityEngine;

    /// <summary>
    /// Game-text localization over the original data table <c>data/messages.txt</c> — 689
    /// entries of the form <c>## "Key"</c> followed by per-locale lines <c>en "…"</c> /
    /// <c>de "…"</c> / … (e.g. messages.txt:2529 <c>## "New Game"</c> → <c>en "New game"</c>,
    /// <c>de "Neues Spiel"</c>). The native <c>lmsg &lt;key&gt;</c> resolves through this table;
    /// the port additionally hands it to UI authored in the Unity editor (LocalizedText /
    /// LocalizedSprite components and plain code via DI).
    ///
    /// Lifecycle: <see cref="DigglesEngine.Build"/> binds the shared instance for the resolved
    /// data root; GameLifetimeScope registers the same instance for injection. Scene components
    /// living outside the container reach it through <see cref="Current"/> and re-apply on
    /// <see cref="Changed"/> (fired when the table binds AND on every locale switch), so a
    /// component enabled before the engine boots still localizes as soon as the table arrives.
    ///
    /// Placeholders use the native <c>%1</c>..<c>%9</c> convention (messages.txt:1-5
    /// " %1 seconds idle") — see <see cref="Format"/>.
    /// </summary>
    public sealed class GameLocalization
    {
        public static GameLocalization? Current { get; private set; }

        /// <summary>Raised when the table binds or the locale switches. Components re-apply here.</summary>
        public static event Action? Changed;

        /// <summary>Bind the shared instance (null unbinds — tests restore the previous one).</summary>
        public static void Bind(GameLocalization? instance)
        {
            Current = instance;
            Changed?.Invoke();
        }

        readonly string _dataRoot;
        readonly Dictionary<string, Dictionary<string, string>> _byKey =
            new(StringComparer.Ordinal);
        readonly HashSet<string> _locales = new(StringComparer.OrdinalIgnoreCase);
        bool _missingWarned;

        public LocalizedAssetLibrary Assets { get; }

        public GameLocalization(string dataRoot)
        {
            _dataRoot = dataRoot ?? throw new ArgumentNullException(nameof(dataRoot));
            Assets = new LocalizedAssetLibrary(dataRoot);
            Reload();
        }

        public string DataRoot => _dataRoot;

        /// <summary>Current locale code — the same value the Tcl `locale` command returns.</summary>
        public string Locale => DigglesText.Locale;

        /// <summary>Locales that actually occur in the table (for a future settings dropdown).</summary>
        public IReadOnlyCollection<string> Locales => _locales;

        /// <summary>
        /// Switch the active locale: routes through <see cref="DigglesText.SetLocale"/> (the
        /// single source of truth for data-file encoding and the Tcl `locale` command), re-reads
        /// the table under the new encoding (ru = Windows-1251), and raises <see cref="Changed"/>.
        /// </summary>
        public void SetLocale(string locale)
        {
            if (string.IsNullOrWhiteSpace(locale)) throw new ArgumentException("empty locale", nameof(locale));
            if (string.Equals(DigglesText.Locale, locale.Trim(), StringComparison.OrdinalIgnoreCase)) return;
            DigglesText.SetLocale(locale);
            Reload();
            Changed?.Invoke();
        }

        /// <summary>
        /// Resolve a message key: current locale → "en" → the entry's first available locale →
        /// the key itself (visible and greppable when nothing is translated). The last hop
        /// matters on Steam data, which ships exactly one locale's lines — a Russian install's
        /// messages.txt has only `ru "…"` rows, no `en` fallback rows at all.
        /// </summary>
        public string Get(string key) => TryGet(key, out var text) ? text : key;

        public bool TryGet(string key, out string text)
        {
            text = "";
            if (string.IsNullOrEmpty(key) || !_byKey.TryGetValue(key, out var translations))
                return false;
            if (translations.TryGetValue(DigglesText.Locale, out text!)
                || translations.TryGetValue("en", out text!))
                return true;
            foreach (var any in translations.Values) { text = any; return true; }
            return false;
        }

        /// <summary>Resolve + substitute the native %1..%9 placeholders with <paramref name="args"/>.</summary>
        public string Format(string key, params object[] args)
        {
            var text = Get(key);
            for (int i = 0; i < args.Length && i < 9; i++)
                text = text.Replace("%" + (i + 1), args[i]?.ToString() ?? "");
            return text;
        }

        void Reload()
        {
            _byKey.Clear();
            _locales.Clear();
            var path = Path.Combine(_dataRoot, "messages.txt");
            if (!File.Exists(path))
            {
                if (!_missingWarned)
                    Debug.LogWarning($"[GameLocalization] messages.txt not found under '{_dataRoot}' — keys pass through");
                _missingWarned = true;
                return;
            }

            Dictionary<string, string>? current = null;
            foreach (var raw in DigglesText.ReadAllText(path)
                         .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.TrimEnd('\r');
                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    var key = Unquote(line.Substring(3));
                    if (key.Length == 0) { current = null; continue; }
                    if (!_byKey.TryGetValue(key, out current))
                        _byKey[key] = current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    continue;
                }
                if (current == null || line.Length < 4 || !char.IsLetter(line[0]) || !char.IsLetter(line[1]))
                    continue;
                var locale = line.Substring(0, 2).ToLowerInvariant();
                var rest = line.Substring(2).TrimStart();
                if (rest.Length == 0 || rest[0] != '"') continue;
                current[locale] = Unquote(rest);
                _locales.Add(locale);
            }
        }

        static string Unquote(string s)
        {
            s = s.Trim();
            int first = s.IndexOf('"');
            int last = s.LastIndexOf('"');
            return first >= 0 && last > first ? s.Substring(first + 1, last - first - 1) : s;
        }
    }
}
