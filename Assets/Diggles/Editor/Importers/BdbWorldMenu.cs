#nullable enable

namespace Diggles.Editor.Importers
{
    using Diggles.Runtime.World;
    using System.IO;
    using UnityEditor;
    using UnityEngine;

    /// <summary>Editor menu items that build a static world into the open scene from the original data.</summary>
    public static class BdbWorldMenu
    {
        static string DataRoot => Diggles.Runtime.DigglesConfig.ConfiguredDataRoot;

        [MenuItem("Diggles/Build Full World From Campaign.tcl", priority = 198)]
        public static void BuildFromCampaign()
        {
            string dataRoot = DataRoot;
            string bdbPath = Path.Combine(dataRoot, "BlockDB2.BDB");
            string campaignTcl = Path.Combine(dataRoot, "Scripts", "gameplay", "Campaign.tcl");
            if (!File.Exists(bdbPath)) { Debug.LogError($"BDB missing: {bdbPath}"); return; }
            if (!File.Exists(campaignTcl)) { Debug.LogError($"Campaign.tcl missing: {campaignTcl}"); return; }

            var existing = GameObject.Find("DigglesCampaignWorld");
            if (existing != null) Object.DestroyImmediate(existing);
            var root = new GameObject("DigglesCampaignWorld");
            root.transform.rotation = Quaternion.Euler(-90f, 0f, 0f);

            CampaignBrowserBuilder.BuildFromCampaign(root, null, bdbPath, campaignTcl, dataRoot,
                renderDefaultEarth: false);

            Selection.activeGameObject = root;
            SceneView.lastActiveSceneView?.FrameSelected();
        }

        [MenuItem("Diggles/Build From Real PMP", priority = 199)]
        public static void BuildFromPmp()
        {
            string dataRoot = DataRoot;
            string bdbPath = Path.Combine(dataRoot, "BlockDB2.BDB");
            string pmpPath = Path.Combine(dataRoot, "Templates", "unq_start.pmp");
            if (!File.Exists(bdbPath)) { Debug.LogError($"BDB missing: {bdbPath}"); return; }
            if (!File.Exists(pmpPath)) { Debug.LogError($"PMP missing: {pmpPath}"); return; }

            var existing = GameObject.Find("DigglesPmpWorld");
            if (existing != null) Object.DestroyImmediate(existing);
            var root = new GameObject("DigglesPmpWorld");
            root.transform.rotation = Quaternion.Euler(-90f, 0f, 0f);

            BdbPmpWorldBuilder.BuildWorld(root, null, new BdbPmpWorldBuilder.BuildArgs
            {
                BdbPath = bdbPath,
                PmpPath = pmpPath,
                // urw_unq_start spawns at (208, 0) per Campaign.tcl.
                OffsetX = 208,
                OffsetY = 0,
            });

            Selection.activeGameObject = root;
            SceneView.lastActiveSceneView?.FrameSelected();
        }
    }
}
