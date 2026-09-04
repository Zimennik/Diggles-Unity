#nullable enable

namespace Diggles.Editor.UI
{
    using System.Collections.Generic;
    using System.IO;
    using Diggles.Runtime;
    using UnityEditor;
    using UnityEngine;

    // Diggles ▸ UI ▸ Build Icon Library — bulk-imports every data/gui/icons/*.tga into
    // Textures/UI/Icons (straight alpha, sprite settings) andbuilds the IconLibrary asset that
    // maps class/action name → Sprite. This is the "Tcl request → icon" correlation, captured by the
    // original's icon-by-class-name convention (see IconLibrary / info_prods.tcl). Idempotent; the
    // asset's manual `overrides` are preserved across runs.
    public static class IconLibraryBuilder
    {
        const string Screen = "Icons";
        public const string AssetPath = "Assets/Diggles/Textures/UI/IconLibrary.asset";

        [MenuItem("Diggles/UI/Build Icon Library", priority = 10)]
        public static void BuildMenu()
        {
            var lib = Build();
            if (lib != null) { Selection.activeObject = lib; EditorGUIUtility.PingObject(lib); }
        }

        // Import icons +build the asset; returns it. Reused by the window builders so generating
        // a window also guarantees its icons exist.
        public static IconLibrary? Build()
        {
            var dir = Path.Combine(GuiSpriteProvisioner.GuiDir, "icons");
            if (!Directory.Exists(dir)) { Debug.LogError($"[UI] icons dir not found: {dir}"); return LoadOrCreate(); }

            var files = Directory.GetFiles(dir, "*.tga");
            var entries = new List<IconLibrary.Entry>(files.Length);
            try
            {
                for (int i = 0; i < files.Length; i++)
                {
                    var name = Path.GetFileNameWithoutExtension(files[i]);
                    if (EditorUtility.DisplayCancelableProgressBar("Building Icon Library",
                            $"{name}  ({i + 1}/{files.Length})", (float)i / files.Length)) break;
                    var sprite = GuiSpriteProvisioner.Ensure("icons/" + Path.GetFileName(files[i]), Screen,
                        sharpenAlpha: true, force: true);
                    if (sprite != null) entries.Add(new IconLibrary.Entry { key = name, sprite = sprite });
                }
            }
            finally { EditorUtility.ClearProgressBar(); }

            var lib = LoadOrCreate();
            lib.EditorSetEntries(entries);
            EditorUtility.SetDirty(lib);
            AssetDatabase.SaveAssets();
            Debug.Log($"[UI] IconLibrary: {entries.Count} icons → {AssetPath}");
            return lib;
        }

        static IconLibrary LoadOrCreate()
        {
            var lib = AssetDatabase.LoadAssetAtPath<IconLibrary>(AssetPath);
            if (lib == null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(AssetPath)!.Replace('/', Path.DirectorySeparatorChar));
                lib = ScriptableObject.CreateInstance<IconLibrary>();
                AssetDatabase.CreateAsset(lib, AssetPath);
            }
            return lib;
        }
    }
}
