#nullable enable

namespace Diggles.Runtime
{
    using UnityEngine;

    // Live tuning component, attached in the scene wherever convenient. DigglesWorldRuntime
    // finds it at world build, seeds it from diggles_config.json (only keys actually present
    // in the file override the scene values), and binds the live targets. No component in the
    // scene means the json keys apply directly.
    //
    // * Tweak the sliders in the Inspector during Play — values apply the same frame.
    // * Press "Save to diggles_config.json" (custom inspector button; also in the context
    // menu) — the keys land in the config and persist for every later run. After a save,
    // the json keys exist and seed/override the scene values at every boot, so the Save
    // button is then the way to change the persistent values.
    //
    // Add future knobs here and mirror them in DigglesConfig (ConfigData field + Resolve/Save).
    public sealed class DigglesTuning : MonoBehaviour
    {
        [Header("Underground darkness (BASELIGHT)")]
        [Tooltip("Darkness floor for unlit underground: 0 = the native pitch-dark law, " +
                 "1 = darkness off. Native flat-tunnel base is ~0.2; 0.6 = the Play-calibrated default.")]
        [Range(0f, 1f)] public float baseLightMin = 0.6f;

        [Header("Torch / class-light brightness")]
        [Tooltip("Ceiling for the ACCUMULATED torch light (the native diffuse saturation " +
                 "min(I,255) = 1.0): overlapping torches plateau here. >1 = brighter-than-native " +
                 "stacking, <1 dims all torches.")]
        [Range(0f, 4f)] public float classLightCap = 1f;

        Diggles.Runtime.World.BaseLightPresenter? _baseLight;
        RuntimeWorldSpawner? _spawner;
        float _appliedMin = -1f, _appliedCap = -1f;

        // Wired by DigglesWorldRuntime at world build (after the config resolve seeded the
        // public fields). Applies immediately.
        public void Bind(Diggles.Runtime.World.BaseLightPresenter? baseLight, RuntimeWorldSpawner? spawner)
        {
            _baseLight = baseLight;
            _spawner = spawner;
            _appliedMin = _appliedCap = -1f;   // force a push
            Apply();
        }

        void Update() => Apply();

        void Apply()
        {
            if (_baseLight != null && !Mathf.Approximately(_appliedMin, baseLightMin))
            {
                _baseLight.MinFactor = baseLightMin;
                _baseLight.PushParams();
                _appliedMin = baseLightMin;
            }
            if (_spawner != null && !Mathf.Approximately(_appliedCap, classLightCap))
            {
                _spawner.ClassLightCap = classLightCap;   // uploaded with the next light refill
                _appliedCap = classLightCap;
            }
        }

        // The Save action (the custom inspector button routes here; ContextMenu keeps it
        // reachable without the editor script).
        [ContextMenu("Save to diggles_config.json")]
        public void SaveToConfig()
        {
            string path = DigglesConfig.SaveTuning(baseLightMin, classLightCap);
            LastSavedPath = path.Length > 0 ? path : "SAVE FAILED — see Console";
        }

        // Shown by the custom inspector under the button.
        [System.NonSerialized] public string LastSavedPath = "";
    }
}
