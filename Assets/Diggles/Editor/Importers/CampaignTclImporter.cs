#nullable enable

namespace Diggles.Editor.Importers
{
    using System.IO;
    using UnityEditor;
    using UnityEditor.AssetImporters;
    using UnityEngine;

    [ScriptedImporter(version: 5, ext: "tcl")]
    public sealed class CampaignTclImporter : ScriptedImporter
    {
        [Tooltip("Absolute path to the original data folder. Blank = the data root from diggles_config.json.")]
        [SerializeField] private string originalDataRoot = "";

        private string DataRoot => string.IsNullOrEmpty(this.originalDataRoot)
            ? Diggles.Runtime.DigglesConfig.ConfiguredDataRoot
            : this.originalDataRoot;

        [Tooltip("World size (Unity units) per map tile.")]
        [SerializeField] private float tileSize = 1f;

        [Tooltip("World size (Unity units) per heightmap unit. Default 3 makes " +
                 "PMP=4 (plain) ≈ Z=12 which matches dwarves' TCL z coords.")]
        [SerializeField] private float tileHeight = 3f;

        [Tooltip("Spawn .tcl object markers (dwarves, mushrooms, etc.)")]
        [SerializeField] private bool spawnObjectMarkers = true;

        [Tooltip("Marker radius in Unity units.")]
        [SerializeField] private float markerRadius = 0.4f;

        [Tooltip("Margin (in tiles) around bbox of non-default tiles when cropping.")]
        [SerializeField] private int cropMargin = 20;

        [Tooltip("Only run on TCL that has `map create` or 2+ `call templates/tcl/`.")]
        [SerializeField] private bool campaignRootOnly = true;

        [Header("first_level proc configuration")]
        [Tooltip("Cave index for left side (0..4 from cavelist).")]
        [SerializeField] private int firstLevelCaveLeft = 4;

        [Tooltip("Cave index for right side (0..4 from cavelist).")]
        [SerializeField] private int firstLevelCaveRight = 0;

        [Tooltip("Y offset for caves placement (from ylist).")]
        [SerializeField] private int firstLevelYOffset = 0;

        [Tooltip("Connection index (0..5 from conlist).")]
        [SerializeField] private int firstLevelConnectionIndex = 3;

        [Tooltip("Variant letter for left tunnel end (a/b/c/d/e/f).")]
        [SerializeField] private char firstLevelLeftTunnelVariant = 'a';

        [Tooltip("Variant letter for right tunnel end (a/b/c/d/e/f/g/h/k).")]
        [SerializeField] private char firstLevelRightTunnelVariant = 'a';

        public override void OnImportAsset(AssetImportContext ctx)
        {
            var text = File.ReadAllText(ctx.assetPath);
            var hasMapCreate = text.Contains("map create");
            var hasMultipleCalls = System.Text.RegularExpressions.Regex.Matches(
                text, @"call\s+templates/tcl/").Count >= 2;
            var isCampaignRoot = hasMapCreate || hasMultipleCalls;
            var root = new GameObject(Path.GetFileNameWithoutExtension(ctx.assetPath));

            if (this.campaignRootOnly && !isCampaignRoot)
            {
                ctx.AddObjectToAsset("Root", root);
                ctx.SetMainObject(root);
                return;
            }

            var firstLevelConfig = new FirstLevelConfig(
                CaveLeftIndex: this.firstLevelCaveLeft,
                CaveRightIndex: this.firstLevelCaveRight,
                YOffset: this.firstLevelYOffset,
                ConnectionIndex: this.firstLevelConnectionIndex,
                VoodooIndex: 0,
                LeftTunnelVariant: this.firstLevelLeftTunnelVariant,
                RightTunnelVariant: this.firstLevelRightTunnelVariant);

            var world = TclCampaignParser.ParseCampaign(
                ctx.assetPath, this.DataRoot, firstLevelConfig);
            var build = CampaignWorldBuilder.BuildWorld(world, this.DataRoot);
            foreach (var d in build.Diagnostics) ctx.LogImportWarning(d);

            var bbox = CampaignWorldBuilder.FindBbox(build, this.cropMargin);
            if (!bbox.HasValue)
            {
                ctx.LogImportWarning("No non-default tiles found.");
                ctx.AddObjectToAsset("Root", root);
                ctx.SetMainObject(root);
                return;
            }
            var (xMin, yMin, xMax, yMax) = bbox.Value;
            CampaignWorldBuilder.EmitMeshAndObjects(
                root, build, ctx, this.tileSize, this.tileHeight,
                xMin, yMin, xMax, yMax,
                this.spawnObjectMarkers, this.markerRadius,
                this.DataRoot);

            ctx.LogImportWarning(
                $"Campaign render: world={build.Width}x{build.Height}, " +
                $"crop=[{xMin}..{xMax}]x[{yMin}..{yMax}] " +
                $"({xMax - xMin + 1}x{yMax - yMin + 1}), " +
                $"chunks={build.ChunksLoaded}/{build.ChunksAttempted}, " +
                $"objects={build.Objects.Count}");

            ctx.AddObjectToAsset("Root", root);
            ctx.SetMainObject(root);
        }
    }
}
