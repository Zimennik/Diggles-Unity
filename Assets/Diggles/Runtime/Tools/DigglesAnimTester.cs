#nullable enable

namespace Diggles.Runtime
{
    using System.Collections.Generic;
    using TMPro;
    using UnityEngine;
    using UnityEngine.EventSystems;
    using UnityEngine.UI;

    // Runtime animation tester. Drives the DigglesModelLibrary + DigglesAnimController
    // to spawn ANY object and play ANY of its animation clips, for visual
    // verification of the whole runtime model/anim pipeline.
    //
    // Scene model (Animation_Test.unity): the spawned model lives under
    // `objectHolder` on the `AnimPreview` layer; a dedicated camera culls that
    // layer into a RenderTexture shown by a RawImage. This component only needs
    // the holder + that camera; the UI widgets are optional — if assigned it
    // populates/binds them, and every action is also a public method you can wire
    // to your own UGUI events in the Inspector.
    [DisallowMultipleComponent]
    public sealed class DigglesAnimTester : MonoBehaviour
    {
        [Header("Data")]
        [Tooltip("Root of the original game data/ directory (contains 3db/, Texture/, sphere.dat).")]
        public string dataRoot = "";

        [Header("Scene (auto-found by name if empty)")]
        public Transform? objectHolder;
        public Camera? previewCamera;
        public string previewLayerName = "AnimPreview";
        [Tooltip("Local rotation applied to the spawned model so it stands upright. " +
                 "Diggles models are authored head at mesh -Y, face at mesh +Z; " +
                 "(180,0,0) puts the head up (+Y) and the face toward -Z.")]
        public Vector3 modelEuler = new Vector3(180f, 0f, 0f);
        [Tooltip("Direction FROM the model TO the preview camera (the model is " +
                 "viewed from here). Default looks at the face from -Z, slightly above.")]
        public Vector3 cameraViewDir = new Vector3(0f, 0.25f, -1f);
        [Tooltip("How much empty margin around the framed model (1 = tight).")]
        public float cameraFillFactor = 1.4f;

        [Header("UI (optional — assign your widgets; dropdowns/text are TMP)")]
        [Tooltip("The RawImage showing the preview. Drag left/right over it to rotate the model.")]
        public RawImage? previewImage;
        [Tooltip("Degrees of yaw per pixel of horizontal drag over the preview.")]
        public float rotateSpeed = 0.4f;
        public TMP_InputField? objectFilter;
        public TMP_Dropdown? objectDropdown;
        public TMP_Dropdown? clipDropdown;
        public TMP_Dropdown? modeDropdown;
        public Slider? frameSlider;
        public Slider? buildStepSlider;
        public TMP_Text? infoText;
        public Button? playButton;
        public Button? stopButton;

        [Header("Behaviour")]
        [Tooltip("Max items shown in the object dropdown (there are ~2500 objects — " +
                 "a huge dropdown tanks FPS when opened). Use the filter to narrow.")]
        public int maxDropdownItems = 200;
        [Tooltip("Log clip/mesh load details to the Console (diagnose missing models).")]
        public bool verboseLog = true;
        [Tooltip("Force the preview camera's culling mask to the AnimPreview layer only.")]
        public bool setPreviewCulling = true;

        DigglesModelLibrary? _lib;
        GameObject? _model;
        DigglesAnimator? _animator;
        DigglesAnimController? _controller;

        IReadOnlyList<string> _objNames = System.Array.Empty<string>();
        readonly List<string> _shown = new();   // objects currently in the dropdown (filtered)
        IReadOnlyList<string> _clipNames = System.Array.Empty<string>();
        string _obj = "";
        string _clip = "";
        int _mode = 2; // LOOP
        float _yaw;    // accumulated drag rotation (degrees) around the vertical axis

        void Start()
        {
            if (objectHolder == null)
            {
                var go = GameObject.Find("ObjectHolder");
                if (go != null) objectHolder = go.transform;
            }
            if (previewCamera == null) previewCamera = FindPreviewCamera();

            // Force the preview camera to render ONLY the AnimPreview layer, so
            // the spawned model shows and stray scene objects (placeholder cubes
            // on other layers) don't. If the model fails to load the preview is
            // simply empty — which, with the Console diagnostics, makes the cause
            // obvious instead of showing a misleading cube.
            int pvLayer = LayerMask.NameToLayer(previewLayerName);
            if (setPreviewCulling && previewCamera != null && pvLayer >= 0)
                previewCamera.cullingMask = 1 << pvLayer;

            if (string.IsNullOrEmpty(dataRoot)) dataRoot = DigglesConfig.ResolveDataRoot("");
            _lib = new DigglesModelLibrary(dataRoot);
            _objNames = _lib.ObjectNames;

            PopulateDropdown(modeDropdown, new[] { "STILL", "ONCE", "LOOP" });
            if (modeDropdown != null) { modeDropdown.value = _mode; modeDropdown.RefreshShownValue(); }

            if (objectFilter != null) objectFilter.onValueChanged.AddListener(OnFilterChanged);
            if (objectDropdown != null) objectDropdown.onValueChanged.AddListener(OnObjectChanged);
            if (clipDropdown != null) clipDropdown.onValueChanged.AddListener(OnClipChanged);
            if (modeDropdown != null) modeDropdown.onValueChanged.AddListener(OnModeChanged);
            if (frameSlider != null) frameSlider.onValueChanged.AddListener(OnFrameChanged);
            if (buildStepSlider != null)
            {
                buildStepSlider.wholeNumbers = true;
                buildStepSlider.minValue = 0;
                buildStepSlider.maxValue = 10;
                buildStepSlider.onValueChanged.AddListener(OnBuildStepChanged);
            }
            if (playButton != null) playButton.onClick.AddListener(OnPlayClicked);
            if (stopButton != null) stopButton.onClick.AddListener(OnStopClicked);

            // Drag over the preview RawImage to rotate the model.
            if (previewImage != null)
            {
                previewImage.raycastTarget = true;
                var drag = previewImage.GetComponent<DigglesPreviewDragRotate>();
                if (drag == null) drag = previewImage.gameObject.AddComponent<DigglesPreviewDragRotate>();
                drag.tester = this;
            }

            ApplyObjectFilter("");
            if (_shown.Count > 0) SelectObject(0);
        }

        // ----- public API (bindable to UGUI events) ------------------------

        public IReadOnlyList<string> ObjectNames => _objNames;
        public IReadOnlyList<string> ClipNames => _clipNames;

        public void OnFilterChanged(string text) => ApplyObjectFilter(text);
        public void OnObjectChanged(int index) => SelectObject(index);
        public void OnClipChanged(int index) => SelectClip(index);
        public void OnModeChanged(int mode) { _mode = Mathf.Clamp(mode, 0, 2); PlayCurrent(); }
        public void OnPlayClicked() => PlayCurrent();
        public void OnStopClicked() { if (_controller != null) _controller.Idle(); }

        public void OnFrameChanged(float v)
        {
            if (_controller == null || string.IsNullOrEmpty(_clip)) return;
            _controller.PlayClip(_clip, DigglesAnimator.AnimMode.Still, Mathf.RoundToInt(v));
        }

        public void OnBuildStepChanged(float v)
        {
            if (_controller == null) return;
            _controller.SetBuildupStep(Mathf.RoundToInt(v), 10);
            UpdateInfo();
        }

        // Rebuild the object dropdown from a (case-insensitive substring) filter.
        // Capped to maxDropdownItems so the dropdown stays small and fast.
        public void ApplyObjectFilter(string text)
        {
            _shown.Clear();
            text = (text ?? "").Trim();
            for (int i = 0; i < _objNames.Count && _shown.Count < maxDropdownItems; i++)
            {
                var name = _objNames[i];
                if (text.Length == 0 ||
                    name.IndexOf(text, System.StringComparison.OrdinalIgnoreCase) >= 0)
                    _shown.Add(name);
            }
            PopulateDropdown(objectDropdown, _shown);
        }

        public void SelectObject(int index)
        {
            if (_lib == null || index < 0 || index >= _shown.Count) return;
            _obj = _shown[index];
            SpawnModel(_obj);
            _clipNames = _lib.GetClipNames(_obj);
            if (verboseLog)
                Debug.Log($"[AnimTester] obj '{_obj}' (db '{_lib.GetObjDb(_obj)}') → {_clipNames.Count} clips");
            PopulateDropdown(clipDropdown, _clipNames);
            if (_clipNames.Count > 0) SelectClip(0);
            else { _clip = ""; UpdateInfo(); }
        }

        public void SelectClip(int index)
        {
            if (index < 0 || index >= _clipNames.Count) return;
            _clip = _clipNames[index];
            PlayCurrent();
            int n = _animator != null ? _animator.CurrentFrameCount : 0;
            if (frameSlider != null)
            {
                frameSlider.wholeNumbers = true;
                frameSlider.minValue = 0;
                frameSlider.maxValue = Mathf.Max(0, n - 1);
                frameSlider.SetValueWithoutNotify(0);
            }
            FrameCamera();
            ApplyModelTransform();
            UpdateInfo();
        }

        // ----- internals ----------------------------------------------------

        // Called by the preview RawImage's drag handler. Horizontal drag yaws the
        // model around the vertical axis through its centre (spins in place, so
        // the framed camera keeps it centred).
        public void OnDragRotate(float screenDeltaX)
        {
            _yaw -= screenDeltaX * rotateSpeed;
            ApplyModelTransform();
        }

        void ApplyModelTransform()
        {
            if (_model == null) return;
            _model.transform.localPosition = Vector3.zero;
            // WORLD rotation — independent of any rotation on ObjectHolder (which
            // in this project often carries a Q_x(-90); a local rotation would
            // combine with it and lay the model on its side).
            _model.transform.rotation = Quaternion.Euler(modelEuler);
            if (Mathf.Abs(_yaw) < 0.001f) return;
            var mr = _animator != null ? _animator.GetComponent<MeshRenderer>() : null;
            var pivot = mr != null ? mr.bounds.center : _model.transform.position;
            _model.transform.RotateAround(pivot, Vector3.up, _yaw);
        }

        void PlayCurrent()
        {
            if (_controller == null || string.IsNullOrEmpty(_clip)) return;
            bool ok = _controller.PlayClip(_clip, (DigglesAnimator.AnimMode)_mode);
            if (verboseLog)
            {
                var mf = _model != null ? _model.GetComponent<MeshFilter>() : null;
                int verts = mf != null && mf.sharedMesh != null ? mf.sharedMesh.vertexCount : -1;
                int frames = _animator != null ? _animator.CurrentFrameCount : 0;
                if (!ok || verts <= 0)
                    Debug.LogWarning($"[AnimTester] '{_obj}.{_clip}' ok={ok} frames={frames} verts={verts} — clip/mesh failed to build (check dataRoot '{dataRoot}').");
                else
                    Debug.Log($"[AnimTester] '{_obj}.{_clip}' frames={frames} verts={verts}");
            }
            UpdateInfo();
        }

        void SpawnModel(string objName)
        {
            if (_model != null) Destroy(_model);
            // Clear any leftover children of the holder (e.g. the placeholder
            // Cube) so only the real preview model renders.
            if (objectHolder != null)
                for (int i = objectHolder.childCount - 1; i >= 0; i--)
                    Destroy(objectHolder.GetChild(i).gameObject);
            _model = new GameObject("PreviewModel_" + objName);
            if (objectHolder != null) _model.transform.SetParent(objectHolder, false);
            _model.transform.localPosition = Vector3.zero;
            _model.transform.rotation = Quaternion.Euler(modelEuler);  // world (see ApplyModelTransform)

            _model.AddComponent<MeshFilter>();
            var mr = _model.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _animator = _model.AddComponent<DigglesAnimator>();
            _controller = _model.AddComponent<DigglesAnimController>();
            _controller.Configure(_lib!, _animator, objName);

            int layer = LayerMask.NameToLayer(previewLayerName);
            if (layer >= 0) SetLayerRecursive(_model.transform, layer);
        }

        Camera? FindPreviewCamera()
        {
            // The preview camera is the one rendering into a RenderTexture.
            var cams = Object.FindObjectsByType<Camera>(FindObjectsSortMode.None);
            foreach (var c in cams) if (c.targetTexture != null) return c;
            var go = GameObject.Find("Camera");
            return go != null ? go.GetComponent<Camera>() : null;
        }

        void FrameCamera()
        {
            if (previewCamera == null || _animator == null) return;
            var mr = _animator.GetComponent<MeshRenderer>();
            if (mr == null) return;
            var b = mr.bounds;
            if (b.size == Vector3.zero) return;
            float radius = b.extents.magnitude;
            if (radius <= 0f) return;
            float fov = previewCamera.fieldOfView * Mathf.Deg2Rad;
            float dist = radius / Mathf.Max(0.01f, Mathf.Sin(fov * 0.5f)) * cameraFillFactor;
            var from = cameraViewDir.sqrMagnitude > 0.0001f ? cameraViewDir.normalized : Vector3.back;
            previewCamera.transform.position = b.center + from * dist;
            previewCamera.transform.LookAt(b.center, Vector3.up);
            previewCamera.nearClipPlane = Mathf.Max(0.01f, dist - radius * 3f);
            previewCamera.farClipPlane = dist + radius * 3f;
        }

        void UpdateInfo()
        {
            if (infoText == null) return;
            if (string.IsNullOrEmpty(_clip)) { infoText.text = _obj; return; }
            int n = _animator != null ? _animator.CurrentFrameCount : 0;
            var mo = _animator != null ? _animator.CurrentMotionOffset : Vector3.zero;
            infoText.text =
                $"{_obj}.{_clip}\n{n} frames | {n / 10f:0.0}s\nmotion {mo:0.0}";
        }

        static void PopulateDropdown(TMP_Dropdown? dd, IReadOnlyList<string> options)
        {
            if (dd == null) return;
            dd.ClearOptions();
            var list = new List<string>(options.Count);
            for (int i = 0; i < options.Count; i++) list.Add(options[i]);
            dd.AddOptions(list);
            dd.SetValueWithoutNotify(0);
            dd.RefreshShownValue();
        }

        static void SetLayerRecursive(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            for (int i = 0; i < t.childCount; i++) SetLayerRecursive(t.GetChild(i), layer);
        }

        void OnDestroy()
        {
            _lib?.ClearCaches();
        }
    }

    // Attached (automatically by DigglesAnimTester) to the preview RawImage.
    // Forwards horizontal drag to the tester to rotate the model. Requires an
    // EventSystem in the scene and raycastTarget enabled on the RawImage.
    [DisallowMultipleComponent]
    public sealed class DigglesPreviewDragRotate : MonoBehaviour, IDragHandler
    {
        public DigglesAnimTester? tester;

        public void OnDrag(PointerEventData eventData)
        {
            if (tester != null) tester.OnDragRotate(eventData.delta.x);
        }
    }
}
