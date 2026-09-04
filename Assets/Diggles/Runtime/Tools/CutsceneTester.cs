#nullable enable

namespace Diggles.Runtime.Tools
{
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using UnityEngine;
    using UnityEngine.UI;

    // PLAY-MODE tool to test cutscenes. Drop it on the same GameObject as (or anywhere near) a
    // DigglesWorldRuntime, press Play, and an in-scene panel lists every cutscene under
    // data/Scripts/sequences. Pick one and:
    // • Play — stage it into the CURRENT (already-loaded) world.
    // • Reload + Play — reload the scene for a FRESH world, then play (the faithful retest:
    // a cutscene mutates the world, so a clean replay needs a clean env).
    // • Stop — leave cutscene mode + delete the trigger.
    // Set the world environment (campaign + loadRealClasses) on the DigglesWorldRuntime to match
    // the cutscene you're testing — cutscenes are staged into the spawned world.
    //
    // Self-builds its UI (no scene setup). NOT headless-verifiable; verify visually in Play.
    public sealed class CutsceneTester : MonoBehaviour
    {
        public DigglesWorldRuntime? Runtime;

        readonly List<string> _all = new();      // all sequence names (basename, sorted, distinct)
        readonly Dictionary<string, string> _subfolder = new();
        string _selected = "";
        string _filter = "";

        Transform? _resultsRoot;
        Text? _status;
        Font? _font;

        void Start()
        {
            if (Runtime == null) Runtime = FindObjectOfType<DigglesWorldRuntime>();
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                 ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
            ScanSequences();
            BuildUi();
            RefreshResults();
        }

        void ScanSequences()
        {
            _all.Clear(); _subfolder.Clear();
            string root = Runtime != null ? Path.Combine(Runtime.dataRoot, "Scripts", "sequences") : "";
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
            foreach (var f in Directory.GetFiles(root, "*.tcl", SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(f);
                if (_subfolder.ContainsKey(name)) continue;
                _subfolder[name] = Path.GetFileName(Path.GetDirectoryName(f) ?? "");
                _all.Add(name);
            }
            _all.Sort(System.StringComparer.OrdinalIgnoreCase);
        }

        // ---- UI ----

        void BuildUi()
        {
            var canvasGo = new GameObject("CutsceneTesterUI");
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 2000;     // above the cutscene presenter
            canvasGo.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            canvasGo.AddComponent<GraphicRaycaster>();
            if (FindObjectOfType<UnityEngine.EventSystems.EventSystem>() == null)
            {
                var es = new GameObject("EventSystem");
                es.AddComponent<UnityEngine.EventSystems.EventSystem>();
                es.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
            }

            // Left panel.
            var panel = Panel(canvasGo.transform, new Vector2(0, 0), new Vector2(0.30f, 1f), new Color(0, 0, 0, 0.78f));

            float y = -8f;
            MakeText(panel, "CUTSCENE TESTER", 18, new Vector2(8, y), new Vector2(-8, y - 26), TextAnchor.MiddleLeft, Color.white);
            y -= 30;

            // Filter box.
            var filterGo = new GameObject("Filter");
            filterGo.transform.SetParent(panel, false);
            var fImg = filterGo.AddComponent<Image>(); fImg.color = new Color(1, 1, 1, 0.12f);
            Stretch(fImg.rectTransform, new Vector2(8, y), new Vector2(-8, y - 26));
            var input = filterGo.AddComponent<InputField>();
            var ph = MakeChildText(filterGo.transform, "filter…", 14, new Color(1, 1, 1, 0.4f));
            var txt = MakeChildText(filterGo.transform, "", 14, Color.white);
            input.textComponent = txt; input.placeholder = ph;
            input.onValueChanged.AddListener(v => { _filter = v ?? ""; RefreshResults(); });
            y -= 30;

            // Results container (simple vertical list — first matches of the filter).
            var resGo = new GameObject("Results");
            resGo.transform.SetParent(panel, false);
            var resImg = resGo.AddComponent<Image>(); resImg.color = new Color(1, 1, 1, 0.04f);
            Stretch(resImg.rectTransform, new Vector2(8, y), new Vector2(-8, 96));   // down to ~96px from bottom
            var vlg = resGo.AddComponent<VerticalLayoutGroup>();
            vlg.childControlHeight = true; vlg.childForceExpandHeight = false; vlg.spacing = 1;
            vlg.padding = new RectOffset(2, 2, 2, 2);
            _resultsRoot = resGo.transform;

            // Action buttons row (bottom).
            MakeButton(panel, "Play", new Vector2(8, 70), new Vector2(0.32f, 44), () => Play(false));
            MakeButton(panel, "Reload+Play", new Vector2(0.33f, 70), new Vector2(0.68f, 44), () => Play(true));
            MakeButton(panel, "Stop", new Vector2(0.69f, 70), new Vector2(-8, 44), () => Runtime?.StopCutscene());

            _status = MakeText(panel, "select a cutscene", 13, new Vector2(8, 38), new Vector2(-8, 8), TextAnchor.LowerLeft, new Color(0.8f, 1f, 0.8f));
        }

        void RefreshResults()
        {
            if (_resultsRoot == null) return;
            for (int i = _resultsRoot.childCount - 1; i >= 0; i--) Destroy(_resultsRoot.GetChild(i).gameObject);
            var matches = _all.Where(n => string.IsNullOrEmpty(_filter)
                                          || n.IndexOf(_filter, System.StringComparison.OrdinalIgnoreCase) >= 0)
                              .Take(24).ToList();
            foreach (var n in matches)
            {
                string label = $"{n}  ({(_subfolder.TryGetValue(n, out var s) ? s : "")})";
                string captured = n;
                var b = MakeListButton(_resultsRoot, label, () => { _selected = captured; SetStatus($"selected: {captured}"); });
            }
            SetStatus(matches.Count == 0 ? $"no match · {_all.Count} cutscenes" : $"{matches.Count} shown · {_all.Count} total");
        }

        void Play(bool reload)
        {
            if (Runtime == null) { SetStatus("no DigglesWorldRuntime in scene"); return; }
            if (string.IsNullOrEmpty(_selected)) { SetStatus("pick a cutscene first"); return; }
            if (reload) { SetStatus($"reloading world → {_selected}"); Runtime.ReloadAndPlay(_selected); }
            else { int h = Runtime.PlayCutscene(_selected); SetStatus(h != 0 ? $"playing: {_selected}" : $"FAILED: {_selected}"); }
        }

        void SetStatus(string s) { if (_status != null) _status.text = s; }

        // ---- tiny UGUI builders ----

        Transform Panel(Transform parent, Vector2 anchorMin, Vector2 anchorMax, Color col)
        {
            var go = new GameObject("Panel"); go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>(); img.color = col;
            var rt = img.rectTransform; rt.anchorMin = anchorMin; rt.anchorMax = anchorMax;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            return go.transform;
        }

        // offsetMin/offsetMax in pixels from the panel's top-left convention used above.
        Text MakeText(Transform parent, string s, int size, Vector2 topLeft, Vector2 botRight,
            TextAnchor anchor, Color col)
        {
            var go = new GameObject("Text"); go.transform.SetParent(parent, false);
            var t = go.AddComponent<Text>(); t.font = _font; t.fontSize = size; t.color = col;
            t.alignment = anchor; t.text = s; t.horizontalOverflow = HorizontalWrapMode.Wrap;
            // Anchored to the panel top; topLeft.x/.y and botRight.x/.y are pixel offsets (y < 0 = below top).
            var rt = t.rectTransform;
            rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(1, 1); rt.pivot = new Vector2(0, 1);
            rt.offsetMin = new Vector2(topLeft.x, botRight.y); rt.offsetMax = new Vector2(botRight.x, topLeft.y);
            return t;
        }

        Text MakeChildText(Transform parent, string s, int size, Color col)
        {
            var go = new GameObject("t"); go.transform.SetParent(parent, false);
            var t = go.AddComponent<Text>(); t.font = _font; t.fontSize = size; t.color = col;
            t.alignment = TextAnchor.MiddleLeft; t.text = s;
            Stretch(t.rectTransform, new Vector2(6, -2), new Vector2(-6, 2));
            return t;
        }

        void MakeButton(Transform parent, string label, Vector2 topLeft, Vector2 botRight, UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject("Btn_" + label); go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>(); img.color = new Color(0.2f, 0.45f, 0.7f, 0.9f);
            var rt = img.rectTransform; rt.anchorMin = new Vector2(0, 0); rt.anchorMax = new Vector2(1, 0); rt.pivot = new Vector2(0, 0);
            rt.offsetMin = new Vector2(topLeft.x, botRight.y); rt.offsetMax = new Vector2(botRight.x, topLeft.y);
            var b = go.AddComponent<Button>(); b.targetGraphic = img; b.onClick.AddListener(onClick);
            var t = MakeChildText(go.transform, label, 14, Color.white); t.alignment = TextAnchor.MiddleCenter;
        }

        Button MakeListButton(Transform parent, string label, UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject("Item"); go.transform.SetParent(parent, false);
            var le = go.AddComponent<LayoutElement>(); le.minHeight = 20; le.preferredHeight = 20;
            var img = go.AddComponent<Image>(); img.color = new Color(1, 1, 1, 0.06f);
            var b = go.AddComponent<Button>(); b.targetGraphic = img; b.onClick.AddListener(onClick);
            var t = MakeChildText(go.transform, label, 13, new Color(0.9f, 0.95f, 1f)); t.alignment = TextAnchor.MiddleLeft;
            return b;
        }

        static void Stretch(RectTransform rt, Vector2 offMin, Vector2 offMax)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = offMin; rt.offsetMax = offMax;
        }
    }
}
