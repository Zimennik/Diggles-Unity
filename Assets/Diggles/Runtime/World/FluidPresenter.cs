#nullable enable

namespace Diggles.Runtime.World
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using Diggles.Runtime.Tcl.Engine;
    using UnityEngine;
    using UnityEngine.Rendering;

    // Presentation-only adapter for FluidSystem. Gameplay queries and flow remain deterministic
    // context state and never depend on this object.
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class FluidPresenter : MonoBehaviour
    {
        public const float NativeFrontDepth = 14.4f;
        public const float NativeAlphaUnit = 64f / 255f;
        public const float FogDepthOrigin = 7f;
        public const float FogDepthScale = 0.125f;
        public const float FogDepthBias = 1.02f;
        public static readonly Color FogVeilColor = new(170f / 255f, 170f / 255f, 155f / 255f, 1f);
        const float EmptyEpsilon = 0.0001f;

        public readonly struct CellVolume
        {
            public float SurfaceY { get; }
            public float BottomY { get; }

            public CellVolume(float surfaceY, float bottomY)
            {
                SurfaceY = surfaceY;
                BottomY = bottomY;
            }
        }

        FluidSystem? _fluids;
        DiggableTerrain? _terrain;
        Mesh? _mesh;
        MeshFilter? _filter;
        MeshRenderer? _renderer;
        Material[] _materials = System.Array.Empty<Material>();
        Texture2D[] _textures = System.Array.Empty<Texture2D>();
        bool _dirty;

        public Mesh? Mesh => _mesh;

        public static CellVolume CalculateCellVolume(int cellY, float fill, int lower, int upper)
        {
            float bottom = cellY + upper * 0.125f;
            float surface = bottom - (upper - lower) * Mathf.Min(fill, 1f) * 0.125f;
            return new CellVolume(surface, bottom);
        }

        public static float CalculateNativeOpacity(float fill, int typeMask)
        {
            float alphaMultiplier = (typeMask & 7) == (int)FluidSystem.FluidKind.Sulfur
                ? 1f
                : Mathf.Max(fill - 1f, 1f);
            return Mathf.Min(alphaMultiplier * NativeAlphaUnit, 1f);
        }

        public static bool NativeFogCellVisible(
            byte current,
            byte south,
            byte east,
            byte southeast)
        {
            return (current | south | east | southeast) != 0;
        }

        public static Color ApplyNativeFog(
            Color source,
            float fogFactor,
            float blackVisibility,
            float localDepth)
        {
            float grayFactor = Mathf.Clamp01(
                (1f - (localDepth - FogDepthOrigin) * FogDepthScale * FogDepthBias) * fogFactor);
            float visibility = Mathf.Clamp01(blackVisibility);
            var sourceRgb = new Vector3(source.r, source.g, source.b);
            var veilRgb = new Vector3(FogVeilColor.r, FogVeilColor.g, FogVeilColor.b);
            Vector3 rgb = Vector3.Lerp(sourceRgb, veilRgb, grayFactor) * visibility;
            return new Color(rgb.x, rgb.y, rgb.z, source.a * visibility);
        }

        void Awake() => EnsureRenderComponents();

        public void Init(FluidSystem fluids, DiggableTerrain terrain, string dataRoot)
        {
            Clear();
            _fluids = fluids;
            _terrain = terrain;
            _fluids.Changed += MarkDirty;
            CreateMaterials(dataRoot);
            _dirty = true;
        }

        public void Clear()
        {
            if (_fluids != null) _fluids.Changed -= MarkDirty;
            _fluids = null;
            _terrain = null;
            _dirty = false;
            if (_mesh != null)
            {
                DestroySafe(_mesh);
                _mesh = null;
            }
            foreach (var material in _materials) DestroySafe(material);
            foreach (var texture in _textures) DestroySafe(texture);
            _materials = System.Array.Empty<Material>();
            _textures = System.Array.Empty<Texture2D>();
            if (_filter == null && TryGetComponent(out MeshFilter filter)) _filter = filter;
            if (_filter != null) _filter.sharedMesh = null;
            if (_renderer == null && TryGetComponent(out MeshRenderer renderer)) _renderer = renderer;
            if (_renderer != null) _renderer.sharedMaterials = System.Array.Empty<Material>();
        }

        void LateUpdate() => PresentIfDirty();

        // Direct entry for EditMode tests: SendMessage("LateUpdate") on an edit-time behaviour
        // trips Unity's ShouldRunBehaviour assert; tests call this instead.
        public void PresentIfDirty()
        {
            if (!_dirty) return;
            _dirty = false;
            Rebuild();
        }

        void OnDestroy() => Clear();
        void MarkDirty() => _dirty = true;

        void CreateMaterials(string dataRoot)
        {
            var shader = Resources.Load<Shader>("DigglesFluid");
            if (shader == null)
                throw new InvalidOperationException("The DigglesFluid shader resource was not found.");

            string textureRoot = Path.Combine(dataRoot, "Texture", "m064");
            var alpha = TgaLoader.LoadFromFile(Path.Combine(dataRoot, "Texture", "Misc", "alpha.tga"));
            string[] names = { "wasser_env.tga", "schwef_nahtlos.tga", "lava_nahtlos.tga" };
            var materials = new List<Material>(3);
            var textures = new List<Texture2D>(3);
            if (alpha != null) textures.Add(alpha.Texture);
            for (int i = 0; i < names.Length; i++)
            {
                var loaded = TgaLoader.LoadFromFile(Path.Combine(textureRoot, names[i]));
                var material = new Material(shader) { name = "Fluid_" + Path.GetFileNameWithoutExtension(names[i]) };
                material.SetColor("_Color", Color.white);
                if (alpha != null) material.SetTexture("_AlphaTex", alpha.Texture);
                if (loaded != null)
                {
                    material.SetTexture("_MainTex", loaded.Texture);
                    textures.Add(loaded.Texture);
                }
                materials.Add(material);
            }
            _materials = materials.ToArray();
            _textures = textures.ToArray();
        }

        void Rebuild()
        {
            EnsureRenderComponents();
            _renderer!.shadowCastingMode = ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.sharedMaterials = _materials;

            if (_fluids == null || _terrain == null || !_fluids.Enabled || _materials.Length != 3)
            {
                if (_mesh != null)
                {
                    DestroySafe(_mesh);
                    _mesh = null;
                }
                _filter!.sharedMesh = null;
                return;
            }

            var cells = _fluids.CaptureCells();
            var vertices = new List<Vector3>(cells.Length * 22);
            var uvs = new List<Vector2>(cells.Length * 22);
            var colors = new List<Color>(cells.Length * 22);
            var water = new List<int>();
            var sulfur = new List<int>();
            var lava = new List<int>();
            foreach (var cell in cells)
            {
                if (cell.Fill <= 0f || !_terrain.TryGetFluidRenderShape(
                        cell.X,
                        cell.Y,
                        out _,
                        out int lower,
                        out int upper,
                        out int depthStripCount))
                    continue;

                List<int> indices = MaterialIndices(cell.TypeMask, water, sulfur, lava);
                CellVolume volume = CalculateCellVolume(cell.Y, cell.Fill, lower, upper);
                var color = new Color(1f, 1f, 1f, CalculateNativeOpacity(cell.Fill, cell.TypeMask));

                if (volume.BottomY - volume.SurfaceY > EmptyEpsilon)
                {
                    AppendQuad(
                        vertices,
                        uvs,
                        colors,
                        indices,
                        NativeToLocal(cell.X, volume.SurfaceY, NativeFrontDepth),
                        NativeToLocal(cell.X + 1f, volume.SurfaceY, NativeFrontDepth),
                        NativeToLocal(cell.X + 1f, volume.BottomY, NativeFrontDepth),
                        NativeToLocal(cell.X, volume.BottomY, NativeFrontDepth),
                        new Vector2(cell.X, -volume.SurfaceY),
                        new Vector2(cell.X + 1f, -volume.SurfaceY),
                        new Vector2(cell.X + 1f, -volume.BottomY),
                        new Vector2(cell.X, -volume.BottomY),
                        color);
                }

                bool exposedSurface = cell.Fill < 1f ||
                    (_terrain.TryGetFluidShape(cell.X, cell.Y - 1, out _, out _, out _) &&
                     _fluids.GetFill(cell.X, cell.Y - 1) <= EmptyEpsilon);
                if (!exposedSurface) continue;
                AppendSurfaceStrips(
                    vertices,
                    uvs,
                    colors,
                    indices,
                    cell.X,
                    volume.SurfaceY,
                    depthStripCount,
                    color);
            }

            if (_mesh != null) DestroySafe(_mesh);
            _mesh = new Mesh { name = "DigglesFluidCells", indexFormat = IndexFormat.UInt32 };
            _mesh.SetVertices(vertices);
            _mesh.SetUVs(0, uvs);
            _mesh.SetColors(colors);
            _mesh.subMeshCount = 3;
            _mesh.SetTriangles(water, 0, false);
            _mesh.SetTriangles(sulfur, 1, false);
            _mesh.SetTriangles(lava, 2, false);
            _mesh.RecalculateBounds();
            _filter!.sharedMesh = _mesh;
        }

        void EnsureRenderComponents()
        {
            if (_filter == null)
            {
                if (!TryGetComponent(out MeshFilter filter) || filter == null)
                    filter = gameObject.AddComponent<MeshFilter>();
                _filter = filter;
            }
            if (_renderer == null)
            {
                if (!TryGetComponent(out MeshRenderer renderer) || renderer == null)
                    renderer = gameObject.AddComponent<MeshRenderer>();
                _renderer = renderer;
            }
        }

        static List<int> MaterialIndices(
            int typeMask, List<int> water, List<int> sulfur, List<int> lava)
        {
            if (typeMask == (int)FluidSystem.FluidKind.Water) return water;
            if (typeMask == (int)FluidSystem.FluidKind.Sulfur) return sulfur;
            return lava;
        }

        static void AppendQuad(
            List<Vector3> vertices,
            List<Vector2> uvs,
            List<Color> colors,
            List<int> indices,
            Vector3 a,
            Vector3 b,
            Vector3 c,
            Vector3 d,
            Vector2 uvA,
            Vector2 uvB,
            Vector2 uvC,
            Vector2 uvD,
            Color color)
        {
            int first = vertices.Count;
            vertices.Add(a);
            vertices.Add(b);
            vertices.Add(c);
            vertices.Add(d);
            uvs.Add(uvA);
            uvs.Add(uvB);
            uvs.Add(uvC);
            uvs.Add(uvD);
            colors.Add(color);
            colors.Add(color);
            colors.Add(color);
            colors.Add(color);
            indices.Add(first);
            indices.Add(first + 1);
            indices.Add(first + 2);
            indices.Add(first);
            indices.Add(first + 2);
            indices.Add(first + 3);
        }

        static void AppendSurfaceStrips(
            List<Vector3> vertices,
            List<Vector2> uvs,
            List<Color> colors,
            List<int> indices,
            int cellX,
            float surfaceY,
            int stripCount,
            Color color)
        {
            int first = vertices.Count;
            for (int line = 0; line <= stripCount; line++)
            {
                float depth = NativeFrontDepth - line;
                vertices.Add(NativeToLocal(cellX, surfaceY, depth));
                vertices.Add(NativeToLocal(cellX + 1f, surfaceY, depth));
                uvs.Add(new Vector2(cellX, depth));
                uvs.Add(new Vector2(cellX + 1f, depth));
                colors.Add(color);
                colors.Add(color);
            }

            for (int strip = 0; strip < stripCount; strip++)
            {
                int frontLeft = first + strip * 2;
                int backLeft = frontLeft + 2;
                indices.Add(frontLeft);
                indices.Add(frontLeft + 1);
                indices.Add(backLeft + 1);
                indices.Add(frontLeft);
                indices.Add(backLeft + 1);
                indices.Add(backLeft);
            }
        }

        // Fluid surfaces live in the NIBBLE height frame (0..15, like tile/entity coords):
        // local y = depth*0.5 + 7 — the RuntimeWorldSpawner.NativeToLocal law. (This used to
        // ride on BackdropLaws.LocalFromRender while that carried the same affine by mistake;
        // the backdrop render frame is metres — local y directly — and no longer rescales.)
        static Vector3 NativeToLocal(float x, float y, float depth)
            => new Vector3(x, depth * 0.5f + 7f, -y);

        static void DestroySafe(UnityEngine.Object value)
        {
            if (Application.isPlaying) Destroy(value);
            else DestroyImmediate(value);
        }
    }
}
