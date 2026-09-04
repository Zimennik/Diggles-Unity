#nullable enable

namespace Diggles.Editor.Importers
{
    using Diggles.Runtime.World;
    using System.IO;
    using System.Linq;
    using UnityEditor;
    using UnityEngine;

    /// <summary>
    /// Editor window to load any Campaign*.tcl / Sandbox*.tcl into the scene.
    /// Uses the real BDB-based pipeline: parses the Tcl → loads each template's
    /// PMP → blits block_idx into the world → emits mesh from BDB block catalogue.
    /// </summary>
    public sealed class DigglesCampaignBrowser : EditorWindow
    {
        private string originalDataRoot = "";
        private string bdbName = "BlockDB2.BDB";
        private string[] campaignFiles = System.Array.Empty<string>();
        private int selectedIdx;
        private bool renderDefaultEarth;
        private bool rotateUpright = true;

        private int firstLevelCaveLeft = 4;
        private int firstLevelCaveRight = 0;
        private int firstLevelYOffset;
        private int firstLevelConnectionIndex = 3;
        private int firstLevelVoodooIndex;
        private string firstLevelLeftTunnelVariant = "a";
        private string firstLevelRightTunnelVariant = "a";

        // Marker Y transform — defaults from engine constants:
        // world.Y = TCL_Z * 0.5 + 7.0 (FUN_004c6b20 in Diggles.exe)
        // 0.5 = DAT_0073708c, 7.0 = DAT_00737520.
        private float markerHeightScale = 0.5f;
        private float markerYOffset = 7.0f;
        private float markerScale = 1.0f;

        private string statusLog = "";
        private Vector2 logScroll;

        // Texture override config (lets you tweak textures without rebuilding)
        private string topStage0 = "Ground/Gnd_B{0:D2}.tga";
        private string topStage1 = "Ground/detail/urw_1111.tga";
        private string sideStage0 = "Ground/Gnd_A{0:D2}.tga";
        private string sideStage1 = "Ground/Gnd_A{0:D2}.tga";
        private string bottomStage0 = "Ground/Gnd_B{0:D2}.tga";
        private string bottomStage1 = "Ground/Gnd_B{0:D2}.tga";
        private bool randomizeVariant = true;
        private int fixedVariant = 1;

        private static readonly string[] CaveLabels =
        {
            "0: urw_hol_001_a",
            "1: urw_hol_014_b",
            "2: urw_hol_018_c",
            "3: urw_hol_019_b",
            "4: urw_hol_027_a",
        };

        private static readonly string[] ConnLabels =
        {
            "0: 013→002→003 (x+0)",
            "1: 013→003→002 (x+0)",
            "2: 002→013→003 (x+0)",
            "3: 003→013→002 (x+4)",
            "4: 003→002→013 (x+4)",
            "5: 002→003→013 (x+4)",
        };

        [MenuItem("Diggles/Campaign Browser")]
        public static void ShowWindow()
        {
            GetWindow<DigglesCampaignBrowser>("Diggles Campaign Browser");
        }

        private void OnEnable()
        {
            if (string.IsNullOrEmpty(this.originalDataRoot))
                this.originalDataRoot = Diggles.Runtime.DigglesConfig.ConfiguredDataRoot;
            this.RescanCampaigns();
        }

        private void RescanCampaigns()
        {
            var dir = Path.Combine(this.originalDataRoot, "Scripts", "gameplay");
            if (!Directory.Exists(dir))
            {
                this.campaignFiles = System.Array.Empty<string>();
                return;
            }
            this.campaignFiles = Directory.GetFiles(dir, "*.tcl")
                .Where(p =>
                {
                    var name = Path.GetFileNameWithoutExtension(p);
                    return name.StartsWith("Campaign") || name.StartsWith("Sandbox");
                })
                .OrderBy(p => p)
                .ToArray();
            this.selectedIdx = System.Math.Clamp(this.selectedIdx, 0,
                System.Math.Max(0, this.campaignFiles.Length - 1));
        }

        private string GetVoodooLabel(int idx)
        {
            // The voodoo list count depends on ConnectionIndex; show the safer range.
            if (this.firstLevelConnectionIndex < 2) return $"{idx} (list2, 0..2)";
            if (this.firstLevelConnectionIndex < 4) return $"{idx} (concat, 0..8)";
            return $"{idx} (list1, 0..5)";
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Diggles Campaign Browser", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            // --- Data root + rescan
            EditorGUILayout.BeginHorizontal();
            this.originalDataRoot = EditorGUILayout.TextField("Original Data Root", this.originalDataRoot);
            if (GUILayout.Button("Rescan", GUILayout.Width(80))) this.RescanCampaigns();
            EditorGUILayout.EndHorizontal();

            this.bdbName = EditorGUILayout.TextField("BDB file", this.bdbName);

            EditorGUILayout.Space();

            // --- Campaign picker
            if (this.campaignFiles.Length == 0)
            {
                EditorGUILayout.HelpBox(
                    $"No Campaign*.tcl / Sandbox*.tcl in {this.originalDataRoot}/Scripts/gameplay",
                    MessageType.Warning);
                return;
            }

            var labels = this.campaignFiles.Select(p => Path.GetFileName(p)).ToArray();
            this.selectedIdx = EditorGUILayout.Popup("Campaign / Sandbox", this.selectedIdx, labels);

            EditorGUILayout.Space();

            // --- Render options
            EditorGUILayout.LabelField("Render options", EditorStyles.boldLabel);
            this.rotateUpright = EditorGUILayout.Toggle(
                new GUIContent("Rotate upright (map on X/Y)",
                    "Tilt root -90° around X so the world lies on Unity's X/Y plane (Z = elevation)."),
                this.rotateUpright);
            this.renderDefaultEarth = EditorGUILayout.Toggle(
                new GUIContent("Render entire 512×640 world",
                    "If on, emit mesh for every tile (including the default solid earth around playable area). Heavy."),
                this.renderDefaultEarth);

            EditorGUILayout.Space();

            // --- Texture overrides (live-tweakable)
            EditorGUILayout.LabelField("Texture overrides", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Paths relative to Original Data Root. Use {0:D2} for the variant number (01..15). " +
                "Stage 0 = base (shown where vertex.alpha = 0). Stage 1 = overlay (shown where = 1).",
                MessageType.None);
            this.topStage0 = EditorGUILayout.TextField("Top stage 0", this.topStage0);
            this.topStage1 = EditorGUILayout.TextField("Top stage 1", this.topStage1);
            this.sideStage0 = EditorGUILayout.TextField("Side stage 0", this.sideStage0);
            this.sideStage1 = EditorGUILayout.TextField("Side stage 1", this.sideStage1);
            this.bottomStage0 = EditorGUILayout.TextField("Bottom stage 0", this.bottomStage0);
            this.bottomStage1 = EditorGUILayout.TextField("Bottom stage 1", this.bottomStage1);
            this.randomizeVariant = EditorGUILayout.Toggle("Randomize variant per tile", this.randomizeVariant);
            if (!this.randomizeVariant)
                this.fixedVariant = EditorGUILayout.IntSlider("Fixed variant", this.fixedVariant, 1, 15);

            EditorGUILayout.Space();

            // --- Marker placement tuning
            EditorGUILayout.LabelField("Marker tuning", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Marker Y formula:  world.Y = TCL_Z * height_scale + Y_offset\n" +
                "Experiment: scale 0.5 + offset 7.1 matched terrain visually.",
                MessageType.None);
            this.markerHeightScale = EditorGUILayout.Slider(
                new GUIContent("Height (Y) scale", "Multiplier for the marker height (Tcl Z). 0.5 = compress by half"),
                this.markerHeightScale, 0.25f, 4f);
            this.markerYOffset = EditorGUILayout.FloatField(
                new GUIContent("Height (Y) offset", "Added to world Y after scaling"),
                this.markerYOffset);
            this.markerScale = EditorGUILayout.Slider(
                new GUIContent("Model scale", "Multiplier for all marker meshes"),
                this.markerScale, 0.01f, 5f);

            EditorGUILayout.Space();

            EditorGUILayout.LabelField("first_level procgen", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Only used when the Campaign calls first_level (e.g. main Campaign.tcl). " +
                "These pick which random cave/tunnel/voodoo layout to instantiate.",
                MessageType.None);
            this.firstLevelCaveLeft = EditorGUILayout.Popup(
                "Left cave", this.firstLevelCaveLeft, CaveLabels);
            this.firstLevelCaveRight = EditorGUILayout.Popup(
                "Right cave", this.firstLevelCaveRight, CaveLabels);
            this.firstLevelYOffset = EditorGUILayout.IntField("Y offset", this.firstLevelYOffset);
            this.firstLevelConnectionIndex = EditorGUILayout.Popup(
                "Connection", this.firstLevelConnectionIndex, ConnLabels);
            this.firstLevelVoodooIndex = EditorGUILayout.IntField(
                new GUIContent("Voodoo layout idx", this.GetVoodooLabel(this.firstLevelVoodooIndex)),
                this.firstLevelVoodooIndex);
            this.firstLevelLeftTunnelVariant = EditorGUILayout.TextField(
                "Left tunnel variant", this.firstLevelLeftTunnelVariant);
            this.firstLevelRightTunnelVariant = EditorGUILayout.TextField(
                "Right tunnel variant", this.firstLevelRightTunnelVariant);

            EditorGUILayout.Space();

            // --- Action buttons
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Parse only (no render)")) this.RunParseOnly();
            if (GUILayout.Button("Load into scene")) this.LoadIntoScene();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Log", EditorStyles.boldLabel);
            this.logScroll = EditorGUILayout.BeginScrollView(
                this.logScroll, GUILayout.MinHeight(200));
            EditorGUILayout.SelectableLabel(this.statusLog,
                EditorStyles.textArea, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
        }

        private FirstLevelConfig BuildFirstLevelConfig() => new(
            CaveLeftIndex: this.firstLevelCaveLeft,
            CaveRightIndex: this.firstLevelCaveRight,
            YOffset: this.firstLevelYOffset,
            ConnectionIndex: this.firstLevelConnectionIndex,
            VoodooIndex: this.firstLevelVoodooIndex,
            LeftTunnelVariant: string.IsNullOrEmpty(this.firstLevelLeftTunnelVariant)
                ? 'a' : this.firstLevelLeftTunnelVariant[0],
            RightTunnelVariant: string.IsNullOrEmpty(this.firstLevelRightTunnelVariant)
                ? 'a' : this.firstLevelRightTunnelVariant[0]);

        private string? ResolveSelectedCampaign()
        {
            if (this.selectedIdx < 0 || this.selectedIdx >= this.campaignFiles.Length) return null;
            return this.campaignFiles[this.selectedIdx];
        }

        private void RunParseOnly()
        {
            var path = this.ResolveSelectedCampaign();
            if (path == null) return;
            var world = TclCampaignParser.ParseCampaign(
                path, this.originalDataRoot, this.BuildFirstLevelConfig());

            var diag = string.Join("\n", world.Diagnostics);
            var tplCount = world.Templates.Count;
            int leafCount = 0, withPmp = 0;
            void Walk(System.Collections.Generic.IEnumerable<CampaignTemplate> ts)
            {
                foreach (var t in ts)
                {
                    if (t.SubChunks.Count == 0) { leafCount++; if (t.PmpPath != null && File.Exists(t.PmpPath)) withPmp++; }
                    else Walk(t.SubChunks);
                }
            }
            Walk(world.Templates);

            this.statusLog =
                $"Campaign: {Path.GetFileName(path)}\n" +
                $"World: {world.WorldWidth}×{world.WorldHeight}\n" +
                $"Top-level templates: {tplCount}\n" +
                $"Leaf templates: {leafCount} ({withPmp} with PMP file)\n\n" +
                diag;
        }

        private void LoadIntoScene()
        {
            var path = this.ResolveSelectedCampaign();
            if (path == null) return;

            var bdbPath = Path.Combine(this.originalDataRoot, this.bdbName);
            if (!File.Exists(bdbPath))
            {
                this.statusLog = $"BDB not found: {bdbPath}";
                Debug.LogError(this.statusLog);
                return;
            }

            var rootName = $"Diggles__{Path.GetFileNameWithoutExtension(path)}";
            var existing = GameObject.Find(rootName);
            if (existing != null) DestroyImmediate(existing);

            var root = new GameObject(rootName);
            if (this.rotateUpright)
                root.transform.rotation = Quaternion.Euler(-90f, 0f, 0f);

            BdbPmpWorldBuilder.MarkerHeightScale = this.markerHeightScale;
            BdbPmpWorldBuilder.MarkerYOffset = this.markerYOffset;
            BdbPmpWorldBuilder.MarkerScale = this.markerScale;
            BdbPmpWorldBuilder.CurrentTextureOverrides = new BdbPmpWorldBuilder.TextureOverrides
            {
                TopStage0Pattern    = string.IsNullOrWhiteSpace(this.topStage0)    ? null : this.topStage0,
                TopStage1Pattern    = string.IsNullOrWhiteSpace(this.topStage1)    ? null : this.topStage1,
                SideStage0Pattern   = string.IsNullOrWhiteSpace(this.sideStage0)   ? null : this.sideStage0,
                SideStage1Pattern   = string.IsNullOrWhiteSpace(this.sideStage1)   ? null : this.sideStage1,
                BottomStage0Pattern = string.IsNullOrWhiteSpace(this.bottomStage0) ? null : this.bottomStage0,
                BottomStage1Pattern = string.IsNullOrWhiteSpace(this.bottomStage1) ? null : this.bottomStage1,
                RandomizeVariant    = this.randomizeVariant,
                FixedVariant        = this.fixedVariant,
            };

            CampaignBrowserBuilder.BuildFromCampaign(
                root, null,
                bdbPath, path, this.originalDataRoot,
                renderDefaultEarth: this.renderDefaultEarth,
                firstLevelConfig: this.BuildFirstLevelConfig());

            Selection.activeGameObject = root;
            SceneView.lastActiveSceneView?.FrameSelected();

            this.statusLog =
                $"Loaded: {Path.GetFileName(path)} → '{rootName}'\n" +
                $"BDB: {this.bdbName}\n" +
                $"first_level config: caves=L{this.firstLevelCaveLeft}/R{this.firstLevelCaveRight} " +
                $"conn={this.firstLevelConnectionIndex} vodo={this.firstLevelVoodooIndex} " +
                $"yOff={this.firstLevelYOffset}\n" +
                $"See Unity Console for full pipeline diagnostics.\n";
        }
    }
}
