#nullable enable

namespace Diggles.Runtime
{
    using System.Collections.Generic;
    using UnityEngine;

    /// <summary>
    /// Per-Tcl-class authorable override descriptor (the modularity backbone).
    ///
    /// Optional: a class with no asset spawns exactly as before (Tcl/TSV-driven). Where a field
    /// is set (non-empty string / non-null reference / not <c>Inherit</c>), it overrides the
    /// Tcl/TSV-derived default at spawn time. This lets a specific class (and later a mod)
    /// replace its model / material / shader / animation clips / collider without touching code
    /// or the shipped data — and is the future hook for prefab spawning.
    ///
    /// Authored as a Unity asset (Create ▸ Diggles ▸ Class Asset), matching the project's
    /// "author in Unity" direction. Resolved by class name through a registry (next step).
    /// Doc: Docs/RENDERING_LIGHTING_RESEARCH.md §5/§6.
    /// </summary>
    [CreateAssetMenu(menuName = "Diggles/Class Asset", fileName = "DigglesClass", order = 0)]
    public sealed class DigglesClassAsset : ScriptableObject
    {
        [Tooltip("Tcl class name this describes (must match def_class, e.g. \"Zwerg\").")]
        public string className = "";

        // ---- Model ----------------------------------------------------------
        [Header("Model  (blank = TSV / Tcl default)")]
        [Tooltip("Override the .3db object this class renders with. Blank = class_model_map_v2.tsv.")]
        public string modelObjectOverride = "";

        [Tooltip("Override the default animation (\"obj.anim\" or bare \"anim\"). Blank = class_defaultanim / TSV.")]
        public string defaultAnimOverride = "";

        [Tooltip("Override the spawn-visibility of this class.")]
        public VisibleOverride visible = VisibleOverride.Inherit;

        // ---- Material / shader ---------------------------------------------
        [Header("Material / shader  (none = DigglesShaders default)")]
        [Tooltip("Override material applied to every submesh of this class. Wins over shaderOverride.")]
        public Material? materialOverride;

        [Tooltip("Override only the shader (a material is still built per .3db material). Used when materialOverride is null.")]
        public Shader? shaderOverride;

        // ---- Animation ------------------------------------------------------
        [Header("Animation  (merged on top of the class anim tables)")]
        [Tooltip("Extra/replacement logical-state → clip mappings. clip = \"obj.anim\".")]
        public List<StateClip> animMapOverrides = new();

        // ---- Collider -------------------------------------------------------
        [Header("Collider")]
        public ColliderPolicy collider = ColliderPolicy.Inherit;

        [Tooltip("Explicit collider size in world units (used only with ColliderPolicy.ExplicitBox).")]
        public Vector3 colliderSize = Vector3.one;

        // --------------------------------------------------------------------
        // NOTE: a prefab-spawn field and a behaviour-component hook are intentionally
        // deferred to a later step (the prefab-spawn refactor); v1 layers overrides onto
        // the existing procedural spawn so nothing breaks.

        [System.Serializable]
        public struct StateClip
        {
            [Tooltip("Logical state, e.g. \"walkloop\", \"digfront\".")]
            public string state;
            [Tooltip("Concrete clip in \"obj.anim\" form.")]
            public string clip;
        }

        public enum VisibleOverride { Inherit, ForceVisible, ForceHidden }
        public enum ColliderPolicy { Inherit, AutoFromBounds, ExplicitBox, None }
    }
}
