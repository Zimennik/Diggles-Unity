#nullable enable

namespace Diggles.Runtime.Tcl.Engine
{
    using System;
    using Diggles.Runtime.Navigation;
    using UnityEngine;

    public interface IWaterReactiveWalk
    {
        void SetWaterMovement(bool active);
    }

    public readonly struct GnomeWaterState
    {
        public bool FeetUnderwater { get; }
        public bool MouthUnderwater { get; }

        public GnomeWaterState(bool feetUnderwater, bool mouthUnderwater)
        {
            FeetUnderwater = feetUnderwater;
            MouthUnderwater = mouthUnderwater;
        }
    }

    public static class GnomeWaterReaction
    {
        public const float FeetProbeOffsetY = -0.1f;

        public static GnomeWaterState Evaluate(
            FluidSystem fluids,
            Vector3 position,
            Vector3 mouthOffset)
        {
            if (fluids == null) throw new ArgumentNullException(nameof(fluids));
            bool feetUnderwater = fluids.IsUnderwater(
                position + new Vector3(0f, FeetProbeOffsetY, 0f));
            bool mouthUnderwater = fluids.IsUnderwater(position + mouthOffset);
            return new GnomeWaterState(feetUnderwater, mouthUnderwater);
        }

        public static void Synchronize(DigglesTclContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            foreach (DigglesTclEntity entity in context.Entities.Values)
            {
                if (!string.Equals(entity.Class?.Kind, "gnome", StringComparison.Ordinal)) continue;

                Vector3 mouthOffset = Vector3.zero;
                DigglesTclEngine.TryGetLinkOffsetNative(
                    context,
                    entity,
                    10,
                    applyRotation: true,
                    out mouthOffset);
                GnomeWaterState state = Evaluate(context.Fluids, entity.Position, mouthOffset);
                entity.VarsScope.Vars["is_underwater"] = state.MouthUnderwater ? "1" : "0";

                if (entity.ActiveAction is IWaterReactiveWalk waterWalk)
                    waterWalk.SetWaterMovement(IsMovementWater(
                        context.Terrain?.Nav,
                        entity.Position,
                        state.FeetUnderwater));
            }
        }

        public static bool IsMovementWater(
            NavWorld? navigation,
            Vector3 position,
            bool volumeFallback)
        {
            if (navigation == null) return volumeFallback;
            int subX = Mathf.RoundToInt(position.x * 8f);
            int subY = Mathf.RoundToInt(position.y * 8f);
            byte flags = navigation.SubAt(subX, subY);
            return (flags & NavWorld.SubWalk) == 0
                ? volumeFallback
                : (flags & NavWorld.SubWater) != 0;
        }
    }
}
