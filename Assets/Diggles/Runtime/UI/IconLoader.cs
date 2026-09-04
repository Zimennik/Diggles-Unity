#nullable enable

namespace Diggles.Runtime
{
    using System.Collections.Generic;
    using System.IO;
    using UnityEngine;

    // Runtime icon provider — loads GUI icons directly from the original data files at play time
    // ({DataRoot}/GUI/icons/<name>.tga) via TgaLoader, conditions the alpha through IconAlpha
    // (color-key matte → filled body + AA edge), wraps them as Sprites and caches by name. No
    // edit-time import / no baked asset needed — the same "reuse original data/ as-is" approach the
    // WorkClockWidget uses for its dial art.
    //
    // Registered as a DI singleton by GameLifetimeScope (constructed from DigglesTclContext.DataRoot);
    // a controller injects it and assigns the Sprites to its uGUI Images. Icon name == class name
    // (the original convention data/gui/icons/$class.tga); GetProduct adds the Red<class>
    // unaffordable variant. Pass sharpen:false to get the raw straight-alpha bitmap instead.
    public sealed class IconLoader : System.IDisposable
    {
        readonly string _dir;
        readonly bool _sharpen;
        readonly Dictionary<string, Sprite?> _cache = new();

        public IconLoader(string dataRoot, bool sharpen = true)
        {
            _dir = Path.Combine(dataRoot ?? "", "GUI", "icons");
            _sharpen = sharpen;
        }

        // The icon for a class / action name (e.g. "Farm", "unpack", "energyon"). Cached; null + a warn
        // once if the source TGA is missing. The cache also remembers misses (null) to avoid re-hitting.
        //
        // Underscore-prefixed classes: the Lager storage-category catalog entries are the
        // service classes `_Kisten_einlagern _Nahrung_einlagern _Pilze_einlagern
        // _Rohmineralien_einlagern _Mineralien_einlagern` (tttitems_Lager,
        // techtreetunes.tcl:175; def_class lager.tcl:4-9) — but their shipped icons are the
        // same names without the underscore (data/Gui/icons/Nahrung_einlagern.tga …; no
        // `_*.tga` exists in the icons dir at all), so a leading-underscore name resolves
        // through the trimmed file.
        public Sprite? Get(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_cache.TryGetValue(name, out var cached)) return cached;
            bool underscored = name[0] == '_';
            var sprite = Load(name, warnOnMiss: !underscored);
            if (sprite == null && underscored)
                sprite = Load(name.TrimStart('_'), warnOnMiss: true);
            _cache[name] = sprite;
            return sprite;
        }

        // Product icon: buildable → "<class>"; not buildable → "Red<class>" (info_prods.tcl), falling
        // back to the base icon when no Red variant ships.
        public Sprite? GetProduct(string className, bool buildable)
        {
            if (!buildable)
            {
                var red = Get("Red" + className);
                if (red != null) return red;
            }
            return Get(className);
        }

        // Native `Crossout<icon>` names are virtual composites: ViWin resolves the base icon and
        // applies GUI/icons/xoverlay_Crossout.tga. prodslot_override disable uses that path for
        // product cells and for packtobox (FUN_00520620/FUN_00521c80). Build the same composite
        // from the original files and cache it like a normal icon.
        public Sprite? GetCrossedOut(string name, bool product)
        {
            string key = "\0crossout:" + (product ? "prod:" : "action:") + name;
            if (_cache.TryGetValue(key, out var cached)) return cached;
            Sprite? source = product ? GetProduct(name, buildable: true) : Get(name);
            Sprite? overlay = Get("xoverlay_Crossout");
            if (source == null || overlay == null)
            {
                _cache[key] = source;
                return source;
            }

            Texture2D baseTexture = source.texture;
            Texture2D overlayTexture = overlay.texture;
            int width = baseTexture.width, height = baseTexture.height;
            Color32[] basePixels = baseTexture.GetPixels32();
            Color32[] overlayPixels = overlayTexture.GetPixels32();
            var composed = new Color32[basePixels.Length];
            for (int y = 0; y < height; y++)
            {
                int oy = y * overlayTexture.height / height;
                for (int x = 0; x < width; x++)
                {
                    Color32 below = basePixels[y * width + x];
                    Color32 above = overlayPixels[oy * overlayTexture.width
                                                  + x * overlayTexture.width / width];
                    int alpha = above.a;
                    int inverse = 255 - alpha;
                    int outAlphaScaled = alpha * 255 + below.a * inverse;
                    composed[y * width + x] = new Color32(
                        BlendChannel(above.r, below.r, alpha, below.a, inverse, outAlphaScaled),
                        BlendChannel(above.g, below.g, alpha, below.a, inverse, outAlphaScaled),
                        BlendChannel(above.b, below.b, alpha, below.a, inverse, outAlphaScaled),
                        (byte)(outAlphaScaled / 255));
                }
            }
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                name = "icon:Crossout" + name,
                wrapMode = TextureWrapMode.Clamp,
            };
            texture.SetPixels32(composed);
            texture.Apply(updateMipmaps: false);
            cached = Sprite.Create(texture, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f), 100f);
            _cache[key] = cached;
            return cached;
        }

        static byte BlendChannel(byte above, byte below, int aboveAlpha, int belowAlpha,
            int inverseAboveAlpha, int outAlphaScaled)
        {
            if (outAlphaScaled == 0) return 0;
            int premultiplied = above * aboveAlpha * 255
                              + below * belowAlpha * inverseAboveAlpha;
            return (byte)(premultiplied / outAlphaScaled);
        }

        public bool Has(string name) => Get(name) != null;

        Sprite? Load(string name, bool warnOnMiss = true)
        {
            var path = Path.Combine(_dir, name + ".tga");
            if (!File.Exists(path))
            {
                if (warnOnMiss) Debug.LogWarning($"[IconLoader] missing icon: {path}");
                return null;
            }
            TgaResult? res;
            try { res = TgaLoader.LoadFromFile(path); }
            catch (System.Exception e) { Debug.LogWarning($"[IconLoader] {name}: {e.Message}"); return null; }
            if (res == null) { Debug.LogWarning($"[IconLoader] missing icon: {path}"); return null; }

            var tex = res.Texture;
            if (_sharpen)
            {
                var px = tex.GetPixels32();
                IconAlpha.Sharpen(px, tex.width, tex.height);
                tex.SetPixels32(px);
                tex.Apply(updateMipmaps: false);
            }
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.name = "icon:" + name;
            return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
        }

        // Free the cached textures + sprites (the container disposes this on scene teardown).
        public void Dispose()
        {
            var destroyed = new HashSet<Object>();
            foreach (var s in _cache.Values)
            {
                if (s == null) continue;
                if (s.texture != null && destroyed.Add(s.texture)) DestroyCachedObject(s.texture);
                if (destroyed.Add(s)) DestroyCachedObject(s);
            }
            _cache.Clear();
        }

        static void DestroyCachedObject(Object value)
        {
            if (Application.isPlaying) Object.Destroy(value);
            else Object.DestroyImmediate(value);
        }
    }
}
