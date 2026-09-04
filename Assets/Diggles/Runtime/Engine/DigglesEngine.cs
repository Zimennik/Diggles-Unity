#nullable enable

namespace Diggles.Runtime
{
    using System;
    using System.IO;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using UnityEngine;

    // The engine core as a single plain-C# graph: the Tcl interpreter + engine bridge
    // (DigglesTclContext and its headless state objects), the runtime model library,
    // the world-coordinate entity spawner, the map/terrain builder, and the fixed-step
    // simulator.
    //
    // This was previously assembled inline in DigglesWorldRuntime.Start. Extracting it
    // gives one place that owns the build order (which is load-bearing — see Build) and
    // lets the Play composition root and the VContainer scope share the exact
    // same wiring. It deliberately touches no Unity scene concerns beyond the spawn-parent
    // Transform it is handed: no Camera, no presenter, no GameObject creation, no statics.
    //
    // The headless EditMode tests do not use this type — they build their own leaner host
    // (interp → DigglesTclEngine.Register → DigglesGameSim) directly, and must keep doing so.
    public sealed class DigglesEngine
    {
        // Valid only after Build. Mirror the fields DigglesWorldRuntime held before.
        public TclInterpreter Interp { get; private set; } = null!;
        public DigglesTclContext Ctx { get; private set; } = null!;
        public DigglesModelLibrary Lib { get; private set; } = null!;
        public RuntimeMapBuilder Map { get; private set; } = null!;
        public DigglesGameSim Sim { get; private set; } = null!;

        public bool IsBuilt { get; private set; }
        public bool RuntimeDefinitionsLoaded { get; private set; }

        // Build the engine-core graph. The order is faithful to the original Start:
        // 1. interpreter
        // 2. DigglesTclEngine.Register → ctx + its 6 state objects (Camera/Dialogue/Audio/
        // Particles/ScreenFx/Elf), bound to this ctx by the native-command closures.
        // 3. tolerance flags (so partial game-data setups don't throw).
        // 4. model library → ctx.SpawnParent / ctx.ModelLibrary / ctx.EntitySpawner.
        // 5. map builder → ctx.MapBuilder.
        // 6. DigglesGameSim (its ctor sets ctx.Sim = this, so it must come after ctx).
        // Nothing may read ctx.Sim before step 6.
        //
        // spawnParent = the WorldRoot transform (carries the Q_x(-90) hack); entities spawn
        // under it. dataRoot = the original game data/ directory. bdbRel = the BDB block
        // archive, data-relative (terrain geometry source).
        public void Build(string dataRoot, Transform spawnParent, string bdbRel)
        {
            Interp = new TclInterpreter();
            Ctx = DigglesTclEngine.Register(Interp);
            Ctx.DataRoot = dataRoot;
            // Shared game-text table (data/messages.txt) + locale-aware art for authored UI
            // and the lmsg command. Rebind only when the data root actually changes — menu/session
            // rebuilds reuse the parsed table (and Changed subscribers stay quiet).
            if (Localization.GameLocalization.Current?.DataRoot != dataRoot)
                Localization.GameLocalization.Bind(new Localization.GameLocalization(dataRoot));
            Ctx.TolerateMissingFiles = true;
            Ctx.TolerateMissingClass = true;
            Ctx.TolerateMissingEntities = true;
            Interp.TolerateExprErrors = true;
            Interp.TolerateMissingVars = true;
            Interp.UnknownCommandHandler ??= (i, a) => "0";

            Lib = new DigglesModelLibrary(dataRoot);
            Ctx.SpawnParent = spawnParent;
            Ctx.ModelLibrary = Lib;   // lets def_texture / set_textureanimation resolve variants
            Ctx.EntitySpawner = new RuntimeWorldSpawner(Lib, dataRoot);

            // Map builder receives map_template/map_setlayer2 from the campaign tcl; the host
            // calls Map.EmitTerrain(...) after evaluating the campaign to build the BDB mesh + nav.
            Map = new RuntimeMapBuilder(dataRoot, Path.Combine(dataRoot, bdbRel));
            Ctx.MapBuilder = Map;
            Sim = new DigglesGameSim(Ctx);

            // Global Tcl file-channel library: the shipped `open/gets/eof/close/read` are
            // procs from misc/file_wrapper.tcl (over the native load_file), which the original
            // loads into each reader object's interp via sequencer/Lorenbahn inits. The port's
            // shared interpreter loads them once globally so every class's text readers
            // (Trigger_Tutorial get_text, story/tournament parsers) resolve them — an unknown
            // `eof` stubbed to "0" would otherwise wedge every `while {![eof $f]}` loop forever.
            // in_class_def is false here, so the wrapper's proc branch is the one that runs.
            RunDefinitionScript("scripts/misc/file_wrapper.tcl");

            IsBuilt = true;
        }

        // Loads class, animation, sound, music and level-generation definitions into this isolated
        // engine graph. These scripts declare runtime metadata only; loading a save must still create
        // entity shells directly by native handle and must never invoke `new`, obj_init or a campaign.
        public void LoadRuntimeDefinitions()
        {
            if (!IsBuilt)
                throw new InvalidOperationException("The engine must be built before definitions are loaded.");
            if (RuntimeDefinitionsLoaded) return;

            int entityCount = Ctx.Entities.Count;
            int classCount = Ctx.Classes.Count;
            RunDefinitionScript("scripts/init/txtinit.tcl");
            RunDefinitionScript("scripts/init/animinit.tcl");
            RunDefinitionScript("scripts/init/classinit.tcl");
            RunDefinitionScript("scripts/init/soundinit.tcl");
            RunDefinitionScript("scripts/init/adaptiveinit.tcl");
            RunDefinitionScript("scripts/init/lginit.tcl");
            Interp.Eval($"ai init 0 {AiSystem.NativeStandardScriptPath}");

            if (Ctx.Entities.Count != entityCount)
            {
                throw new InvalidOperationException(
                    "Runtime definition loading created entities; save candidates require a declaration-only boot.");
            }

            RuntimeDefinitionsLoaded = true;
            Debug.Log($"[DigglesEngine] runtime definitions: +{Ctx.Classes.Count - classCount} " +
                      $"classes ({Ctx.Classes.Count} total); material variants = " +
                      $"{Lib.VariantRegistryCount}");
        }

        private void RunDefinitionScript(string relativePath)
        {
            try
            {
                Interp.Eval("call " + relativePath);
            }
            catch (Exception exception)
            {
                // Keep the established tolerant boot contract while interpreter coverage is still
                // incomplete. Candidate preflight separately requires every referenced save class.
                Debug.LogWarning(
                    $"[DigglesEngine] definition script '{relativePath}' aborted: {exception.Message}");
            }
        }
    }
}
