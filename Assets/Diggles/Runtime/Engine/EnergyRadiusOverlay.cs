#nullable enable

namespace Diggles.Runtime
{
    using System;
    using System.Collections.Generic;
    using Diggles.Runtime.Tcl.Engine;
    using UnityEngine;
    using UnityEngine.Rendering;

    /// <summary>
    /// Draws the selected energy source's native EnergyField on the fixed front plane.
    /// </summary>
    public sealed class EnergyRadiusOverlay : MonoBehaviour
    {
        private const string EnergyKind = "energy";
        private const string BoxedVariable = "_boxed";
        private const string DigMarkShaderName = "Diggles/DigMark";
        private const string FallbackShaderName = "Hidden/Internal-Colored";
        private const int SegmentCount = 32;
        private const int RingCount = 8;
        private const int VertexCount = 1 + SegmentCount * RingCount;
        private const int TriangleIndexCount = (SegmentCount + (RingCount - 1) * SegmentCount * 2) * 3;
        private const float PlaneY = 14.5f;
        private const float FullTurn = Mathf.PI * 2f;
        private const float PhaseSpeed = 1.4f;
        private const float CenterPhase = 1f;
        private const float CenterWaveAmplitude = 100f;
        private const float CenterWaveOffset = 155f;
        private const float RingWaveAmplitude = 50f;
        private const float RingWaveOffset = 200f;
        private const float LowChannelScale = 0.137f * 3f;
        private const float HighChannelScale = 0.274f * 3f;
        private const float MiddleChannelScale = 0.141f * 3f;
        private const byte OuterEdgeChannel = 0x9f;

        private static readonly int WobbleAmplitudeId = Shader.PropertyToID("_WobbleAmp");
        private static readonly int SourceBlendId = Shader.PropertyToID("_SrcBlend");
        private static readonly int DestinationBlendId = Shader.PropertyToID("_DstBlend");
        private static readonly int CullId = Shader.PropertyToID("_Cull");
        private static readonly int ZTestId = Shader.PropertyToID("_ZTest");
        private static readonly int ZWriteId = Shader.PropertyToID("_ZWrite");

        private static EnergyRadiusOverlay? instance;

        private readonly List<Vector3> vertices = new(VertexCount);
        private readonly List<Color32> colors = new(VertexCount);
        private readonly List<Vector2> textureCoordinates = new(VertexCount);
        private readonly List<int> triangles = new(TriangleIndexCount);

        private DigglesTclContext? context;
        private int selectedHandle;
        private Mesh? mesh;
        private MeshRenderer? meshRenderer;
        private Material? material;
        private OverlayState geometryState;
        private bool hasGeometry;

        public readonly struct OverlayState
        {
            public OverlayState(
                int selectedHandle,
                Vector2 center,
                float radius,
                float storeRatio,
                bool visible)
            {
                SelectedHandle = selectedHandle;
                Center = center;
                Radius = radius;
                StoreRatio = storeRatio;
                Visible = visible;
            }

            public static OverlayState Hidden => default;

            public int SelectedHandle { get; }
            public Vector2 Center { get; }
            public float Radius { get; }
            public float StoreRatio { get; }
            public bool Visible { get; }
        }

        public static OverlayState ResolveState(DigglesTclContext context, int selectedHandle)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (selectedHandle <= 0 ||
                !context.Entities.TryGetValue(selectedHandle, out DigglesTclEntity? entity) ||
                entity.Class?.Kind != EnergyKind ||
                IsPacked(entity) ||
                !IsFinite(entity.EnergyRange))
            {
                return OverlayState.Hidden;
            }

            float storeRatio = entity.EnergyMaxStore > 0f &&
                               IsFinite(entity.EnergyStore) &&
                               IsFinite(entity.EnergyMaxStore)
                ? Mathf.Clamp01(entity.EnergyStore / entity.EnergyMaxStore)
                : 0f;
            return new OverlayState(
                selectedHandle,
                new Vector2(entity.Position.x, entity.Position.y),
                entity.EnergyRange,
                storeRatio,
                true);
        }

        internal static void PublishSelection(DigglesTclContext context, int selectedHandle)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (!Application.isPlaying) return;

            OverlayState state = ResolveState(context, selectedHandle);
            if ((!state.Visible || context.SpawnParent == null) && instance == null) return;

            if (instance == null) instance = CreateInstance();
            instance!.SetSelection(context, selectedHandle);
        }

        private static EnergyRadiusOverlay CreateInstance()
        {
            var root = new GameObject(nameof(EnergyRadiusOverlay))
            {
                hideFlags = HideFlags.DontSave,
            };
            EnergyRadiusOverlay created = root.AddComponent<EnergyRadiusOverlay>();
            instance = created;
            return created;
        }

        private static bool IsPacked(DigglesTclEntity entity)
        {
            if (entity.ContainedIn >= 0) return true;
            return entity.VarsScope.Vars.TryGetValue(BoxedVariable, out string? boxed) &&
                   (boxed == "1" || string.Equals(boxed, "true", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private void Awake()
        {
            this.mesh = new Mesh { name = "EnergyRadius" };
            this.mesh.MarkDynamic();

            MeshFilter meshFilter = gameObject.AddComponent<MeshFilter>();
            meshFilter.sharedMesh = this.mesh;
            this.meshRenderer = gameObject.AddComponent<MeshRenderer>();
            this.meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            this.meshRenderer.receiveShadows = false;
            this.meshRenderer.lightProbeUsage = LightProbeUsage.Off;
            this.meshRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            this.meshRenderer.enabled = false;

            Shader? shader = Shader.Find(DigMarkShaderName) ?? Shader.Find(FallbackShaderName);
            if (shader == null)
                throw new InvalidOperationException("No vertex-color overlay shader is available.");

            this.material = new Material(shader)
            {
                name = "EnergyRadiusMaterial",
                hideFlags = HideFlags.HideAndDontSave,
            };
            this.material.SetFloat(WobbleAmplitudeId, 0f);
            this.material.SetInt(SourceBlendId, (int)BlendMode.SrcAlpha);
            this.material.SetInt(DestinationBlendId, (int)BlendMode.OneMinusSrcAlpha);
            this.material.SetInt(CullId, (int)CullMode.Off);
            this.material.SetInt(ZTestId, (int)CompareFunction.LessEqual);
            this.material.SetInt(ZWriteId, 0);
            this.meshRenderer.sharedMaterial = this.material;
        }

        private void Update() => Refresh();

        private void OnDestroy()
        {
            if (this.mesh != null) DestroyOwned(this.mesh);
            if (this.material != null) DestroyOwned(this.material);
            if (instance == this) instance = null;
        }

        private static void DestroyOwned(UnityEngine.Object value)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(value);
            else UnityEngine.Object.DestroyImmediate(value);
        }

        private void SetSelection(DigglesTclContext context, int selectedHandle)
        {
            this.context = context;
            this.selectedHandle = selectedHandle;
            Refresh();
        }

        private void Refresh()
        {
            if (this.meshRenderer == null || this.mesh == null) return;

            OverlayState state = this.context == null
                ? OverlayState.Hidden
                : ResolveState(this.context, this.selectedHandle);
            Transform? parent = this.context?.SpawnParent;
            if (!state.Visible || parent == null)
            {
                this.meshRenderer.enabled = false;
                return;
            }

            AttachTo(parent);
            if (!this.hasGeometry ||
                this.geometryState.Center != state.Center ||
                this.geometryState.Radius != state.Radius)
            {
                BuildGeometry(state);
                this.geometryState = state;
                this.hasGeometry = true;
            }

            UpdateColors(state, Time.time);
            this.meshRenderer.enabled = true;
        }

        private void AttachTo(Transform parent)
        {
            if (transform.parent == parent) return;
            transform.SetParent(parent, false);
            transform.localPosition = Vector3.zero;
            transform.localRotation = Quaternion.identity;
            transform.localScale = Vector3.one;
            gameObject.layer = parent.gameObject.layer;
        }

        private void BuildGeometry(OverlayState state)
        {
            this.vertices.Clear();
            this.textureCoordinates.Clear();
            this.triangles.Clear();

            this.vertices.Add(ToPlane(state.Center));
            this.textureCoordinates.Add(Vector2.zero);
            for (int ring = 1; ring <= RingCount; ring++)
            {
                float radius = state.Radius * ring / RingCount;
                for (int segment = 0; segment < SegmentCount; segment++)
                {
                    float angle = segment * FullTurn / SegmentCount;
                    var nativePoint = new Vector2(
                        state.Center.x + Mathf.Sin(angle) * radius,
                        state.Center.y + Mathf.Cos(angle) * radius);
                    this.vertices.Add(ToPlane(nativePoint));
                    this.textureCoordinates.Add(Vector2.zero);
                }
            }

            for (int segment = 0; segment < SegmentCount; segment++)
            {
                int current = 1 + segment;
                int next = 1 + (segment + 1) % SegmentCount;
                this.triangles.Add(0);
                this.triangles.Add(next);
                this.triangles.Add(current);
            }

            for (int ring = 1; ring < RingCount; ring++)
            {
                int innerStart = 1 + (ring - 1) * SegmentCount;
                int outerStart = innerStart + SegmentCount;
                for (int segment = 0; segment < SegmentCount; segment++)
                {
                    int nextSegment = (segment + 1) % SegmentCount;
                    int innerCurrent = innerStart + segment;
                    int innerNext = innerStart + nextSegment;
                    int outerCurrent = outerStart + segment;
                    int outerNext = outerStart + nextSegment;
                    this.triangles.Add(innerCurrent);
                    this.triangles.Add(outerNext);
                    this.triangles.Add(outerCurrent);
                    this.triangles.Add(innerCurrent);
                    this.triangles.Add(innerNext);
                    this.triangles.Add(outerNext);
                }
            }

            this.mesh!.Clear();
            this.mesh.SetVertices(this.vertices);
            this.mesh.SetUVs(0, this.textureCoordinates);
            this.mesh.SetTriangles(this.triangles, 0, true);
        }

        private void UpdateColors(OverlayState state, float elapsedSeconds)
        {
            float phase = elapsedSeconds * PhaseSpeed;
            int centerWave = Mathf.RoundToInt(
                Mathf.Sin(CenterPhase - phase) * CenterWaveAmplitude + CenterWaveOffset);

            this.colors.Clear();
            this.colors.Add(FieldColor(centerWave, state.StoreRatio));
            for (int ring = 0; ring < RingCount; ring++)
            {
                Color32 color;
                if (ring == RingCount - 1)
                {
                    color = new Color32(
                        OuterEdgeChannel,
                        OuterEdgeChannel,
                        byte.MaxValue,
                        byte.MinValue);
                }
                else
                {
                    int ringWave = Mathf.RoundToInt(
                        Mathf.Sin(ring - phase) * RingWaveAmplitude + RingWaveOffset);
                    color = FieldColor(ringWave, state.StoreRatio);
                }

                for (int segment = 0; segment < SegmentCount; segment++)
                    this.colors.Add(color);
            }

            this.mesh!.SetColors(this.colors);
        }

        private static Vector3 ToPlane(Vector2 nativePoint) =>
            new(nativePoint.x, PlaneY, -nativePoint.y);

        private static Color32 FieldColor(int wave, float storeRatio)
        {
            float low = wave * LowChannelScale;
            float high = wave * HighChannelScale;
            float middle = wave * MiddleChannelScale;
            return new Color32(
                ToByte(Mathf.Lerp(high, low, storeRatio)),
                ToByte(middle),
                ToByte(Mathf.Lerp(low, high, storeRatio)),
                byte.MaxValue);
        }

        private static byte ToByte(float value) => (byte)Mathf.Clamp((int)value, byte.MinValue, byte.MaxValue);
    }
}
