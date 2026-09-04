#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using UnityEngine;
    using UnityEngine.Scripting;

    // World-owned render adapters are initialized only while a guarded session activation is in
    // progress. Fog and base-light presenters write shader globals, so running them during staging
    // would visibly mutate the still-active session and make rollback dishonest.
    public sealed class OriginalSaveRuntimePresentationParticipant :
        IRuntimeSessionPresentationParticipant
    {
        [Preserve]
        public OriginalSaveRuntimePresentationParticipant() { }

        public void BindSession(RuntimeGameSession session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            DigglesTclContext context = session.Engine.Ctx;
            DiggableTerrain? terrain = context.Terrain;
            if (terrain == null) return;

            GameObject root = session.ContentRoot;
            DigMarkOverlay digMarks = GetOrAdd<DigMarkOverlay>(root);
            digMarks.Init(terrain);

            // The walkability gizmo the fresh-world path adds via RuntimeMapBuilder; the load path
            // needs it too. Debug visualization only, the nav data itself lives on the terrain.
            GetOrAdd<Diggles.Runtime.Navigation.NavWorldGizmo>(root);

            FluidPresenter fluid = GetOrAdd<FluidPresenter>(root);
            fluid.Init(context.Fluids, terrain, context.DataRoot);

            FogOfWarPresenter fogPresenter = GetOrAdd<FogOfWarPresenter>(root);
            FogOfWarOverlay fogOverlay = GetOrAdd<FogOfWarOverlay>(root);
            if (context.FogOfWar != null)
            {
                fogPresenter.Init(context.FogOfWar);
                fogOverlay.Init(
                    context.FogOfWar,
                    terrain.GetHmap,
                    terrain.RendersTerrain,
                    fogPresenter);
                if (session.OriginalSaveState != null &&
                    session.OriginalSaveState.TryMarkPresentationEventsWired())
                {
                    terrain.TilesChanged += fogOverlay.MarkRectDirty;
                }
            }
            else
            {
                fogOverlay.Clear();
                fogPresenter.Clear();
            }

            BaseLightPresenter baseLightPresenter = GetOrAdd<BaseLightPresenter>(root);
            if (context.BaseLight != null)
            {
                baseLightPresenter.MinFactor =
                    DigglesConfig.ResolveBaseLightMin(baseLightPresenter.MinFactor);
                baseLightPresenter.Init(context.BaseLight);
            }
            else
            {
                baseLightPresenter.Clear();
            }

            if (context.EntitySpawner is RuntimeWorldSpawner spawner)
            {
                spawner.FogPresenter = context.FogOfWar != null ? fogPresenter : null;
                spawner.FogState = context.FogOfWar;
                spawner.ClassLightCap = DigglesConfig.ResolveClassLightCap(spawner.ClassLightCap);
                spawner.UploadClassLights();
            }
        }

        private static T GetOrAdd<T>(GameObject root) where T : Component =>
            root.TryGetComponent(out T component) ? component : root.AddComponent<T>();
    }
}
