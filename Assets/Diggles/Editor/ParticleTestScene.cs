#if UNITY_EDITOR
namespace Diggles.Editor
{
    using System.IO;
    using Diggles.Runtime;
    using UnityEditor;
    using UnityEditor.SceneManagement;
    using UnityEngine;

    // Diggles → Particle Test Scene: creates an empty scene with a single ParticleTestHarness object
    // (which builds the grid of all 43 particle types + camera + presenter at Play), saves it, and opens
    // it. Press Play to see every particle type at once and calibrate fpsRef/sizeScale/_Intensity.
    public static class ParticleTestScene
    {
        const string ScenePath = "Assets/Diggles/Scenes/Particle_Test.unity";

        [MenuItem("Diggles/Particle Test Scene")]
        public static void CreateAndOpen()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var go = new GameObject("ParticleTestHarness");
            go.AddComponent<ParticleTestHarness>();

            Directory.CreateDirectory("Assets/Diggles/Scenes");
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();
            Debug.Log($"[Diggles] Particle test scene created at {ScenePath}. Press Play; drag to orbit, scroll to zoom.");
        }
    }
}
#endif
