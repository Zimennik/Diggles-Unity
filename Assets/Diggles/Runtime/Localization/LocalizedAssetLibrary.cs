#nullable enable

namespace Diggles.Runtime.Localization
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using UnityEngine;

    /// <summary>
    /// Locale-aware texture/sprite loading from the game data directory, for UI authored in the
    /// Unity editor (menu logo, localized plates). A path is data-relative and may carry the
    /// <c>{locale}</c> token: <c>"GUI/logo_{locale}.tga"</c> resolves to the first existing of
    /// 1. the token replaced with the current locale (<c>GUI/logo_ru.tga</c>),
    /// 2. the token replaced with <c>en</c>,
    /// 3. the token (with one adjoining <c>_</c>) removed (<c>GUI/logo.tga</c>).
    /// A token-less path loads exactly as written (the exact-path case). Shipped art is
    /// TGA (TgaLoader); user-added art may be PNG/JPG (Texture2D.LoadImage). Results are cached
    /// per (locale, pattern), misses included; textures/sprites are destroyed on Dispose.
    /// </summary>
    public sealed class LocalizedAssetLibrary : IDisposable
    {
        const string LocaleToken = "{locale}";

        readonly string _dataRoot;
        readonly Dictionary<string, Sprite?> _sprites = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Texture2D?> _textures = new(StringComparer.OrdinalIgnoreCase);

        public LocalizedAssetLibrary(string dataRoot)
        {
            _dataRoot = dataRoot ?? throw new ArgumentNullException(nameof(dataRoot));
        }

        public Sprite? GetSprite(string pathPattern)
        {
            if (string.IsNullOrEmpty(pathPattern)) return null;
            var key = CacheKey(pathPattern);
            if (_sprites.TryGetValue(key, out var cached)) return cached;
            var texture = GetTexture(pathPattern);
            var sprite = texture == null
                ? null
                : Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height),
                    new Vector2(0.5f, 0.5f), 100f);
            if (sprite != null) sprite.name = "loc:" + pathPattern;
            _sprites[key] = sprite;
            return sprite;
        }

        public Texture2D? GetTexture(string pathPattern)
        {
            if (string.IsNullOrEmpty(pathPattern)) return null;
            var key = CacheKey(pathPattern);
            if (_textures.TryGetValue(key, out var cached)) return cached;
            var path = ResolvePath(pathPattern);
            var texture = path == null ? null : LoadTexture(path);
            if (texture != null)
            {
                texture.name = "loc:" + pathPattern;
                texture.wrapMode = TextureWrapMode.Clamp;
            }
            else
            {
                Debug.LogWarning($"[LocalizedAssets] no file for '{pathPattern}' (locale {DigglesText.Locale}) under '{_dataRoot}'");
            }
            _textures[key] = texture;
            return texture;
        }

        /// <summary>The resolved absolute path for a pattern, or null (exposed for tests).</summary>
        public string? ResolvePath(string pathPattern)
        {
            foreach (var candidate in Candidates(pathPattern))
            {
                var absolute = Path.Combine(_dataRoot, candidate);
                if (File.Exists(absolute)) return absolute;
            }
            return null;
        }

        IEnumerable<string> Candidates(string pattern)
        {
            if (pattern.IndexOf(LocaleToken, StringComparison.OrdinalIgnoreCase) < 0)
            {
                yield return pattern;
                yield break;
            }
            var locale = DigglesText.Locale;
            yield return ReplaceToken(pattern, locale);
            if (!string.Equals(locale, "en", StringComparison.OrdinalIgnoreCase))
                yield return ReplaceToken(pattern, "en");
            yield return ReplaceToken(pattern.Replace("_" + LocaleToken, "").Replace(LocaleToken + "_", ""), "");
        }

        static string ReplaceToken(string pattern, string locale) =>
            pattern.Replace(LocaleToken, locale);

        string CacheKey(string pattern) => DigglesText.Locale + ":" + pattern;

        static Texture2D? LoadTexture(string absolutePath)
        {
            try
            {
                if (absolutePath.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
                    return TgaLoader.LoadFromFile(absolutePath)?.Texture;
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (texture.LoadImage(File.ReadAllBytes(absolutePath))) return texture;
                UnityEngine.Object.Destroy(texture);
                return null;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[LocalizedAssets] {absolutePath}: {e.Message}");
                return null;
            }
        }

        public void Dispose()
        {
            foreach (var sprite in _sprites.Values)
                if (sprite != null) DestroyResource(sprite);
            foreach (var texture in _textures.Values)
                if (texture != null) DestroyResource(texture);
            _sprites.Clear();
            _textures.Clear();
        }

        static void DestroyResource(UnityEngine.Object resource)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(resource);
            else UnityEngine.Object.DestroyImmediate(resource);
        }
    }
}
