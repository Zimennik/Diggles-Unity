#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Globalization;
    using System.IO;
    using Diggles.Runtime.Navigation;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Stats;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using UnityEngine;
    using UnityEngine.Scripting;

    public interface IOriginalSaveRuntimeSessionStager
    {
        /// <summary>
        /// Reconstructs an isolated inactive candidate without running campaign or per-object Tcl.
        /// </summary>
        OriginalSaveRuntimeSessionState StageCandidate(
            RuntimeGameSession candidate,
            OriginalSaveRuntimeSnapshot source,
            string bdbRelativePath);
    }

    public sealed class OriginalSaveRuntimeSessionStager : IOriginalSaveRuntimeSessionStager
    {
        private const string ProductionItemsMethod = "prod_items";
        private const string BoxedPresentationAnimation = "kiste.standard";
        private const float NativeLightColorMaximum = 31f;

        // animinit.tcl registers these databases in this order. Its repeated muetzen load reuses
        // slot 4, so later slots stay stable across boots and across native saves.
        private static readonly string[] NativeModelDatabaseNames =
        {
            "mann",
            "frau",
            "werkzeuge",
            "halbzeuge",
            "muetzen",
            "licht",
            "produktionsstaetten",
            "rohstoffe",
            "nahrung",
            "kampf",
            "hamster",
            "freizeit",
            "transport",
            "wuker",
            "troll",
            "fenrir",
            "einrichtung",
            "baby",
            "fisch_a",
            "ringe",
            "wandboden",
            "hinterlegung",
            "trollhausen",
            "flusslandschaft",
            "metalltech",
            "oberwelt",
            "kristall",
            "lavawelt",
            "auskleidung",
            "drache",
            "drache01",
            "krake",
            "spinne",
            "brut",
            "riesenhamster",
            "riesenelfe",
            "joinedobjs",
            "odin",
            "odinshand",
            "fifi",
        };

        private readonly IOriginalSaveRuntimeLightingInitializer _lightingInitializer;
        private readonly IOriginalSaveActionCandidateStager _actionStager;
        private readonly IOriginalSaveActionInstaller _actionInstaller;

        [Preserve]
        public OriginalSaveRuntimeSessionStager(
            IOriginalSaveRuntimeLightingInitializer lightingInitializer,
            IOriginalSaveActionCandidateStager actionStager,
            IOriginalSaveActionInstaller actionInstaller)
        {
            _lightingInitializer = lightingInitializer ??
                throw new ArgumentNullException(nameof(lightingInitializer));
            _actionStager = actionStager ?? throw new ArgumentNullException(nameof(actionStager));
            _actionInstaller = actionInstaller ?? throw new ArgumentNullException(nameof(actionInstaller));
        }

        public OriginalSaveRuntimeSessionState StageCandidate(
            RuntimeGameSession candidate,
            OriginalSaveRuntimeSnapshot source,
            string bdbRelativePath)
        {
            if (candidate == null) throw new ArgumentNullException(nameof(candidate));
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (bdbRelativePath == null) throw new ArgumentNullException(nameof(bdbRelativePath));
            if (candidate.State != RuntimeGameSessionState.Candidate)
                throw new InvalidOperationException("Original save staging requires an inactive candidate session.");
            if (candidate.OriginalSaveState != null)
                throw new InvalidOperationException("The candidate already contains staged original save state.");

            DigglesEngine engine = candidate.Engine;
            DigglesTclContext context = engine.Ctx;
            if (context.Entities.Count != 0)
                throw new InvalidOperationException("A save candidate must start with an empty entity registry.");
            var loadWarnings = new List<string>();

            string bdbPath = ResolveDataPath(context.DataRoot, bdbRelativePath);
            BdbModel bdb = BdbParser.ParseCached(bdbPath);
            BdbPmpWorldBuilder.TclWorldResult worldResult = source.RuntimeWorld.CreateWorldResult();
            ValidateWorldBounds(source.RuntimeWorld, worldResult, bdb);

            engine.LoadRuntimeDefinitions();
            PreflightEntityClasses(source.Entities, context, loadWarnings);
            Dictionary<string, string[]> productionSlotsByClass =
                BuildProductionSlotCatalogs(source.Entities, context, loadWarnings);

            DiggableTerrain terrain = candidate.ContentRoot.AddComponent<DiggableTerrain>();
            DiggableTerrain.SaveState terrainState =
                source.RuntimeWorld.CreateTerrainState(worldResult.Voxels);
            terrain.InitializeFromSave(worldResult, bdb, context.DataRoot, () => { }, terrainState);

            // Rendering is deliberately created only after the exact native arrays, the full-map
            // EFFPASSWALK sub-grid refresh, and the archived +0x06/raw packed +0x12 graph restore are installed.
            worldResult.Chunks = new BdbPmpWorldBuilder.TerrainChunkGrid(
                candidate.ContentRoot,
                bdb,
                worldResult,
                context.DataRoot,
                resetSharedTextureCache: false);
            worldResult.Chunks.MarkAllDirty();
            worldResult.Chunks.RebuildDirty();

            engine.Map.AttachLiveWorld(worldResult, terrain);
            context.MapBuilder = engine.Map;
            context.Terrain = terrain;
            terrain.RestoreCaveRecords(
                source.CaveRecords.CreateRuntimeRecords(terrain, context.LocalPlayer));
            var navigation = candidate.ContentRoot.AddComponent<NavWorldService>();
            context.NavService = navigation;
            if (context.EntitySpawner is RuntimeWorldSpawner runtimeSpawner)
                runtimeSpawner.Terrain = terrain;

            Dictionary<int, DigglesTclEntity> entities = CreateEntityShells(source, context);
            // Second half of native shell construction: the original creates every archived object
            // through the same creation callback as `new` (obj_init runs in the object's fresh
            // interp) and only then overlays the archived state. obj_init installs the per-object
            // behavior procs (z_events/z_procs/z_dignwalk `else` branches); without it a restored
            // class has no procs and every order/state body silently no-ops. Rebase first so a
            // `new` issued inside an obj_init cannot collide with an archived id; the archived
            // overlays below then re-assert everything obj_init touched, in the native loader's
            // order. Timers, attributes and particles are restored later in this method, which
            // overwrites those obj_init side effects wholesale, same as native.
            context.RebaseNextHandle();
            context.RestoredShellConstructionActive = true;
            try
            {
                RunObjInitConstructors(source, context, entities);
            }
            finally
            {
                context.RestoredShellConstructionActive = false;
            }
            RestoreAttributeState(source, context, entities);
            RestoreObjectGraphState(source, entities);
            bool dwarfExtensionRestoreRan = RestoreDwarfExtensionState(
                source,
                context,
                entities);
            RestoreProductionExtensionState(
                source,
                productionSlotsByClass,
                entities,
                loadWarnings);
            RestoreDerivedInventoryCapacities(context, entities);
            RestoreParticleState(source, context);
            RestoreTimerState(source, context);
            context.Energy.Restore(source.Energy.CreateSaveState());
            RestoreTriggerState(source, context);
            RestoreSelectionControlGroupState(source, context);
            RestoreSparetimeState(source, context, entities);
            RestoreHorizonState(source, context);
            RestoreAdaptiveSoundMarkerState(source, context);
            RestoreNewsTickerState(source, context);
            context.RebaseNextHandle();

            var fog = new FogOfWarState(
                source.RuntimeWorld.Width,
                source.RuntimeWorld.Height,
                terrain.FogFillAt)
            {
                DigNibbleAt = terrain.RawDigNibble,
                ClearDigNibble = terrain.ClearDigMark,
            };
            fog.RestoreSaveState(source.RuntimeWorld.CreateFogState());
            context.FogOfWar = fog;

            OriginalSaveRuntimeLightingState lighting = _lightingInitializer.InitializeCandidate(
                source.RuntimeWorld,
                context,
                terrain.GetHmap);
            bool classLightRestoreRan = RestoreClassLights(candidate, source);
            terrain.TilesChanged += (left, top, right, bottom) => lighting.BaseLight.RecomputeRect(
                left - 1,
                top - 1,
                right + 1,
                bottom + BaseLightMap.SkyScanTiles + 1);

            context.Fluids.Restore(source.RuntimeWorld.CreateFluidState());
            engine.Sim.GameTime = source.GameTime;
            RestoreStoryState(source, context);
            RestorePlayerState(source, context);
            RestoreAiState(source, context);
            RestoreElfState(source, context);

            OriginalSavePreparedActionSet preparedActions = _actionStager.StageCandidates(
                source,
                entities,
                context);
            _actionInstaller.Install(preparedActions, entities);

            bool entityPresentationRestoreRan = MaterializeEntityPresentation(candidate, entities);
            RegisterSaveDecoSeeds(candidate, source);
            bool animatorRestoreRan = RestoreAnimatorContinuation(
                source,
                entities,
                context.EntitySpawner as RuntimeWorldSpawner,
                loadWarnings);
            RestoreTextureAnimations(
                source,
                entities,
                context.EntitySpawner as RuntimeWorldSpawner);
            RestoreObjectGraphPresentation(entities);
            RestoreSnapToWallRotationPresentation(context, entities);
            if (context.EntitySpawner is RuntimeWorldSpawner spawner)
            {
                NavWorld navigationWorld = terrain.Nav ??
                    throw new InvalidOperationException("Candidate navigation was not initialized.");
                terrain.TilesChanged += (left, top, right, bottom) =>
                    RuntimeWorldSpawner.ResettleLooseItems(
                        navigationWorld,
                        context.Entities.Values,
                        left,
                        top,
                        right,
                        bottom,
                        entity =>
                        {
                            if (entity.GameObject is GameObject gameObject)
                                spawner.ApplyPosition(gameObject, entity.Position);
                        });
            }

            // Campaign flag for the presentation layer: DigglesWorldRuntime.BindSession derives its
            // menu/game split from ctx.IsStoryMgr (_atMenu = !IsStoryMgr), so without it a loaded
            // campaign is treated as the main menu and the backdrop cutscene chain plays over the
            // restored world. A campaign archive always carries its CStoryMgr object; a save
            // without one (e.g. a saved menu diorama) is faithfully a non-story world.
            context.IsStoryMgr = false;
            foreach (DigglesTclEntity stagedEntity in entities.Values)
            {
                if (string.Equals(stagedEntity.Class?.Name, "StoryMgr", StringComparison.Ordinal))
                {
                    context.IsStoryMgr = true;
                    break;
                }
            }

            var unmetActivationConditions = new List<string>();
            CollectUnmetActivationConditions(
                source,
                context,
                entities,
                preparedActions,
                dwarfExtensionRestoreRan,
                classLightRestoreRan &&
                entityPresentationRestoreRan &&
                animatorRestoreRan,
                unmetActivationConditions);

            var state = new OriginalSaveRuntimeSessionState(
                source,
                worldResult,
                bdb,
                terrain,
                navigation,
                fog,
                lighting,
                entities,
                preparedActions,
                _actionInstaller,
                loadWarnings,
                unmetActivationConditions);
            candidate.AttachOriginalSaveState(state);

            // Everything installed up to here (archived channels plus any port-side staging
            // arming, e.g. face animation) counts as the restored baseline: capture round-trips
            // the archived table verbatim until the table truly mutates in play (a
            // set_textureanimation call or a playback advance).
            foreach (DigglesTclEntity stagedEntity in candidate.Engine.Ctx.Entities.Values)
            {
                if (stagedEntity.GameObject == null) continue;
                DigglesTextureAnimator? textureAnimator = stagedEntity.GameObject
                    .GetComponentInChildren<DigglesTextureAnimator>(true);
                textureAnimator?.MarkNativeBaseline();
            }
            return state;
        }

        private static Dictionary<int, DigglesTclEntity> CreateEntityShells(
            OriginalSaveRuntimeSnapshot source,
            DigglesTclContext context)
        {
            var entities = new Dictionary<int, DigglesTclEntity>(source.Entities.Count);
            foreach (OriginalSaveRuntimeEntitySnapshot saved in source.Entities)
            {
                DigglesClass runtimeClass = context.Classes[saved.ClassName];
                // Keep the native "<Class>_<n>" auto-name counters ahead of every archived
                // suffix so post-load spawns continue the numbering without collisions.
                context.SeedAutoInstanceName(saved.ClassName, saved.InstanceName);
                var entity = new DigglesTclEntity
                {
                    Class = runtimeClass,
                    InstanceName = saved.InstanceName,
                    InfoString = saved.DisplayState,
                    NativeObjectType = saved.ObjectType,
                    Position = Vector(saved.Position),
                    SpawnPosition = Vector(saved.Position),
                    HasSpawnPosition = true,
                    Velocity = Vector(saved.Velocity),
                    Rotation = Vector(saved.Rotation),
                    NativePreviousPosition = Vector(saved.Position),
                    NativeMotionDelta = Vector(saved.MotionDelta),
                    NativeMotionSpeed = saved.MotionSpeed,
                    NativePreviousRotation = Vector(saved.PreviousRotation),
                    NativeCoreStateInitialized = true,
                    NativeInitialAnimationWasSeeded = saved.InitialAnimationWasSeeded,
                    RenderDirtyLatch = saved.RenderDirtyLatch,
                    NativeInitialAnimationUsesRawIdentity = true,
                    NativeInitialAnimationModelDatabaseSlot =
                        saved.InitialAnimationModelDatabaseSlot,
                    NativeInitialAnimationState = saved.InitialAnimationState,
                    NativeInitialAnimationStartFrame = saved.InitialAnimationStartFrame,
                    NativeInitialAnimationMode = saved.InitialAnimationMode,
                    Owner = saved.Owner,
                    CreationTimeHours = saved.CreationTimeHours,
                    WorkStart = saved.WorkStart,
                    WorkLen = saved.WorkLength,
                    FowSizeX = saved.FogOfWarSizeX,
                    FowSizeY = saved.FogOfWarSizeY,
                    FowLastX = saved.FogOfWarLastX,
                    FowLastY = saved.FogOfWarLastY,
                    FowLastZ = saved.FogOfWarLastZ,
                    LightOffset = new UnityEngine.Vector3(
                        saved.LightOffsetX, saved.LightOffsetY, saved.LightOffsetZ),
                    LightRange = saved.LightRange,
                    LightColor = new UnityEngine.Vector3(
                        saved.LightColorR, saved.LightColorG, saved.LightColorB),
                    LinkedToHandle = 0,
                    LinkedSlot = -1,
                    PlaceSnapMode = saved.PlaceSnapMode,
                    WalkResult = saved.WalkResult,
                    WeaponClassId = saved.WeaponClassId,
                    ShieldClassId = saved.ShieldClassId,
                    PickTargetMode = saved.PickTargetMode,
                    NativeMutationFlags = saved.DirtyFlags,
                    DigSetId = saved.DigOrderId,
                };

                // Archived N08 tasks go into the live queue: the native loader restores the task
                // vector and the state body resumes popping it, which is why a chopping dwarf
                // keeps chopping after a load. Without it every interrupted job is abandoned on
                // load. The residue copy is kept as a capture fallback for entities whose queue
                // never mutates.
                var restoredTasks = new string[saved.TaskList.Count];
                for (int taskIndex = 0; taskIndex < restoredTasks.Length; taskIndex++)
                    restoredTasks[taskIndex] = saved.TaskList[taskIndex];
                entity.RestoredTaskListResidue = restoredTasks;
                entity.TaskList.Clear();
                entity.TaskList.AddRange(restoredTasks);
                entity.TaskListDirty = false;
                entity.FowCache = new int[8];
                for (int cacheIndex = 0; cacheIndex < entity.FowCache.Length; cacheIndex++)
                {
                    entity.FowCache[cacheIndex] = cacheIndex < saved.FogBrightnessCache.Count
                        ? saved.FogBrightnessCache[cacheIndex]
                        : -1;
                }
                entity.FowCacheVersion = context.FogOfWar?.CacheVersion ?? -1;

                foreach (string member in context.CollectMemberVars(runtimeClass))
                    entity.VarsScope.Vars[member] = context.MemberDefaultFor(runtimeClass, member);
                ApplyArchivedStateWords(saved, entity, runtimeClass);
                ApplyArchivedScriptState(saved, entity);

                context.RegisterEntityAtHandle(entity, saved.ObjectId);
                entities.Add(saved.ObjectId, entity);
            }
            return entities;
        }

        // Tcl state machine (native +0x200/+0x204/+0x208, CObj::HandleStates 005cac90):
        // CurrentState is the TARGET name resolved through the class's registration-order
        // state table; live==target means the state already went live (enter must NOT
        // re-run); the enable word is the port count + 1 (state_enable/disable are
        // absolute writes: native 1/0 ↔ port 0/-1). Raw words are retained on the entity
        // so an untouched machine round-trips byte-exactly regardless of registry gaps.
        // Was NEVER restored before — every loaded world had CurrentState == null,
        // so no object ever thought again (the frozen-gnome root).
        private static void ApplyArchivedStateWords(
            OriginalSaveRuntimeEntitySnapshot saved,
            DigglesTclEntity entity,
            DigglesClass runtimeClass)
        {
            entity.RestoredStateLive = saved.StateLiveWord;
            entity.RestoredStateTarget = saved.StateTargetWord;
            entity.RestoredStateEnabled = saved.StateEnabledWord;
            entity.LiveStateIndex = saved.StateLiveWord;
            entity.StateEnabledCount = saved.StateEnabledWord - 1;
            entity.StateWordsDirty = false;
            // Target only: a live-without-target combo natively runs the leave once and goes
            // dormant (005cac90:30-55 then:81 live:= -1) — dormant-without-leave is the
            // closest port equivalent.
            if (saved.StateTargetWord >= 0)
            {
                entity.CurrentState = runtimeClass.StateNameAt(saved.StateTargetWord);
                entity.StateWentLive = saved.StateLiveWord == saved.StateTargetWord;
                if (entity.CurrentState == null)
                {
                    UnityEngine.Debug.LogWarning(
                        $"[OriginalSave] {saved.ClassName}#{saved.ObjectId}: archived state " +
                        $"index {saved.StateTargetWord} is outside the port's {runtimeClass.Name} " +
                        $"state table ({runtimeClass.StateOrder.Count} states) — the state " +
                        "machine stays dormant for this object.");
                }
            }
            else
            {
                entity.CurrentState = null;
                entity.StateWentLive = false;
            }
        }

        // The archived interpreter overlay: variables, object flags and the consumed-event ring —
        // exactly what the native loader re-asserts AFTER the creation callback ran obj_init
        // (FUN_005d0ce0 plain `set`s + section restores, B5 + load-shell-interp-re.md).
        private static void ApplyArchivedScriptState(
            OriginalSaveRuntimeEntitySnapshot saved,
            DigglesTclEntity entity)
        {
            foreach (OriginalSaveTclVariable variable in saved.TclVariables)
                entity.VarsScope.Vars[variable.Name] = variable.Value;
            DigglesTclEngine.RestoreNativeObjectFlags(entity, saved.ObjectFlags);
            entity.VarsScope.Vars["this"] =
                saved.ObjectId.ToString(CultureInfo.InvariantCulture);

            var nativeEventRingSlots = new OriginalSaveTimerEventPayloadDocument?[
                DigglesTclEntity.NativeEventRingCapacity];
            for (int slot = 0; slot < nativeEventRingSlots.Length; slot++)
            {
                nativeEventRingSlots[slot] =
                    saved.EventQueueSlots[slot]?.CreatePayloadDocument();
            }
            entity.RestoreNativeEventRing(
                saved.EventQueueReadCursor,
                saved.EventQueueWriteCursor,
                nativeEventRingSlots);
        }

        // The native creation-callback half of a load: run obj_init for every restored shell,
        // then re-assert the archived identity over everything the init may have touched. The
        // procs/handlers the init installs are the part that must SURVIVE; the side effects
        // (timers, anims, names, fog pokes, stats) are all overwritten by the archive — timers,
        // attributes and particles by the later Restore* passes of StageCandidate, the per-entity
        // fields right here.
        private static void RunObjInitConstructors(
            OriginalSaveRuntimeSnapshot source,
            DigglesTclContext context,
            IReadOnlyDictionary<int, DigglesTclEntity> entities)
        {
            foreach (OriginalSaveRuntimeEntitySnapshot saved in source.Entities)
            {
                DigglesTclEntity entity = entities[saved.ObjectId];
                DigglesClass? runtimeClass = entity.Class;
                if (runtimeClass == null || runtimeClass.DisableScripting) continue;
                try
                {
                    DigglesTclEngine.RunObjInitForRestoredShell(context, entity);
                }
                catch (Exception exception)
                {
                    UnityEngine.Debug.LogWarning(
                        $"[OriginalSave] obj_init for restored {runtimeClass.Name}" +
                        $"#{saved.ObjectId} threw: {exception.Message} — archived state only.");
                }

                // Archive-authoritative overlay (native order: construct, then overlay).
                entity.InstanceName = saved.InstanceName;
                entity.InfoString = saved.DisplayState;
                entity.Position = Vector(saved.Position);
                entity.SpawnPosition = Vector(saved.Position);
                entity.Velocity = Vector(saved.Velocity);
                entity.Rotation = Vector(saved.Rotation);
                entity.Owner = saved.Owner;
                entity.WorkStart = saved.WorkStart;      // auto_choose_workingtime in obj_init
                entity.WorkLen = saved.WorkLength;       // re-rolls the window; archive wins
                entity.FowSizeX = saved.FogOfWarSizeX;   // set_fogofwar in obj_init re-seeds
                entity.FowSizeY = saved.FogOfWarSizeY;   // the size and the last-uncover tile
                entity.FowLastX = saved.FogOfWarLastX;
                entity.FowLastY = saved.FogOfWarLastY;
                entity.FowLastZ = saved.FogOfWarLastZ;
                entity.WalkResult = saved.WalkResult;
                entity.WeaponClassId = saved.WeaponClassId;
                entity.ShieldClassId = saved.ShieldClassId;
                entity.PickTargetMode = saved.PickTargetMode;
                entity.NativeMutationFlags = saved.DirtyFlags;
                entity.RenderDirtyLatch = saved.RenderDirtyLatch;   // obj_init toggles must not stamp it
                entity.PlaceSnapMode = saved.PlaceSnapMode;
                entity.DigSetId = saved.DigOrderId;
                entity.LightOffset = new UnityEngine.Vector3(
                    saved.LightOffsetX, saved.LightOffsetY, saved.LightOffsetZ);
                entity.LightRange = saved.LightRange;
                entity.LightColor = new UnityEngine.Vector3(
                    saved.LightColorR, saved.LightColorG, saved.LightColorB);
                // obj_init may have queued its own tasks (state_trigger chains); the archived
                // N08 queue is authoritative and goes back LIVE (the native resume law).
                entity.TaskList.Clear();
                for (int taskIndex = 0; taskIndex < saved.TaskList.Count; taskIndex++)
                    entity.TaskList.Add(saved.TaskList[taskIndex]);
                entity.TaskListDirty = false;
                entity.PendingEvents.Clear();
                ApplyArchivedStateWords(saved, entity, runtimeClass);
                ApplyArchivedScriptState(saved, entity);
            }
        }

        private static void RestoreAttributeState(
            OriginalSaveRuntimeSnapshot source,
            DigglesTclContext context,
            IReadOnlyDictionary<int, DigglesTclEntity> entities)
        {
            // NATIVE slot mapping: archive pages are laid out in the ORIGINAL's
            // def_attrib slot order (sentinel-probed OriginalSaveNativeAttributeSlots), which is
            // a different permutation from the port registry order. Reading by registry order
            // scrambled every restored stat (native atr_Mood=29 was read into the name at the
            // port's index 29); capture then wrote the same wrong permutation back, so byte
            // gates never saw it. Slots without a mapped name stay retained-raw on the snapshot.
            IReadOnlyDictionary<string, int> nativeSlots =
                OriginalSaveNativeAttributeSlots.SlotByName;
            foreach (OriginalSaveRuntimeEntitySnapshot saved in source.Entities)
            {
                if (saved.AttributePageBits.Count == 0) continue;
                DigglesTclEntity entity = entities[saved.ObjectId];
                IDictionary<string, double> mappedValues = WritableStatValues(entity.Stats);
                foreach (KeyValuePair<string, int> entry in nativeSlots)
                {
                    if (entry.Value >= saved.AttributePageBits.Count) continue;
                    mappedValues[entry.Key] =
                        BitConverter.Int32BitsToSingle(saved.AttributePageBits[entry.Value]);
                }
            }

            context.OwnerAttribs.Clear();
            // Production-knowledge anchors: each archived CProd slot names the exact native page
            // word of its Bp<Class> attribute (slot +0x04 — the panel predicate FUN_005ed620).
            // The sentinel table was probed on ONE install and the registry drifts between game
            // versions/mod sets (rich fixture 2020: BpKleiner_Heiltrank=79/BpHeiltrank=81 vs the
            // table's 77/79), so Bp readings follow the archive's own anchors — SYMMETRIC with
            // CaptureOwnerAttributes' CollectProductionKnowledgeSlots, or capture writes them
            // back to different words than restore read and the byte round-trip breaks.
            Dictionary<int, Dictionary<string, int>> knowledgeAnchorsByOwner =
                CollectSnapshotProductionKnowledgeSlots(source);
            for (int block = 0; block < source.OwnerAttributes.Blocks.Count; block++)
            {
                OriginalSaveRuntimeOwnerAttributeBlockSnapshot saved =
                    source.OwnerAttributes.Blocks[block];
                int expectedOwner = block - 1;
                if (saved.Owner != expectedOwner)
                {
                    throw new InvalidDataException(
                        $"Original save owner-attribute block {block} maps to owner " +
                        $"{saved.Owner}, expected {expectedOwner}.");
                }

                var mappedValues = new Dictionary<string, double>(
                    nativeSlots.Count, StringComparer.Ordinal);
                foreach (KeyValuePair<string, int> entry in nativeSlots)
                {
                    if (entry.Value >= saved.PrimaryValueBits.Count) continue;
                    mappedValues.Add(
                        entry.Key,
                        BitConverter.Int32BitsToSingle(saved.PrimaryValueBits[entry.Value]));
                }
                if (knowledgeAnchorsByOwner.TryGetValue(
                        saved.Owner,
                        out Dictionary<string, int>? anchors))
                {
                    foreach (KeyValuePair<string, int> anchor in anchors)
                    {
                        if (anchor.Value >= saved.PrimaryValueBits.Count) continue;
                        mappedValues[anchor.Key] =
                            BitConverter.Int32BitsToSingle(saved.PrimaryValueBits[anchor.Value]);
                    }
                }
                context.OwnerAttribs.Add(saved.Owner, mappedValues);
            }
        }

        // The restore-side twin of the capture's CollectProductionKnowledgeSlots: archive CProd
        // slot registry words → the per-owner Bp<Class> page anchors, taken from the SNAPSHOT
        // (live entities have no production slots yet at attribute-restore time).
        private static Dictionary<int, Dictionary<string, int>>
            CollectSnapshotProductionKnowledgeSlots(OriginalSaveRuntimeSnapshot source)
        {
            var result = new Dictionary<int, Dictionary<string, int>>();
            foreach (OriginalSaveRuntimeEntitySnapshot saved in source.Entities)
            {
                if (saved.Production is not OriginalSaveRuntimeEntityProductionSnapshot production)
                    continue;
                foreach (OriginalSaveRuntimeEntityProductionSlotSnapshot slot in production.Slots)
                {
                    if (slot.RegistryValue2 < 0 || slot.RegistryValue2 >= 512) continue;
                    if (!result.TryGetValue(saved.Owner, out Dictionary<string, int>? anchors))
                    {
                        anchors = new Dictionary<string, int>(StringComparer.Ordinal);
                        result.Add(saved.Owner, anchors);
                    }
                    string attributeName = "Bp" + slot.ClassName;
                    if (anchors.TryGetValue(attributeName, out int existing) &&
                        existing != slot.RegistryValue2)
                    {
                        throw new InvalidDataException(
                            $"Production knowledge attribute '{attributeName}' maps to native " +
                            $"slots {existing} and {slot.RegistryValue2}.");
                    }
                    anchors[attributeName] = slot.RegistryValue2;
                }
            }
            return result;
        }

        private static IDictionary<string, double> WritableStatValues(StatValues values)
        {
            if (values.Raw is IDictionary<string, double> writable) return writable;
            throw new InvalidOperationException(
                "The entity stat store does not expose its raw save-restoration dictionary.");
        }

        private static void RestoreObjectGraphState(
            OriginalSaveRuntimeSnapshot source,
            IReadOnlyDictionary<int, DigglesTclEntity> entities)
        {
            foreach (OriginalSaveRuntimeEntitySnapshot saved in source.Entities)
            {
                DigglesTclEntity owner = entities[saved.ObjectId];
                foreach (int childObjectId in saved.ChildObjectIds)
                {
                    owner.Inventory.Add(childObjectId);
                    DigglesTclEntity child = entities[childObjectId];
                    if ((source.EntitiesById[childObjectId].ObjectFlags & 0x00000002) != 0 &&
                        child.ContainedIn < 0)
                    {
                        // Native saves can contain stale duplicate inventory slots. The native
                        // flag has no parent back-reference, so the port uses the first ID-ordered
                        // inventory owner while retaining every serialized list entry verbatim.
                        child.ContainedIn = owner.Handle;
                    }
                }

                if (saved.LinkedObjectId >= 0 &&
                    entities.ContainsKey(saved.LinkedObjectId))
                {
                    owner.LinkedToHandle = saved.LinkedObjectId;
                    owner.LinkedSlot = saved.LinkedSlot;
                }
            }
        }

        // C2d — type-1/11 social + planner sub-state. Runs after the object graph exists so family
        // handles and the idle pool see the reconstructed entities. NO Tcl and NO dispatcher tick:
        // the pseudo-vars are written directly and the idle flag only re-seeds pool membership.
        private static bool RestoreDwarfExtensionState(
            OriginalSaveRuntimeSnapshot source,
            DigglesTclContext context,
            IReadOnlyDictionary<int, DigglesTclEntity> entities)
        {
            bool restoreRan = true;
            foreach (OriginalSaveRuntimeEntitySnapshot saved in source.Entities)
            {
                DigglesTclEntity entity = entities[saved.ObjectId];

                // Family graph (types 1 and 11). The tri-state {id,state} is retained verbatim on
                // the live entity; the engine materializes partner_info pseudo-vars for linked
                // (state 1) records only, tolerating dangling ids.
                if (saved.Family is OriginalSaveRuntimeEntityFamilySnapshot family)
                {
                    entity.HasFamilyState = true;
                    entity.FamilyPartnerId = family.Partner.Id;
                    entity.FamilyPartnerState = family.Partner.State;
                    entity.FamilyPartnerReferenceObjectId = family.Partner.ReferenceObjectId;
                    entity.FamilyMotherId = family.Mother.Id;
                    entity.FamilyMotherState = family.Mother.State;
                    entity.FamilyMotherReferenceObjectId = family.Mother.ReferenceObjectId;
                    entity.FamilyFatherId = family.Father.Id;
                    entity.FamilyFatherState = family.Father.State;
                    entity.FamilyFatherReferenceObjectId = family.Father.ReferenceObjectId;
                    entity.FamilyChildrenNames.Clear();
                    entity.FamilyChildrenNames.AddRange(family.ChildrenNames);
                    entity.FamilyPregnancyFlag = family.PregnancyFlag;
                    DigglesTclEngine.RestoreFamilyLinks(
                        entity,
                        family.Partner.Id, family.Partner.State,
                        family.Mother.Id, family.Mother.State,
                        family.Father.Id, family.Father.State,
                        family.ChildrenNames,
                        family.PregnancyFlag);
                }

                // Talkissue ledger (type 1). The per-issue weights rebuild the live ledger 1:1; the
                // running desire sum is persisted independently (it is not Σweights, B2 §3).
                if (saved.TalkIssues is OriginalSaveRuntimeEntityTalkIssueSnapshot talk)
                {
                    entity.TalkIssuesSum = talk.Sum;
                    if (talk.Entries.Count > 0)
                    {
                        Dictionary<string, double> ledger =
                            entity.TalkIssues ??= new Dictionary<string, double>();
                        foreach (OriginalSaveRuntimeEntityTalkIssue issue in talk.Entries)
                            ledger[issue.Name] = issue.Weight;
                    }
                }

                // Planner gnome sub-state (type 1). All four words are restored to live entity state;
                // the idle-announce flag also re-seeds the dispatcher pool without ticking it.
                if (saved.Planner is OriginalSaveRuntimeEntityPlannerSnapshot planner)
                {
                    entity.PlannerIdleAnnounceFlag = planner.IdleAnnounceFlag;
                    entity.PlannerIdleSince = planner.IdleSince;
                    entity.PlannerCurrentWorkplaceId = planner.CurrentWorkplaceObjectId;
                    entity.AssignedWorkplaceId = planner.AssignedWorkplaceObjectId;
                    if (planner.IdleAnnounceFlag == 1)
                    {
                        if (context.ProdDispatcher == null) restoreRan = false;
                        else context.ProdDispatcher.RestoreIdlePoolMembership(entity);
                    }
                }
            }
            return restoreRan;
        }

        private static Dictionary<string, string[]> BuildProductionSlotCatalogs(
            IReadOnlyList<OriginalSaveRuntimeEntitySnapshot> savedEntities,
            DigglesTclContext context,
            List<string> loadWarnings)
        {
            var catalogs = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (OriginalSaveRuntimeEntitySnapshot saved in savedEntities)
            {
                if (saved.Production == null) continue;

                DigglesClass runtimeClass = context.Classes[saved.ClassName];
                if (!catalogs.TryGetValue(saved.ClassName, out string[]? slotNames))
                {
                    slotNames = ReadProductionSlotNames(context, runtimeClass);
                    catalogs.Add(saved.ClassName, slotNames);
                }
                ValidateProductionState(saved, runtimeClass, slotNames, loadWarnings);
            }
            return catalogs;
        }

        internal static string[] ReadProductionSlotNames(
            DigglesTclContext context,
            DigglesClass runtimeClass)
        {
            TclProc? method = context.FindMethod(runtimeClass, ProductionItemsMethod);
            if (method == null) return Array.Empty<string>();

            List<string> command = TclList.Parse(method.Body);
            if (command.Count != 2 ||
                !string.Equals(command[0], "return", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Class '{runtimeClass.Name}' has a non-literal {ProductionItemsMethod} " +
                    "method that cannot be used during Tcl-free save staging.");
            }

            // The shipped elevator classes spell an empty catalog as `return [list]`. Treat that
            // exact data expression as empty without evaluating it; every non-empty catalog is a
            // literal list baked into the inherited prod_items method by the tech-tree loader.
            if (string.Equals(command[1], "[list]", StringComparison.Ordinal))
                return Array.Empty<string>();

            List<string> parsedNames = TclList.Parse(command[1]);
            var slotNames = new string[parsedNames.Count];
            parsedNames.CopyTo(slotNames);
            var uniqueNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (string slotName in slotNames)
            {
                if (slotName.Length == 0 || !uniqueNames.Add(slotName))
                {
                    throw new InvalidDataException(
                        $"Class '{runtimeClass.Name}' has an invalid production slot catalog.");
                }
            }
            return slotNames;
        }

        private static void ValidateProductionState(
            OriginalSaveRuntimeEntitySnapshot saved,
            DigglesClass runtimeClass,
            IReadOnlyList<string> slotNames,
            List<string> loadWarnings)
        {
            OriginalSaveRuntimeEntityProductionSnapshot production = saved.Production ??
                throw new InvalidOperationException("A production snapshot was expected.");
            var classSlotNames = new HashSet<string>(slotNames, StringComparer.Ordinal);
            foreach (OriginalSaveRuntimeEntityProductionSlotSnapshot slot in production.Slots)
            {
                if (slot.OrderCount is < 0 or > 10)
                {
                    throw new InvalidDataException(
                        $"Object {saved.ObjectId} production slot '{slot.ClassName}' has invalid " +
                        $"order count {slot.OrderCount}.");
                }
                if (!runtimeClass.DisableScripting && !classSlotNames.Contains(slot.ClassName))
                {
                    // Saves made on a modded install archive production slots the vanilla class
                    // does not offer (e.g. PowerPotion in Brauerei, Barrow's Schubkarren in
                    // Saegewerk). The unmodded original cannot produce them either. The live
                    // overlay retains the inert count and the raw slot rides the snapshot for byte
                    // round-trip. Tolerate, loudly.
                    LogCompatibilityWarning(
                        loadWarnings,
                        $"Original save object {saved.ObjectId} archives production slot " +
                        $"'{slot.ClassName}' absent from class '{saved.ClassName}' — likely a " +
                        "mod-defined product. Retaining the archived slot in the live overlay.");
                }
            }
            if (production.WorkState is < 0 or > 6)
            {
                throw new InvalidDataException(
                    $"Object {saved.ObjectId} has invalid production work-state " +
                    $"{production.WorkState}.");
            }
            ValidateProductionBooleanByte(
                saved.ObjectId,
                production.UnpackRequestedByte,
                "unpack request");
            ValidateProductionBooleanByte(
                saved.ObjectId,
                production.PackRequestedByte,
                "pack request");
            ValidateProductionBooleanByte(
                saved.ObjectId,
                production.BuildupRequestedByte,
                "buildup request");
            ValidateProductionBooleanByte(
                saved.ObjectId,
                production.ProductionEnabled,
                "enabled flag");
        }

        private static void ValidateProductionBooleanByte(
            int objectId,
            byte value,
            string label)
        {
            if (value > 1)
            {
                throw new InvalidDataException(
                    $"Object {objectId} has invalid production {label} value {value}.");
            }
        }

        private static void RestoreProductionExtensionState(
            OriginalSaveRuntimeSnapshot source,
            IReadOnlyDictionary<string, string[]> productionSlotsByClass,
            IReadOnlyDictionary<int, DigglesTclEntity> entities,
            List<string> loadWarnings)
        {
            foreach (OriginalSaveRuntimeEntitySnapshot saved in source.Entities)
            {
                if (saved.Production is not OriginalSaveRuntimeEntityProductionSnapshot production)
                    continue;

                DigglesTclEntity entity = entities[saved.ObjectId];
                entity.NativeProductionSlots = new List<DigglesTclEntity.NativeProductionSlotState>(
                    production.Slots.Count);
                entity.ProdSlots.Clear();
                foreach (string slotName in productionSlotsByClass[saved.ClassName])
                    entity.ProdSlots.Add(slotName, 0.0);
                foreach (OriginalSaveRuntimeEntityProductionSlotSnapshot slot in production.Slots)
                {
                    entity.NativeProductionSlots.Add(new DigglesTclEntity.NativeProductionSlotState
                    {
                        RegistryValue1 = slot.RegistryValue1,
                        RegistryValue2 = slot.RegistryValue2,
                        ClassName = slot.ClassName,
                        ResourceName = slot.ResourceName,
                        DemandBits = slot.DemandBits,
                        Invented = slot.Invented,
                        Buildable = slot.Buildable,
                    });
                    // Overlay EVERY archived slot, not only class prod_items entries: live Tcl
                    // registers runtime slots the catalog never lists (internal task verbs such
                    // as `_Pilzhut_einlagern`), and modded-era saves carry mod product names.
                    // The archived counts ARE the Tcl state of the original session; production
                    // planning independently gates on class recipes, so an unknown name is inert.
                    entity.ProdSlots[slot.ClassName] = slot.OrderCount;
                }

                entity.ProdState = ProductionWorkStateName(production.WorkState);
                entity.CurrentWorkerHandle = ResolveProductionWorker(
                    saved,
                    production,
                    source,
                    entities,
                    loadWarnings);
                entity.UnpackRequested = production.UnpackRequestedByte == 1;
                entity.PackRequested = production.PackRequestedByte == 1;
                entity.BuildupRequested = production.BuildupRequestedByte == 1;
                entity.NativeProductionUnpackFlag = production.UnpackFlagByte;
                entity.NativeProductionPackFlag = production.PackFlagByte;
                entity.NativeProductionResidue2D = production.PlacementVariantByte;
                entity.NativeProductionFlowBytes = CopyBytes(production.FlowBytes);
                entity.OrderPos = new Vector3(
                    production.PendingPositionX,
                    production.PendingPositionY,
                    production.PendingPositionZ);
                entity.ProdProgressCur = production.ProgressCurrent;
                entity.ProdProgressMax = production.ProgressMaximum;
                entity.NativeProductionCurrentProductStamp = production.CurrentProductStamp;
                entity.NativeProductionInventionCandidateSlot = production.InventionCandidateSlot;
                entity.VarsScope.Vars["_prod_enabled"] =
                    production.ProductionEnabled == 0 ? "0" : "1";
                entity.EnergyClass = production.EnergyClass;
                entity.EnergyConsumption = production.EnergyConsumption;
                entity.EnergyStore = production.EnergyStore;
                entity.EnergyMaxStore = production.EnergyMaximumStore;
                entity.EnergyCurrentLoad = production.EnergyCurrentLoad;
                entity.NativeProductionState3C = production.State3C;
                entity.NativeProductionState140 = production.State140;
                RestoreCurrentProduct(entity);
                RestoreProductionGuestSeats(production, entity, entities);
            }
        }

        private static byte[] CopyBytes(IReadOnlyList<byte> source)
        {
            var result = new byte[source.Count];
            for (int index = 0; index < result.Length; index++) result[index] = source[index];
            return result;
        }

        private static int ResolveProductionWorker(
            OriginalSaveRuntimeEntitySnapshot saved,
            OriginalSaveRuntimeEntityProductionSnapshot production,
            OriginalSaveRuntimeSnapshot source,
            IReadOnlyDictionary<int, DigglesTclEntity> entities,
            List<string> loadWarnings)
        {
            int workerObjectId = production.AssignedWorkerObjectId;
            if (workerObjectId < 0) return -1;
            if (!entities.TryGetValue(workerObjectId, out DigglesTclEntity? worker))
            {
                LogCompatibilityWarning(
                    loadWarnings,
                    $"Original save production object {saved.ObjectId} references missing worker " +
                    $"{workerObjectId}; leaving it unassigned.");
                return -1;
            }

            OriginalSaveRuntimeEntityPlannerSnapshot? planner =
                source.EntitiesById[workerObjectId].Planner;
            if (planner != null && planner.CurrentWorkplaceObjectId != saved.ObjectId)
            {
                LogCompatibilityWarning(
                    loadWarnings,
                    $"Original save production object {saved.ObjectId} assigns worker " +
                    $"{workerObjectId}, while that worker's planner names workplace " +
                    $"{planner.CurrentWorkplaceObjectId}; retaining the production assignment.");
            }
            return worker.Handle;
        }

        private static void RestoreCurrentProduct(DigglesTclEntity entity)
        {
            if (entity.VarsScope.Vars.TryGetValue("current_itemtype", out string currentItem) &&
                currentItem != "0" &&
                entity.ProdSlots.ContainsKey(currentItem))
            {
                entity.CurrentProdItem = currentItem;
            }
        }

        private static void RestoreProductionGuestSeats(
            OriginalSaveRuntimeEntityProductionSnapshot production,
            DigglesTclEntity entity,
            IReadOnlyDictionary<int, DigglesTclEntity> entities)
        {
            var guestLinks = new int[DigglesTclEntity.GuestSeatCount];
            var guests = new int[DigglesTclEntity.GuestSeatCount];
            var guestOrders = new double[DigglesTclEntity.GuestSeatCount];
            for (int index = 0; index < DigglesTclEntity.GuestSeatCount; index++)
            {
                OriginalSaveRuntimeEntityProductionGuestSeatSnapshot seat =
                    production.GuestSeats[index];
                guestLinks[index] = seat.SeatLinkId;
                if (seat.GuestObjectId > 0 &&
                    entities.TryGetValue(seat.GuestObjectId, out DigglesTclEntity? guest))
                {
                    guests[index] = guest.Handle;
                }
                guestOrders[index] = seat.OrderCount;
            }
            entity.GuestLinks = guestLinks;
            entity.Guests = guests;
            entity.GuestOrders = guestOrders;
        }

        private static string ProductionWorkStateName(int workState) => workState switch
        {
            0 => "idle",
            1 => "research",
            2 => "produce",
            3 => "bring",
            4 => "pack",
            5 => "buildup",
            6 => "unpack",
            _ => throw new ArgumentOutOfRangeException(nameof(workState)),
        };

        private static void RestoreDerivedInventoryCapacities(
            DigglesTclContext context,
            IReadOnlyDictionary<int, DigglesTclEntity> entities)
        {
            // Native def_attrib does not serialize inventory capacity. Recompute the gnome base
            // slots plus restored transport bonuses only after the complete inventory graph exists.
            foreach (DigglesTclEntity entity in entities.Values)
                entity.InventoryCapacity = DigglesTclEngine.EffectiveInvCapacity(context, entity);
        }

        private static void RestoreParticleState(
            OriginalSaveRuntimeSnapshot source,
            DigglesTclContext context)
        {
            ParticleSystemState particles = context.Particles ??
                throw new InvalidOperationException("Candidate particle state was not initialized.");
            particles.Sources.Clear();
            particles.Temps.Clear();

            var recordsByHandle = new Dictionary<int, OriginalSaveRuntimeParticle>(
                source.Particles.Records.Count);
            foreach (OriginalSaveRuntimeParticle record in source.Particles.Records)
                recordsByHandle.Add(record.SlotIndex, record);

            var attachedHandles = new HashSet<int>();
            foreach (OriginalSaveRuntimeEntitySnapshot entity in source.Entities)
            {
                foreach (OriginalSaveObjectParticleSourceRecord attachment in
                         entity.ParticleState.Sources)
                {
                    if (!attachment.HasParticleHandle) continue;
                    attachedHandles.Add(attachment.ParticleHandle);
                    if (!recordsByHandle.TryGetValue(
                            attachment.ParticleHandle,
                            out OriginalSaveRuntimeParticle native))
                    {
                        continue;
                    }

                    ParticleSystemState.Source projected = particles.Configure(
                        entity.ObjectId,
                        attachment.SourceIndex,
                        native.ParticleKind,
                        new Vector3(
                            attachment.RelativePositionX.AsSingle,
                            attachment.RelativePositionY.AsSingle,
                            attachment.RelativePositionZ.AsSingle),
                        new Vector3(
                            native.DirectionX.AsSingle,
                            native.DirectionY.AsSingle,
                            native.DirectionZ.AsSingle),
                        Math.Max(0, native.ParticleCount - 1),
                        Math.Max(0, native.RawValue50),
                        native.RawFlag64 != 0,
                        NormalizeModelLink(attachment.RawLinkId),
                        NormalizeSubMesh(native.RawValue74));
                    ApplyNativeParticleState(projected, native, false);
                }
            }

            foreach (OriginalSaveRuntimeParticle native in source.Particles.Records)
            {
                if (attachedHandles.Contains(native.SlotIndex)) continue;
                float expiry = native.RawValue68.AsSingle;
                if (native.Mode == 1 && expiry > source.GameTime)
                {
                    ParticleSystemState.TempSource temp = particles.CreateTemp(
                        native.ParticleKind,
                        new Vector3(
                            native.PositionX.AsSingle,
                            native.PositionY.AsSingle,
                            native.PositionZ.AsSingle),
                        new Vector3(
                            native.DirectionX.AsSingle,
                            native.DirectionY.AsSingle,
                            native.DirectionZ.AsSingle),
                        Math.Max(0, native.RawValue50),
                        Math.Max(0.01f, expiry - (float)source.GameTime));
                    temp.NativeParticleHandle = native.SlotIndex;
                    continue;
                }

                ParticleSystemState.Source projected = particles.Configure(
                    ParticleSystemState.NativeWorldSourceObjectId,
                    native.SlotIndex,
                    native.ParticleKind,
                    new Vector3(
                        native.PositionX.AsSingle,
                        native.PositionY.AsSingle,
                        native.PositionZ.AsSingle),
                    new Vector3(
                        native.DirectionX.AsSingle,
                        native.DirectionY.AsSingle,
                        native.DirectionZ.AsSingle),
                    Math.Max(0, native.ParticleCount - 1),
                    Math.Max(0, native.RawValue50),
                    native.RawFlag64 != 0,
                    -1,
                    NormalizeSubMesh(native.RawValue74));
                ApplyNativeParticleState(projected, native, true);
            }
        }

        private static void ApplyNativeParticleState(
            ParticleSystemState.Source projected,
            OriginalSaveRuntimeParticle native,
            bool worldAnchored)
        {
            projected.Active = native.Mode == 1;
            projected.WorldAnchored = worldAnchored;
            projected.MaxRangeSquared = native.RawValue60.AsSingle;
            projected.HasNativeState = true;
            projected.NativeParticleHandle = native.SlotIndex;
            projected.NativeMode = native.Mode;
            projected.NativeRawObjectToken = native.LinkedObjectToken;
            projected.NativeRawValue74 = native.RawValue74;
            projected.NativeRawValue80 = native.RawValue80;
        }

        private static int NormalizeModelLink(int rawLinkId) =>
            rawLinkId >= -1 && rawLinkId < 32 ? rawLinkId : -1;

        private static int NormalizeSubMesh(int rawSubMesh) =>
            rawSubMesh >= -1 && rawSubMesh < 256 ? rawSubMesh : -1;

        private static void RestoreTimerState(
            OriginalSaveRuntimeSnapshot source,
            DigglesTclContext context)
        {
            var registryEntries = new TimerRegistry.Entry[source.Timers.Records.Count];
            var schedulerEntries = new TimerScheduler.Entry[source.Timers.Records.Count];
            for (int index = 0; index < source.Timers.Records.Count; index++)
            {
                OriginalSaveRuntimeTimer timer = source.Timers.Records[index];
                long serial = index + 1L;
                registryEntries[index] = new TimerRegistry.Entry
                {
                    Serial = serial,
                    Handle = timer.OwnerObjectId,
                    UserId = timer.UserId,
                    Event = timer.EventName,
                    RemainingFires = timer.RepeatsLeft,
                };
                schedulerEntries[index] = new TimerScheduler.Entry
                {
                    Handle = timer.OwnerObjectId,
                    Event = timer.EventName,
                    NextFire = timer.NextFire.AsDouble,
                    Interval = timer.Interval.AsDouble,
                    RepeatsLeft = timer.RepeatsLeft,
                    Payload = CreateTimerPayload(timer, serial),
                };
            }

            context.Timers.RestoreEntries(registryEntries);
            TimerScheduler scheduler = context.Sim?.Scheduler ??
                throw new InvalidOperationException("Candidate timer scheduler was not initialized.");
            scheduler.RestoreEntries(schedulerEntries);
        }

        private static Dictionary<string, string> CreateTimerPayload(
            OriginalSaveRuntimeTimer timer,
            long serial)
        {
            var payload = new Dictionary<string, string>
            {
                [TimerRegistry.SerialKey] = serial.ToString(CultureInfo.InvariantCulture),
                [TimerRegistry.UserIdKey] = timer.UserId.ToString(CultureInfo.InvariantCulture),
                ["-pos1"] = FormatTimerVector(
                    timer.Position1X.AsSingle,
                    timer.Position1Y.AsSingle,
                    timer.Position1Z.AsSingle),
                ["-pos2"] = FormatTimerVector(
                    timer.Position2X.AsSingle,
                    timer.Position2Y.AsSingle,
                    timer.Position2Z.AsSingle),
                ["-pos3"] = FormatTimerVector(
                    timer.Position3X.AsSingle,
                    timer.Position3Y.AsSingle,
                    timer.Position3Z.AsSingle),
                ["-num1"] = timer.Number1.AsSingle.ToString("R", CultureInfo.InvariantCulture),
                ["-num2"] = timer.Number2.AsSingle.ToString("R", CultureInfo.InvariantCulture),
                ["-num3"] = timer.Number3.AsSingle.ToString("R", CultureInfo.InvariantCulture),
                ["-text1"] = timer.Text1.Text,
                ["-text2"] = timer.Text2.Text,
            };
            AddTimerSubject(payload, "-subject1", timer.Subject1ObjectId);
            AddTimerSubject(payload, "-subject2", timer.Subject2ObjectId);
            AddTimerSubject(payload, "-subject3", timer.Subject3ObjectId);
            return payload;
        }

        private static void AddTimerSubject(
            Dictionary<string, string> payload,
            string key,
            int objectId)
        {
            if (objectId >= 0)
                payload[key] = objectId.ToString(CultureInfo.InvariantCulture);
        }

        private static string FormatTimerVector(float x, float y, float z) =>
            x.ToString("R", CultureInfo.InvariantCulture) + " " +
            y.ToString("R", CultureInfo.InvariantCulture) + " " +
            z.ToString("R", CultureInfo.InvariantCulture);

        private static void RestoreTriggerState(
            OriginalSaveRuntimeSnapshot source,
            DigglesTclContext context)
        {
            TriggerService triggerService = context.TriggerService ??
                throw new InvalidOperationException("Candidate trigger service was not initialized.");
            var records = new TriggerRecord?[source.Triggers.Count];
            for (int index = 0; index < records.Length; index++)
            {
                OriginalSaveRuntimeTriggerSnapshot? saved = source.Triggers[index];
                if (saved == null) continue;
                if (saved.UnsupportedTargetTypeMask != 0)
                {
                    throw new NotSupportedException(
                        $"Native trigger {saved.Index} has unsupported target type mask 0x{saved.UnsupportedTargetTypeMask:X8}.");
                }
                records[index] = saved.CreateRuntimeRecord(index);
            }
            triggerService.RestoreRecords(records);
        }

        private static void RestoreSelectionControlGroupState(
            OriginalSaveRuntimeSnapshot source,
            DigglesTclContext context)
        {
            if (source.SelectionControlGroups.Groups.Count !=
                context.SelectionControlGroups.Length)
            {
                throw new InvalidOperationException(
                    "Native selection control groups do not match the runtime dimensions.");
            }
            for (int group = 0; group < context.SelectionControlGroups.Length; group++)
            {
                List<int> restored = context.SelectionControlGroups[group];
                restored.Clear();
                foreach (int objectId in source.SelectionControlGroups.Groups[group])
                    restored.Add(objectId);
            }
        }

        private static void RestoreSparetimeState(
            OriginalSaveRuntimeSnapshot source,
            DigglesTclContext context,
            IReadOnlyDictionary<int, DigglesTclEntity> entities)
        {
            context.SpareRegistry.Clear();
            foreach (OriginalSaveRuntimeSparetimeRegistration saved in source.Sparetime.Registrations)
            {
                var category = (SparetimeCategory)saved.Category;
                if (entities.TryGetValue(saved.ObjectId, out DigglesTclEntity? entity))
                    entity.SpareKind = SparetimeCategoryCodec.GetName(category);
                context.SpareRegistry.Add(new SparetimeRegistration(category, saved.ObjectId));
            }
        }

        private static void RestoreHorizonState(
            OriginalSaveRuntimeSnapshot source,
            DigglesTclContext context)
        {
            context.HorizonRegions.Clear();
            foreach (OriginalSaveRuntimeHorizonRegion saved in source.Horizon.Regions)
            {
                context.HorizonRegions.Add(new HorizonRegionState(
                    new Vector3(saved.PositionX, saved.PositionY, saved.PositionZ),
                    saved.Red,
                    saved.Green,
                    saved.Blue,
                    saved.Depth,
                    saved.Size));
            }
        }

        private static void RestoreAdaptiveSoundMarkerState(
            OriginalSaveRuntimeSnapshot source,
            DigglesTclContext context)
        {
            AudioState audio = context.Audio ??
                throw new InvalidOperationException("Candidate audio state was not initialized.");
            audio.MusicMarkers.Clear();
            foreach (OriginalSaveRuntimeAdaptiveSoundMarker saved in
                     source.AdaptiveSoundMarkers.Markers)
            {
                audio.MusicMarkers.Add(new AudioState.MusicMarker
                {
                    ObjectId = saved.ObjectId,
                    Pos = new Vector3(saved.PositionX, saved.PositionY, saved.PositionZ),
                    Theme = saved.Theme,
                    Radius = saved.Range,
                });
            }
        }

        private static void RestoreNewsTickerState(
            OriginalSaveRuntimeSnapshot source,
            DigglesTclContext context)
        {
            context.NewsTicker.Restore(source.NewsTicker.CreateSaveState());
        }

        private static void RestoreAiState(
            OriginalSaveRuntimeSnapshot source,
            DigglesTclContext context)
        {
            AiSystem ai = context.Ai ??
                throw new InvalidOperationException("Candidate AI system was not initialized.");
            foreach (OriginalSaveRuntimeAiOwnerSnapshot saved in source.Ai.Owners)
            {
                var names = new string[saved.Variables.Count];
                var values = new string[saved.Variables.Count];
                for (int index = 0; index < saved.Variables.Count; index++)
                {
                    names[index] = saved.Variables[index].Name;
                    values[index] = saved.Variables[index].Value;
                }
                ai.RestoreNativeOwnerState(
                    saved.Owner,
                    saved.InterpreterPresent,
                    saved.Enabled,
                    names,
                    values,
                    source.GameTime);
            }
        }

        private static void RestoreStoryState(
            OriginalSaveRuntimeSnapshot source,
            DigglesTclContext context)
        {
            OriginalSaveRuntimeStorySnapshot saved = source.Story;
            context.StoryEvents.Clear();
            context.StoryEventOrder.Clear();
            context.StoryEventTriggers.Clear();
            foreach (OriginalSaveRuntimeStoryEventSnapshot storyEvent in saved.Events)
            {
                context.StoryEventOrder.Add(storyEvent.Name);
                context.StoryEvents.Add(storyEvent.Name, storyEvent.Completed ? "1" : "0");
                context.StoryEventTriggers.Add(
                    storyEvent.Name,
                    (storyEvent.Trigger, storyEvent.Extra));
            }

            context.StoryMap.Clear();
            context.StoryMapW = saved.MapWidth;
            context.StoryMapH = saved.MapHeight;
            for (int index = 0; index < saved.MapValues.Count; index++)
            {
                int value = saved.MapValues[index];
                if (value == 0) continue;
                int x = index % saved.MapWidth;
                int y = index / saved.MapWidth;
                context.StoryMap.Add((x, y), value);
            }

            context.StoryZones.Clear();
            foreach (OriginalSaveRuntimeStoryZoneSnapshot zone in saved.Zones)
            {
                context.StoryZones.Add(new DigglesTclContext.StoryZone
                {
                    Name = zone.Name,
                    Bound = zone.UpperBound,
                });
            }
            context.StoryZoneFloor = saved.ZoneFloor;

            context.StoryTempGroups.Clear();
            foreach (OriginalSaveRuntimeStoryGroupSnapshot group in saved.Groups)
                context.StoryTempGroups.Add(group.Name, group.BuildPortSpec());

            context.StoryTemps.Clear();
            context.StoryPlacedTemps.Clear();
            foreach (OriginalSaveRuntimeStoryTempSnapshot temp in saved.Temps)
            {
                OriginalSaveRuntimeStoryGroupSnapshot? group = temp.TemplateGroupIndex >= 0
                    ? saved.Groups[temp.TemplateGroupIndex]
                    : null;
                int digConnectGroupEntry = group != null ? group.HeaderValues[2] : 0;
                var restored = new DigglesTclContext.StoryTemp
                {
                    Name = NormalizeStoryTemplateName(temp.Name),
                    Lo = temp.LowerDepth,
                    Hi = temp.UpperDepth,
                    Zone = temp.ZoneIndex,
                    Exclusive = temp.Exclusive,
                    ZoneBorder = temp.ZoneBorderIndex >= 0
                        ? saved.Zones[temp.ZoneBorderIndex].Name
                        : "",
                    StoneIdx = temp.StoneIndex,
                    MinDistRef = temp.MinimumDistanceReferenceIndex >= 0
                        ? NormalizeStoryTemplateName(
                            saved.Temps[temp.MinimumDistanceReferenceIndex].Name)
                        : "",
                    MinDistVal = temp.MinimumDistance,
                    MidDist = temp.MiddleDistance,
                    NonRelevant = temp.NonRelevantProbability,
                    DigConnect = digConnectGroupEntry.ToString(CultureInfo.InvariantCulture) + " " +
                        temp.DigConnectValue.ToString(CultureInfo.InvariantCulture),
                    Connect = group?.BuildPortConnectSpec() ?? "",
                    CheckAlign = temp.CheckAlign ? 1 : 0,
                    Placed = temp.Placed,
                    PlacedX = temp.PlacedX,
                    PlacedY = temp.PlacedY,
                };
                foreach (int eventIndex in temp.EventIndices)
                    restored.EventNames.Add(saved.Events[eventIndex].Name);
                if (group != null)
                {
                    foreach (OriginalSaveRuntimeStoryGroupEntrySnapshot entry in group.Entries)
                    {
                        if (entry.Completed)
                            restored.SeenChildren.Add(NormalizeStoryTemplateName(entry.Name));
                    }
                }
                context.StoryTemps.Add(restored);
                if (restored.Placed)
                    context.StoryPlacedTemps.Add(
                        (restored.Name, restored.PlacedX, restored.PlacedY));
            }

            context.StoryCallbacks.Clear();
            if (saved.CallbackObjectId >= 0)
                context.StoryCallbacks.Add(saved.CallbackObjectId, saved.CallbackScript);

            context.StoryRectX = saved.RectX;
            context.StoryRectY = saved.RectY;
            context.StoryRectW = saved.RectWidth;
            context.StoryRectH = saved.RectHeight;
            context.StoryRawRectX = saved.RawRectX;
            context.StoryRawRectY = saved.RawRectY;
            context.StoryRawRectW = saved.RawRectWidth;
            context.StoryRawRectH = saved.RawRectHeight;
            context.StoryScanX = saved.ScanX;
            context.StoryScanY = saved.ScanY;
            context.StoryUtemp = NormalizeStoryTemplateName(saved.SelectedTempName);
            context.StoryUtempPlaced = false;
            context.StoryUtempPlacedName = "";
            context.StoryUtempWorldX = 0;
            context.StoryUtempWorldY = 0;
            context.StoryUtempExtW = 0;
            context.StoryUtempExtH = 0;
            context.StoryLatchDir = saved.LatchDirection;
            context.StoryLatchX = saved.LatchX;
            context.StoryLatchY = saved.LatchY;
            context.StoryLatchConsumed = saved.LatchConsumed;
            context.StoryDigWeight = saved.DigWeight;
            context.StoryDigMessage = saved.DigMessageEnabled;
            context.StoryDigThreshold = saved.DigThreshold;
        }

        private static string NormalizeStoryTemplateName(string value)
        {
            return value.EndsWith(".tcl", StringComparison.OrdinalIgnoreCase)
                ? value.Substring(0, value.Length - 4)
                : value;
        }

        private static void RestorePlayerState(
            OriginalSaveRuntimeSnapshot source,
            DigglesTclContext context)
        {
            if (source.Players.ReservedFraming.Count !=
                context.NativePlayerReservedFraming.Length ||
                source.Players.Slots.Count != context.PlayerSlots.Length)
            {
                throw new InvalidOperationException(
                    "Native player state does not match the runtime dimensions.");
            }
            for (int index = 0; index < context.NativePlayerReservedFraming.Length; index++)
                context.NativePlayerReservedFraming[index] =
                    source.Players.ReservedFraming[index];

            // B5 proved that the 32 words historically labelled as player framing are the
            // native prodslot_override table. Keep the legacy mirror for parsed-model
            // compatibility, but restore the authoritative live service as well.
            context.ProdSlotOverrides.Restore(source.Players.ReservedFraming);

            for (int owner = 0; owner < context.PlayerSlots.Length; owner++)
            {
                OriginalSaveRuntimePlayerSlotSnapshot saved = source.Players.Slots[owner];
                DigglesTclContext.PlayerSlotState restored = context.PlayerSlots[owner];
                restored.PlayerName = saved.PlayerName;
                restored.SlotState = saved.SlotState;
                restored.PlayerOwnerId = saved.PlayerOwnerId;
                restored.ClanOwnerId = saved.ClanOwnerId;
                restored.SecondaryName = saved.SecondaryName;
                for (int target = 0; target < context.PlayerSlots.Length; target++)
                    context.Diplomacy[owner, target] = saved.Diplomacy[target];
            }
        }

        private static void RestoreElfState(
            OriginalSaveRuntimeSnapshot source,
            DigglesTclContext context)
        {
            ElfState elf = context.Elf ??
                throw new InvalidOperationException("Candidate CElf state was not initialized.");
            DigglesModelLibrary models = context.ModelLibrary ??
                throw new InvalidOperationException("Candidate model library was not initialized.");
            OriginalSaveRuntimeElfSnapshot saved = source.Elf;
            string? modelAnimation = null;
            int modelAnimationFrame = 0;
            int modelAnimationPhase = 0;
            if (saved.NativeMeshId >= 0)
            {
                if (!models.TryResolveNativeMesh(
                        "elfe",
                        saved.NativeMeshId,
                        out string resolvedAnimation,
                        out modelAnimationFrame))
                {
                    throw new NotSupportedException(
                        $"Native CElf mesh {saved.NativeMeshId} cannot be resolved in elfe.3db.");
                }
                modelAnimation = resolvedAnimation;
                modelAnimationPhase = ResolveElfAnimationPhase(
                    saved.CurrentRegisteredAnimation,
                    modelAnimation);
            }

            elf.RestoreNativeSaveState(new ElfState.NativeSaveState
            {
                TextureVariant = saved.TextureVariant,
                CurrentRegisteredAnimation = saved.CurrentRegisteredAnimation,
                RequestedRegisteredAnimation = saved.RequestedRegisteredAnimation,
                NativeModelDatabaseId = saved.NativeModelDatabaseId,
                NativeMeshId = saved.NativeMeshId,
                FrameAlpha = saved.FrameAlpha,
                LookAtObjectId = saved.LookAtObjectId,
                Position = Vector(saved.Position),
                LeanA = saved.LeanA,
                LeanB = saved.LeanB,
                NativeState64Bits = saved.NativeState64Bits,
                Target = Vector(saved.Target),
                Mode = saved.Mode,
                DrawOverUi = saved.DrawOverUi,
                AnimationHasMoreFrames = saved.AnimationHasMoreFrames,
                FirstParticleSourceHandle = saved.FirstParticleSourceHandle,
                SecondParticleSourceHandle = saved.SecondParticleSourceHandle,
                ResolvedModelAnimation = modelAnimation,
                ResolvedModelAnimationFrame = modelAnimationFrame,
                ResolvedModelAnimationPhase = modelAnimationPhase,
            });
        }

        private static int ResolveElfAnimationPhase(
            int registeredAnimation,
            string modelAnimation)
        {
            if ((uint)registeredAnimation >= (uint)ElfState.Anims.Length) return 0;
            ElfState.AnimDef definition = ElfState.Anims[registeredAnimation];
            if (string.Equals(modelAnimation, definition.End, StringComparison.Ordinal)) return 2;
            if (string.Equals(modelAnimation, definition.Loop, StringComparison.Ordinal)) return 1;
            return 0;
        }

        private static bool RestoreClassLights(
            RuntimeGameSession candidate,
            OriginalSaveRuntimeSnapshot source)
        {
            if (candidate.Engine.Ctx.EntitySpawner is not RuntimeWorldSpawner spawner) return false;

            foreach (OriginalSaveRuntimeClassLight light in source.ClassLights.Records)
            {
                if (light.IsObjectBound ||
                    (light.SourceToken != 0 && light.SourceToken != 1))
                {
                    continue;
                }

                spawner.AddStaticLight(
                    candidate.ContentRoot.transform,
                    new Vector3(
                        light.PositionX.AsSingle,
                        light.PositionY.AsSingle,
                        light.PositionZ.AsSingle),
                    new Color(
                        light.Red / NativeLightColorMaximum,
                        light.Green / NativeLightColorMaximum,
                        light.Blue / NativeLightColorMaximum),
                    light.Radius.AsSingle);
            }
            return true;
        }

        private static bool RestoreAnimatorContinuation(
            OriginalSaveRuntimeSnapshot source,
            IReadOnlyDictionary<int, DigglesTclEntity> entities,
            RuntimeWorldSpawner? runtimeSpawner,
            List<string> loadWarnings)
        {
            foreach (OriginalSaveRuntimeAnimationController record in
                     source.AnimationControllers.Records)
            {
                if (!entities.TryGetValue(record.ControllerIndex, out DigglesTclEntity entity) ||
                    entity.GameObject == null)
                {
                    continue;
                }
                if (IsBoxed(entity)) continue;

                DigglesAnimator? animator =
                    entity.GameObject.GetComponentInChildren<DigglesAnimator>(true);
                if (animator == null) continue;

                DigglesAnimController? controller =
                    entity.GameObject.GetComponentInChildren<DigglesAnimController>(true);
                if (controller != null && entity.AlternateAnimDb &&
                    string.Equals(controller.ObjName, "mann", StringComparison.Ordinal))
                {
                    controller.SetAlternate(true, "frau");
                }

                if (!TryResolveAnimatorContinuation(
                        record,
                        animator,
                        runtimeSpawner?.Library,
                        out ThreeDbModel? sourceModel,
                        out string ownerName,
                        out string clipName,
                        out int savedFrame,
                        out DigglesAnimator.AnimMode savedMode,
                        out string reason))
                {
                    WarnAnimatorContinuationSkipped(record, entity, reason, loadWarnings);
                    continue;
                }

                DigglesAnimClip? clip = FindLoadedAnimationClip(
                    animator,
                    sourceModel!,
                    ownerName,
                    clipName);
                clip ??= runtimeSpawner?.Library.GetClip(ownerName, clipName);
                if (clip == null || !ReferenceEquals(clip.SourceModel, sourceModel) ||
                    !string.Equals(clip.SourceObjName, ownerName, StringComparison.Ordinal) ||
                    clip.Frames.Length != record.DeclaredValueCount)
                {
                    WarnAnimatorContinuationSkipped(
                        record,
                        entity,
                        $"port clip '{ownerName}.{clipName}' is unavailable or does not match " +
                        "the saved model/frame identity",
                        loadWarnings);
                    continue;
                }

                string baseObject = controller == null
                    ? string.Empty
                    : controller.UseAlternate && !string.IsNullOrEmpty(controller.AlternateObjName)
                        ? controller.AlternateObjName
                        : controller.ObjName;
                bool isCrossObject = !string.Equals(
                    ownerName,
                    baseObject,
                    StringComparison.Ordinal);
                string animatorKey = isCrossObject ? ownerName + "." + clipName : clipName;
                animator.AddClip(clip, isCrossObject ? animatorKey : null);
                animator.SetAnim(animatorKey, savedFrame, savedMode);
            }
            return runtimeSpawner != null;
        }

        private static void RestoreTextureAnimations(
            OriginalSaveRuntimeSnapshot source,
            IReadOnlyDictionary<int, DigglesTclEntity> entities,
            RuntimeWorldSpawner? runtimeSpawner)
        {
            foreach (OriginalSaveRuntimeEntitySnapshot saved in source.Entities)
            {
                if (saved.TextureAnimations.Count == 0 ||
                    !entities.TryGetValue(saved.ObjectId, out DigglesTclEntity? entity) ||
                    entity.GameObject == null)
                {
                    continue;
                }

                DigglesAnimator? animator =
                    entity.GameObject.GetComponentInChildren<DigglesAnimator>(true);
                if (animator == null) continue;
                DigglesTextureAnimator textureAnimator =
                    animator.GetComponent<DigglesTextureAnimator>() ??
                    animator.gameObject.AddComponent<DigglesTextureAnimator>();
                for (int submesh = 0; submesh < saved.TextureAnimations.Count; submesh++)
                {
                    OriginalSaveRuntimeTextureAnimationSnapshot channel =
                        saved.TextureAnimations[submesh];
                    var textures = new Texture2D?[channel.FrameValues.Count];
                    string? baseMaterial = animator.GetSubmeshBaseMaterialName(submesh);
                    if (runtimeSpawner?.Library != null && !string.IsNullOrEmpty(baseMaterial))
                    {
                        for (int frame = 0; frame < textures.Length; frame++)
                        {
                            textures[frame] = runtimeSpawner.Library.GetVariantTexture(
                                baseMaterial!,
                                channel.FrameValues[frame]);
                        }
                    }
                    textureAnimator.RestoreNativeChannel(
                        submesh,
                        textures,
                        channel.FrameValues,
                        channel.Cursor,
                        channel.PlaybackMode,
                        submesh < saved.CurrentPartFrames.Count
                            ? saved.CurrentPartFrames[submesh]
                            : 0);
                }
            }
        }

        private static bool TryResolveAnimatorContinuation(
            OriginalSaveRuntimeAnimationController record,
            DigglesAnimator animator,
            DigglesModelLibrary? library,
            out ThreeDbModel? sourceModel,
            out string ownerName,
            out string clipName,
            out int savedFrame,
            out DigglesAnimator.AnimMode savedMode,
            out string reason)
        {
            sourceModel = null;
            ownerName = string.Empty;
            clipName = string.Empty;
            savedFrame = 0;
            savedMode = DigglesAnimator.AnimMode.Still;
            reason = string.Empty;

            foreach (DigglesAnimClip loadedClip in animator.Clips)
            {
                ThreeDbModel? candidateModel = loadedClip.SourceModel;
                if (candidateModel == null ||
                    !NativeModelMatchesSlot(candidateModel, record.ModelDatabaseSlot))
                {
                    continue;
                }

                if (sourceModel != null && !ReferenceEquals(sourceModel, candidateModel))
                {
                    reason = $"model slot {record.ModelDatabaseSlot} matches multiple port models";
                    return false;
                }
                sourceModel = candidateModel;
            }

            // Cross-database continuations (a saved clip from a database the live animator never
            // preloaded — e.g. a BOXED building's crate pose lives in 'transport', slot 12):
            // resolve the database by its native slot name straight from the library, exactly
            // like the live set_boxed swap does. Without this, every boxed building restored
            // visually UNPACKED.
            if (sourceModel == null && library != null &&
                (uint)record.ModelDatabaseSlot < (uint)NativeModelDatabaseNames.Length)
            {
                sourceModel = library.GetDatabaseModel(
                    NativeModelDatabaseNames[record.ModelDatabaseSlot]);
            }

            if (sourceModel == null)
            {
                reason = $"model slot {record.ModelDatabaseSlot} is unavailable on the live animator";
                return false;
            }
            if ((uint)record.AnimationState >= (uint)sourceModel.Anims.Count)
            {
                reason = $"animation id {record.AnimationState} is outside model slot " +
                    record.ModelDatabaseSlot;
                return false;
            }

            ThreeDbObject? owner = null;
            foreach (ThreeDbObject candidateOwner in sourceModel.Objects)
            {
                bool ownsAnimation = false;
                foreach (uint animationIndex in candidateOwner.Indices)
                {
                    if (animationIndex != (uint)record.AnimationState) continue;
                    ownsAnimation = true;
                    break;
                }
                if (!ownsAnimation) continue;
                if (owner != null && !ReferenceEquals(owner, candidateOwner))
                {
                    reason = $"animation id {record.AnimationState} has multiple port object owners";
                    return false;
                }
                owner = candidateOwner;
            }

            if (owner == null)
            {
                reason = $"animation id {record.AnimationState} has no port object owner";
                return false;
            }

            ThreeDbAnim animation = sourceModel.Anims[record.AnimationState];
            int matchingNames = 0;
            foreach (uint animationIndex in owner.Indices)
            {
                if (animationIndex >= (uint)sourceModel.Anims.Count) continue;
                if (string.Equals(
                        sourceModel.Anims[(int)animationIndex].Name1,
                        animation.Name1,
                        StringComparison.Ordinal))
                {
                    matchingNames++;
                }
            }
            if (matchingNames != 1 || string.IsNullOrEmpty(animation.Name1))
            {
                reason = $"animation id {record.AnimationState} has an ambiguous port clip name";
                return false;
            }
            if (animation.Indices.Length != record.DeclaredValueCount)
            {
                reason = $"saved frame count {record.DeclaredValueCount} disagrees with port clip " +
                    $"frame count {animation.Indices.Length}";
                return false;
            }
            if (record.RawControlA < (int)DigglesAnimator.AnimMode.Still ||
                record.RawControlA > (int)DigglesAnimator.AnimMode.Loop)
            {
                reason = $"saved animation mode {record.RawControlA} is unsupported";
                return false;
            }

            savedFrame = record.CurrentOrCompatibilityValue == -1
                ? 0
                : record.CurrentOrCompatibilityValue;
            if ((uint)savedFrame >= (uint)record.DeclaredValueCount)
            {
                reason = $"saved frame {savedFrame} is outside frame count " +
                    record.DeclaredValueCount;
                return false;
            }

            ownerName = owner.Name;
            clipName = animation.Name1;
            savedMode = (DigglesAnimator.AnimMode)record.RawControlA;
            return true;
        }

        private static bool NativeModelMatchesSlot(ThreeDbModel model, int modelSlot)
        {
            if ((uint)modelSlot >= (uint)NativeModelDatabaseNames.Length) return false;
            string expectedName = NativeModelDatabaseNames[modelSlot];
            if (string.Equals(model.ModelName, expectedName, StringComparison.OrdinalIgnoreCase))
                return true;

            return modelSlot == 4 &&
                model.ModelName.StartsWith("muetzen_", StringComparison.OrdinalIgnoreCase);
        }

        internal static int NativeModelSlot(ThreeDbModel model)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            for (int slot = 0; slot < NativeModelDatabaseNames.Length; slot++)
            {
                if (NativeModelMatchesSlot(model, slot)) return slot;
            }
            return -1;
        }

        private static DigglesAnimClip? FindLoadedAnimationClip(
            DigglesAnimator animator,
            ThreeDbModel sourceModel,
            string ownerName,
            string clipName)
        {
            foreach (DigglesAnimClip clip in animator.Clips)
            {
                if (ReferenceEquals(clip.SourceModel, sourceModel) &&
                    string.Equals(clip.SourceObjName, ownerName, StringComparison.Ordinal) &&
                    string.Equals(clip.Name, clipName, StringComparison.Ordinal))
                {
                    return clip;
                }
            }
            return null;
        }

        private static void WarnAnimatorContinuationSkipped(
            OriginalSaveRuntimeAnimationController record,
            DigglesTclEntity entity,
            string reason,
            List<string> loadWarnings)
        {
            LogCompatibilityWarning(
                loadWarnings,
                $"Original save animation controller {record.ControllerIndex} for " +
                $"'{entity.Class.Name}' was retained but not applied: {reason}.");
        }

        private static void RestoreObjectGraphPresentation(
            IReadOnlyDictionary<int, DigglesTclEntity> entities)
        {
            foreach (DigglesTclEntity entity in entities.Values)
            {
                if (entity.LinkedToHandle > 0 &&
                    entities.TryGetValue(entity.LinkedToHandle, out DigglesTclEntity parent))
                {
                    DigglesTclEngine.RestoreLinkAttachment(
                        entity,
                        parent,
                        entity.LinkedSlot);
                }
                DigglesTclEngine.SyncRenderVisible(entity);
                DigglesTclEngine.SetCollidersEnabled(entity, entity.ContainedIn < 0);
            }
        }

        private static void RestoreSnapToWallRotationPresentation(
            DigglesTclContext context,
            IReadOnlyDictionary<int, DigglesTclEntity> entities)
        {
            if (context.EntitySpawner == null) return;
            foreach (DigglesTclEntity entity in entities.Values)
            {
                if (entity.Class is not { SnapToWall: true } || entity.GameObject == null) continue;
                context.EntitySpawner.ApplyRotation(entity.GameObject, entity.Rotation);
            }
        }

        private static bool MaterializeEntityPresentation(
            RuntimeGameSession candidate,
            IReadOnlyDictionary<int, DigglesTclEntity> entities)
        {
            DigglesTclContext context = candidate.Engine.Ctx;
            if (context.EntitySpawner == null) return false;
            var runtimeSpawner = context.EntitySpawner as RuntimeWorldSpawner;
            // Restored entities keep their exact archived position: suppress the live dig-drop
            // spawn settle for the whole candidate materialization.
            if (runtimeSpawner != null) runtimeSpawner.SuppressSpawnSettle = true;
            try
            {
                foreach (KeyValuePair<int, DigglesTclEntity> pair in entities)
                {
                    DigglesTclEntity entity = pair.Value;
                    // The archived model identity WINS over the obj_init spawn pose: the native
                    // loader rebuilds the model pointer from the archived model-DB slot
                    // (anim-controller restore, B5). obj_init picks a fresh random pose for
                    // computed-model classes (brocken.tcl `stein_0[irandom …]`), so without this
                    // override a restored boulder could swap its mesh on every load; and for
                    // classes obj_init cannot dress (no TSV/class model) this seed is what
                    // builds the real mesh instead of a wireframe placeholder (manual test
                    // boulders/doors/wheels missing from loaded worlds).
                    // An all-zero N36 quartet is the native VIRGIN latch (the ~108 deco classes
                    // whose spawn anim bypasses the latch — a documented residual), NOT
                    // "mann slot 0 anim 0": treating it as real dressed meadow deco as idle
                    // gnome models and wiped the meadow.
                    if (IsBoxed(entity))
                    {
                        entity.CurrentAnim = BoxedPresentationAnimation;
                        entity.CurrentAnimMode = (int)DigglesAnimator.AnimMode.Loop;
                    }
                    else if (entity.NativeInitialAnimationWasSeeded &&
                        (entity.NativeInitialAnimationModelDatabaseSlot != 0 ||
                         entity.NativeInitialAnimationState != 0 ||
                         entity.NativeInitialAnimationStartFrame != 0 ||
                         entity.NativeInitialAnimationMode != 0) &&
                        runtimeSpawner != null &&
                        TryResolveArchivedLatchAnim(
                            runtimeSpawner.Library,
                            entity.NativeInitialAnimationModelDatabaseSlot,
                            entity.NativeInitialAnimationState,
                            out string latchAnim))
                    {
                        entity.CurrentAnim = latchAnim;
                        entity.CurrentAnimMode = entity.NativeInitialAnimationMode;
                    }
                    entity.GameObject = context.EntitySpawner.Spawn(entity, candidate.ContentRoot.transform);
                }
            }
            finally
            {
                if (runtimeSpawner != null) runtimeSpawner.SuppressSpawnSettle = false;
            }
            return runtimeSpawner != null;
        }

        private static bool IsBoxed(DigglesTclEntity entity) =>
            entity.VarsScope.Vars.TryGetValue("_boxed", out string value) &&
            (value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));

        // Materialize the loaded save's marker-7 spatial index as dormant scenery-seed deco
        // (B3 §3 — Docs/save-re-reports/B3-support-class-spatial.md). Root cause of "late-game
        // saves load with bare dug caves": every ARCHIVED OBJECT body already spawns (census
        // 1583/1583), but the moss/beams/walls/props (Dummy_*/Urw_caves_*) live in marker 7 —
        // which the port retained as bytes but never materialized ("empty allowed" was the
        // fresh-world law). save_test.sav alone carries 1867 such seeds. Load registers lightweight
        // shells in ctx.MapDecoSeeds; the frame pump materializes only nearby, visible records under
        // a strict budget. They stay OUT of ctx.Entities so the save-capture object/light/particle
        // graph remains byte-identical and the retained marker-7 bytes round-trip verbatim.
        private static int RegisterSaveDecoSeeds(
            RuntimeGameSession candidate,
            OriginalSaveRuntimeSnapshot source)
        {
            OriginalSaveSpatialSeedDocument? seeds = source.SourceDocument.SpatialSeeds;
            if (seeds == null || seeds.Records.Count == 0) return 0;
            DigglesTclContext context = candidate.Engine.Ctx;
            long started = Diggles.Runtime.Diagnostics.HitchFamilyTimingDiag.Start();
            int registered = 0;
            using (Diggles.Runtime.Diagnostics.PerfTrace.Section("seed.register", 20))
            {
                foreach (OriginalSaveSpatialSeed seed in seeds.Records)
                {
                    if (DigglesTclEngine.RegisterDecoSeed(
                            context,
                            seed.ClassName,
                            new Vector3(seed.PositionX, seed.PositionY, seed.PositionZ),
                            new Vector3(seed.RotationX, seed.RotationY, seed.RotationZ),
                            seed) != null)
                    {
                        registered++;
                    }
                }
            }
            Diggles.Runtime.Diagnostics.HitchFamilyTimingDiag.Record(
                "seed.register",
                started,
                registered);
            UnityEngine.Debug.Log(
                $"[OriginalSave] registered {registered}/{seeds.Records.Count} marker-7 deco seeds " +
                "for distance/fog-gated presentation (unregistered = classes absent from the port registry).");
            return registered;
        }

        // The archived N36 latch → "<object>.<clip>" identity, resolved through the same
        // slot→database mapping the animator-continuation restore uses. Ambiguous or
        // out-of-range identities resolve to nothing (the placeholder stays, honestly).
        private static bool TryResolveArchivedLatchAnim(
            DigglesModelLibrary library,
            int modelSlot,
            int animationId,
            out string anim)
        {
            anim = string.Empty;
            if (library == null) return false;
            if ((uint)modelSlot >= (uint)NativeModelDatabaseNames.Length) return false;
            ThreeDbModel? model = library.GetDatabaseModel(NativeModelDatabaseNames[modelSlot]);
            if (model == null || (uint)animationId >= (uint)model.Anims.Count) return false;

            ThreeDbObject? owner = null;
            foreach (ThreeDbObject candidate in model.Objects)
            {
                foreach (uint animationIndex in candidate.Indices)
                {
                    if (animationIndex != (uint)animationId) continue;
                    if (owner != null && !ReferenceEquals(owner, candidate)) return false;
                    owner = candidate;
                    break;
                }
            }

            string clipName = model.Anims[animationId].Name1;
            if (owner == null || string.IsNullOrEmpty(clipName)) return false;
            anim = owner.Name + "." + clipName;
            return true;
        }

        private static void PreflightEntityClasses(
            IReadOnlyList<OriginalSaveRuntimeEntitySnapshot> entities,
            DigglesTclContext context,
            List<string> loadWarnings)
        {
            int previousId = 0;
            foreach (OriginalSaveRuntimeEntitySnapshot entity in entities)
            {
                if (entity.ObjectId <= previousId)
                    throw new InvalidOperationException("Save entity identities are not in strict native ID order.");
                previousId = entity.ObjectId;
                if (!context.Classes.ContainsKey(entity.ClassName))
                {
                    // The unmodified original loads saves referencing classes absent from the
                    // shipped data (typically saves made on a modded install; mod-defined classes
                    // such as 'Holzkiepe_') by materializing a degenerate object shell instead of
                    // rejecting the archive (unknown class name → -1 class handle in the native
                    // loader, B5 §3/§11). Reproduce that tolerance inside the isolated candidate
                    // context: register a scripting-disabled placeholder so the shell participates
                    // in identity, containment and attribute restoration but has no model, no
                    // members and no behavior. Kind stays empty, so kind-filtered queries skip it.
                    context.Classes[entity.ClassName] = new DigglesClass
                    {
                        Name = entity.ClassName,
                        DisableScripting = true,
                    };
                    LogCompatibilityWarning(
                        loadWarnings,
                        $"Original save references class '{entity.ClassName}' (entity " +
                        $"{entity.ObjectId}) that is not present in the current data — likely a " +
                        "mod-defined class. Materializing a degenerate placeholder shell, " +
                        "matching the unmodded original's loader behavior.");
                }
            }
        }

        private static void CollectUnmetActivationConditions(
            OriginalSaveRuntimeSnapshot source,
            DigglesTclContext context,
            IReadOnlyDictionary<int, DigglesTclEntity> entities,
            OriginalSavePreparedActionSet preparedActions,
            bool bodyExtensionRestoresRan,
            bool managerRestoresRan,
            List<string> unmetActivationConditions)
        {
            if (!bodyExtensionRestoresRan ||
                !RequiredObjectBodyStateWasApplied(source, context, entities))
            {
                unmetActivationConditions.Add(
                    "A parsed object-body tail or required type-1/5/6/7/10/11 extension was not applied.");
            }
            if (!InventoryAndEquipmentStateWasApplied(source, context, entities))
            {
                unmetActivationConditions.Add(
                    "Inventory capacity or native weapon/shield restoration did not complete.");
            }
            if (!SchedulerStoryAndStructuralAbsenceAreComplete(source, context))
            {
                unmetActivationConditions.Add(
                    "Scheduler/story restoration or the no-global-Tcl/no-RNG EOF invariant is unmet.");
            }
            if (!managerRestoresRan)
            {
                unmetActivationConditions.Add(
                    "Class-light, animator-continuation or producer-driven presentation restoration could not run.");
            }
            if (!FluidBufferOneWasApplied(source, context))
            {
                unmetActivationConditions.Add(
                    "The authoritative native fluid buffer was not applied to the live fluid state.");
            }
            if (preparedActions.UnmetActivationConditions.Count != 0)
            {
                foreach (string unmetActionCondition in
                         preparedActions.UnmetActivationConditions)
                {
                    unmetActivationConditions.Add(unmetActionCondition);
                }
            }
            else if (!ActionContinuationsWereInstalled(source, preparedActions, entities))
            {
                unmetActivationConditions.Add(
                    "A native action lacks an installed runtime continuation path.");
            }
        }

        private static bool RequiredObjectBodyStateWasApplied(
            OriginalSaveRuntimeSnapshot source,
            DigglesTclContext context,
            IReadOnlyDictionary<int, DigglesTclEntity> entities)
        {
            OriginalSaveObjectCollection? objects = source.SourceDocument.Objects;
            if (objects == null || objects.Count != source.Entities.Count ||
                entities.Count != source.Entities.Count ||
                context.Entities.Count != source.Entities.Count)
            {
                return false;
            }

            foreach (OriginalSaveRuntimeEntitySnapshot saved in source.Entities)
            {
                OriginalSaveObjectDocument? document = objects.FindById(saved.ObjectId);
                if (document == null ||
                    document.TailParseEndOffset != document.Offset + document.Size ||
                    document.ObjectType != saved.ObjectType ||
                    !entities.TryGetValue(saved.ObjectId, out DigglesTclEntity? entity) ||
                    !context.Entities.TryGetValue(saved.ObjectId, out DigglesTclEntity? registered) ||
                    !ReferenceEquals(entity, registered))
                {
                    return false;
                }

                bool requiredExtensionPresent = saved.ObjectType switch
                {
                    1 => saved.Family != null &&
                        saved.TalkIssues != null &&
                        saved.Planner != null,
                    11 => saved.Family != null,
                    5 or 6 or 7 or 10 => saved.Production != null,
                    _ => true,
                };
                if (!requiredExtensionPresent) return false;
            }
            return true;
        }

        private static bool InventoryAndEquipmentStateWasApplied(
            OriginalSaveRuntimeSnapshot source,
            DigglesTclContext context,
            IReadOnlyDictionary<int, DigglesTclEntity> entities)
        {
            foreach (OriginalSaveRuntimeEntitySnapshot saved in source.Entities)
            {
                if (!entities.TryGetValue(saved.ObjectId, out DigglesTclEntity? entity) ||
                    entity.WeaponClassId != saved.WeaponClassId ||
                    entity.ShieldClassId != saved.ShieldClassId ||
                    entity.Inventory.Count != saved.ChildObjectIds.Count ||
                    entity.InventoryCapacity !=
                    DigglesTclEngine.EffectiveInvCapacity(context, entity))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool SchedulerStoryAndStructuralAbsenceAreComplete(
            OriginalSaveRuntimeSnapshot source,
            DigglesTclContext context)
        {
            TimerScheduler.Entry[] scheduler = context.Sim?.Scheduler.CaptureEntries() ??
                Array.Empty<TimerScheduler.Entry>();
            if (scheduler.Length != source.Timers.Records.Count ||
                context.StoryEventOrder.Count != source.Story.Events.Count)
            {
                return false;
            }
            for (int index = 0; index < scheduler.Length; index++)
            {
                if (scheduler[index].Handle != source.Timers.Records[index].OwnerObjectId ||
                    !string.Equals(
                        scheduler[index].Event,
                        source.Timers.Records[index].EventName,
                        StringComparison.Ordinal))
                {
                    return false;
                }
            }
            for (int index = 0; index < context.StoryEventOrder.Count; index++)
            {
                if (!string.Equals(
                        context.StoryEventOrder[index],
                        source.Story.Events[index].Name,
                        StringComparison.Ordinal))
                {
                    return false;
                }
            }

            OriginalSaveLogicMetadata? logic = source.SourceDocument.Metadata.Logic;
            OriginalSaveTrailingPaddingDocument? padding =
                source.SourceDocument.TrailingPadding;
            return logic != null &&
                padding != null &&
                padding.Length == OriginalSaveTrailingPaddingDocument.SerializedLength &&
                padding.IsCanonicalZeroPadding &&
                logic.FinalBlockOffset + padding.Length == source.SourceDocument.PayloadLength &&
                logic.PayloadLength == source.SourceDocument.PayloadLength;
        }

        private static bool FluidBufferOneWasApplied(
            OriginalSaveRuntimeSnapshot source,
            DigglesTclContext context)
        {
            FluidSystem.SaveState restored = context.Fluids.Capture();
            FluidSystem.SaveState expected = source.RuntimeWorld.CreateFluidState();
            if (restored.Cells.Length != expected.Cells.Length) return false;
            for (int index = 0; index < restored.Cells.Length; index++)
            {
                FluidSystem.CellSaveState actualCell = restored.Cells[index];
                FluidSystem.CellSaveState expectedCell = expected.Cells[index];
                if (actualCell.X != expectedCell.X ||
                    actualCell.Y != expectedCell.Y ||
                    actualCell.TypeMask != expectedCell.TypeMask ||
                    BitConverter.SingleToInt32Bits(actualCell.Fill) !=
                    BitConverter.SingleToInt32Bits(expectedCell.Fill))
                {
                    return false;
                }
            }
            // The compatibility sidecar retains the compact archive layout.
            return source.RuntimeWorld.FluidCompatibility.TileCount ==
                source.SourceDocument.World.Terrain.Count;
        }

        private static bool ActionContinuationsWereInstalled(
            OriginalSaveRuntimeSnapshot source,
            OriginalSavePreparedActionSet preparedActions,
            IReadOnlyDictionary<int, DigglesTclEntity> entities)
        {
            if (preparedActions.Actions.Count != source.Actions.Count) return false;
            foreach (OriginalSaveRuntimeActionSnapshot saved in source.Actions)
            {
                if (!preparedActions.ActionsByOwnerId.TryGetValue(
                        saved.OwnerObjectId,
                        out OriginalSavePreparedAction? prepared) ||
                    prepared.SourceType != saved.Type ||
                    !entities.TryGetValue(saved.OwnerObjectId, out DigglesTclEntity? owner) ||
                    !ReferenceEquals(owner.ActiveAction, prepared.Action))
                {
                    return false;
                }
                if (saved.HasActiveAction && prepared.Action == null) return false;
                if (saved.Type == OriginalSaveActionType.Crawl &&
                    prepared.Action is not RestoredNativeCrawlAction)
                {
                    return false;
                }
                if (saved.Type == OriginalSaveActionType.AttackBallistic &&
                    prepared.Action is not RestoredNativeAttackBallisticAction)
                {
                    return false;
                }
                if (saved.Type == OriginalSaveActionType.Lore &&
                    prepared.Action is not RestoredNativeLoreAction)
                {
                    return false;
                }
            }
            return true;
        }

        private static void LogCompatibilityWarning(
            List<string> loadWarnings,
            string message)
        {
            loadWarnings.Add(message);
            Debug.LogWarning(message);
        }

        private static void ValidateWorldBounds(
            OriginalSaveRuntimeWorldSnapshot source,
            BdbPmpWorldBuilder.TclWorldResult result,
            BdbModel bdb)
        {
            if (source.Width <= 0 || source.Height <= 0 ||
                source.MaxXExclusive <= source.MinX || source.MaxYExclusive <= source.MinY)
            {
                throw new InvalidDataException("The native save world bounds are invalid.");
            }
            if (bdb.Header.NBlocks <= 0)
                throw new InvalidDataException("The BDB contains no terrain blocks.");
            for (int index = 0; index < result.BlockIdx.Length; index++)
            {
                if (result.BlockIdx[index] >= bdb.Header.NBlocks)
                {
                    throw new InvalidDataException(
                        $"Saved terrain block {result.BlockIdx[index]} at tile {index} is outside the BDB.");
                }
            }
        }

        private static string ResolveDataPath(string dataRoot, string relativePath)
        {
            if (Path.IsPathRooted(relativePath))
                throw new ArgumentException("The BDB path must be relative to the original data root.", nameof(relativePath));
            string root = Path.GetFullPath(dataRoot ?? throw new ArgumentNullException(nameof(dataRoot)));
            string path = Path.GetFullPath(Path.Combine(root, relativePath));
            string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The BDB path escapes the original data root.", nameof(relativePath));
            if (!File.Exists(path))
                throw new FileNotFoundException("The candidate BDB was not found.", path);
            return path;
        }

        private static Vector3 Vector(OriginalSaveVector3 source) =>
            new(source.X, source.Y, source.Z);
    }

    public sealed class OriginalSaveRuntimeSessionState
    {
        private readonly ReadOnlyDictionary<int, DigglesTclEntity> _entities;
        private readonly ReadOnlyDictionary<
            int,
            IReadOnlyList<OriginalSaveRuntimeEntityEventSnapshot>> _pendingEntityEvents;
        private readonly IOriginalSaveActionInstaller _actionInstaller;
        private readonly ReadOnlyCollection<string> _loadWarnings;
        private readonly ReadOnlyCollection<string> _unmetActivationConditions;
        private readonly Dictionary<string, int> _publishedSystemEventRecipients =
            new(StringComparer.Ordinal);
        private readonly ReadOnlyDictionary<string, int> _readOnlyPublishedSystemEventRecipients;
        private bool _presentationEventsWired;
        private bool _pendingEntityEventsInstalled;

        internal OriginalSaveRuntimeSessionState(
            OriginalSaveRuntimeSnapshot source,
            BdbPmpWorldBuilder.TclWorldResult worldResult,
            BdbModel bdb,
            DiggableTerrain terrain,
            NavWorldService navigation,
            FogOfWarState fog,
            OriginalSaveRuntimeLightingState lighting,
            Dictionary<int, DigglesTclEntity> entities,
            OriginalSavePreparedActionSet preparedActions,
            IOriginalSaveActionInstaller actionInstaller,
            List<string> loadWarnings,
            List<string> unmetActivationConditions)
        {
            Source = source ?? throw new ArgumentNullException(nameof(source));
            WorldResult = worldResult ?? throw new ArgumentNullException(nameof(worldResult));
            Bdb = bdb ?? throw new ArgumentNullException(nameof(bdb));
            Terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));
            Navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
            Fog = fog ?? throw new ArgumentNullException(nameof(fog));
            Lighting = lighting ?? throw new ArgumentNullException(nameof(lighting));
            _entities = new ReadOnlyDictionary<int, DigglesTclEntity>(
                entities ?? throw new ArgumentNullException(nameof(entities)));
            var pendingEntityEvents = new Dictionary<
                int,
                IReadOnlyList<OriginalSaveRuntimeEntityEventSnapshot>>(Source.Entities.Count);
            foreach (OriginalSaveRuntimeEntitySnapshot entity in Source.Entities)
                pendingEntityEvents.Add(entity.ObjectId, entity.PendingEvents);
            _pendingEntityEvents = new ReadOnlyDictionary<
                int,
                IReadOnlyList<OriginalSaveRuntimeEntityEventSnapshot>>(pendingEntityEvents);
            PreparedActions = preparedActions ?? throw new ArgumentNullException(nameof(preparedActions));
            _actionInstaller = actionInstaller ??
                throw new ArgumentNullException(nameof(actionInstaller));
            _loadWarnings = Array.AsReadOnly(
                (loadWarnings ?? throw new ArgumentNullException(nameof(loadWarnings))).ToArray());
            _unmetActivationConditions = Array.AsReadOnly(
                (unmetActivationConditions ??
                    throw new ArgumentNullException(nameof(unmetActivationConditions))).ToArray());
            _readOnlyPublishedSystemEventRecipients =
                new ReadOnlyDictionary<string, int>(_publishedSystemEventRecipients);
        }

        public OriginalSaveRuntimeSnapshot Source { get; }
        public BdbPmpWorldBuilder.TclWorldResult WorldResult { get; }
        public BdbModel Bdb { get; }
        public DiggableTerrain Terrain { get; }
        public NavWorldService Navigation { get; }
        public FogOfWarState Fog { get; }
        public OriginalSaveRuntimeLightingState Lighting { get; }
        public IReadOnlyDictionary<int, DigglesTclEntity> Entities => _entities;
        public IReadOnlyDictionary<
            int,
            IReadOnlyList<OriginalSaveRuntimeEntityEventSnapshot>> PendingEntityEvents =>
            _pendingEntityEvents;
        public OriginalSavePreparedActionSet PreparedActions { get; }
        public OriginalFluidCompatibilityState FluidCompatibility =>
            Source.RuntimeWorld.FluidCompatibility;
        public IReadOnlyList<string> Warnings => _loadWarnings;
        public IReadOnlyDictionary<string, int> PublishedSystemEventRecipients =>
            _readOnlyPublishedSystemEventRecipients;
        public bool PendingEntityEventsInstalled => _pendingEntityEventsInstalled;
        public IReadOnlyList<string> ActivationBlockers => ComputeActivationBlockers();
        public bool IsActivationReady => ComputeActivationBlockers().Count == 0;

        public IReadOnlyList<string> ComputeActivationBlockers() =>
            _unmetActivationConditions;

        internal void InstallPendingEntityEvents(DigglesTclContext context)
        {
            if (_pendingEntityEventsInstalled)
                throw new InvalidOperationException("Pending original-save events are already installed.");
            _actionInstaller.InstallPendingEntityEvents(
                _pendingEntityEvents,
                _entities,
                context,
                Source.GameTime);
            _pendingEntityEventsInstalled = true;
        }

        internal int PublishSystemEvent(string eventName, DigglesTclContext context)
        {
            if (_publishedSystemEventRecipients.ContainsKey(eventName))
            {
                throw new InvalidOperationException(
                    $"System event '{eventName}' was already published for this loaded session.");
            }
            int recipients = _actionInstaller.PublishSystemEvent(eventName, context, _entities);
            _publishedSystemEventRecipients.Add(eventName, recipients);
            return recipients;
        }

        internal bool TryMarkPresentationEventsWired()
        {
            if (_presentationEventsWired) return false;
            _presentationEventsWired = true;
            return true;
        }
    }
}
