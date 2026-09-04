#nullable enable

namespace Diggles.Runtime
{
    using UnityEngine;

    /// <summary>
    /// Central resolution of the three Diggles custom shaders, preserving the original
    /// per-call fallback chains. This is the single switch-point the rendering modernization
    /// hooks into: a per-class descriptor (DigglesClassAsset) / RenderConfig can override the
    /// material/shader on top of these defaults, and the defaults can later swap to
    /// ShaderGraph Lit variants here in one place.
    ///
    /// Results are cached (Shader.Find is not free); a failed lookup is not cached, so it
    /// retries on the next access. Doc: Docs/RENDERING_LIGHTING_RESEARCH.md §8.
    /// </summary>
    public static class DigglesShaders
    {
        private static Shader? _model;
        private static Shader? _ground;
        private static Shader? _particle;

        /// "Diggles/Model" → URP/Lit → Standard.
        public static Shader? Model => _model != null ? _model
            : (_model = Shader.Find("Diggles/Model")
                ?? Shader.Find("Universal Render Pipeline/Lit")
                ?? Shader.Find("Standard"));

        /// "Diggles/Ground" → URP/Lit → Standard.
        public static Shader? Ground => _ground != null ? _ground
            : (_ground = Shader.Find("Diggles/Ground")
                ?? Shader.Find("Universal Render Pipeline/Lit")
                ?? Shader.Find("Standard"));

        /// "Diggles/Particle" → Sprites/Default.
        public static Shader? Particle => _particle != null ? _particle
            : (_particle = Shader.Find("Diggles/Particle")
                ?? Shader.Find("Sprites/Default"));
    }
}
