#nullable enable

namespace Diggles.Editor.Tcl
{
    using Diggles.Runtime.World;
    using System.Text;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using UnityEditor;
    using UnityEngine;

    // Visual smoke test for the Tcl → Unity scene bridge. Sets up an empty scene
    // with a properly-rotated root and an EditorEntitySpawner wired into a fresh
    // Tcl interpreter; "Spawn one of each" runs a small Tcl script that calls `new`
    // for common character classes, and each `new` materializes as a GameObject
    // under the root with the resolved.3db mesh and anim. Companion to
    // TclTestWindow, which is only an interpreter playground.
    public sealed class TclSpawnTestWindow : EditorWindow
    {
        const string DefaultClassesScript = @"# Declare a handful of common content classes by hand. In Phase 5 these
# will be loaded from original/data/Scripts/classes/**/*.tcl; for now we
# stub the smallest viable shape so the runtime loader (DigglesModelLibrary)
# can resolve class→object via the class→model TSV and build the clips.
#
# Two classes (Zwerg, Hamster) also declare a `state` body and an event
# handler — wired up by the default spawn script below so clicking
# Run sim immediately demonstrates Phase 7 (event loop + state ticks).
def_class Zwerg none gnome 0 {} {
    class_defaultanim mann.standard
    member step
    obj_init { set step 0 }
    # State body — runs every sim step (40 Hz). Drifts the dwarf in +X
    # so the effect is visible: after ~5 sim-seconds it's slid by ~4 units.
    state walking {
        set step [expr {$step + 1}]
        set_pos this [expr {$step * 0.02}] 0 12
    }
    # Timer event handler — fires on schedule via timer_event below.
    handle_event hello { log ""Zwerg hello @ gametime=[gametime]"" }
}
def_class Hamster none animal 0 {} {
    class_defaultanim hamster.standard
    member ticks
    obj_init { set ticks 0 }
    # Silent counter — verifies the state body fires without console spam.
    # Inspect the entity's `ticks` member through the Tcl context after
    # the sim has run for a bit.
    state idle { set ticks [expr {$ticks + 1}] }
}
def_class Troll none monster 0 {} { class_defaultanim troll.standard }
def_class Drache none monster 0 {} { class_defaultanim drache01.standard }
def_class Raupe none animal 0 {} { class_defaultanim raupe01.standard }
def_class Baby none gnome 0 {} { class_defaultanim baby.standard }
def_class Spinne none monster 0 {} { class_defaultanim spinne.haengen_warten }
def_class Pilz food material 0 {} { class_defaultanim pilz.standard }
log ""classes declared""
";

        const string DefaultSpawnScript = @"# Lay out one of each class along +X so we can see them side by side.
# Native frame: X horiz, Y depth, Z elevation — Z=12 lands well above
# the marker Y-offset (engine constants: scale 0.5, base 7.0).
set h1 [new Zwerg ""z1"" ""0 0 12"" ""0 4.71 0""]
set h2 [new Hamster ""h1"" ""4 0 12"" ""0 4.71 0""]
set h3 [new Troll ""t1"" ""8 0 12"" ""0 4.71 0""]
set h4 [new Drache ""d1"" ""12 0 12"" ""0 4.71 0""]
set h5 [new Raupe ""r1"" ""16 0 12"" ""0 4.71 0""]
set h6 [new Baby ""b1"" ""20 0 12"" ""0 4.71 0""]
set h7 [new Spinne ""s1"" ""24 0 12"" ""0 4.71 0""]
set h8 [new Pilz ""p1"" ""28 0 12"" ""0 0 0""]
log ""spawned: $h1 $h2 $h3 $h4 $h5 $h6 $h7 $h8""

# Phase 7 demo — wire state body + timer event so Run sim has something
# to drive. The dwarf will drift in +X; the hamster's tick counter will
# advance silently; the dwarf will get 3 `hello` events at gametime 1/2/3.
state_trigger $h1 walking
state_trigger $h2 idle
timer_event $h1 hello -interval 1.0 -repeat 3 -attime 1.0
log ""Phase 7 wired: Zwerg state=walking, Hamster state=idle, Zwerg hello x3 at t=1,2,3""
";

        string dataRoot = "";
        string classesScript = DefaultClassesScript;
        string spawnScript = DefaultSpawnScript;
        string campaignTclRel = "Scripts/gameplay/Campaign.tcl";
        // BlockDB2.BDB matches the urw_unq_start campaign per
        // RENDERING_PIPELINE_COMPLETE.md §2.1 (runtime dump verified).
        // Other campaigns may need BlockDB0/1.BDB.
        string bdbRel = "BlockDB2.BDB";
        string output = "";
        Vector2 scrollOut;

        TclInterpreter? interp;
        DigglesTclContext? ctx;
        DigglesGameSim? sim;
        GameSimDriver? simDriver;
        RuntimeMapBuilder? mapBuilder;
        GameObject? sceneRoot;

        [MenuItem("Diggles/Tcl Spawn Test")]
        public static void Open()
        {
            var w = GetWindow<TclSpawnTestWindow>("Tcl Spawn Test");
            w.minSize = new Vector2(560, 600);
        }

        void OnEnable()
        {
            if (string.IsNullOrEmpty(dataRoot)) dataRoot = Diggles.Runtime.DigglesConfig.ConfiguredDataRoot;
        }

        void OnGUI()
        {
            EditorGUILayout.LabelField("Data root", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            dataRoot = EditorGUILayout.TextField(dataRoot);
            if (GUILayout.Button("…", GUILayout.MaxWidth(30)))
            {
                var picked = EditorUtility.OpenFolderPanel("original/data", dataRoot, "data");
                if (!string.IsNullOrEmpty(picked)) dataRoot = picked;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Scene + interp setup", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Setup Scene", GUILayout.MaxWidth(120))) SetupScene();
            if (GUILayout.Button("Clear Scene", GUILayout.MaxWidth(100))) ClearScene();
            GUI.enabled = ctx != null;
            if (GUILayout.Button("Frame Root", GUILayout.MaxWidth(100))) FrameRoot();
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField(
                interp == null
                    ? "Status: no interpreter — click Setup Scene"
                    : $"Status: ready (interp + spawner + scene root '{sceneRoot?.name}')",
                EditorStyles.miniLabel);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("1. Declare classes", EditorStyles.boldLabel);
            classesScript = EditorGUILayout.TextArea(classesScript, GUILayout.MinHeight(80));
            GUI.enabled = interp != null;
            if (GUILayout.Button("Run classes script", GUILayout.MaxWidth(160)))
                RunScript(classesScript);
            GUI.enabled = true;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("2. Spawn entities", EditorStyles.boldLabel);
            spawnScript = EditorGUILayout.TextArea(spawnScript, GUILayout.MinHeight(80));
            EditorGUILayout.BeginHorizontal();
            GUI.enabled = interp != null;
            if (GUILayout.Button("Spawn one of each", GUILayout.MaxWidth(160)))
                RunScript(spawnScript);
            if (GUILayout.Button("Reset to defaults", GUILayout.MaxWidth(140)))
            {
                classesScript = DefaultClassesScript;
                spawnScript = DefaultSpawnScript;
            }
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("3. Load campaign (Phase 5 — Tcl-driven map)", EditorStyles.boldLabel);
            campaignTclRel = EditorGUILayout.TextField("Campaign TCL", campaignTclRel);
            bdbRel = EditorGUILayout.TextField("BDB file", bdbRel);
            GUI.enabled = interp != null;
            if (GUILayout.Button("Load Campaign via Tcl", GUILayout.MaxWidth(200)))
                LoadCampaign();
            GUI.enabled = true;
            EditorGUILayout.LabelField(
                mapBuilder == null
                    ? "Map builder: not wired (Setup Scene)"
                    : $"Map builder: {mapBuilder.Width}x{mapBuilder.Height} world, {mapBuilder.PmpsLoaded} PMPs, {mapBuilder.L2msLoaded} L2Ms",
                EditorStyles.miniLabel);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Navigation (mesh-sampled NavGraph)", EditorStyles.boldLabel);
            GUI.enabled = sceneRoot != null;
            if (GUILayout.Button("Regenerate Navigation", GUILayout.MaxWidth(200)))
                RegenerateNavigation();
            GUI.enabled = true;
            EditorGUILayout.LabelField("Rebuilds the nav graph from the current terrain (no campaign reload).",
                EditorStyles.miniLabel);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Sim (Phase 7 — event loop + state ticks)", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            GUI.enabled = simDriver != null;
            string lbl = simDriver != null && simDriver.Running ? "Pause sim" : "Run sim";
            if (GUILayout.Button(lbl, GUILayout.MaxWidth(110)))
                simDriver!.Running = !simDriver.Running;
            if (GUILayout.Button("Step once", GUILayout.MaxWidth(100)))
                sim!.Step(simDriver!.FixedStep);
            GUI.enabled = true;
            EditorGUILayout.LabelField(
                sim != null ? $"GameTime: {sim.GameTime:F3} s   |   Scheduled: {sim.Scheduler.Count}"
                            : "GameTime: —", EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
            // Force repaint while sim running so GameTime label updates live.
            if (simDriver != null && simDriver.Running) Repaint();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
            scrollOut = EditorGUILayout.BeginScrollView(scrollOut, GUILayout.MinHeight(120));
            EditorGUILayout.TextArea(output, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
        }

        void SetupScene()
        {
            ClearScene();

            sceneRoot = new GameObject("TclSpawnTest");
            // Same root rotation as DigglesCampaignBrowser uses — the
            // OLD-style world hack (root Q_x(-90)) that puts terrain on
            // the visual XY plane and lets the -90X model-pitch chain
            // resolve to upright. See Docs/COORDINATE_SYSTEM.md §3.
            sceneRoot.transform.rotation = Quaternion.Euler(270f, 0f, 0f);

            interp = new TclInterpreter();
            ctx = DigglesTclEngine.Register(interp);
            ctx.DataRoot = dataRoot;
            ctx.SpawnParent = sceneRoot.transform;
            // Same runtime spawner the standalone build uses (RuntimeWorldSpawner)
            // — DigglesModelLibrary decode + a DigglesAnimController per entity, so
            // Edit-mode and Play-mode spawning are identical.
            ctx.EntitySpawner = new Diggles.Runtime.RuntimeWorldSpawner(dataRoot);
            ctx.TolerateMissingEntities = true;
            ctx.TolerateMissingClass = true;

            // Sim driver — paused by default; user clicks "Run sim" when ready.
            sim = new DigglesGameSim(ctx);
            simDriver = sceneRoot.AddComponent<GameSimDriver>();
            simDriver.Sim = sim;
            simDriver.Running = false;

            // Map builder — receives map_template / map_setlayer2 calls.
            string bdbAbs = System.IO.Path.Combine(dataRoot, bdbRel);
            mapBuilder = new RuntimeMapBuilder(dataRoot, bdbAbs);
            ctx.MapBuilder = mapBuilder;

            // Capture puts / log into our output panel.
            var sb = new StringBuilder();
            interp.RegisterCommand("puts", (i, args) =>
            {
                int k = 1;
                if (k < args.Length && args[k] == "-nonewline") k++;
                if (k + 1 < args.Length && (args[k] == "stdout" || args[k] == "stderr")) k++;
                if (k < args.Length) sb.AppendLine(args[k]);
                return "";
            });
            interp.RegisterCommand("log", (i, args) =>
            {
                var msg = args.Length > 1 ? string.Join(" ", args, 1, args.Length - 1) : "";
                sb.AppendLine($"[log] {msg}");
                Debug.Log($"[TclSpawn] {msg}");
                return "";
            });
            _capture = sb;
            output = "";
            Selection.activeGameObject = sceneRoot;
        }

        StringBuilder? _capture;

        void RunScript(string script)
        {
            if (interp == null) { output += "[error] no interp — click Setup Scene\n"; return; }
            try
            {
                interp.Eval(script);
                if (_capture != null)
                {
                    output += _capture.ToString();
                    _capture.Clear();
                }
            }
            catch (System.Exception e)
            {
                output += $"[ERROR] {e.Message}\n";
                Debug.LogException(e);
            }
        }

        void ClearScene()
        {
            if (sceneRoot != null)
            {
                Object.DestroyImmediate(sceneRoot);
                sceneRoot = null;
            }
            interp = null;
            ctx = null;
            sim = null;
            simDriver = null;
            mapBuilder = null;
            _capture = null;
        }

        // Re-derive only the navigation data from the terrain already in the scene — no campaign
        // reload. The faithful NavWorld lives in DiggableTerrain.Nav (+0x3c sub-grid + FUN_005a6350
        // tile graph); this re-runs the derivation + rebuilds the gizmo's floor strips.
        void RegenerateNavigation()
        {
            // Recover the root after a domain reload (which nulls this field
            // but leaves the scene object intact) so the button still works.
            if (sceneRoot == null) sceneRoot = GameObject.Find("TclSpawnTest");
            if (sceneRoot == null) { output += "[nav] no scene root — Setup Scene + Load Campaign first\n"; return; }
            var terr = sceneRoot.GetComponent<Diggles.Runtime.World.DiggableTerrain>();
            if (terr == null || terr.Nav == null)
            {
                output += "[nav] no DiggableTerrain/NavWorld — load a campaign first\n";
                return;
            }
            terr.Nav.DeriveAll();
            if (sceneRoot.GetComponent<Diggles.Runtime.Navigation.NavWorldService>() == null)
                sceneRoot.AddComponent<Diggles.Runtime.Navigation.NavWorldService>();
            var giz = sceneRoot.GetComponent<Diggles.Runtime.Navigation.NavWorldGizmo>();
            if (giz == null) giz = sceneRoot.AddComponent<Diggles.Runtime.Navigation.NavWorldGizmo>();
            giz.Rebuild();
            EditorUtility.SetDirty(sceneRoot);
            SceneView.RepaintAll();
            output += $"[nav] NavWorld re-derived: walkable +0x3c sub-cells={terr.Nav.WalkableSubCellCount()}\n";
        }

        void LoadCampaign()
        {
            if (interp == null || ctx == null || mapBuilder == null || sceneRoot == null)
            {
                output += "[error] no host — click Setup Scene first\n";
                return;
            }
            // Resolve campaign path against data root (campaignTclRel is
            // expected to be `data`-relative, e.g. Scripts/gameplay/Campaign.tcl).
            string absCampaign = System.IO.Path.Combine(dataRoot, campaignTclRel);
            if (!System.IO.File.Exists(absCampaign))
            {
                output += $"[error] campaign tcl not found: {absCampaign}\n";
                return;
            }
            // Tolerance flags so 195 stub'd commands inside campaign don't abort.
            ctx.TolerateMissingFiles = true;
            ctx.TolerateMissingClass = true;
            ctx.TolerateMissingEntities = true;
            ctx.Interp.TolerateExprErrors = true;
            ctx.Interp.TolerateMissingVars = true;
            ctx.Interp.UnknownCommandHandler ??= (i, a) => "0";

            try
            {
                string content = System.IO.File.ReadAllText(absCampaign,
                    System.Text.Encoding.GetEncoding("ISO-8859-1"));
                interp.Eval(content);
                if (_capture != null) { output += _capture.ToString(); _capture.Clear(); }
                output += $"[ok] campaign ran — map builder accumulated " +
                          $"{mapBuilder.PmpsLoaded} PMPs, {mapBuilder.L2msLoaded} L2Ms\n";
                // Emit terrain mesh via BDB block-based geometry.
                mapBuilder.EmitTerrain(sceneRoot);
                output += $"[ok] terrain emitted under {sceneRoot.name}\n";
                foreach (var d in mapBuilder.Diagnostics) output += d + "\n";
                Selection.activeGameObject = sceneRoot;
                SceneView.RepaintAll();
            }
            catch (System.Exception e)
            {
                output += $"[ERROR] {e.Message}\n";
                if (_capture != null) { output += _capture.ToString(); _capture.Clear(); }
                Debug.LogException(e);
            }
        }

        void FrameRoot()
        {
            if (sceneRoot == null) return;
            Selection.activeGameObject = sceneRoot;
            SceneView.FrameLastActiveSceneView();
        }

    }
}
