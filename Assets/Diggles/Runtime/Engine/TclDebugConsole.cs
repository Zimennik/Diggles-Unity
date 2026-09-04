#nullable enable

namespace Diggles.Runtime
{
    using System.Collections.Generic;
    using Diggles.Runtime.Tcl.Engine;
    using UnityEngine;

    // In-game Tcl debug console.
    // * Toggle: ` (backquote) or F10.
    // * The input line evals as Tcl in the live game context; `this` = the currently
    // selected entity (so `set_event this evt_task_walk -pos1 {...}`, `get_pos this`,
    // `new Baby "" "246 30.5 10" "0 0 0"`, `gamestats numgnomes 0` … all just work).
    // * Up/Down = history; Esc closes; the result (or the error) prints to the log pane
    // and the Unity console.
    // Purely additive dev tooling — nothing gameplay-side references it.
    public sealed class TclDebugConsole : MonoBehaviour
    {
        static TclDebugConsole? s_instance;
        DigglesTclContext? _ctx;

        bool _open;
        string _input = "";
        readonly List<string> _log = new();
        readonly List<string> _history = new();
        int _histCursor = -1;
        Vector2 _scroll;
        bool _focusPending;

        // `@mouse` substitution: the native coords of the terrain under the cursor, sampled
        // continuously while the cursor is over the world (not the console box) — so aim first,
        // then type/submit; the token expands to "x y z" (e.g. `new Stein "" "@mouse" "0 0 0"`,
        // `set_event this evt_task_walk -pos1 {@mouse}`).
        Vector3 _mouseNative;
        bool _mouseNativeValid;
        Camera? _cam;
        readonly RaycastHit[] _hitBuf = new RaycastHit[64];   // reused — no per-frame RaycastAll alloc while open

        public static void Attach(DigglesTclContext ctx)
        {
            if (s_instance == null)
            {
                var go = new GameObject("TclDebugConsole") { hideFlags = HideFlags.HideAndDontSave };
                DontDestroyOnLoad(go);
                s_instance = go.AddComponent<TclDebugConsole>();
            }
            s_instance._ctx = ctx;

            // Debug-console commands for the horizon-zone / rear-skirt-fade chain (cave rim,
            // Docs/BACKDROP_PARALLAX_RE.md §6). Registered here (dev tooling, not gameplay):
            // horizon_debug — dump zones registered, the picked zone + distances, the
            // _HorizonFog shader globals and the presenter/gate state.
            // horizon_tint 1|0 — hot-pink the rear-fade region in DigglesGround/Model
            // (forced depth 32, works with no active zone) so one Play
            // glance shows whether the rim triangles sit inside it.
            // RegisterCommand overwrites, so re-Attach on a new context is safe; the names
            // collide with nothing (the horizon_* gameplay trio is set/rem/clear).
            ctx.Interp.RegisterCommand("horizon_debug", (_, a) =>
            {
                var p = Object.FindFirstObjectByType<Diggles.Runtime.World.BackdropPresenter>();
                if (p == null)
                    return $"no BackdropPresenter in scene; ctx zones registered: {ctx.HorizonRegions.Count}";
                return p.DebugReport();
            });
            ctx.Interp.RegisterCommand("horizon_tint", (_, a) =>
            {
                bool on = a.Length > 1 && a[1] != "0";
                Diggles.Runtime.World.BackdropPresenter.SetDebugTint(on);
                return on ? "horizon tint ON (fade region = hot pink)" : "horizon tint OFF";
            });
        }

        /// <summary>
        /// Evaluate Tcl in the live context with <c>this</c> temporarily set to the
        /// selected entity. Must be called on the Unity main thread.
        /// </summary>
        public static string EvalExternal(string tcl)
        {
            var ctx = s_instance?._ctx;
            if (ctx?.Interp == null)
                throw new System.InvalidOperationException("Tcl context not ready");

            // Same token conveniences the in-game console applies at submit (:161): remote
            // clients (the tracker's action buttons) send the identical "@mouse" recipes.
            if (tcl.Contains("@mouse")) tcl = tcl.Replace("@mouse", s_instance!.MouseToken());

            var previous = ctx.This;
            if (ctx.SelectedHandle != 0 && ctx.Entities.TryGetValue(ctx.SelectedHandle, out var selected))
                ctx.This = selected;
            try
            {
                return ctx.Interp.Eval(tcl);
            }
            finally
            {
                ctx.This = previous;
            }
        }

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.BackQuote) || Input.GetKeyDown(KeyCode.F10))
            {
                _open = !_open;
                _focusPending = _open;
            }
            if (_open && Input.GetKeyDown(KeyCode.Escape)) _open = false;

            // Track the terrain point under the cursor for `@mouse` (skip while the cursor is
            // over the console box; entity colliders are skipped — spawn coords want the ground).
            // Also sampled while a remote console client is attached: the tracker's spawn
            // buttons carry "@mouse", and their token must mean "the last terrain point the
            // cursor hovered in the game window" even with the in-game console closed.
            if ((!_open && !RemoteConsoleService.AnyClient) || _ctx?.SpawnParent == null) return;
            if (_cam == null) _cam = Camera.main;
            if (_cam == null) return;
            var mp = Input.mousePosition;
            var guiPoint = new Vector2(mp.x, Screen.height - mp.y);
            var box = new Rect(10, Screen.height - 280 - 10, 760, 280);   // the OnGUI draw rect (y from top)
            if (box.Contains(guiPoint)) return;                            // keep the last world sample
            // NonAlloc + nearest-terrain scan (RaycastAll allocated a fresh RaycastHit[] every
            // frame the console was open). Order-independent: track the min.
            int hitCount = Physics.RaycastNonAlloc(_cam.ScreenPointToRay(mp), _hitBuf, 10000f);
            float best = float.MaxValue;
            for (int i = 0; i < hitCount; i++)
            {
                var h = _hitBuf[i];
                if (h.distance >= best) continue;
                if (h.collider.GetComponentInParent<DigglesObject>() != null) continue;   // entity — skip
                best = h.distance;
                _mouseNative = RuntimeWorldSpawner.LocalToNative(
                    _ctx.SpawnParent.InverseTransformPoint(h.point));
                _mouseNativeValid = true;
            }
        }

        void OnGUI()
        {
            if (!_open || _ctx == null) return;
            const int W = 760, H = 280;
            var rect = new Rect(10, Screen.height - H - 10, W, H);
            GUILayout.BeginArea(rect, GUI.skin.box);

            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(H - 60));
            foreach (var line in _log) GUILayout.Label(line);
            GUILayout.EndScrollView();

            GUILayout.Label(_mouseNativeValid
                ? $"@mouse = {MouseToken()}   (hover the terrain outside the console to update)"
                : "@mouse: hover the terrain outside the console window");

            GUI.SetNextControlName("TclConsoleInput");
            var ev = Event.current;
            if (ev.type == EventType.KeyDown && GUI.GetNameOfFocusedControl() == "TclConsoleInput")
            {
                if (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter)
                {
                    Submit();
                    ev.Use();
                }
                else if (ev.keyCode == KeyCode.UpArrow && _history.Count > 0)
                {
                    _histCursor = _histCursor < 0 ? _history.Count - 1 : Mathf.Max(0, _histCursor - 1);
                    _input = _history[_histCursor];
                    ev.Use();
                }
                else if (ev.keyCode == KeyCode.DownArrow && _histCursor >= 0)
                {
                    _histCursor++;
                    _input = _histCursor >= _history.Count ? "" : _history[_histCursor];
                    if (_histCursor >= _history.Count) _histCursor = -1;
                    ev.Use();
                }
            }
            _input = GUILayout.TextField(_input);
            if (_focusPending) { GUI.FocusControl("TclConsoleInput"); _focusPending = false; }

            GUILayout.EndArea();
        }

        void Submit()
        {
            string cmd = _input.Trim();
            _input = "";
            _histCursor = -1;
            if (cmd.Length == 0 || _ctx?.Interp == null) return;
            _history.Add(cmd);
            if (cmd.Contains("@mouse"))
            {
                if (!_mouseNativeValid) { _log.Add("ERROR: @mouse not captured — hover the terrain outside the console"); return; }
                cmd = cmd.Replace("@mouse", MouseToken());
            }
            _log.Add("> " + cmd);

            try
            {
                string result = EvalExternal(cmd);
                _log.Add(string.IsNullOrEmpty(result) ? "(ok)" : result);
                Debug.Log($"[tcl-console] {cmd} -> {result}");
            }
            catch (System.Exception e)
            {
                _log.Add("ERROR: " + e.Message);
                Debug.LogWarning($"[tcl-console] {cmd} !! {e.Message}");
            }
            if (_log.Count > 200) _log.RemoveRange(0, _log.Count - 200);
            _scroll.y = float.MaxValue;
        }

        string MouseToken()
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            return _mouseNative.x.ToString("F2", inv) + " " + _mouseNative.y.ToString("F2", inv)
                 + " " + _mouseNative.z.ToString("F2", inv);
        }
    }
}
