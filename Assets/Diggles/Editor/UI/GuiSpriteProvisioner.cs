#nullable enable

namespace Diggles.Editor.UI
{
    using System.IO;
    using Diggles.Runtime;     // TgaLoader / TgaResult / IconAlpha
    using UnityEditor;
    using UnityEngine;

    // Per-screen GUI sprite provisioning for UI layouts authored in the Unity editor. Converts the original
    // `data/GUI`.tga files into Sprite PNGs under Assets/Diggles/Textures/UI/<screen>/ with
    // straight alpha preserved, no compression (crisp UI), bilinear + clamp, and an optional
    // 9-slice border. Idempotent and stable-pathed: the same source always lands at the same asset
    // path → its GUID is stable → scene references survive a re-import. Window builders call Ensure
    // as they place each image; the bulk Diggles ▸ UI ▸ Import GUI Sprites… window is the ad-hoc
    // alternative (different, non-categorised destination).
    //
    // For icons, the edit-time bake is optional — IconLoader loads the same TGAs directly at runtime.
    // Both share the alpha conditioning in Diggles.Runtime.IconAlpha.
    public static class GuiSpriteProvisioner
    {
        public static string GuiDirDefault => System.IO.Path.Combine(Diggles.Runtime.DigglesConfig.ConfiguredDataRoot, "GUI");
        const string GuiDirPrefKey = "Diggles.UI.GuiDir";
        public const string TexRoot = "Assets/Diggles/Textures/UI";

        // Override-able (EditorPrefs) so a different checkout location doesn't need a code edit.
        public static string GuiDir
        {
            get => EditorPrefs.GetString(GuiDirPrefKey, GuiDirDefault);
            set => EditorPrefs.SetString(GuiDirPrefKey, value);
        }

        // Ensure the original sprite `originalRel` (e.g. "TXT_Production.tga" or "icons/Aufbau.tga")
        // exists as a Sprite under Textures/UI/<screen>/ and return it. `border` = (left,bottom,
        // right,top) in px makes it a 9-slice sprite (set Image.Type = Sliced to use). `sharpenAlpha`
        // runs the icon alpha pass (IconAlpha). Returns null and logs if the source is missing/bad.
        public static Sprite? Ensure(string originalRel, string screen, Vector4 border = default,
            bool sharpenAlpha = false, bool force = false)
        {
            var src = Path.Combine(GuiDir, originalRel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(src)) { Debug.LogError($"[UI] GUI source not found: {src}"); return null; }

            var destDir = $"{TexRoot}/{screen}";
            var dest = $"{destDir}/{Path.GetFileNameWithoutExtension(originalRel)}.png";
            var destFs = dest.Replace('/', Path.DirectorySeparatorChar);

            //convert when forced (e.g. the icon alpha pass changed), missing, or source-newer.
            if (force || !File.Exists(destFs) || File.GetLastWriteTimeUtc(src) > File.GetLastWriteTimeUtc(destFs))
            {
                TgaResult? tga;
                try { tga = TgaLoader.LoadFromFile(src); }
                catch (System.Exception e) { Debug.LogError($"[UI] TGA load failed {originalRel}: {e.Message}"); return null; }
                if (tga == null) { Debug.LogError($"[UI] TGA decode returned null: {originalRel}"); return null; }

                var tex = tga.Texture;
                if (sharpenAlpha)
                {
                    var px = tex.GetPixels32();
                    IconAlpha.Sharpen(px, tex.width, tex.height);
                    tex.SetPixels32(px);
                    tex.Apply(updateMipmaps: false);
                }

                Directory.CreateDirectory(destDir.Replace('/', Path.DirectorySeparatorChar));
                File.WriteAllBytes(destFs, tex.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(tex);
                AssetDatabase.ImportAsset(dest, ImportAssetOptions.ForceUpdate);
            }

            Configure(dest, border);
            return AssetDatabase.LoadAssetAtPath<Sprite>(dest);
        }

        // Force crisp-UI sprite import settings (+ a 9-slice border when given). Only re-imports when
        // something actually changed, so user tweaks in the importer survive a builder re-run.
        static void Configure(string dest, Vector4 border)
        {
            var imp = AssetImporter.GetAtPath(dest) as TextureImporter;
            if (imp == null) return;
            bool dirty = false;

            if (imp.textureType != TextureImporterType.Sprite) { imp.textureType = TextureImporterType.Sprite; dirty = true; }
            if (imp.spriteImportMode != SpriteImportMode.Single) { imp.spriteImportMode = SpriteImportMode.Single; dirty = true; }
            if (imp.mipmapEnabled) { imp.mipmapEnabled = false; dirty = true; }
            if (!imp.alphaIsTransparency) { imp.alphaIsTransparency = true; dirty = true; }
            if (imp.textureCompression != TextureImporterCompression.Uncompressed) { imp.textureCompression = TextureImporterCompression.Uncompressed; dirty = true; }
            if (imp.filterMode != FilterMode.Bilinear) { imp.filterMode = FilterMode.Bilinear; dirty = true; }
            if (imp.wrapMode != TextureWrapMode.Clamp) { imp.wrapMode = TextureWrapMode.Clamp; dirty = true; }
            if (imp.spriteBorder != border) { imp.spriteBorder = border; dirty = true; }

            if (dirty) imp.SaveAndReimport();
        }
    }
}
