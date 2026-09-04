#nullable enable

namespace Diggles.Runtime
{
    using System.Collections.Generic;
    using UnityEngine;

    /// <summary>
    /// Authorable registry mapping Tcl class names → <see cref="DigglesClassAsset"/> descriptors.
    /// Assign it on the scene's DigglesGameInstaller; it is registered in the VContainer and handed
    /// to <c>RuntimeWorldSpawner</c> so spawns can apply per-class overrides. An empty list / no
    /// matching entry → the class spawns exactly as before.
    /// Doc: Docs/RENDERING_LIGHTING_RESEARCH.md §5/§6.
    /// </summary>
    [CreateAssetMenu(menuName = "Diggles/Class Asset Registry", fileName = "DigglesClassRegistry", order = 1)]
    public sealed class DigglesClassAssetRegistry : ScriptableObject
    {
        [Tooltip("Per-class override descriptors. Looked up by DigglesClassAsset.className.")]
        public List<DigglesClassAsset> classes = new();

        private Dictionary<string, DigglesClassAsset>? _byName;

        /// <summary>The descriptor for a Tcl class name, or null if none (→ spawn unchanged).</summary>
        public DigglesClassAsset? Resolve(string className)
        {
            if (string.IsNullOrEmpty(className)) return null;
            if (_byName == null)
            {
                _byName = new Dictionary<string, DigglesClassAsset>();
                foreach (var c in classes)
                    if (c != null && !string.IsNullOrEmpty(c.className))
                        _byName[c.className] = c;   // last entry wins on duplicate names
            }
            return _byName.TryGetValue(className, out var a) ? a : null;
        }

        // Rebuild the lookup if the list is edited (inspector / runtime).
        private void OnEnable() => _byName = null;
        private void OnValidate() => _byName = null;
    }
}
