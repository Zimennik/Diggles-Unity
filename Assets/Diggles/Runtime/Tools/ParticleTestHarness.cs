#nullable enable

namespace Diggles.Runtime
{
    using System.Collections.Generic;
    using Diggles.Runtime.Particles;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using UnityEngine;

    // Self-contained particle test bed. Builds a minimal engine context (no terrain/sim — just what the
    // presenter reads), spawns one emitter per particle setId (0..42) in a labelled grid, wires the REAL
    // ParticlePresenter, and frames an orbit camera on the grid. Lets you see ALL particle types at once
    // in Play and calibrate fpsRef/sizeScale/_Intensity without a campaign full of forges/workshops.
    // Drop on a GameObject and press Play, or use the editor menu "Diggles → Particle Test Scene".
    public sealed class ParticleTestHarness : MonoBehaviour
    {
        [Header("Data")]
        [Tooltip("Original game data root (for the 6 particle .tga atlases).")]
        [SerializeField] string dataRoot = "";

        [Header("Grid")]
        [SerializeField] int columns = 8;
        [SerializeField] float spacing = 6f;            // native units between cells
        [SerializeField] int maxParticles = 100;
        [SerializeField] int ratePerFrame = 3;
        [Tooltip("Base velocity given to every test source (native). Zero = each type's own motion shows.")]
        [SerializeField] Vector3 testDir = Vector3.zero;
        [SerializeField] bool showLabels = true;
        [Tooltip("Ground-collision family: drop a small collider plane this far below the marker (the particles raycast onto it).")]
        [SerializeField] float groundPlaneDrop = 3f;

        [Header("Camera")]
        [SerializeField] float orbitSpeed = 120f;
        [SerializeField] float zoomSpeed = 8f;

        DigglesTclContext? _ctx;
        ParticlePresenter? _presenter;
        Camera? _cam;
        readonly List<Transform> _labels = new();
        readonly List<int> _burstHandles = new();   // one-shot/burst sources, pulsed so their animation reads
        float _burstClock;
        Vector3 _focus;
        float _yaw = 35f, _pitch = 28f, _dist = 40f;

        // setIds that are one-shot bursts in game (brief activation). The test pulses them so the
        // flipbook animates instead of overlapping all frames into a static-looking composite.
        static bool IsBurst(int setId) => setId is 36 or 37 or 38 or 39 or 40;

        // Ground-collision family: the original samples the L2M terrain; in the bench we give each a small
        // collider plane below the marker so the Unity-collider raycast in CpuParticleEmitter has a surface.
        static bool IsGroundFamily(int setId) => setId is 1 or 8 or 10 or 11 or 15 or 17 or 18 or 26;

        static readonly string[] Names = {
            "ember","gndspark","emberglow","FIRE","swirl","inert","smoke","spray","dust","smoke",
            "droplet","FORGE","driftsmoke","puff","burst","gnddebris","cone","CHIPS","SPARKS","DUST",
            "dirt","mist","steam","greypuff","sheet","rope","CHIPS","meshsmoke","grysmk","grysmk",
            "grysmk","spiral","sparkle","ELF-AURA","grysmk","settledust","poofA","poofB","poofC","glow",
            "glow","ember","flame",
        };

        void Start()
        {
            // 1) WorldRoot — identical to DigglesWorldRuntime (the Q_x(-90) coordinate hack).
            var worldRoot = new GameObject("WorldRoot").transform;
            worldRoot.SetParent(transform, false);
            worldRoot.localRotation = Quaternion.Euler(270f, 0f, 0f);

            // 2) Minimal engine context — the presenter only reads Particles/Entities/DataRoot/SpawnParent.
            var interp = new TclInterpreter();
            if (string.IsNullOrEmpty(dataRoot)) dataRoot = DigglesConfig.ResolveDataRoot("");
            _ctx = new DigglesTclContext(interp) { DataRoot = dataRoot, SpawnParent = worldRoot };
            _ctx.Particles = new ParticleSystemState();

            // 3) One source per setId, in a grid.
            var cls = new DigglesClass { Name = "ParticleTest" };
            var bounds = new Bounds();
            bool first = true;
            int count = ParticleTypeTable.All.Length;
            for (int setId = 0; setId < count; setId++)
            {
                var type = ParticleTypeTable.Get(setId);
                int col = setId % columns, row = setId / columns;
                var nativePos = new Vector3(col * spacing, row * spacing, 0f);

                var marker = new GameObject($"set{setId}_{Name(setId)}").transform;
                marker.SetParent(worldRoot, false);
                marker.localPosition = RuntimeWorldSpawner.NativeToLocal(nativePos);

                var ent = new DigglesTclEntity { Class = cls, Position = nativePos, GameObject = marker.gameObject };
                int h = _ctx.RegisterEntity(ent);

                if (type.Dispatch != DispatchKind.Inert)
                {
                    // Feed the REAL Tcl spawn params (relpos/dir/max/num) where known, so each effect moves
                    // as in-game (the characteristic motion is often dir-driven). Filled in as types are ported.
                    var rc = RealCall(setId);
                    _ctx.Particles.Configure(h, 0, setId,
                        rc?.rel ?? Vector3.zero, rc?.dir ?? testDir,
                        rc?.max ?? maxParticles, rc?.num ?? ratePerFrame, false, -1, -1);
                    _ctx.Particles.SetActive(h, 0, true);
                    if (IsBurst(setId)) _burstHandles.Add(h);
                }

                if (showLabels) MakeLabel(marker, setId);
                if (IsGroundFamily(setId)) MakeGroundPlane(marker);

                var wp = marker.position;
                if (first) { bounds = new Bounds(wp, Vector3.zero); first = false; } else bounds.Encapsulate(wp);
            }

            // 4) The REAL presenter (call its [Inject] method directly — no DI scope needed here).
            _presenter = new GameObject("ParticlePresenter").AddComponent<ParticlePresenter>();
            _presenter.transform.SetParent(transform, false);
            _presenter.Construct(_ctx);

            // 5) Light, ground reference, camera framed on the grid.
            _focus = bounds.center;
            _dist = Mathf.Max(20f, bounds.size.magnitude * 1.1f + 8f);
            SetupLightAndGround(bounds);
            SetupCamera();
        }

        string Name(int setId) => setId >= 0 && setId < Names.Length ? Names[setId] : setId.ToString();

        // Real change_particlesource args from the original Tcl (relpos, dir, max, num/frame), per setId.
        // Filled in as each type is faithfully ported; null → use the harness defaults.
        static (Vector3 rel, Vector3 dir, int max, int num)? RealCall(int setId) => setId switch
        {
            0 => (new Vector3(0f, -0.1f, 0f), Vector3.zero, 32, 1),   // single_Minimal.tcl — forge embers
            1 => (Vector3.zero, new Vector3(0f, -0.2f, 0f), 64, 2),   // GROUND debris — bench dir down (no gravity, so it needs a downward dir to reach the plane + stick)
            2 => (Vector3.zero, Vector3.zero, 255, 8),                // static small-fireball case (dir 0)
            6 => (Vector3.zero, Vector3.zero, 128, 6),                // brauerei/dampfmaschine — chimney smoke (dir 0; rise from the -0.05 lift)
            8 => (Vector3.zero, Vector3.zero, 64, 4),                 // tempel "Blutfontänen" — dir {0,0,0}; dust drifts down via the spawn down-bias, sticks on the plane
            10 => (Vector3.zero, new Vector3(0f, -0.2f, 0f), 64, 1),  // GROUND droplet drip — dir down (vel.y=dir.y, no gravity); 1/frame, sticks on the plane
            11 => (Vector3.zero, Vector3.zero, 128, 8),               // brauerei/dampfmaschine "Rauch" smoke (dir 0; rises via the -0.12 lift)
            12 => (Vector3.zero, Vector3.zero, 4, 1),                 // pflanzen.tcl — grows in place (dir 0)
            13 => (Vector3.zero, Vector3.zero, 128, 1), // magie.tcl. Real dir {0,-0.1,0.1} is caster-relative (world-rotated in game); on the bare bench it just drifts +Z (depth). dir=0 shows the intrinsic fall: vel.y=sin(φ)·r ≥ 0 (down in Y-down) + the -0.005/frame drift. max bumped 16→128 for continuous trails
            15 => (Vector3.zero, new Vector3(0f, -0.3f, 0f), 64, 2),  // GROUND family — Unity-space dir (y<0 = down); drops fall onto the test plane below
            17 => (Vector3.zero, new Vector3(0f, -0.2f, 0f), 64, 2),  // schreinerei "Späne" Werkbank — single emitter, straight down; the spawn cone gives the spread. Falls, bounces, settles.
            18 => (Vector3.zero, new Vector3(0f, -0.15f, 0f), 128, 8),  // "Funken". Real game pairs two emitters {±0.1,-0.1,0} → a symmetric DOWN cone; one bench emitter would show only one diagonal side (sideways), so use straight-down here — the spawn jitter makes the narrow cone. Bounces on the plane.
            16 => (Vector3.zero, Vector3.zero, 256, 4),  // UNUSED in scripts. dir=0 → POINT spawn (radius=dir.x, column=dir.y both 0); the cone forms purely from the rising velocity + per-frame random walk
            19 => (Vector3.zero, new Vector3(2f, 2f, 4f), 1024, 8),   // single_Minimal F6 — Staub box+spread
            21 => (Vector3.zero, Vector3.zero, 64, 4),                // Industriebad/Luxusbad — bath mist (dir 0; max bumped 16→64)
            26 => (Vector3.zero, Vector3.zero, 64, 2),                // brauerei/feuerstelle "Späne auf dem Boden" — real dir {0,0,0} (debris near a surface); the added gravity drops them onto the plane
            22 => (Vector3.zero, Vector3.zero, 32, 8),                // UNUSED in scripts — test dir 0 (gated ~25%, so num 8 → ~2/frame)
            23 => (Vector3.zero, Vector3.zero, 64, 8),                // bath bubbles (dir 0; gated ~25%, num 8 → ~2/frame; rise from the -0.008 lift)
            28 => (Vector3.zero, Vector3.zero, 511, 8),               // drachenb.tcl — rising smoke (dir 0 in test)
            32 => (Vector3.zero, Vector3.zero, 256, 64),              // UNUSED in scripts — big flash (self-gated ~3.3%/frame; num 64/burst)
            36 => (Vector3.zero, Vector3.zero, 32, 1),                // z_methods energy bursts (one-shot in game)
            37 => (Vector3.zero, Vector3.zero, 32, 1),
            38 => (Vector3.zero, Vector3.zero, 32, 1),
            39 => (Vector3.zero, Vector3.zero, 8, 1),                 // long growing glows (one-shot)
            40 => (Vector3.zero, Vector3.zero, 8, 1),
            _ => null,
        };

        void MakeLabel(Transform marker, int setId)
        {
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var go = new GameObject($"label{setId}");
            go.transform.SetParent(marker, false);
            go.transform.localPosition = Vector3.zero;
            var tm = go.AddComponent<TextMesh>();
            tm.text = $"{setId}\n{Name(setId)}";
            tm.fontSize = 48;
            tm.characterSize = 0.04f;
            tm.anchor = TextAnchor.LowerCenter;
            tm.alignment = TextAlignment.Center;
            tm.color = ParticleTypeTable.Get(setId).Dispatch == DispatchKind.Inert ? Color.grey : Color.white;
            if (font != null) { tm.font = font; go.GetComponent<MeshRenderer>().sharedMaterial = font.material; }
            _labels.Add(go.transform);
        }

        // A small horizontal collider plane below a ground-family marker, so the emitter's downward
        // raycast (CpuParticleEmitter.GroundUnder) has a surface to land particles on. Uses a thin
        // BoxCollider (reliable from either side) + a flat quad for the visual.
        void MakeGroundPlane(Transform marker)
        {
            var plane = GameObject.CreatePrimitive(PrimitiveType.Quad);
            plane.name = $"groundplane_{marker.name}";
            var mc = plane.GetComponent<Collider>(); if (mc != null) Destroy(mc);   // drop the single-sided MeshCollider
            var box = plane.AddComponent<BoxCollider>();
            box.size = new Vector3(1f, 1f, 0.05f);                                    // thin slab along the quad normal
            plane.transform.SetParent(transform, false);
            plane.transform.position = marker.position + Vector3.down * groundPlaneDrop;
            plane.transform.rotation = Quaternion.Euler(-90f, 0f, 0f);               // lie flat, face up
            plane.transform.localScale = Vector3.one * (spacing * 0.85f);
            var sh = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            var mat = new Material(sh) { color = new Color(0.22f, 0.20f, 0.16f) };
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", new Color(0.22f, 0.20f, 0.16f));
            plane.GetComponent<MeshRenderer>().sharedMaterial = mat;
        }

        void SetupLightAndGround(Bounds bounds)
        {
            var lightGo = new GameObject("Sun");
            var l = lightGo.AddComponent<Light>();
            l.type = LightType.Directional; l.intensity = 1f;
            lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            // A neutral backdrop quad behind the grid so additive sprites read against something dark.
            var bg = GameObject.CreatePrimitive(PrimitiveType.Quad);
            bg.name = "Backdrop";
            var col = bg.GetComponent<Collider>(); if (col != null) Destroy(col);
            bg.transform.position = bounds.center;
            bg.transform.localScale = Vector3.one * (bounds.size.magnitude + 20f);
            var sh = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            var mat = new Material(sh) { color = new Color(0.06f, 0.06f, 0.08f) };
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", new Color(0.06f, 0.06f, 0.08f));
            bg.GetComponent<MeshRenderer>().sharedMaterial = mat;
        }

        void SetupCamera()
        {
            var go = new GameObject("TestCamera");
            _cam = go.AddComponent<Camera>();
            _cam.backgroundColor = new Color(0.04f, 0.04f, 0.05f);
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.fieldOfView = 40f;
            ApplyCamera();
        }

        void ApplyCamera()
        {
            if (_cam == null) return;
            var rot = Quaternion.Euler(_pitch, _yaw, 0f);
            _cam.transform.position = _focus - rot * Vector3.forward * _dist;
            _cam.transform.rotation = rot;
            // keep the backdrop behind the grid relative to the camera
            var bg = GameObject.Find("Backdrop");
            if (bg != null) bg.transform.rotation = Quaternion.LookRotation(_focus - _cam.transform.position);
        }

        void Update()
        {
            // Pulse one-shot/burst sources so their flipbook plays as a discrete burst (in game these
            // are triggered, not held active). Runs in Update so it lands before the presenter's LateUpdate.
            if (_ctx?.Particles == null || _burstHandles.Count == 0) return;
            const float period = 1.4f, window = 0.05f;   // window < 1/fpsRef → at most ONE emit step per
                                                          // period → a single synchronized burst (no
                                                          // overlapping offset batches). Occasional skip is fine.
            _burstClock += Time.deltaTime;
            if (_burstClock >= period) _burstClock -= period;
            bool on = _burstClock < window;
            foreach (var h in _burstHandles) _ctx.Particles.SetActive(h, 0, on);
        }

        void LateUpdate()
        {
            // Orbit: hold left mouse to rotate, scroll to zoom. Guarded so a project on the new Input
            // System (where legacy UnityEngine.Input throws) just falls back to the framed static camera.
            try
            {
                if (Input.GetMouseButton(0))
                {
                    _yaw += Input.GetAxis("Mouse X") * orbitSpeed * Time.deltaTime;
                    _pitch = Mathf.Clamp(_pitch - Input.GetAxis("Mouse Y") * orbitSpeed * Time.deltaTime, -85f, 85f);
                }
                _dist = Mathf.Max(5f, _dist - Input.mouseScrollDelta.y * zoomSpeed);
            }
            catch (System.Exception) { /* legacy Input disabled — static camera */ }
            ApplyCamera();

            // Billboard the labels toward the camera.
            if (_cam != null)
                foreach (var t in _labels)
                    if (t != null) t.rotation = Quaternion.LookRotation(t.position - _cam.transform.position);
        }

        void OnGUI()
        {
            GUI.color = Color.white;
            GUILayout.BeginArea(new Rect(8, 8, 460, 130), GUI.skin.box);
            GUILayout.Label("PARTICLE TEST BED — all 43 setIds in a grid (label = setId + effect). Faithful CPU engine.");
            GUILayout.Label("Drag = orbit · scroll = zoom.");
            GUILayout.Label("Calibrate in Play: select the runtime 'ParticlePresenter' → fpsRef / sizeScale / intensity / atlasVFlip.");
            GUILayout.Label("swirl/spiral/ground/flock/rope are first-pass approximations — to be tightened against the decompile.");
            GUILayout.EndArea();
        }
    }
}
