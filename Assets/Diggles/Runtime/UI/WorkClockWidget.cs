#nullable enable

namespace Diggles.Runtime
{
    using System.Collections.Generic;
    using System.IO;
    using TMPro;
    using UnityEngine;
    using UnityEngine.EventSystems;
    using UnityEngine.UI;
    using Diggles.Runtime.Tcl.Engine;

    // The HUD work clock — the original's ViWinClock (GameUI/ViWinClock.cpp; Draw FUN_0050ab10,
    // Init FUN_0050a7b0, HandleMsg FUN_0050c2b0, Tick FUN_0050cae0; constructed in the ViWinRoot
    // ctor FUN_00527fa0:102-158). In the original the bottom-right clock is the work-window
    // editor for the current selection — the only work-time UI in the shipped game:
    // • dial Uhr_normal.tga 128×128 (Uhr_gehov.tga on hover), center (64,64), radius 37;
    // • one hand per 12-hour day revolution (hour = GameTime/150 s);
    // • the selection's work window as a #DAB674 pie sector (alpha fading 0→0x60);
    // • drag the sun marker = work start (snap whole hours, end fixed), drag the moon =
    // work end (duration snap half hours); click/wheel near the center = shift ±1 h;
    // • AutoProd_On/Off 40×40 icon toggles the per-gnome auto-schedule flag;
    // • markers only when the whole selection shares one window; hover preview when nothing
    // is selected; date line d.m.y under the dial.
    // All edits write the same entity fields the set_worktime native writes (WorkStart/WorkLen,
    // native obj+0x354/+0x358 — the original edits those fields directly with no Tcl round-trip).
    //
    // Build style: code-built uGUI under the existing HUD canvas (created by GameplayHud — no
    // scene edit), original art loaded from DataRoot via TgaLoader like the rest of the port.
    // Math/snapping lives in WorkClockModel (POCO, EditMode-tested).
    public sealed class WorkClockWidget : MonoBehaviour
    {
        // ---- original layout (FUN_00527fa0:101,157 + FUN_0050a7b0), in 800×600 UI pixels ----
        // window at (right−152, bottom−150); dial 128×128; widget height 128 + the date strip.
        const float UiScale = 1080f / 600f;          // canvas reference height / original UI height
        const float DialSize = 128f;
        const float HandleSize = 28f;                // UhrButtonTag/Nacht.tga (FUN_0050a7b0:44-52,:82-87)
        const float AutoProdSize = 40f;              // AutoProd_On/Off.tga (FUN_0050a7b0:122-126)
        const float RightInset = 152f, BottomRise = 150f;
        // Root = a 48 px AutoProd column on the left + the 128 px dial; the dial keeps the
        // original right−152..right−24 berth. (The decompile extract has no offset for the
        // AutoProd button — the left column is a port placement choice.)
        const float LeftColumn = 48f;
        const float RootW = LeftColumn + DialSize, RootH = 150f;
        static readonly Vector2 DialCenter = new(LeftColumn + 64f, 64f);   // ctor +0x58/+0x5c = 64,64

        enum DragKind { None, Start, End }

        // True while the pointer is over (or dragging) the clock — ClickToMoveService skips the
        // world click then, like the original's ViWin chain eating its messages before the world.
        public static bool PointerCaptured { get; private set; }

        DigglesTclContext? _ctx;
        GameObject? _content;                 // toggled with gameplay/cutscene visibility
        RectTransform? _dialRect;
        Image? _dial, _sector, _autoProd;
        RectTransform? _hand, _sunRect, _moonRect;
        GameObject? _sun, _moon, _autoProdGo;
        TMP_Text? _date;
        Sprite? _dialNormal, _dialHover, _autoOn, _autoOff;

        readonly List<DigglesTclEntity> _selection = new();
        readonly List<(float s, float l)> _windows = new();
        DragKind _drag;
        int _pointerOver;
        float _fade;                          // 0..0x60, the native this+0x44 fade level
        double _shownHour = double.MinValue;  // hand redraw threshold bookkeeping
        (int day, int month, int year) _shownDate = (0, 0, 0);
        float _winStart, _winLen;             // the currently displayed window (hours)
        bool _winValid, _editable;
        float _hoverNextT;                    // hover-preview raycast throttle
        bool _hoverHit; float _hoverStart, _hoverLen;

        // Factory — GameplayHud calls this once with the HUD canvas rect + live context.
        public static WorkClockWidget Create(RectTransform parent, DigglesTclContext ctx)
        {
            var go = new GameObject("WorkClock", typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            // Anchor the bottom-right corner; pivot top-right so (−24·s, +150·s) reproduces the
            // original berth: dial spans right−152..right−24, bottom..bottom−128 (FUN_00527fa0:101,157).
            rt.anchorMin = rt.anchorMax = new Vector2(1f, 0f);
            rt.pivot = new Vector2(1f, 1f);
            rt.sizeDelta = new Vector2(RootW, RootH);
            rt.localScale = new Vector3(UiScale, UiScale, 1f);
            rt.anchoredPosition = new Vector2(-(RightInset - DialSize) * UiScale, BottomRise * UiScale);
            var w = go.AddComponent<WorkClockWidget>();
            w._ctx = ctx;
            w.BuildUi();
            return w;
        }

        // ---- construction ------------------------------------------------------------------

        void BuildUi()
        {
            _content = new GameObject("Content", typeof(RectTransform));
            var crt = (RectTransform)_content.transform;
            crt.SetParent(transform, false);
            crt.anchorMin = Vector2.zero; crt.anchorMax = Vector2.one;
            crt.offsetMin = crt.offsetMax = Vector2.zero;

            _dialNormal = LoadSprite("GUI/buttons/Uhr_normal.tga");
            _dialHover = LoadSprite("GUI/buttons/Uhr_gehov.tga");
            _autoOn = LoadSprite("GUI/icons/Autoprod_On.tga");
            _autoOff = LoadSprite("GUI/icons/Autoprod_Off.tga");
            var sun = LoadSprite("GUI/buttons/UhrButtonTag.tga");
            var moon = LoadSprite("GUI/buttons/UhrButtonNacht.tga");

            // Draw order = sibling order: dial face, then the work-window arc blended over it,
            // then the hand, then the rim markers / AutoProd / date (FUN_0050ab10 draws arc → hand).
            _dial = MakeImage("Dial", DialCenter, new Vector2(DialSize, DialSize), _dialNormal, raycast: true);
            // Don't let the dial bitmap's transparent corners eat world clicks (TgaLoader keeps
            // the texture readable, so per-pixel hit testing is available).
            _dial.alphaHitTestMinimumThreshold = 0.1f;
            _dialRect = _dial.rectTransform;
            var dialInput = _dial.gameObject.AddComponent<DialInput>();
            dialInput.Owner = this;

            _sector = MakeImage("WorkSector", DialCenter,
                new Vector2(2f * WorkClockModel.DialRadius + 6f, 2f * WorkClockModel.DialRadius + 6f),
                BuildDiscSprite(), raycast: false);
            _sector.type = Image.Type.Filled;
            _sector.fillMethod = Image.FillMethod.Radial360;
            _sector.fillOrigin = (int)Image.Origin360.Top;
            _sector.fillClockwise = true;
            _sector.color = new Color32(WorkClockModel.SectorColor.r, WorkClockModel.SectorColor.g,
                WorkClockModel.SectorColor.b, 0);

            var hand = MakeImage("Hand", DialCenter, new Vector2(16f, 40f), BuildHandSprite(), raycast: false);
            hand.rectTransform.pivot = new Vector2(0.5f, 0f);          // rotate about the dial center
            hand.rectTransform.anchoredPosition = ToLocal(DialCenter); // re-seat after the pivot change
            _hand = hand.rectTransform;

            _sun = MakeHandle("SunHandle", sun, isSun: true);
            _moon = MakeHandle("MoonHandle", moon, isSun: false);
            _sunRect = (RectTransform)_sun.transform;
            _moonRect = (RectTransform)_moon.transform;

            _autoProd = MakeImage("AutoProd", new Vector2(LeftColumn * 0.5f, 108f),
                new Vector2(AutoProdSize, AutoProdSize), _autoOn, raycast: true);
            _autoProdGo = _autoProd.gameObject;
            var api = _autoProdGo.AddComponent<AutoProdInput>();
            api.Owner = this;

            var dgo = new GameObject("Date", typeof(RectTransform));
            var drt = (RectTransform)dgo.transform;
            drt.SetParent(_content.transform, false);
            drt.anchorMin = drt.anchorMax = new Vector2(0f, 1f);
            drt.pivot = new Vector2(0.5f, 0.5f);
            drt.anchoredPosition = ToLocal(new Vector2(DialCenter.x, 139f));   // centered under the dial (Draw:759)
            drt.sizeDelta = new Vector2(DialSize, 22f);
            var txt = dgo.AddComponent<TextMeshProUGUI>();
            txt.fontSize = 14f;
            txt.alignment = TextAlignmentOptions.Center;
            txt.raycastTarget = false;
            txt.color = WorkClockModel.DateColor;
            _date = txt;

            _content.SetActive(false);
        }

        // Child placement helper: original top-left pixel coords → uGUI (anchor top-left, y down).
        static Vector2 ToLocal(Vector2 topLeftPx) => new(topLeftPx.x, -topLeftPx.y);

        Image MakeImage(string name, Vector2 centerPx, Vector2 size, Sprite? sprite, bool raycast)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(_content!.transform, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = ToLocal(centerPx);
            rt.sizeDelta = size;
            var img = go.AddComponent<Image>();
            img.sprite = sprite;
            img.raycastTarget = raycast;
            return img;
        }

        GameObject MakeHandle(string name, Sprite? sprite, bool isSun)
        {
            var img = MakeImage(name, DialCenter, new Vector2(HandleSize, HandleSize), sprite, raycast: true);
            var h = img.gameObject.AddComponent<HandleInput>();
            h.Owner = this;
            h.IsSun = isSun;
            return img.gameObject;
        }

        Sprite? LoadSprite(string rel)
        {
            if (_ctx == null || string.IsNullOrEmpty(_ctx.DataRoot)) return null;
            var path = Path.Combine(_ctx.DataRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            TgaResult? res = null;
            try { res = TgaLoader.LoadFromFile(path); }
            catch (System.Exception e) { Debug.LogWarning($"[WorkClock] {rel}: {e.Message}"); }
            if (res == null) { Debug.LogWarning($"[WorkClock] missing GUI art: {path}"); return null; }
            res.Texture.wrapMode = TextureWrapMode.Clamp;
            return Sprite.Create(res.Texture, new Rect(0, 0, res.Texture.width, res.Texture.height),
                new Vector2(0.5f, 0.5f), 100f);
        }

        // Solid disc (radius = dial radius 37) the radial-fill turns into the pie sector. White —
        // tinted #DAB674 via Image.color (ctor +0x64 = 0x80dab674), alpha = the fade level.
        static Sprite BuildDiscSprite()
        {
            const int S = 80; const float r = WorkClockModel.DialRadius, c = (S - 1) * 0.5f;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false) { name = "WorkClockDisc" };
            var px = new Color32[S * S];
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                    byte a = (byte)(Mathf.Clamp01(r - d + 0.5f) * 255f);
                    px[y * S + x] = new Color32(255, 255, 255, a);
                }
            tex.SetPixels32(px); tex.Apply(false, false);
            return Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), 100f);
        }

        // The hand. Original = a fan of 7 fading grey lines from two base points at radius 8
        // (±45°, DAT_0073752c/DAT_007370d0) to the tip at radius 37, grey ramp from 0xa0
        // (FUN_0050ab10:668-758). Approximated as one solid grey dart with that silhouette
        // (the exact fan visual is medium-confidence in the RE anyway).
        static Sprite BuildHandSprite()
        {
            const int W = 16, H = 40;
            Vector2 tip = new(W * 0.5f, 37f), bl = new(W * 0.5f - 5.66f, 5.66f), br = new(W * 0.5f + 5.66f, 5.66f);
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false) { name = "WorkClockHand" };
            var px = new Color32[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    // tip→bl→br is a CCW winding: interior points sit on the positive side of every
                    // edge (Side ≥ 0). regression: the old ≤ test matched no pixel — a fully
                    // transparent hand.
                    bool inside = Side(tip, bl, p) >= 0f && Side(bl, br, p) >= 0f && Side(br, tip, p) >= 0f;
                    px[y * W + x] = new Color32(0xa0, 0xa0, 0xa0, inside ? (byte)255 : (byte)0);
                }
            tex.SetPixels32(px); tex.Apply(false, false);
            return Sprite.Create(tex, new Rect(0, 0, W, H), new Vector2(0.5f, 0f), 100f);

            static float Side(Vector2 a, Vector2 b, Vector2 p)
                => (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);
        }

        // ---- per-frame drive (the ViWinClock::Tick analogue) ---------------------------------

        void Update()
        {
            if (_ctx == null || _content == null) return;

            // Gameplay only: hidden at the main menu (no StoryMgr world) and during cutscenes.
            bool show = _ctx.IsStoryMgr && !_ctx.SequenceActive && _ctx.Sim != null;
            if (_content.activeSelf != show)
            {
                _content.SetActive(show);
                if (!show) { _drag = DragKind.None; _pointerOver = 0; _fade = 0f; UpdateCapture(); }
            }
            if (!show) return;

            // Lost-edge release guard: a drag armed on pointer-down can lose its
            // pointer-up — Unity delivers the up only to the active press object, and
            // UpdateMarkers deactivates the sun/moon handles mid-drag when the fade/validity
            // drops (plus the usual focus-loss edge miss). The stale _drag then latched
            // PointerCaptured until another clock click happened to clear it. Poll the button
            // (same pattern as the dig-marquee guard): button no longer held and no up-edge
            // this frame (a normal release's up-edge is delivered by the EventSystem the same
            // frame — let the pointer-up handler do the clean release) means the edge was lost.
            if (_drag != DragKind.None && !Input.GetMouseButton(0) && !Input.GetMouseButtonUp(0))
            {
                _drag = DragKind.None;
                Debug.LogWarning("[WorkClock] drag pointer-up was lost — capture released by the poll guard");
            }

            double t = _ctx.Sim!.GameTime;
            double hourSec = Diggles.Runtime.Sim.ProdDispatcher.HourSec;

            // Hand: redraw when the hour moved ≥ 0.05 h (Tick:110-116, DAT_00737190).
            double hours = t / hourSec;
            if (System.Math.Abs(hours - _shownHour) >= WorkClockModel.HandRedrawHours)
            {
                _shownHour = hours;
                if (_hand != null)
                    _hand.localEulerAngles = new Vector3(0f, 0f, WorkClockModel.HandZRotationDeg(t, hourSec));
            }

            // Date line "%2d.%2d.%2d" (Draw:759-791).
            var date = WorkClockModel.Date(t, hourSec);
            if (date != _shownDate && _date != null)
            {
                _shownDate = date;
                _date.text = $"{date.day,2}.{date.month,2}.{date.year,2}";
            }

            // Selection → the displayed window (Tick:117-251): markers/arc only when every
            // selected gnome shares one window; with an empty selection a hovered own gnome
            // previews its window (arc only — there is nothing the handles could edit).
            CollectSelection(_selection);
            _windows.Clear();
            foreach (var e in _selection) _windows.Add((e.WorkStart, e.WorkLen));
            _editable = _selection.Count > 0;
            _winValid = WorkClockModel.TrySharedWindow(_windows, out _winStart, out _winLen);
            if (!_editable)
            {
                UpdateHoverPreview();
                if (_hoverHit) { _winValid = true; _winStart = _hoverStart; _winLen = _hoverLen; }
            }

            // Arc fade 0→0x60 in 0x20 steps per UI tick (Tick:220-223,:286-289; the native UI
            // ticks at the 10 Hz logic rate → 0x20 per 0.1 s).
            float fadeTarget = _winValid && WorkClockModel.SectorVisible(_winLen) ? WorkClockModel.SectorMaxAlpha : 0f;
            _fade = Mathf.MoveTowards(_fade, fadeTarget,
                WorkClockModel.SectorAlphaStep * (Time.unscaledDeltaTime / 0.1f));

            UpdateSector();
            UpdateMarkers();
            UpdateAutoProd();
            HandleKeys();
            UpdateCapture();
        }

        void UpdateSector()
        {
            if (_sector == null) return;
            var c = _sector.color;
            _sector.color = new Color(c.r, c.g, c.b, _fade / 255f);
            if (!_winValid) return;
            // Radial360/Top + clockwise: rotate the fill origin to the start marker, fill the span.
            _sector.rectTransform.localEulerAngles =
                new Vector3(0f, 0f, -360f * WorkClockModel.HoursToDayFrac(_winStart));
            _sector.fillAmount = Mathf.Clamp01(_winLen / WorkClockModel.DayHours);
        }

        void UpdateMarkers()
        {
            // Rim markers ride at center + dir·(radius+10) (Tick:336-358) and only exist while
            // the selection shares a window (the hover preview shows the arc, not the handles).
            bool markers = _editable && _winValid && _fade > 0f;
            if (_sun != null && _sun.activeSelf != markers) _sun.SetActive(markers);
            if (_moon != null && _moon.activeSelf != markers) _moon.SetActive(markers);
            if (!markers || _sunRect == null || _moonRect == null) return;

            float sFrac = WorkClockModel.HoursToDayFrac(_winStart);
            float eFrac = WorkClockModel.HoursToDayFrac(_winStart + _winLen);
            _sunRect.anchoredPosition = ToLocal(DialCenter) + WorkClockModel.Dir(sFrac) * WorkClockModel.MarkerRadius;
            _moonRect.anchoredPosition = ToLocal(DialCenter) + WorkClockModel.Dir(eFrac) * WorkClockModel.MarkerRadius;
        }

        void UpdateAutoProd()
        {
            // Icon visible only while a valid gnome is selected; texture = AutoProd_On when the
            // first selected gnome's flag says auto, else _Off (Tick:359-399).
            bool vis = _editable;
            if (_autoProdGo != null && _autoProdGo.activeSelf != vis) _autoProdGo.SetActive(vis);
            if (vis && _autoProd != null)
                _autoProd.sprite = _selection[0].ProdAutoSchedule ? _autoOn : _autoOff;
        }

        void HandleKeys()
        {
            if (!_editable) return;
            // PageUp/PageDown = shift ±1 h (HandleMsg:91-102, VK 0x21/0x22 via FUN_0050c1d0/240);
            // Insert forces auto-schedule ON, Delete OFF (:103-129, VK 0x2d/0x2e).
            if (Input.GetKeyDown(KeyCode.PageUp)) ShiftWindow(+1);
            if (Input.GetKeyDown(KeyCode.PageDown)) ShiftWindow(-1);
            if (Input.GetKeyDown(KeyCode.Insert)) SetAutoProd(true);
            if (Input.GetKeyDown(KeyCode.Delete)) SetAutoProd(false);
        }

        // Hover preview (Tick:117-251): empty selection + pointer over an own gnome → preview
        // its window. The port has no world-hover cache, so this raycasts like ClickToMoveService
        // (throttled to 10 Hz).
        void UpdateHoverPreview()
        {
            if (Time.unscaledTime < _hoverNextT) return;
            _hoverNextT = Time.unscaledTime + 0.1f;
            _hoverHit = false;
            var cam = Camera.main;
            if (cam == null || _ctx == null) return;
            var ray = cam.ScreenPointToRay(Input.mousePosition);
            if (!Physics.Raycast(ray, out var hit, 10000f)) return;
            var dob = hit.collider.GetComponentInParent<DigglesObject>();
            if (dob == null) return;
            foreach (var e in _ctx.Entities.Values)
                if (e.GameObject == dob.gameObject)
                {
                    // owner check (hover id vs +0x36c) + the Zwerg/Baby class gate (+0xe8 == 1|0xb)
                    if (IsScheduleable(e) && e.Owner == _ctx.LocalPlayer)
                    { _hoverHit = true; _hoverStart = e.WorkStart; _hoverLen = e.WorkLen; }
                    return;
                }
        }

        // ---- selection + edits ---------------------------------------------------------------

        // Selection seam: the original applies every edit to every selected Zwerg/Baby
        // (selection list at DAT_00772400+0xa0c4/+0xa0c8, class +0xe8 == 1 || 0xb —
        // FUN_0050c2b0). Multi-select (ctx.Selection) iterates the whole set; everything
        // downstream already iterates this list.
        void CollectSelection(List<DigglesTclEntity> into)
        {
            into.Clear();
            if (_ctx == null) return;
            foreach (var h in _ctx.Selection.Handles)
                if (_ctx.Entities.TryGetValue(h, out var e) && IsScheduleable(e)
                    && e.Owner == _ctx.LocalPlayer)
                    into.Add(e);
        }

        static bool IsScheduleable(DigglesTclEntity e)
            => e.Class != null && (e.Class.Kind == "gnome" || e.Class.Kind == "baby");

        // Direct field write — the native path (obj+0x354/+0x358, the same fields set_worktime
        // FUN_0068b370 writes; no Tcl round-trip, no confirm). The dispatcher's per-tick
        // half-hour snap (FUN_005cb760) keeps the stored values on the native grid.
        void ApplyWindow(float startH, float lenH)
        {
            foreach (var e in _selection) { e.WorkStart = startH; e.WorkLen = lenH; }
        }

        void ShiftWindow(int dh)
        {
            // Both markers move together: start += ±1 h (mod 12), duration unchanged
            // (HandleMsg:138-169 — markers += 1/12 or 11/12 of a day).
            foreach (var e in _selection) e.WorkStart = WorkClockModel.ShiftHours(e.WorkStart, dh);
        }

        void ToggleAutoProd()
        {
            // Native: `obj+0x3d0 ^= 0x800000` for every selected gnome (HandleMsg:75-89) —
            // a per-entity XOR, not "set all to the first's opposite".
            foreach (var e in _selection) e.ProdAutoSchedule = !e.ProdAutoSchedule;
        }

        void SetAutoProd(bool on)
        {
            foreach (var e in _selection) e.ProdAutoSchedule = on;
        }

        (float s, float l) CurrentWindow()
            => _selection.Count > 0 ? (_selection[0].WorkStart, _selection[0].WorkLen) : (_winStart, _winLen);

        // ---- pointer plumbing ------------------------------------------------------------------

        bool TryPointerFrac(PointerEventData ev, out float frac, out float distPx)
        {
            frac = 0f; distPx = float.MaxValue;
            if (_dialRect == null) return false;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _dialRect, ev.position, ev.pressEventCamera ?? ev.enterEventCamera, out var local))
                return false;
            frac = WorkClockModel.PointerDayFrac(local);   // dial pivot = its center
            distPx = local.magnitude;
            return true;
        }

        void DragTo(DragKind kind, PointerEventData ev)
        {
            if (kind == DragKind.None || !_editable || !TryPointerFrac(ev, out var frac, out _)) return;
            var (s0, l0) = CurrentWindow();
            var (s, l) = kind == DragKind.Start
                ? WorkClockModel.DragStart(frac, s0, l0)
                : WorkClockModel.DragEnd(frac, s0, l0);
            ApplyWindow(s, l);   // applied every drag tick, exactly like the native Tick:50-109
        }

        // The capture law, pure for headless tests: the clock owns the pointer while hovered,
        // or while a drag is live — and a drag is only live while the button is actually held
        // (a latched _drag whose up-edge was lost stops capturing the instant the button is
        // up, even before the Update poll clears it).
        public static bool ComputeCapture(bool contentActive, int pointerOver, bool dragActive, bool mouseHeld)
            => contentActive && (pointerOver > 0 || (dragActive && mouseHeld));

        void UpdateCapture()
            => PointerCaptured = ComputeCapture(
                _content != null && _content.activeSelf, _pointerOver,
                _drag != DragKind.None, Input.GetMouseButton(0));

        void OnDisable() { _drag = DragKind.None; _pointerOver = 0; PointerCaptured = false; }

        void PointerEnter() { _pointerOver++; UpdateCapture(); }
        void PointerExit() { if (_pointerOver > 0) _pointerOver--; UpdateCapture(); }

        // The dial face: hover swap (Uhr_gehov, hover flag +0x6c), center click / wheel = shift
        // ±1 h, and a mousedown away from the center arms the work-end drag with mouse capture
        // (HandleMsg:175-186 — msg DAT_007723c4 doubles as the end-marker-drag arm).
        sealed class DialInput : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler,
            IPointerDownHandler, IDragHandler, IPointerUpHandler, IPointerClickHandler, IScrollHandler
        {
            public WorkClockWidget Owner = null!;
            bool _over;

            public void OnPointerEnter(PointerEventData ev)
            {
                if (!_over) { _over = true; Owner.PointerEnter(); }
                if (Owner._dial != null && Owner._dialHover != null) Owner._dial.sprite = Owner._dialHover;
            }

            public void OnPointerExit(PointerEventData ev)
            {
                if (_over) { _over = false; Owner.PointerExit(); }
                if (Owner._dial != null && Owner._dialNormal != null) Owner._dial.sprite = Owner._dialNormal;
            }

            // A deactivated element never gets OnPointerExit — release the hover count here
            // or the clock would capture the pointer forever.
            void OnDisable() { if (_over) { _over = false; Owner.PointerExit(); } }

            public void OnPointerDown(PointerEventData ev)
            {
                if (ev.button != PointerEventData.InputButton.Left) return;
                if (!Owner.TryPointerFrac(ev, out _, out var downDist)) return;
                if (downDist > WorkClockModel.CenterClickRadius && Owner._editable && Owner._winValid)
                { Owner._drag = DragKind.End; Owner.UpdateCapture(); }
            }

            public void OnDrag(PointerEventData ev) => Owner.DragTo(Owner._drag, ev);

            public void OnPointerUp(PointerEventData ev)
            {
                // Unconditional: any pointer-up on the clock releases the drag capture.
                // regression: previously gated on _drag == End, so a stale Start latched by a
                // handle that vanished mid-drag was never released here.
                if (Owner._drag != DragKind.None) { Owner._drag = DragKind.None; Owner.UpdateCapture(); }
            }

            public void OnPointerClick(PointerEventData ev)
            {
                // Click within 20 px (L1 in the native, DAT_00737578) of the center shifts the
                // window: +1 h / −1 h (msg DAT_007723c4 / DAT_007723cc — the exact buttons are
                // medium confidence in the RE; left/right is the natural mapping).
                if (!Owner._editable || ev.dragging) return;
                if (!Owner.TryPointerFrac(ev, out _, out var dist) || dist > WorkClockModel.CenterClickRadius) return;
                Owner.ShiftWindow(ev.button == PointerEventData.InputButton.Right ? -1 : +1);
            }

            public void OnScroll(PointerEventData ev)
            {
                if (!Owner._editable || ev.scrollDelta.y == 0f) return;
                Owner.ShiftWindow(ev.scrollDelta.y > 0f ? +1 : -1);
            }
        }

        // The sun/moon rim markers: drag = edit work start / work end (HandleMsg:196-218,
        // drag flags +0x54/+0x55; the angle→window math runs in Tick:50-109 → WorkClockModel).
        sealed class HandleInput : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler,
            IPointerDownHandler, IDragHandler, IPointerUpHandler
        {
            public WorkClockWidget Owner = null!;
            public bool IsSun;
            bool _over;

            public void OnPointerEnter(PointerEventData ev) { if (!_over) { _over = true; Owner.PointerEnter(); } }
            public void OnPointerExit(PointerEventData ev) { if (_over) { _over = false; Owner.PointerExit(); } }

            void OnDisable()
            {
                if (_over) { _over = false; Owner.PointerExit(); }
                // Deactivated mid-drag (UpdateMarkers hides the handles when the fade/validity
                // drops while dragging): Unity never delivers the pointer-up to an inactive
                // press object — release the drag now or PointerCaptured latches.
                if (Owner != null && Owner._drag != DragKind.None)
                { Owner._drag = DragKind.None; Owner.UpdateCapture(); }
            }

            public void OnPointerDown(PointerEventData ev)
            {
                if (ev.button != PointerEventData.InputButton.Left) return;
                Owner._drag = IsSun ? DragKind.Start : DragKind.End;
                Owner.UpdateCapture();
            }

            public void OnDrag(PointerEventData ev) => Owner.DragTo(Owner._drag, ev);

            public void OnPointerUp(PointerEventData ev)
            {
                Owner._drag = DragKind.None;
                Owner.UpdateCapture();
            }
        }

        // The AutoProd icon: click toggles the auto-schedule flag of every selected gnome
        // (HandleMsg:75-89 — obj+0x3d0 ^= 0x800000; backed by entity.ProdAutoSchedule +
        // the set/get_prodautoschedule natives).
        sealed class AutoProdInput : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
        {
            public WorkClockWidget Owner = null!;
            bool _over;

            public void OnPointerEnter(PointerEventData ev) { if (!_over) { _over = true; Owner.PointerEnter(); } }
            public void OnPointerExit(PointerEventData ev) { if (_over) { _over = false; Owner.PointerExit(); } }
            void OnDisable() { if (_over) { _over = false; Owner.PointerExit(); } }

            public void OnPointerClick(PointerEventData ev)
            {
                if (ev.button == PointerEventData.InputButton.Left) Owner.ToggleAutoProd();
            }
        }
    }
}
