#nullable enable

namespace Diggles.Runtime
{
    using System.Collections.Generic;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using UnityEngine;

    // Proof that ANIMATION IS DRIVEN BY TCL on the new library path.
    //
    // Sets up an embedded Tcl interpreter + the engine bridge + a runtime
    // library-backed spawner, spawns one character via `new`, and runs the
    // original idle pattern — a self-chaining `action this anim X {demo_idle}`
    // loop that picks a random idle variant each time, exactly like the engine's
    // seq_idle. The DigglesGameSim drives the action layer: when each ONCE clip
    // finishes, its Tcl finishcode fires and picks the next variant. No C# tells
    // it which idle to play — the Tcl does.
    //
    // Drop on an empty GameObject in a scene with a holder + a camera; press Play.
    [DisallowMultipleComponent]
    public sealed class DigglesTclAnimDemo : MonoBehaviour
    {
        [Header("Data")]
        public string dataRoot = "";
        [Tooltip("Class to spawn (e.g. Zwerg/Troll/Wuker/Drachenbaby — the classes " +
                 "with a real seq_idle table). Falls back to a plain obj name.")]
        public string className = "Zwerg";
        [Tooltip("On: cycle a variety of actions (idles, walk, run, dig, work, " +
                 "climb, greet, die …) driven by Tcl. Overrides Walk Demo / idle.")]
        public bool showcase = true;
        [Tooltip("On (and Showcase off): walk forward / turn 180 / walk back loop. " +
                 "Both off: the faithful seq_idle idle picker.")]
        public bool walkDemo;
        [Tooltip("Walk distance per leg = model height × this (auto-scales with modelScale).")]
        public float walkLegFactor = 0.7f;
        [Tooltip("Body walk speed as a fraction of model height per second. Tune " +
                 "with Leg Cadence until the feet look planted (no skating).")]
        public float walkBodyFactor = 0.5f;
        [Tooltip("Leg-cycle rate (1 = native 10 FPS). Raise a bit if feet drag, " +
                 "lower if feet slip forward.")]
        public float legCadence = 1f;

        [Header("Scene (auto-found by name if empty)")]
        public Transform? holder;
        public Camera? previewCamera;
        public string previewLayerName = "AnimPreview";
        public bool setPreviewCulling = true;
        public Vector3 modelEuler = new Vector3(180f, 90f, 0f);  // upright + 90° Y yaw
        [Tooltip("Model→world scale. Default = DigglesModelLibrary.ModelToWorldScale " +
                 "(0.005, the engine-faithful 1/200) so this matches the world spawn " +
                 "size. MotionOffset is scaled by this same factor.")]
        public float modelScale = DigglesModelLibrary.ModelToWorldScale;
        [Tooltip("GPU vertex-morph interpolation: smooth 10 FPS → fluid. Off = hard frames.")]
        public bool interpolate = true;
        [Tooltip("Preview camera follows the character as it moves.")]
        public bool followCamera = true;
        public Vector3 cameraViewDir = new Vector3(0f, 0.25f, -1f);
        public float cameraFillFactor = 2.2f;  // extra margin so the walk stays in view

        TclInterpreter? _interp;
        DigglesTclContext? _ctx;
        DigglesGameSim? _sim;
        DigglesModelLibrary? _lib;
        DigglesTclEntity? _ent;
        float _camDist;

        void Start()
        {
            if (holder == null)
            {
                var go = GameObject.Find("ObjectHolder");
                holder = go != null ? go.transform : transform;
            }
            _interp = new TclInterpreter();
            _ctx = DigglesTclEngine.Register(_interp);
            if (string.IsNullOrEmpty(dataRoot)) dataRoot = DigglesConfig.ResolveDataRoot("");
            _ctx.DataRoot = dataRoot;
            _ctx.TolerateMissingFiles = true;
            _lib = new DigglesModelLibrary(dataRoot);
            WalkAction.BodySpeedFactor = walkBodyFactor;
            WalkAction.LegCadence = legCadence;
            _ctx.SpawnParent = holder;
            _ctx.EntitySpawner = new RuntimeLibrarySpawner(_lib, modelEuler, modelScale:modelScale);
            _sim = new DigglesGameSim(_ctx);

            // Minimal class shell — the FAITHFUL idle below plays literal
            // `<obj>.<anim>` clips (class2DB), so no set_class_anim map is needed.
            string obj = _lib.ResolveClass(className)?.Obj ?? className;
            var cls = new DigglesClass { Name = className, DefaultAnim = obj + ".standard", DefaultAnimMode = 2 };
            _ctx.Classes[className] = cls;

            // Load the REAL anim tables (AnimMap + animsets) from z_anims.tcl into
            // the class, so walk/turn states resolve to the actual clips
            // (walk_start→gehen_start, turn_left_90→drehen_links, …) — not hardcoded.
            LoadClassAnims(cls);

            // Load the REAL idle behaviour: the actual weighted seq_idle_anims
            // table (from zwerg.tcl) + the real seq_idle.tcl proc (weighted random,
            // no immediate repeat, floor/wall filtered). seq_idle plays
            // action this anim [class2DB <class>].<variant> {seq_idle}
            // chaining itself forever — exactly the engine's idle picker.
            LoadRealIdle();

            string handle;
            try { handle = _interp.Eval($"new {className} \"\" \"0 0 0\" \"0 0 0\""); }
            catch (System.Exception e) { Debug.LogError($"[TclAnimDemo] spawn failed: {e.Message}"); return; }

            _ent = _ctx.GetEntity(handle.Trim());
            if (_ent == null) { Debug.LogError($"[TclAnimDemo] no entity for handle '{handle}'"); return; }
            if (_ent.GameObject != null)
            {
                var anim = _ent.GameObject.GetComponent<DigglesAnimator>();
                if (anim != null) anim.Interpolate = interpolate;
                FrameCamera(_ent.GameObject);
            }

            // Leg distance for locomotion, scaled to model height.
            float h = 0f;
            var mrr = _ent.GameObject != null ? _ent.GameObject.GetComponentInChildren<MeshRenderer>() : null;
            if (mrr != null) h = mrr.bounds.size.y;
            float leg = (h > 0.001f ? h : 2f) * walkLegFactor;
            var legStr = leg.ToString(System.Globalization.CultureInfo.InvariantCulture);

            // Kick off the Tcl-driven behaviour on this entity.
            var prev = _ctx.This;
            _ctx.This = _ent;
            try
            {
                if (showcase)
                {
                    BuildShowcase(obj, legStr);
                    _interp!.Eval("sc0");
                }
                else if (walkDemo)
                {
                    _interp!.Eval(
                        $"proc walk_demo {{}} {{ action this walk {legStr} 0 {{turn_demo}} }}\n" +
                        "proc turn_demo {} { action this rotate 180 {walk_demo} }");
                    _interp.Eval("walk_demo");
                }
                else
                {
                    _interp!.Eval("seq_idle");
                }
            }
            catch (System.Exception e) { Debug.LogWarning($"[TclAnimDemo] kick-off: {e.Message}"); }
            finally { _ctx.This = prev; }
        }

        // Run animclassinit.tcl + z_anims.tcl inside a class-def context so the
        // real set_class_anim / set_class_animset calls populate the class tables.
        void LoadClassAnims(DigglesClass cls)
        {
            var prev = _ctx!.CurrentClass;
            _ctx.CurrentClass = cls;
            try
            {
                EvalFileInClass("Scripts/misc/animclassinit.tcl");
                EvalFileInClass("Scripts/classes/zwerg/z_anims.tcl");
            }
            catch (System.Exception e) { Debug.LogWarning($"[TclAnimDemo] LoadClassAnims: {e.Message}"); }
            finally { _ctx.CurrentClass = prev; }
        }

        void EvalFileInClass(string relPath)
        {
            var path = System.IO.Path.Combine(dataRoot, relPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (System.IO.File.Exists(path)) _interp!.Eval(System.IO.File.ReadAllText(path));
            else Debug.LogWarning($"[TclAnimDemo] missing {path}");
        }

        // Sets the real Zwerg seq_idle_anims weighted table (zwerg.tcl:119-137)
        // then evaluates the real Scripts/misc/seq_idle.tcl from disk.
        void LoadRealIdle()
        {
            const string table =
                "set seq_idle_anims [list]\n" +
                "lappend seq_idle_anims {10 {stand_anim_a}}\n" +
                "lappend seq_idle_anims {10 {stand_anim_b}}\n" +
                "lappend seq_idle_anims {10 {stand_anim_c}}\n" +
                "lappend seq_idle_anims {1 {spaehen}}\n" +
                "lappend seq_idle_anims {1 {zeigen_rechts}}\n" +
                "lappend seq_idle_anims {1 {strippe_ziehen}}\n" +
                "lappend seq_idle_anims {1 {raeuspern}}\n" +
                "lappend seq_idle_anims {3 {wippen}}\n" +
                "lappend seq_idle_anims {1 {verlegen}}\n" +
                "lappend seq_idle_anims {5 {warten}}\n" +
                "lappend seq_idle_anims {3 {kratzen}}\n" +
                "lappend seq_idle_anims {1 {kopf_kratzen}}\n" +
                "lappend seq_idle_anims {2 {aufatmen}}\n" +
                "lappend seq_idle_anims {1 {popo_waermen}}\n" +
                "lappend seq_idle_anims {2 {hungrig}}\n" +
                "lappend seq_idle_anims {w1 {kletterstand_anim}}\n" +
                "lappend seq_idle_anims {3 {blicken_rechts_links}}\n";
            try
            {
                _interp!.Eval(table);
                var path = System.IO.Path.Combine(dataRoot, "Scripts", "misc", "seq_idle.tcl");
                if (System.IO.File.Exists(path))
                    _interp.Eval(System.IO.File.ReadAllText(path));
                else
                    Debug.LogWarning($"[TclAnimDemo] seq_idle.tcl not found at {path}");
            }
            catch (System.Exception e) { Debug.LogWarning($"[TclAnimDemo] LoadRealIdle: {e.Message}"); }
        }

        // Builds a chain of Tcl procs sc0→sc1→…→sc0, each running one action
        // (anim / walk / rotate) and chaining to the next via its finish code —
        // so the gnome cycles a variety of animations forever, all Tcl-driven.
        // Anim steps are included only if the model actually has that clip.
        void BuildShowcase(string obj, string legStr)
        {
            var have = new HashSet<string>(_lib!.GetClipNames(obj));
            var steps = new List<string>();
            void AddAnim(string clip) { if (have.Contains(clip)) steps.Add($"anim {obj}.{clip}"); }

            AddAnim("stand_anim_a");
            AddAnim("stand_anim_b");
            steps.Add($"walk {legStr} 0");     // walk out
            steps.Add("rotate 180");
            steps.Add($"walk {legStr} 1");     // run back
            steps.Add("rotate 180");
            foreach (var clip in new[]
            {
                "spaehen", "blicken_rechts_links", "kratzen", "aufatmen", "wippen",
                "hungrig", "gehen_gruessen", "buddeln_vorn", "werkeln",
                "kletter_hoch_loop", "kletter_runter_loop", "sterben",
            })
                AddAnim(clip);

            if (steps.Count == 0) { steps.Add($"walk {legStr} 0"); steps.Add("rotate 180"); }

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < steps.Count; i++)
            {
                int nx = (i + 1) % steps.Count;
                sb.Append($"proc sc{i} {{}} {{ action this {steps[i]} {{sc{nx}}} }}\n");
            }
            _interp!.Eval(sb.ToString());
            Debug.Log($"[TclAnimDemo] showcase: {steps.Count} steps for '{obj}'");
        }

        void Update()
        {
            _sim?.Step(Time.deltaTime);
            // Follow the character (keep it framed as it walks/runs around).
            if (followCamera && previewCamera != null && _camDist > 0f &&
                _ent != null && _ent.GameObject != null)
            {
                var mr = _ent.GameObject.GetComponentInChildren<MeshRenderer>();
                if (mr != null)
                {
                    var from = cameraViewDir.sqrMagnitude > 0.0001f ? cameraViewDir.normalized : Vector3.back;
                    var c = mr.bounds.center;
                    previewCamera.transform.position = c + from * _camDist;
                    previewCamera.transform.LookAt(c, Vector3.up);
                }
            }
        }

        Camera? FindPreviewCamera()
        {
            var cams = Object.FindObjectsByType<Camera>(FindObjectsSortMode.None);
            foreach (var c in cams) if (c.targetTexture != null) return c;
            var go = GameObject.Find("Camera");
            return go != null ? go.GetComponent<Camera>() : null;
        }

        void FrameCamera(GameObject model)
        {
            if (previewCamera == null) return;
            var mr = model.GetComponentInChildren<MeshRenderer>();
            if (mr == null) return;
            var b = mr.bounds;
            float radius = b.extents.magnitude;
            if (radius <= 0f) return;
            float fov = previewCamera.fieldOfView * Mathf.Deg2Rad;
            float dist = radius / Mathf.Max(0.01f, Mathf.Sin(fov * 0.5f)) * cameraFillFactor;
            _camDist = dist;   // remembered for follow (Update)
            var from = cameraViewDir.sqrMagnitude > 0.0001f ? cameraViewDir.normalized : Vector3.back;
            previewCamera.transform.position = b.center + from * dist;
            previewCamera.transform.LookAt(b.center, Vector3.up);
        }
    }
}
