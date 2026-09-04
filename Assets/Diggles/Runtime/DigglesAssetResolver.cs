#nullable enable

namespace Diggles.Runtime
{
    using System.Collections.Generic;
    using System.IO;

    /// <summary>
    /// Resolves original-game data files (3db models, Texture/*, sphere.dat, …) with a
    /// <b>mods-over-data</b> search order: each registered mod root is checked first (in
    /// registration order), then the base <see cref="DataRoot"/>.
    ///
    /// With no mod roots (the default) it is a pure pass-through — <see cref="Resolve"/>
    /// returns <c>DataRoot/relativePath</c> and <see cref="Roots"/> yields just DataRoot —
    /// so behaviour is byte-identical to the old literal <c>Path.Combine(DataRoot, …)</c>.
    ///
    /// This is the asset-indirection seam for modding: a mod can drop a replacement
    /// <c>3db/Foo.3db</c> or <c>Texture/m256/Bar.tga</c> into a mod root and it
    /// transparently overrides the shipped data. The mod-root discovery convention (where
    /// mod folders live and their priority) is intentionally not decided here — call
    /// <see cref="AddModRoot"/> from whatever discovery layer we settle on.
    /// Doc: Docs/RENDERING_LIGHTING_RESEARCH.md §6/§8.
    /// </summary>
    public sealed class DigglesAssetResolver
    {
        public string DataRoot { get; }

        private readonly List<string> _modRoots = new();
        public IReadOnlyList<string> ModRoots => _modRoots;

        public DigglesAssetResolver(string dataRoot)
        {
            DataRoot = dataRoot ?? "";
        }

        /// <summary>
        /// Register a mod root directory. Earlier-registered roots win over later ones and
        /// over <see cref="DataRoot"/>. Duplicate / empty roots are ignored.
        /// </summary>
        public void AddModRoot(string root)
        {
            if (!string.IsNullOrEmpty(root) && !_modRoots.Contains(root))
                _modRoots.Add(root);
        }

        /// <summary>All roots in search order: mod roots (priority order) then the base DataRoot.</summary>
        public IEnumerable<string> Roots
        {
            get
            {
                for (int i = 0; i < _modRoots.Count; i++) yield return _modRoots[i];
                yield return DataRoot;
            }
        }

        /// <summary>
        /// Absolute path for a data-relative file, preferring the first mod root that contains
        /// it, else <c>DataRoot/relativePath</c> (returned even when missing, so callers keep
        /// their own File.Exists handling unchanged).
        /// </summary>
        public string Resolve(string relativePath)
        {
            for (int i = 0; i < _modRoots.Count; i++)
            {
                var p = Path.Combine(_modRoots[i], relativePath);
                if (File.Exists(p)) return p;
            }
            return Path.Combine(DataRoot, relativePath);
        }

        public bool Exists(string relativePath) => File.Exists(Resolve(relativePath));
    }
}
