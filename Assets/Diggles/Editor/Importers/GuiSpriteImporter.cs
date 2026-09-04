#nullable enable

namespace Diggles.Editor
{
    using System.Collections.Generic;
    using System.IO;
    using Diggles.Runtime;
    using UnityEditor;
    using UnityEngine;

    // One-shot importer: original `data/GUI` TGAs → Sprite assets for scene-authored UI
    // layouts. PNGs land under Assets/Diggles/UI/Sprites preserving the original subfolder
    // and file name, so a layout references "buttons/Uhr_normal" exactly like the original
    // GUI scripts do. Re-import overwrites in place, so GUIDs and scene references survive.
    public sealed class GuiSpriteImporter : EditorWindow
    {
        static string DefaultSource => System.IO.Path.Combine(Diggles.Runtime.DigglesConfig.ConfiguredDataRoot, "GUI");
        const string DestRoot = "Assets/Diggles/UI/Sprites";

        string _source = "";
        string _filter = "*";
        bool _recursive = true;
        Vector2 _scroll;
        readonly List<string> _log = new();

        [MenuItem("Diggles/UI/Import GUI Sprites…")]
        static void Open() => GetWindow<GuiSpriteImporter>("GUI Sprites");

        void OnEnable()
        {
            if (string.IsNullOrEmpty(_source)) _source = DefaultSource;
        }

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Converts original GUI .tga files into Sprite assets under " + DestRoot +
                " (subfolders preserved). Filter is a file wildcard, e.g. Uhr_* or buttons\\*.",
                MessageType.Info);
            _source = EditorGUILayout.TextField("Source dir", _source);
            _filter = EditorGUILayout.TextField("Filter", _filter);
            _recursive = EditorGUILayout.Toggle("Recursive", _recursive);

            if (GUILayout.Button("Import"))
            {
                _log.Clear();
                Import();
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var line in _log) EditorGUILayout.LabelField(line, EditorStyles.miniLabel);
            EditorGUILayout.EndScrollView();
        }

        void Import()
        {
            if (!Directory.Exists(_source)) { _log.Add($"source not found: {_source}"); return; }
            var files = Directory.GetFiles(_source, _filter.EndsWith(".tga") ? _filter : _filter + ".tga",
                _recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
            int ok = 0, skip = 0;
            try
            {
                AssetDatabase.StartAssetEditing();
                foreach (var f in files)
                {
                    var rel = Path.GetRelativePath(_source, f).Replace('\\', '/');
                    var dest = $"{DestRoot}/{Path.ChangeExtension(rel, ".png")}";
                    try
                    {
                        var tga = TgaLoader.LoadFromFile(f);
                        if (tga == null) { skip++; continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        File.WriteAllBytes(dest, tga.Texture.EncodeToPNG());
                        DestroyImmediate(tga.Texture);
                        ok++;
                    }
                    catch (System.Exception e)
                    {
                        _log.Add($"FAIL {rel}: {e.Message}");
                        skip++;
                    }
                }
            }
            finally { AssetDatabase.StopAssetEditing(); }
            AssetDatabase.Refresh();

            // Second pass: force Sprite import settings on everything we just wrote.
            foreach (var f in files)
            {
                var rel = Path.GetRelativePath(_source, f).Replace('\\', '/');
                var dest = $"{DestRoot}/{Path.ChangeExtension(rel, ".png")}";
                var imp = AssetImporter.GetAtPath(dest) as TextureImporter;
                if (imp == null) continue;
                if (imp.textureType == TextureImporterType.Sprite && imp.textureCompression == TextureImporterCompression.Uncompressed)
                    continue;   // already configured — keep manual tweaks
                imp.textureType = TextureImporterType.Sprite;
                imp.spriteImportMode = SpriteImportMode.Single;
                imp.mipmapEnabled = false;
                imp.alphaIsTransparency = true;
                imp.textureCompression = TextureImporterCompression.Uncompressed;
                imp.filterMode = FilterMode.Bilinear;
                imp.SaveAndReimport();
            }
            _log.Add($"done: {ok} imported, {skip} skipped → {DestRoot}");
        }
    }
}
