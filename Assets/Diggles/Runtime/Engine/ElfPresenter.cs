#nullable enable

namespace Diggles.Runtime
{
    using Diggles.Runtime.Tcl.Engine;
    using UnityEngine;
    using VContainer;
    using VContainer.Unity;

    // Renders the tutorial/narrator elf from the faithful ElfState core (native CElf::Draw,
    // FUN_0047d000) and wires the state's screen/camera callbacks to the real scene. Scene-placed
    // (on DigglesControllers), injected with the context. Owns the elf model: pre-spawns the
    // 'elfe'.3db once (hidden) in IStartable.Start and drives it every frame.
    //
    // Faithful rules:
    // * Renders whenever state.Visible — including outside cutscenes: the tutorial drives her in
    // normal gameplay (Trigger_Tutorial.tcl elf_action). No SequenceActive gate.
    // * No yaw rotation, ever. The native orientation is the lean-angle triple at +0x5c (base =
    // facing the screen); flight direction is conveyed by the directional clips. Leans are
    // applied only behind a default-off flag (open question: the native angle->matrix
    // convention, FUN_00488550, is un-RE'd — leans=0 is strictly more correct than a guessed
    // mapping).
    // * Model scale 2.0 (FUN_0047d000), standard native->world mapping (drawZ = z*0.5+7).
    // * Anim: one DigglesAnimator clip per state phase — re-issued as Once on every state clip
    // start (ClipSerial); the state machine owns start->loop->end sequencing at 10 fps.
    // * texvar: body material 'Elfen_Koerper' -> 'Elfen_Koerper_variante_{n}.tga' (head slot
    // 'Elfen_Kopf' untouched), via a per-instance material clone re-asserted after anim swaps
    // (same pattern as DigglesTextureAnimator).
    // * lockview / movescreen-no-arg go through the state's view-focus callbacks onto
    // DigglesCameraController.FocusX/FocusY (the 0.02/0.98 lerp runs in the 200 Hz state tick).
    // * Speech anchor (speechicon channel 0): native pos - (0, 0.6, 0) — exposed as a world
    // position for the UI layer.
    // * Sparkles: the native CElf owns two CParticles sources of setId 13 ("magic sparkles with
    // comet trails", FUN_004aa180) — these are particle sources, not light-buffer lights:
    // FUN_00427cf0 configures them via the CParticles
    // singleton (FUN_00492250, source array stride 0x88) with FUN_00495940(handle, type=13,
    // dir, count=150+1 pool, 2/frame, active=1); FUN_00427a70 re-pins them every tick to the
    // model's link points 0 and 1 (frame+0x5c + i*4 = link-table slots — the hands), and
    // Reset (FUN_00427610) frees them (FUN_004957e0). The drift dir is per-flight-anim
    // (FUN_00428630). Ported as two WorldAnchored ParticleSystemState sources updated here
    // each frame and freed on hide.
    //
    // Execution order (200): runs after CutsceneCameraDriver (100), which poses the cutscene
    // camera synchronously each frame, so the elf reads the same-frame camera pose.
    [DefaultExecutionOrder(200)]
    public sealed class ElfPresenter : MonoBehaviour, IStartable, IRuntimeSessionPresentationParticipant
    {
        [Tooltip("Native CElf::Draw model scale (FUN_0047d000) = 2.0.")]
        [SerializeField] float modelScale = 2f;
        [Header("Lean angles (open Q1 — native convention unverified; ship OFF)")]
        [Tooltip("Apply the state's leanA/leanB as small rotations. The native angle->matrix " +
                 "convention (FUN_00488550) is not RE'd yet, so this ships OFF: a fixed " +
                 "screen-facing pose is faithful to within the (small, clamped) lean angles.")]
        [SerializeField] bool applyLeans;
        [Tooltip("Degrees per native lean unit when applyLeans is on (tune in Play).")]
        [SerializeField] float leanDegreesPerUnit = 10f;

        DigglesTclContext? _ctx;

        [UnityEngine.Scripting.Preserve]
        [Inject]
        public void Construct(IActiveGameSession sessions) => BindSession(sessions.Current);

        GameObject? _elfModel;
        IDigglesEntitySpawner? _elfSpawner;
        Transform? _worldRoot;
        Camera? _camera;
        DigglesCameraController? _cameraController;
        bool _camResolved;

        ElfState? _wiredState;
        DigglesAnimator? _animator;
        Transform? _innerModel;
        int _lastClipSerial = int.MinValue;
        int _lastTexVar = int.MinValue;
        int _lastMatVersion = int.MinValue;
        Material? _bodyClone;
        bool _sparklesLive;

        // ---- native CElf sparkle-source constants (FUN_00427cf0 → FUN_00495940) ----
        // Source key: the pre-spawned elf model's sentinel handle (the elf is not in ctx.Entities,
        // so the sources are WorldAnchored and we feed them absolute native positions).
        const int SparkleHandle = -999;
        const int SparkleSetId = 13;     // FUN_00427cf0: type arg 0xd = "magic sparkles with comet trails"
        const int SparklePool = 151;     // FUN_00495940: count arg 0x96 (150) + 1 pool entries
        const int SparkleRate = 2;       // source+0x50 = 2 spawns/frame

        // Where the elf's speech bubble (speechicon channel 0) anchors this frame: native
        // pos - (0, 0.6, 0) (0.6 above her head, FUN_004d2420) mapped to world. Null while hidden.
        public Vector3? SpeechAnchorWorld { get; private set; }

        public void BindSession(RuntimeGameSession session)
        {
            _ctx = session.Engine.Ctx;
            _elfSpawner = _ctx.EntitySpawner;
            _worldRoot = _ctx.SpawnParent;
            _elfModel = null;
            _wiredState = null;
            _animator = null;
            _innerModel = null;
            _bodyClone = null;
            _sparklesLive = false;
            SpeechAnchorWorld = null;
            _lastClipSerial = int.MinValue;
            _lastTexVar = int.MinValue;
            _lastMatVersion = int.MinValue;
            _camera = null;
            _cameraController = null;
            _camResolved = false;
            EnsureModel();
        }

        // Pre-spawn one elf model under each session content root. Retired roots retain their model,
        // so a failed candidate activation can rebind the old session without reviving destroyed
        // presentation objects.
        void IStartable.Start() => EnsureModel();

        void EnsureModel()
        {
            if (_elfModel != null || _ctx == null) return;
            _elfSpawner = _ctx.EntitySpawner;
            _worldRoot = _ctx.SpawnParent;
            if (_elfSpawner == null || _worldRoot == null) return;
            Transform? existing = _worldRoot.Find("CutsceneElf");
            if (existing != null)
            {
                _elfModel = existing.gameObject;
                return;
            }
            try
            {
                // elfe.3db's hover clip is "standanim" (there is NO "standard"); spawn with it so a
                // real mesh builds (else the loader can't build frame-0 -> wireframe fallback).
                var elfEnt = new DigglesTclEntity { Handle = -999, Class = new DigglesClass { Name = "elfe", DefaultAnim = "elfe.standanim", DefaultAnimMode = 2 } };
                var elfGo = _elfSpawner.Spawn(elfEnt, _worldRoot);
                if (elfGo != null)
                {
                    // DigglesWorldRuntime keeps this exact name alive across world rebuilds.
                    elfGo.name = "CutsceneElf";
                    // Native draw scale 2.0 — applied to the inner Model so the outer transform
                    // (used for positioning) stays unit.
                    if (modelScale > 0f && !Mathf.Approximately(modelScale, 1f))
                    {
                        var inner = elfGo.transform.Find("Model");
                        if (inner != null) inner.localScale *= modelScale;
                    }
                    elfGo.SetActive(false);
                    _elfModel = elfGo;
                }
                else Debug.LogWarning("[elf] model spawn returned null");
            }
            catch (System.Exception e) { Debug.LogWarning($"[elf] model spawn failed: {e.Message}"); }
        }

        void LateUpdate()
        {
            var elf = _ctx?.Elf;
            if (elf == null || _elfModel == null) return;
            ResolveCamera();
            WireState(elf);

            // Lifecycle: visibility = state.Visible only (tutorial drives her outside cutscenes).
            if (_elfModel.activeSelf != elf.Visible)
            {
                _elfModel.SetActive(elf.Visible);
                // Re-shown: force the current clip back onto the animator (clip starts that
                // happened while hidden never reached it).
                if (elf.Visible) _lastClipSerial = int.MinValue;
            }
            if (!elf.Visible)
            {
                SpeechAnchorWorld = null;
                FreeSparkles();        // native Reset (FUN_00427610) frees both sources on hide
                return;
            }

            // Position: standard native->world mapping (drawZ = z*0.5+7), same as every entity.
            _elfSpawner?.ApplyPosition(_elfModel, elf.Position);

            ApplyFacing(elf);
            ApplyAnim(elf);
            ApplyTexVariant(elf);
            UpdateSparkles(elf);

            SpeechAnchorWorld = _worldRoot != null
                ? (Vector3?)_worldRoot.TransformPoint(RuntimeWorldSpawner.NativeToLocal(
                      elf.Position - new Vector3(0f, 0.6f, 0f)))
                : null;
        }

        // The camera controller is created by DigglesWorldRuntime after the scope builds, so
        // resolve it lazily (cached).
        void ResolveCamera()
        {
            if (_camResolved && _camera != null) return;
            _camera = Camera.main;
            _cameraController = _camera != null ? _camera.GetComponent<DigglesCameraController>() : null;
            if (_camera != null) _camResolved = true;
        }

        // Wire the pure-logic state's host callbacks to the real scene (once per state instance —
        // a new ElfState appears when the engine re-registers).
        void WireState(ElfState elf)
        {
            if (ReferenceEquals(_wiredState, elf)) return;
            _wiredState = elf;
            _lastClipSerial = int.MinValue;
            _lastTexVar = int.MinValue;

            elf.IsOnScreen = IsOnScreenNative;
            elf.UnprojectScreenToNative = UnprojectScreenToNative;
            elf.ScreenSize = () => new Vector2(Screen.width, Screen.height);
            elf.GetViewFocus = () => _cameraController != null
                ? new Vector2(_cameraController.FocusX, _cameraController.FocusY)
                : Vector2.zero;
            elf.SetViewFocus = f =>
            {
                if (_cameraController == null) return;
                _cameraController.FocusX = f.x;
                _cameraController.FocusY = f.y;
            };
            // The port camera has no live pan-velocity state (input writes Focus directly), so the
            // pan-follow lean input stays 0 and lockview's x0.9 decay is a no-op — leave unwired.
            elf.ResolveObjPos = id => _ctx?.GetEntity(id.ToString())?.Position;
            elf.IsObjAlive = id => _ctx != null && _ctx.Entities.ContainsKey(id);
            elf.GetClipFrameCount = clip =>
            {
                int n = _ctx?.ModelLibrary?.GetClipFrameCount("elfe", clip) ?? 0;
                return n > 0 ? n : 10;     // unknown clip: keep the state machine moving at ~1 s
            };
        }

        // ---- screen callbacks (FUN_00428a70 / FUN_00479150 equivalents) ----

        // Project the native point; true if within the screen rect grown by marginPx per side.
        // Native margins are authored against the original window — scale by Screen/800x600.
        bool IsOnScreenNative(Vector3 nativePos, int marginPx)
        {
            if (_camera == null || _worldRoot == null) return false;
            Vector3 world = _worldRoot.TransformPoint(RuntimeWorldSpawner.NativeToLocal(nativePos));
            Vector3 sp = _camera.WorldToScreenPoint(world);
            if (sp.z <= 0f) return false;                       // behind the camera
            float mx = marginPx * Screen.width / 800f;
            float my = marginPx * Screen.height / 600f;
            return sp.x >= -mx && sp.x <= Screen.width + mx
                && sp.y >= -my && sp.y <= Screen.height + my;
        }

        // Screen point (real pixels, native top-left origin) onto the world plane of constant
        // native z = depthZ (the native unproject targets the plane z≈16 — not a camera-distance
        // fraction; an earlier presenter used "fraction of camera->ground depth", which was wrong).
        Vector3 UnprojectScreenToNative(float x, float y, float depthZ)
        {
            if (_camera == null || _worldRoot == null) return new Vector3(x, y, depthZ);
            Ray r = _camera.ScreenPointToRay(new Vector3(x, Screen.height - y, 0f));
            // Native z maps to WorldRoot-local height y = z*0.5+7; the plane normal is local +Y.
            Vector3 planePoint = _worldRoot.TransformPoint(RuntimeWorldSpawner.NativeToLocal(new Vector3(0f, 0f, depthZ)));
            Vector3 planeNormal = _worldRoot.TransformDirection(Vector3.up);
            float denom = Vector3.Dot(r.direction, planeNormal);
            Vector3 world = Mathf.Abs(denom) < 1e-5f
                ? r.origin + r.direction * 10f                  // grazing ray: degrade gracefully
                : r.origin + r.direction * (Vector3.Dot(planePoint - r.origin, planeNormal) / denom);
            Vector3 native = RuntimeWorldSpawner.LocalToNative(_worldRoot.InverseTransformPoint(world));
            return new Vector3(native.x, native.y, depthZ);     // pin the depth exactly
        }

        // ---- facing: fixed screen-facing base, optional leans ----
        void ApplyFacing(ElfState elf)
        {
            var t = _elfModel!.transform;
            if (!applyLeans || _camera == null || _worldRoot == null)
            {
                // Native rotation (0,0,0): the elfe model's authored pose faces the viewer.
                if (t.localRotation != Quaternion.identity) t.localRotation = Quaternion.identity;
                return;
            }
            // Open Q1 (unverified convention): leanB as yaw about world-up, leanA as pitch about
            // the screen-x (camera right) axis, scaled by leanDegreesPerUnit. Default OFF.
            Quaternion lean =
                Quaternion.AngleAxis(elf.LeanB * leanDegreesPerUnit, Vector3.up)
 * Quaternion.AngleAxis(elf.LeanA * leanDegreesPerUnit, _camera.transform.right);
            t.rotation = lean * _worldRoot.rotation;
        }

        // ---- anim: re-issue the state's clip on everystart ----
        void ApplyAnim(ElfState elf)
        {
            if (elf.ClipSerial == _lastClipSerial) return;
            _lastClipSerial = elf.ClipSerial;
            if (elf.ModelAnim == null) return;
            // Once, always: the state machine owns looping (it restarts the loop clip at each clip
            // end) and phase sequencing (start -> loop -> end outros, one-shot auto-return).
            _elfSpawner?.ApplyAnim(_elfModel!, "elfe." + elf.ModelAnim, 0, DigglesAnimator.AnimMode.Once);
            if (CutsceneTrace.Enabled)
                CutsceneTrace.Log($"elf: clip=elfe.{elf.ModelAnim} phase={elf.AnimPhase} " +
                    $"anim={(elf.CurAnimIdx >= 0 ? ElfState.Anims[elf.CurAnimIdx].Name : "-")} " +
                    $"mode={elf.Mode} natpos={elf.Position} vis={elf.Visible}");
        }

        // ---- texvar: body texture variant (native renderer variant list {texvar, 0}) ----
        void ApplyTexVariant(ElfState elf)
        {
            if (_elfModel == null) return;
            if (_animator == null) _animator = _elfModel.GetComponentInChildren<DigglesAnimator>();
            var mr = _elfModel.GetComponentInChildren<MeshRenderer>();
            if (mr == null) return;
            int matVersion = _animator != null ? _animator.MaterialVersion : 0;
            if (elf.TexVariant == _lastTexVar && matVersion == _lastMatVersion) return;
            _lastTexVar = elf.TexVariant;
            _lastMatVersion = matVersion;

            // Re-assert our per-instance clone into every body slot (DigglesAnimator.SetAnim
            // re-assigns the shared materials per clip — same pattern as DigglesTextureAnimator).
            var mats = mr.sharedMaterials;
            bool changed = false;
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                if (m == null) continue;
                bool isBody = m.name.IndexOf("Elfen_Koerper", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || ReferenceEquals(m, _bodyClone);
                if (!isBody) continue;        // slot 1 'Elfen_Kopf' stays base
                if (_bodyClone == null) _bodyClone = new Material(m) { name = m.name + " (texvar)" };
                if (!ReferenceEquals(m, _bodyClone)) { mats[i] = _bodyClone; changed = true; }
            }
            if (changed) mr.sharedMaterials = mats;
            if (_bodyClone == null) return;

            // Variant 0 = the base Elfen_Koerper.tga; n>0 = Elfen_Koerper_variante_{n}.tga. Loaded
            // through the library's texture cache (GetVariantTexture with no registered def_texture
            // list resolves index 0 = the named TGA — the same path the ZFrau_ reskins load by).
            var lib = _ctx?.ModelLibrary;
            if (lib == null) return;
            string texName = elf.TexVariant <= 0 ? "Elfen_Koerper" : $"Elfen_Koerper_variante_{elf.TexVariant}";
            var tex = lib.GetVariantTexture(texName, 0);
            if (tex == null && elf.TexVariant > 0)               // missing variant file: keep base
                tex = lib.GetVariantTexture("Elfen_Koerper", 0);
            if (tex == null) return;
            if (_bodyClone.HasProperty("_BaseMap")) _bodyClone.SetTexture("_BaseMap", tex);
            else if (_bodyClone.HasProperty("_MainTex")) _bodyClone.SetTexture("_MainTex", tex);
        }

        // ---- sparkles: two setId-13 sources riding link points 0/1 (the hands) ----

        // Re-pin both sources to the model's current link-point world positions (the native does
        // this every 5 ms tick in FUN_00427a70; per render frame is the same to within a frame)
        // and re-assert the native source params. Configure is idempotent, so doing it per frame
        // matches the native's re-arm on every anim start/loop restart.
        void UpdateSparkles(ElfState elf)
        {
            var ps = _ctx?.Particles;
            if (ps == null || _worldRoot == null || _elfModel == null) return;
            if (_innerModel == null) _innerModel = _elfModel.transform.Find("Model");
            if (_animator == null) _animator = _elfModel.GetComponentInChildren<DigglesAnimator>();
            Vector3 dir = SparkleDir(elf);
            for (int link = 0; link < 2; link++)
            {
                // FUN_00427a70 reads frame+0x5c + link*4 = link-table slots 0 and 1 (same table
                // get_linkpos/hats use; +0x5c is its base, so these are link ids 0/1 = the hands).
                Vector3 world =
                    _innerModel != null && _animator != null && _animator.TryGetLinkOffset(link, out var off)
                        ? _innerModel.TransformPoint(off)
                        : _elfModel.transform.position;       // no link data: sparkle at her center
                Vector3 native = RuntimeWorldSpawner.LocalToNative(_worldRoot.InverseTransformPoint(world));
                var s = ps.Configure(SparkleHandle, link, SparkleSetId, native, dir,
                    SparklePool, SparkleRate, false, -1, -1);
                s.WorldAnchored = true;
                s.Active = true;                              // FUN_00495940 active byte (+0x64) = 1
            }
            _sparklesLive = true;
        }

        void FreeSparkles()
        {
            if (!_sparklesLive) return;
            _sparklesLive = false;
            var ps = _ctx?.Particles;
            if (ps == null) return;
            ps.Free(SparkleHandle, 0);
            ps.Free(SparkleHandle, 1);
        }

        void OnDestroy() => FreeSparkles();

        // Per-flight-anim sparkle drift, native frame (x = screen-right, y = screen-down; the
        // emitter's ToUnity flips y). FUN_00428630: the per-direction vector is armed when the
        // loop clipstarts; the start/outro paths (FUN_004284d0 / FUN_00428580) always arm
        // (0, -0.05, 0). Constants read from Diggles.exe: DAT_00737720 = -0.05,
        // DAT_00737724 = -0.10, DAT_00737010 = +0.05; note move_l/move_r keep the default
        // y = -0.05 (the decompile only overwrites x on those branches).
        static Vector3 SparkleDir(ElfState elf)
        {
            if (elf.AnimPhase != 1) return new Vector3(0f, -0.05f, 0f);
            switch (elf.CurAnimIdx)
            {
                case 1: return Vector3.zero;                    // move_u: no drift
                case 2: return new Vector3(0f, -0.10f, 0f);     // move_d: strong up-drift
                case 3: return new Vector3(0.05f, -0.05f, 0f);  // move_l: trail right+up
                case 4: return new Vector3(-0.05f, -0.05f, 0f); // move_r: trail left+up
                case 5: return new Vector3(0f, 0.05f, 0f);      // fast-up: trail down
                default: return new Vector3(0f, -0.05f, 0f);    // standard / one-shots: drift up
            }
        }
    }
}
