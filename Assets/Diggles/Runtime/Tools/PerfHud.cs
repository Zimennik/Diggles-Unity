#nullable enable

namespace Diggles.Runtime.Tools
{
    using System.Text;
    using Unity.Profiling;
    using UnityEngine;

    // Lightweight in-Play performance overlay. The project has no GPU/dispatch visibility headless
    // (-nographics can't measure SetPass/Draw Calls, and no PlayerLoop runs), so this is the only way
    // to read steady-state FPS, per-frame GC alloc, and draw-call/batch counts on the real scene with
    // many objects and a big lit world.
    //
    // Auto-creates itself on Play (disabled overlay, ~0 cost) and toggles with F3. It also logs any
    // slow frame (> SlowFrameMs) with its GC alloc + draw calls, which catches one-off hitches like the
    // cutscene-skip drain without instrumenting the sim.
    //
    // Phase breakdown: marker-time recorders split the main-thread frame into named PlayerLoop
    // phases (script Update/LateUpdate dispatch, physics, coroutines, IMGUI, SRP render submission,
    // present wait, EditorLoop, GC, synchronous shader compiles), naming where a CPU-bound frame goes
    // even when total CPU time equals frame time. Every slow-frame log carries the breakdown.
    //
    // Freeze watchdog: a background thread polls the main-thread heartbeat (stamped here each
    // Update). If the main thread is stuck for more than 3 s, it logs the active PerfTrace section
    // (see PerfTrace.Section) from the background thread, so even a freeze that never produces a
    // frame gets named live — "no section" means the freeze sits on an uninstrumented path.
    public sealed class PerfHud : MonoBehaviour
    {
        public static bool Visible;                 // F3 toggles; also readable by other diagnostics
        const float SlowFrameMs = 33f;              // >2 frames @ 60 → log it (hitch detector)

        // Published by EngineTickService every frame: the fixed-step sim's wall cost this frame,
        // how many 25 ms steps ran, and the live entity count. Lets Play separate sim CPU (scales
        // with entities × speed) from render-side cost without a profiler: at x1 expect <1 ms; at
        // fast-forward xK expect ≈K× the x1 value.
        public static double SimMs;
        public static int SimSteps;
        public static int SimEntities;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            var go = new GameObject("~PerfHud");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<PerfHud>();
        }

        ProfilerRecorder _mainThread, _gcAlloc, _drawCalls, _setPass, _batches, _verts;

        // Phase markers (guarded by.Valid — a name/category miss just shows nothing).
        struct Phase { public string Short; public ProfilerRecorder Rec; }
        Phase[] _phases = System.Array.Empty<Phase>();

        float _fps;
        double _slowCooldown;
        readonly StringBuilder _sb = new(512);
        GUIStyle? _style;

        // Freeze watchdog.
        System.Threading.Thread? _watchdog;
        volatile bool _watchdogRun;
        static volatile bool s_focused = true;

        // Console-log storm detector: every Debug.Log in the Editor captures a stack trace and
        // appends to the console — tens of thousands in one frame is a multi-second pure-CPU
        // freeze with only modest allocs. Counted via logMessageReceived (cheap int increment),
        // reported on slow frames.
        static int s_logsThisFrame, s_logsLastFrame;
        static void CountLog(string condition, string stackTrace, LogType type) => s_logsThisFrame++;

        void OnEnable()
        {
            Application.logMessageReceived += CountLog;
            _mainThread = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread", 15);
            _gcAlloc    = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
            _drawCalls  = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
            _setPass    = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
            _batches    = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
            _verts      = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Vertices Count");

            _phases = new[]
            {
                P("upd",     ProfilerCategory.Scripts,  "BehaviourUpdate"),
                P("late",    ProfilerCategory.Scripts,  "LateBehaviourUpdate"),
                P("fixed",   ProfilerCategory.Scripts,  "FixedBehaviourUpdate"),
                P("coro",    ProfilerCategory.Scripts,  "CoroutinesDelayedCalls"),
                P("phys",    ProfilerCategory.Physics,  "Physics.Processing"),
                P("gui",     ProfilerCategory.Gui,      "GUI.Repaint"),
                P("render",  ProfilerCategory.Render,   "RenderPipelineManager.DoRenderLoop"),
                P("present", ProfilerCategory.Render,   "Gfx.WaitForPresentOnGfxThread"),
                P("editor",  ProfilerCategory.Internal, "EditorLoop"),
                P("gc",      ProfilerCategory.Memory,   "GC.Collect"),
                P("shader",  ProfilerCategory.Render,   "Shader.CreateGPUProgram"),
            };

            _watchdogRun = true;
            _watchdog = new System.Threading.Thread(WatchdogLoop)
            { IsBackground = true, Name = "Diggles.FreezeWatchdog" };
            _watchdog.Start();
        }

        static Phase P(string label, ProfilerCategory cat, string marker) =>
            new() { Short = label, Rec = ProfilerRecorder.StartNew(cat, marker) };

        void OnDisable()
        {
            Application.logMessageReceived -= CountLog;
            _watchdogRun = false;
            _mainThread.Dispose(); _gcAlloc.Dispose(); _drawCalls.Dispose();
            _setPass.Dispose(); _batches.Dispose(); _verts.Dispose();
            for (int i = 0; i < _phases.Length; i++) _phases[i].Rec.Dispose();
            _phases = System.Array.Empty<Phase>();
        }

        // Names the frozen main thread's location while it is still frozen. Debug.LogWarning is
        // thread-safe. Gated on focus (an unfocused editor legitimately stops pumping frames).
        void WatchdogLoop()
        {
            while (_watchdogRun)
            {
                System.Threading.Thread.Sleep(500);
                if (!s_focused) continue;
                double stallMs = Diggles.Runtime.Diagnostics.PerfTrace.MsSinceHeartbeat();
                if (stallMs > 3000)
                {
                    string? sec = Diggles.Runtime.Diagnostics.PerfTrace.ActiveSection;
                    double secS = Diggles.Runtime.Diagnostics.PerfTrace.ActiveSectionMs / 1000.0;
                    Debug.LogWarning(sec != null
                        ? $"[perf] MAIN-THREAD STALL {stallMs / 1000.0:F1}s — inside section '{sec}' ({secS:F1}s) [live watchdog]"
                        : $"[perf] MAIN-THREAD STALL {stallMs / 1000.0:F1}s — NO active PerfTrace section (uninstrumented path, or editor paused) [live watchdog]");
                    System.Threading.Thread.Sleep(2500);   // throttle while stuck
                }
            }
        }

        // Appends " | upd 3.2 late 1.0 …" for every valid phase above minMs. Alloc-bounded: only
        // runs for the HUD (when visible) and for slow-frame logs.
        void AppendPhases(StringBuilder sb, double minMs)
        {
            for (int i = 0; i < _phases.Length; i++)
            {
                ref var ph = ref _phases[i];
                if (!ph.Rec.Valid) continue;
                double ms = ph.Rec.LastValue / 1e6;
                if (ms < minMs) continue;
                sb.Append(' ').Append(ph.Short).Append(' ').Append(ms.ToString("F1"));
            }
        }

        void Update()
        {
            Diggles.Runtime.Diagnostics.PerfTrace.Heartbeat();
            s_focused = Application.isFocused;
            s_logsLastFrame = s_logsThisFrame;   // frame boundary for the storm detector
            s_logsThisFrame = 0;

            if (Input.GetKeyDown(KeyCode.F3)) Visible = !Visible;
            // F4 = live A/B of the morph-model GPU instancing: watch the draws/batches lines flip
            // while many entities are on screen. Off = the faithful per-renderer MPB path
            // (SRP-ineligible → draws ≈ visible models).
            if (Input.GetKeyDown(KeyCode.F4))
            {
                DigglesAnimator.UseInstancing = !DigglesAnimator.UseInstancing;
                Debug.Log($"[PerfHud] morph GPU instancing: {(DigglesAnimator.UseInstancing ? "ON" : "OFF")}");
            }
#if UNITY_EDITOR
            // F6 = editor-only entity hierarchy hiding: flip the static and re-sweep every
            // already-spawned entity, since Spawn only stamps future ones. See below.
            if (Input.GetKeyDown(KeyCode.F6)) ToggleEntityHierarchyHiding();
#endif

            float dt = Time.unscaledDeltaTime;
            if (dt > 0f) _fps = Mathf.Lerp(_fps, 1f / dt, 0.1f);

            // Hitch logger — always on (cheap), independent of the overlay. Catches the cutscene-skip
            // drain frame and any other one-off stall so it can be reported without a profiler capture.
            if (_slowCooldown > 0.0) _slowCooldown -= dt;
            float frameMs = dt * 1000f;
            if (frameMs > SlowFrameMs && _slowCooldown <= 0.0)
            {
                _slowCooldown = 0.5;   // don't spam during a multi-frame stall
                // cpuMs = main-thread CPU time. cpu ≈ frame → CPU-bound (sim/dispatch/scripts);
                // cpu << frame → GPU-bound (the frame waited on the GPU: lights/overdraw/fill).
                double cpuMs = _mainThread.Valid ? _mainThread.LastValue / 1e6 : 0.0;
                _sb.Clear();
                _sb.Append("[PerfHud] SLOW FRAME ").Append(frameMs.ToString("F0"))
                   .Append(" ms (cpu ").Append(cpuMs.ToString("F0"))
                   .Append(") | gcAlloc=").Append(_gcAlloc.LastValue / 1024).Append(" KB | draws=")
                   .Append(_drawCalls.LastValue).Append(" setpass=").Append(_setPass.LastValue)
                   .Append(" batches=").Append(_batches.LastValue).Append(" |");
                AppendPhases(_sb, 0.5);
                var maxSec = Diggles.Runtime.Diagnostics.PerfTrace.FrameMaxSection;
                if (maxSec != null)
                    _sb.Append(" | top section: ").Append(maxSec).Append(' ')
                       .Append(Diggles.Runtime.Diagnostics.PerfTrace.FrameMaxSectionMs.ToString("F0")).Append(" ms");
                if (s_logsLastFrame > 50)
                    _sb.Append(" | LOG STORM: ").Append(s_logsLastFrame).Append(" console logs");
                Debug.LogWarning(_sb.ToString());
            }
        }

#if UNITY_EDITOR
        // Editor-only entity hierarchy hiding, toggled live by F6. RuntimeWorldSpawner.Spawn only
        // stamps HideFlags.HideInHierarchy on future spawns, so a live flip must re-sweep the
        // already-spawned roots too. DigglesObject sits on the outer GameObject of every visible
        // entity (only Dummy_ / invisible markers lack one), so its transforms are the roots to
        // (un)hide. FindObjects* ignores hideFlags, so this keeps working regardless of current
        // state. Setting hideFlags marks the scene dirty in the editor — acceptable for a
        // diagnostics toggle.
        static void ToggleEntityHierarchyHiding()
        {
            bool hide = !RuntimeWorldSpawner.HideEntitiesInHierarchy;
            RuntimeWorldSpawner.HideEntitiesInHierarchy = hide;

            var objs = Object.FindObjectsByType<DigglesObject>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var o in objs)
            {
                var go = o.gameObject;
                if (hide) go.hideFlags |= HideFlags.HideInHierarchy;
                else      go.hideFlags &= ~HideFlags.HideInHierarchy;
            }
            UnityEditor.EditorApplication.RepaintHierarchyWindow();
            Debug.Log($"[PerfHud] entity hierarchy hiding: {(hide ? "ON" : "OFF")} — swept {objs.Length} entities");
        }
#endif

        void OnGUI()
        {
            if (!Visible) return;
            _style ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 14, fontStyle = FontStyle.Bold, richText = false,
                normal = { textColor = Color.white },
            };
            double cpuMs = _mainThread.Valid ? _mainThread.LastValue / 1e6 : 0.0;
            long gcKb = _gcAlloc.Valid ? _gcAlloc.LastValue / 1024 : 0;

            _sb.Clear();
            _sb.Append("FPS ").Append(Mathf.RoundToInt(_fps))
               .Append("   CPU ").Append(cpuMs.ToString("F1")).Append(" ms\n");
            _sb.Append("GC/frame ").Append(gcKb).Append(" KB\n");
            _sb.Append("draws ").Append(_drawCalls.LastValue)
               .Append("  setpass ").Append(_setPass.LastValue)
               .Append("  batches ").Append(_batches.LastValue).Append('\n');
            _sb.Append("verts ").Append(_verts.LastValue / 1000).Append("k\n");
            _sb.Append("sim ").Append(SimMs.ToString("F1")).Append(" ms (")
               .Append(SimSteps).Append(" steps, ").Append(SimEntities).Append(" ents)\n");
            var (na, nc, nt, nl, np) = DigglesFrameDriver.Counts;
            _sb.Append("army ").Append(na).Append('/').Append(nc).Append('/').Append(nt)
               .Append('/').Append(nl).Append('/').Append(np).Append(" (anim/ctrl/tex/link/pick)\n");
            _sb.Append("phases:");
            AppendPhases(_sb, 0.05);

            var bg = new Rect(8, 8, 460, 150);
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(bg, Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(new Rect(16, 12, 448, 136), _sb.ToString(), _style);
        }
    }
}
