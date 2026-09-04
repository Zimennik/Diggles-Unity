#nullable enable

namespace Diggles.Editor.Importers
{
    using Diggles.Runtime.World;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using UnityEditor;
    using UnityEngine;

    public sealed class DigglesLevelBrowser : EditorWindow
    {
        private const string BrowserAssetFolder = "Assets/Diggles/Browser";
        private const string BrowserAssetFile = "Current.l2m";
        private const string BrowserTclSidecar = "Current.tcl";
        private const string EditorPrefsDataRoot = "Diggles.LevelBrowser.DataRoot";

        private string dataRoot = "";
        private string filter = "";
        private Vector2 scrollPosition;
        private List<FileInfo> files = new();
        private int selectedIndex = -1;
        private GameObject? currentInstance;
        private string currentMetadataText = "";

        private float tileSize = 1f;
        private float tileHeight = 1f;
        private bool stackColumns = true;
        private L2mScriptedImporter.Orientation orientation =
            L2mScriptedImporter.Orientation.Floor;
        private L2mScriptedImporter.RenderStyle renderStyle =
            L2mScriptedImporter.RenderStyle.OberweltSideView;
        private bool renderEmptyCells;
        private bool placeObjectMarkers = true;
        private float markerRadius = 0.3f;
        private bool showAdvanced = true;

        [MenuItem("Tools/Diggles/Level Browser")]
        public static void ShowWindow()
        {
            var window = GetWindow<DigglesLevelBrowser>("Diggles Levels");
            window.minSize = new Vector2(700, 400);
        }

        private void OnEnable()
        {
            this.dataRoot = EditorPrefs.GetString(EditorPrefsDataRoot, this.dataRoot);
            if (string.IsNullOrEmpty(this.dataRoot)) this.dataRoot = Diggles.Runtime.DigglesConfig.ConfiguredDataRoot;
            this.RefreshFileList();
        }

        private void OnDisable()
        {
            EditorPrefs.SetString(EditorPrefsDataRoot, this.dataRoot);
        }

        private void OnGUI()
        {
            this.DrawHeader();
            EditorGUILayout.BeginHorizontal();
            this.DrawLeftPane();
            this.DrawRightPane();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawHeader()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            var newRoot = EditorGUILayout.TextField(
                "Data Root", this.dataRoot, EditorStyles.toolbarTextField);
            if (newRoot != this.dataRoot)
            {
                this.dataRoot = newRoot;
                this.RefreshFileList();
            }
            if (GUILayout.Button("Browse...", EditorStyles.toolbarButton, GUILayout.Width(80)))
            {
                var picked = EditorUtility.OpenFolderPanel("Diggles data root", this.dataRoot, "");
                if (!string.IsNullOrEmpty(picked))
                {
                    this.dataRoot = picked;
                    this.RefreshFileList();
                }
            }
            if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(70)))
            {
                this.RefreshFileList();
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawLeftPane()
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(320));

            this.filter = EditorGUILayout.TextField("Filter", this.filter);

            EditorGUILayout.LabelField($"{this.files.Count} .l2m files");

            this.scrollPosition = EditorGUILayout.BeginScrollView(this.scrollPosition);
            for (var i = 0; i < this.files.Count; i++)
            {
                var f = this.files[i];
                if (this.filter.Length > 0 &&
                    f.Name.IndexOf(this.filter, System.StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                var isSelected = i == this.selectedIndex;
                var bgColor = GUI.backgroundColor;
                if (isSelected) GUI.backgroundColor = new Color(0.5f, 0.7f, 1f);

                var subFolder = GetSubFolder(f, this.dataRoot);
                if (GUILayout.Button(
                        $"{f.Name}\n   {subFolder} | {FormatSize(f.Length)}",
                        GUILayout.Height(36)))
                {
                    this.selectedIndex = i;
                    this.ImportSelected();
                }

                GUI.backgroundColor = bgColor;
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.EndVertical();
        }

        private void DrawRightPane()
        {
            EditorGUILayout.BeginVertical();

            if (this.selectedIndex < 0 || this.selectedIndex >= this.files.Count)
            {
                EditorGUILayout.HelpBox(
                    "Select a level from the list to preview.",
                    MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            var f = this.files[this.selectedIndex];
            EditorGUILayout.LabelField(f.Name, EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"Path: {GetSubFolder(f, this.dataRoot)}");
            EditorGUILayout.LabelField($"Size: {FormatSize(f.Length)}");
            EditorGUILayout.Space();

            if (!string.IsNullOrEmpty(this.currentMetadataText))
            {
                EditorGUILayout.SelectableLabel(
                    this.currentMetadataText,
                    EditorStyles.helpBox,
                    GUILayout.MinHeight(140));
            }

            EditorGUILayout.Space();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Frame in Scene", GUILayout.Width(140)) && this.currentInstance != null)
            {
                Selection.activeGameObject = this.currentInstance;
                SceneView.FrameLastActiveSceneView();
            }
            if (GUILayout.Button("Remove from Scene", GUILayout.Width(160)))
            {
                this.RemoveCurrent();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space();
            this.showAdvanced = EditorGUILayout.Foldout(
                this.showAdvanced, "Visual tuning (live)", true);
            if (this.showAdvanced)
            {
                EditorGUI.indentLevel++;
                this.DrawVisualToggles();
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawVisualToggles()
        {
            var changed = false;
            EditorGUI.BeginChangeCheck();
            this.renderStyle = (L2mScriptedImporter.RenderStyle)
                EditorGUILayout.EnumPopup("Render Style", this.renderStyle);
            this.orientation = (L2mScriptedImporter.Orientation)
                EditorGUILayout.EnumPopup("Orientation", this.orientation);
            this.tileSize = EditorGUILayout.FloatField("Tile Size", this.tileSize);
            this.tileHeight = EditorGUILayout.FloatField("Tile Height", this.tileHeight);
            using (new EditorGUI.DisabledScope(
                this.renderStyle != L2mScriptedImporter.RenderStyle.PlaceholderCubes))
            {
                this.stackColumns = EditorGUILayout.Toggle(
                    "Stack Columns (cubes only)", this.stackColumns);
                this.renderEmptyCells = EditorGUILayout.Toggle(
                    "Render Empty (cubes only)", this.renderEmptyCells);
            }
            this.placeObjectMarkers = EditorGUILayout.Toggle(
                "Place .tcl Object Markers", this.placeObjectMarkers);
            using (new EditorGUI.DisabledScope(!this.placeObjectMarkers))
            {
                this.markerRadius = EditorGUILayout.Slider(
                    "Marker Radius", this.markerRadius, 0.05f, 2f);
            }
            if (EditorGUI.EndChangeCheck()) changed = true;

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Apply (reimport)", GUILayout.Width(140)) || changed)
            {
                this.ReimportCurrent();
            }
            if (GUILayout.Button("Reset defaults", GUILayout.Width(120)))
            {
                this.tileSize = 1f;
                this.tileHeight = 1f;
                this.stackColumns = true;
                this.orientation = L2mScriptedImporter.Orientation.Floor;
                this.renderStyle = L2mScriptedImporter.RenderStyle.OberweltSideView;
                this.renderEmptyCells = false;
                this.placeObjectMarkers = true;
                this.markerRadius = 0.3f;
                this.ReimportCurrent();
            }
            EditorGUILayout.EndHorizontal();
        }

        private void RefreshFileList()
        {
            if (Directory.Exists(this.dataRoot))
            {
                this.files = Directory.GetFiles(this.dataRoot, "*.l2m", SearchOption.AllDirectories)
                    .Select(p => new FileInfo(p))
                    .OrderBy(f => f.Name)
                    .ToList();
            }
            else
            {
                this.files = new List<FileInfo>();
            }
        }

        private void ImportSelected()
        {
            var src = this.files[this.selectedIndex];
            try
            {
                using var stream = src.OpenRead();
                var map = L2mParser.Parse(stream, src.Length);
                this.currentMetadataText = BuildMetadataText(map, src, this.dataRoot);
            }
            catch (System.Exception e)
            {
                this.currentMetadataText = $"Failed to parse: {e.Message}";
                return;
            }

            this.ReimportCurrent();
        }

        private void ReimportCurrent()
        {
            if (this.selectedIndex < 0) return;
            var src = this.files[this.selectedIndex];
            EnsureBrowserFolder();

            var dst = Path.Combine(BrowserAssetFolder, BrowserAssetFile)
                .Replace('\\', '/');
            var projectRoot = Path.GetDirectoryName(Application.dataPath)
                ?? throw new System.InvalidOperationException("Cannot determine project root");
            var absoluteDst = Path.Combine(projectRoot, dst);
            File.Copy(src.FullName, absoluteDst, overwrite: true);

            CopySiblingTclIfAny(src, projectRoot, this.dataRoot);

            var importer = AssetImporter.GetAtPath(dst) as L2mScriptedImporter;
            if (importer != null)
            {
                var so = new SerializedObject(importer);
                so.FindProperty("originalDataRoot").stringValue = this.dataRoot;
                so.FindProperty("tileSize").floatValue = this.tileSize;
                so.FindProperty("tileHeight").floatValue = this.tileHeight;
                so.FindProperty("stackColumns").boolValue = this.stackColumns;
                so.FindProperty("orientation").enumValueIndex = (int)this.orientation;
                so.FindProperty("renderStyle").enumValueIndex = (int)this.renderStyle;
                so.FindProperty("renderEmptyCells").boolValue = this.renderEmptyCells;
                so.FindProperty("placeObjectMarkers").boolValue = this.placeObjectMarkers;
                so.FindProperty("markerRadius").floatValue = this.markerRadius;
                so.ApplyModifiedProperties();
                importer.SaveAndReimport();
            }
            else
            {
                AssetDatabase.ImportAsset(dst, ImportAssetOptions.ForceUpdate);
            }

            this.RemoveCurrent();
            var prefab = AssetDatabase.LoadMainAssetAtPath(dst) as GameObject;
            if (prefab != null)
            {
                this.currentInstance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
                if (this.currentInstance != null)
                {
                    this.currentInstance.name =
                        $"BrowserLevel_{Path.GetFileNameWithoutExtension(src.Name)}";
                }
            }
        }

        private void RemoveCurrent()
        {
            if (this.currentInstance != null)
            {
                DestroyImmediate(this.currentInstance);
                this.currentInstance = null;
            }
        }

        private static void CopySiblingTclIfAny(FileInfo src, string projectRoot, string dataRoot)
        {
            var basenameNoExt = Path.GetFileNameWithoutExtension(src.Name);
            var candidates = new[]
            {
                Path.Combine(src.DirectoryName ?? "", basenameNoExt + ".tcl"),
                Path.Combine(dataRoot, "Templates", basenameNoExt + ".tcl"),
                Path.Combine(dataRoot, "Templates", "tcl", basenameNoExt + ".tcl"),
            };
            var dstTcl = Path.Combine(projectRoot, BrowserAssetFolder, BrowserTclSidecar);
            foreach (var candidate in candidates)
            {
                if (!File.Exists(candidate)) continue;
                File.Copy(candidate, dstTcl, overwrite: true);
                AssetDatabase.ImportAsset(
                    Path.Combine(BrowserAssetFolder, BrowserTclSidecar).Replace('\\', '/'),
                    ImportAssetOptions.ForceUpdate);
                return;
            }

            if (File.Exists(dstTcl)) File.Delete(dstTcl);
        }

        private static void EnsureBrowserFolder()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Diggles"))
            {
                AssetDatabase.CreateFolder("Assets", "Diggles");
            }
            if (!AssetDatabase.IsValidFolder(BrowserAssetFolder))
            {
                AssetDatabase.CreateFolder("Assets/Diggles", "Browser");
            }
        }

        private static string BuildMetadataText(L2mMap map, FileInfo src, string dataRoot)
        {
            var sb = new System.Text.StringBuilder();
            if (map.FileSize == 0)
            {
                sb.AppendLine("(empty L2M file)");
                return sb.ToString();
            }

            var nonEmpty = map.Cells.Count(c => c != 0);
            var total = map.Cells.Length;
            var fillPct = total == 0 ? 0f : 100f * nonEmpty / total;
            var distinct = map.Cells.Where(c => c != 0).Distinct().Count();
            var maxId = map.Cells.Where(c => c != 0).DefaultIfEmpty((ushort)0).Max();

            sb.AppendLine($"Grid:        {map.Width} x {map.Height}");
            sb.AppendLine($"Cells:       {nonEmpty:N0} / {total:N0} non-empty ({fillPct:F1}%)");
            sb.AppendLine($"Distinct IDs: {distinct:N0}");
            sb.AppendLine($"Max cell ID: {maxId}");
            if (map.BytesRemaining != 0)
            {
                sb.AppendLine($"Bytes unread: {map.BytesRemaining}");
            }
            sb.AppendLine();

            var bdb = FindMatchingBdb(maxId, dataRoot);
            sb.AppendLine($"Matched BDB: {bdb ?? "<none>"}");

            var pmp = TryFindPairedPmp(src, map, dataRoot, out var pmpSource);
            if (pmp != null)
            {
                var minH = pmp.HeightMap.Min();
                var maxH = pmp.HeightMap.Max();
                var avgH = pmp.HeightMap.Average(b => (double)b);
                var airCount = pmp.SolidityVoxels.Count(b => b != 0);
                sb.AppendLine($"Paired PMP: {pmpSource}");
                sb.AppendLine($"  per-tile height: {minH}..{maxH} (avg {avgH:F2})");
                sb.AppendLine($"  air voxels: {airCount:N0} / {pmp.SolidityVoxels.Length:N0} " +
                    $"({100.0 * airCount / pmp.SolidityVoxels.Length:F2}%)");
            }
            else
            {
                sb.AppendLine("Paired PMP: <not found — rendering flat>");
            }
            return sb.ToString();
        }

        private static PmpMap? TryFindPairedPmp(
            FileInfo src, L2mMap map, string dataRoot, out string source)
        {
            source = "";
            var basenameNoExt = Path.GetFileNameWithoutExtension(src.Name);
            var candidateTcls = new[]
            {
                Path.Combine(src.DirectoryName ?? "", basenameNoExt + ".tcl"),
                Path.Combine(dataRoot, "Templates", basenameNoExt + ".tcl"),
            };
            foreach (var tcl in candidateTcls)
            {
                if (!File.Exists(tcl)) continue;
                var text = File.ReadAllText(tcl);
                var m = System.Text.RegularExpressions.Regex.Match(
                    text, @"map_template\s+(\S+\.pmp)\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                var rel = m.Groups[1].Value.Replace('\\', '/');
                var idx = rel.IndexOf("data/", System.StringComparison.OrdinalIgnoreCase);
                var tail = idx >= 0 ? rel.Substring(idx + "data/".Length) : rel;
                var absolute = Path.Combine(dataRoot, tail);
                if (!File.Exists(absolute)) continue;
                try
                {
                    var pmp = PmpParser.Parse(absolute);
                    source = $"tcl→{Path.GetFileName(absolute)}";
                    return pmp;
                }
                catch
                {
                    continue;
                }
            }

            var sibling = Path.ChangeExtension(src.FullName, ".pmp");
            if (sibling != null && File.Exists(sibling))
            {
                try
                {
                    var pmp = PmpParser.Parse(sibling);
                    source = $"sibling→{Path.GetFileName(sibling)}";
                    return pmp;
                }
                catch { /* fall through */ }
            }
            return null;
        }

        private static string? FindMatchingBdb(ushort maxCellId, string dataRoot)
        {
            if (maxCellId == 0) return null;
            var bdbFiles = new[] { "BlockDB0.BDB", "BlockDB1.BDB", "BlockDB2.BDB" }
                .Select(n => Path.Combine(dataRoot, n))
                .Where(File.Exists)
                .ToArray();
            foreach (var path in bdbFiles)
            {
                try
                {
                    var bdb = BdbParser.Parse(path);
                    if (maxCellId < bdb.Header.NDescriptors)
                    {
                        return Path.GetFileName(path);
                    }
                }
                catch
                {
                    continue;
                }
            }
            return null;
        }

        private static string GetSubFolder(FileInfo f, string dataRoot)
        {
            var dirName = f.DirectoryName ?? "";
            var rootFull = Path.GetFullPath(dataRoot).TrimEnd('\\', '/');
            var dirFull = Path.GetFullPath(dirName);
            if (dirFull.StartsWith(rootFull, System.StringComparison.OrdinalIgnoreCase))
            {
                var rel = dirFull.Substring(rootFull.Length).TrimStart('\\', '/');
                return rel.Length == 0 ? "." : rel.Replace('\\', '/');
            }
            return dirName;
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / 1024.0 / 1024.0:F1} MB";
        }
    }
}
