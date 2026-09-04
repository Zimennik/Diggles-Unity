#nullable enable

namespace Diggles.Editor.Importers
{
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using Diggles.Runtime;
    using UnityEditor;
    using UnityEngine;

    public sealed class DigglesModelBrowser : EditorWindow
    {
        private const string BrowserAssetFolder = "Assets/Diggles/Browser";
        private const string BrowserAssetFile = "Current.3db";
        private const string EditorPrefsDataRoot = "Diggles.ModelBrowser.DataRoot";

        private string dataRoot = "";
        private string filter = "";
        private Vector2 scrollPosition;
        private List<FileInfo> files = new();
        private int selectedIndex = -1;
        private GameObject? currentInstance;
        private string currentMetadataText = "";
        private int meshIndexToShow = 0;
        private int totalMeshesInCurrent;
        private bool autoCycle;
        private float autoCycleFps = 8f;
        private double lastCycleTime;

        private bool flipWinding;
        private bool flipNormals;
        private bool useProvidedNormals = true;
        private bool transformNormalsWithVertices;
        private float extraYRotationDegrees;
        private bool flipUvV = true;
        private bool doubleSided;
        private ThreeDbScriptedImporter.AxisConvention axisConvention = ThreeDbScriptedImporter.AxisConvention.RotateX180;
        private ThreeDbScriptedImporter.DequantizeMode dequantizeMode = ThreeDbScriptedImporter.DequantizeMode.UnitCubeCentered;
        private bool showAdvanced;

        [MenuItem("Tools/Diggles/Model Browser")]
        public static void ShowWindow()
        {
            var window = GetWindow<DigglesModelBrowser>("Diggles Models");
            window.minSize = new Vector2(700, 400);
        }

        private void OnEnable()
        {
            this.dataRoot = EditorPrefs.GetString(EditorPrefsDataRoot, this.dataRoot);
            if (string.IsNullOrEmpty(this.dataRoot)) this.dataRoot = Diggles.Runtime.DigglesConfig.ConfiguredDataRoot;
            this.RefreshFileList();
            EditorApplication.update += this.OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= this.OnEditorUpdate;
            EditorPrefs.SetString(EditorPrefsDataRoot, this.dataRoot);
        }

        private void OnEditorUpdate()
        {
            if (!this.autoCycle) return;
            if (this.totalMeshesInCurrent <= 1) return;
            var now = EditorApplication.timeSinceStartup;
            if (now - this.lastCycleTime < 1.0 / this.autoCycleFps) return;
            this.lastCycleTime = now;
            this.meshIndexToShow = (this.meshIndexToShow + 1) % this.totalMeshesInCurrent;
            this.ReimportCurrent();
            this.Repaint();
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
            EditorGUILayout.BeginVertical(GUILayout.Width(280));

            this.filter = EditorGUILayout.TextField("Filter", this.filter);

            EditorGUILayout.LabelField($"{this.files.Count} .3db files");

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

                if (GUILayout.Button(
                        $"{f.Name}\n   {FormatSize(f.Length)}",
                        GUILayout.Height(36)))
                {
                    this.selectedIndex = i;
                    this.meshIndexToShow = 0;
                    this.autoCycle = false;
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
                    "Select a model from the list to preview.",
                    MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            var f = this.files[this.selectedIndex];
            EditorGUILayout.LabelField(f.Name, EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"Size: {FormatSize(f.Length)}");
            EditorGUILayout.Space();

            if (!string.IsNullOrEmpty(this.currentMetadataText))
            {
                EditorGUILayout.SelectableLabel(
                    this.currentMetadataText,
                    EditorStyles.helpBox,
                    GUILayout.MinHeight(120));
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Mesh / animation frame", EditorStyles.boldLabel);

            if (this.totalMeshesInCurrent > 1)
            {
                EditorGUILayout.BeginHorizontal();
                var newIdx = EditorGUILayout.IntSlider(
                    "Mesh index", this.meshIndexToShow, 0, this.totalMeshesInCurrent - 1);
                if (newIdx != this.meshIndexToShow)
                {
                    this.meshIndexToShow = newIdx;
                    this.ReimportCurrent();
                }
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                this.autoCycle = EditorGUILayout.ToggleLeft(
                    "Auto-cycle (play animation)", this.autoCycle, GUILayout.Width(220));
                this.autoCycleFps = EditorGUILayout.Slider("FPS", this.autoCycleFps, 1f, 30f);
                EditorGUILayout.EndHorizontal();
            }
            else
            {
                EditorGUILayout.LabelField("(only one mesh in this file)");
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
            this.flipWinding = EditorGUILayout.Toggle("Flip Winding", this.flipWinding);
            this.useProvidedNormals = EditorGUILayout.Toggle(
                "Use Provided Normals (sphere.dat)", this.useProvidedNormals);
            using (new EditorGUI.DisabledScope(!this.useProvidedNormals))
            {
                this.flipNormals = EditorGUILayout.Toggle("  Flip Normals", this.flipNormals);
                this.transformNormalsWithVertices = EditorGUILayout.Toggle(
                    "  Apply Axis to Normals", this.transformNormalsWithVertices);
            }
            this.flipUvV = EditorGUILayout.Toggle("Flip UV V", this.flipUvV);
            this.doubleSided = EditorGUILayout.Toggle("Double Sided (_Cull=Off)", this.doubleSided);
            this.extraYRotationDegrees = EditorGUILayout.FloatField(
                "Extra Y Rotation (deg)", this.extraYRotationDegrees);
            this.axisConvention = (ThreeDbScriptedImporter.AxisConvention)
                EditorGUILayout.EnumPopup("Axis Convention", this.axisConvention);
            this.dequantizeMode = (ThreeDbScriptedImporter.DequantizeMode)
                EditorGUILayout.EnumPopup("Dequantize Mode", this.dequantizeMode);
            if (EditorGUI.EndChangeCheck()) changed = true;

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Apply (reimport)", GUILayout.Width(140)) || changed)
            {
                this.ReimportCurrent();
            }
            if (GUILayout.Button("Reset defaults", GUILayout.Width(120)))
            {
                this.flipWinding = false;
                this.flipNormals = false;
                this.useProvidedNormals = true;
                this.transformNormalsWithVertices = false;
                this.extraYRotationDegrees = 0f;
                this.flipUvV = true;
                this.doubleSided = false;
                this.axisConvention = ThreeDbScriptedImporter.AxisConvention.RotateX180;
                this.dequantizeMode = ThreeDbScriptedImporter.DequantizeMode.UnitCubeCentered;
                this.ReimportCurrent();
            }
            EditorGUILayout.EndHorizontal();
        }

        private void RefreshFileList()
        {
            var dir = Path.Combine(this.dataRoot, "3db");
            if (Directory.Exists(dir))
            {
                this.files = Directory.GetFiles(dir, "*.3db")
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
                var model = ThreeDbParser.Parse(stream, src.Length);
                this.totalMeshesInCurrent = model.Meshes.Count;
                this.currentMetadataText = BuildMetadataText(model);
            }
            catch (System.Exception e)
            {
                this.currentMetadataText = $"Failed to parse: {e.Message}";
                this.totalMeshesInCurrent = 0;
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

            var importer = AssetImporter.GetAtPath(dst) as ThreeDbScriptedImporter;
            if (importer != null)
            {
                var so = new SerializedObject(importer);
                so.FindProperty("maxMeshesToImport").intValue = 1;
                so.FindProperty("startMeshIndex").intValue = this.meshIndexToShow;
                so.FindProperty("originalDataRoot").stringValue = this.dataRoot;
                so.FindProperty("flipWinding").boolValue = this.flipWinding;
                so.FindProperty("flipNormals").boolValue = this.flipNormals;
                so.FindProperty("useProvidedNormals").boolValue = this.useProvidedNormals;
                so.FindProperty("transformNormalsWithVertices").boolValue =
                    this.transformNormalsWithVertices;
                so.FindProperty("extraYRotationDegrees").floatValue =
                    this.extraYRotationDegrees;
                so.FindProperty("flipUvV").boolValue = this.flipUvV;
                so.FindProperty("doubleSided").boolValue = this.doubleSided;
                so.FindProperty("axisConvention").enumValueIndex = (int)this.axisConvention;
                so.FindProperty("dequantizeMode").enumValueIndex = (int)this.dequantizeMode;
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
                    this.currentInstance.name = $"Browser_{Path.GetFileNameWithoutExtension(src.Name)}_{this.meshIndexToShow}";
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

        private static string BuildMetadataText(ThreeDbModel model)
        {
            var subMeshes = model.Meshes.Sum(m => m.SubMeshes.Count);
            var verts = model.VLists.Sum(v => v.Length);
            var tris = model.IndexLists.Sum(i => i.Length / 3);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Model name: {model.ModelName}");
            sb.AppendLine($"Materials:  {model.Materials.Count}");
            sb.AppendLine($"Meshes:     {model.Meshes.Count} (≈ animation frames)");
            sb.AppendLine($"Submeshes:  {subMeshes}");
            sb.AppendLine($"Anims:      {model.Anims.Count}");
            sb.AppendLine($"Vertices:   {verts:N0} across {model.VLists.Count} lists");
            sb.AppendLine($"Triangles:  {tris:N0} across {model.IndexLists.Count} lists");
            sb.AppendLine($"Objects:    {model.Objects.Count}");
            sb.AppendLine($"Shadows:    {model.Shadows.Count}");
            sb.AppendLine($"CMaps:      {model.CMaps.Count}");
            sb.AppendLine();
            sb.AppendLine("Materials:");
            foreach (var m in model.Materials.Take(10))
            {
                sb.AppendLine($"  • {m.Name}");
            }
            if (model.Materials.Count > 10)
            {
                sb.AppendLine($"  … +{model.Materials.Count - 10} more");
            }
            if (model.Anims.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Animations:");
                foreach (var a in model.Anims.Take(10))
                {
                    sb.AppendLine($"  • {a.Name1} ({a.Indices.Length} frames)");
                }
                if (model.Anims.Count > 10)
                {
                    sb.AppendLine($"  … +{model.Anims.Count - 10} more");
                }
            }
            return sb.ToString();
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / 1024.0 / 1024.0:F1} MB";
        }
    }
}
