#nullable enable

namespace Diggles.EditorTools
{
    using Diggles.Runtime;
    using UnityEditor;
    using UnityEngine;

    // Inspector for the live-tuning component: the default sliders plus a Save button
    // that writes the values back into diggles_config.json (the same keys the boot
    // resolve reads), so no hand-editing and no restart are needed.
    [CustomEditor(typeof(DigglesTuning))]
    public sealed class DigglesTuningEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var t = (DigglesTuning)target;

            EditorGUILayout.Space(6);
            if (GUILayout.Button("Save to diggles_config.json", GUILayout.Height(28)))
                t.SaveToConfig();
            if (!string.IsNullOrEmpty(t.LastSavedPath))
                EditorGUILayout.HelpBox(t.LastSavedPath, MessageType.Info);
            EditorGUILayout.HelpBox(
                "Values apply live in Play mode. Save writes them to " +
                "diggles_config.json (next to the exe, otherwise StreamingAssets); they are " +
                "picked up on every following run.", MessageType.None);
        }
    }
}
