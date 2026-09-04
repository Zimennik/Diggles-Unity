#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using UnityEngine.Scripting;

    public interface IOriginalSaveRuntimeLightingInitializer
    {
        /// <summary>
        /// Creates restored lighting services and publishes them only into an isolated candidate context.
        /// </summary>
        OriginalSaveRuntimeLightingState InitializeCandidate(
            OriginalSaveRuntimeWorldSnapshot world,
            DigglesTclContext candidateContext,
            Func<int, int, int> fillAt);
    }

    public sealed class OriginalSaveRuntimeLightingInitializer : IOriginalSaveRuntimeLightingInitializer
    {
        [Preserve]
        public OriginalSaveRuntimeLightingInitializer() { }

        public OriginalSaveRuntimeLightingState InitializeCandidate(
            OriginalSaveRuntimeWorldSnapshot world,
            DigglesTclContext candidateContext,
            Func<int, int, int> fillAt)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            if (candidateContext == null) throw new ArgumentNullException(nameof(candidateContext));
            if (fillAt == null) throw new ArgumentNullException(nameof(fillAt));
            if (world.Width <= 0 || world.Height <= 0)
                throw new ArgumentException("Saved lighting dimensions must be positive.", nameof(world));

            var baseLight = new BaseLightMap(world.Width, world.Height, fillAt)
            {
                LightBegin = world.LightBegin,
            };
            baseLight.RecomputeAll();

            var tunnelTorches = new TunnelTorchSystem(world.Width, world.Height, fillAt)
            {
                LightBegin = world.LightBegin,
                SkyLitAt = baseLight.SkyLit,
                NearExistingLight = (x, y) => DigglesTclEngine.AnyLightNear(candidateContext, x, y),
                SpawnTorch = (x, y, z) =>
                    DigglesTclEngine.TunnelTorchSpawn(candidateContext, x, y, z),
            };

            candidateContext.LightBeginRow = world.LightBegin;
            candidateContext.GroundBeginRow = world.GroundBegin;
            candidateContext.BaseLight = baseLight;
            candidateContext.TunnelTorches = tunnelTorches;
            return new OriginalSaveRuntimeLightingState(baseLight, tunnelTorches);
        }
    }

    public sealed class OriginalSaveRuntimeLightingState
    {
        internal OriginalSaveRuntimeLightingState(
            BaseLightMap baseLight,
            TunnelTorchSystem tunnelTorches)
        {
            BaseLight = baseLight ?? throw new ArgumentNullException(nameof(baseLight));
            TunnelTorches = tunnelTorches ?? throw new ArgumentNullException(nameof(tunnelTorches));
        }

        public BaseLightMap BaseLight { get; }
        public TunnelTorchSystem TunnelTorches { get; }
    }
}
