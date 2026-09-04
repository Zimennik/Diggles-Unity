#nullable enable

namespace Diggles.Runtime.Particles
{
    using System.Collections.Generic;
    using Diggles.Runtime;
    using UnityEngine;

    // One particle source as a faithful CPU integrator (the original's per-emitter sub-pool + the
    // hand-coded per-type update). NOT a Unity ParticleSystem — Unity only renders the billboard mesh
    // we build here. Per Step (= one original render frame) it runs three phases, exactly like the
    // decompiled type functions:
    // A) emitter random-walk drift (damped accumulator coupled into particle accel)
    // B) spawn ~num/frame (shape-spread position, dir+jitter velocity, age 0, atlas cell)
    // C) integrate live (pos += vel; gravity/buoyancy/drag; colour from age; age++; kill)
    // Simulation is in WORLD space with identity coordinate mapping (gravity acts on ±Y, matching
    // Unity), so relpos/dir are used as-is — the coord bug the Shuriken pass fought is gone by design.
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class CpuParticleEmitter : MonoBehaviour
    {
        // Mirrors the original 0x44 particle struct (the fields the renderer + integrator touch).
        struct P
        {
            public Vector3 Pos;     // +00 world position
            public Vector3 Vel;     // +0c per-frame velocity
            public Vector3 Aux;     // spawn origin / phase data for swirl/spiral/flock/ground
            public float Size;      // +28 current billboard size (native units; ×sizeScale at draw)
            public float Base;      // spawn size (after SizeJitter) — the size curve scales this
            public int Age;         // +2c frames since birth
            public float Spin;      // +30 size/spin rate accumulator
            public Color32 Color;   // +34 diffuse (RGB carries the fade for faithful types)
            public Color32 Spec;    // +38 specular — added after the texture: tex*diffuse + specular
            public int Cell;        // +3c atlas cell, packed u | v<<16
            public byte Bill;       // +40 0 = screen-aligned, else velocity-oriented
            public int Parent;      // +24 (setId 13 only) trail-child's parent particle index; 0 = main spark
            public bool Alive;
        }

        ParticleType _type = null!;
        ParticleSystemState.Source? _src;
        ParticleLcg _rng;

        P[] _pool = System.Array.Empty<P>();
        int _capacity;
        int _high;                 // high-water of used slots
        Vector3 _drift;            // emitter random-walk accumulator (+24/28/2c)
        Vector3 _worldPos;         // emitter world position (entity + relpos)
        bool _active;
        bool _paused;              // fog/terrain gate — wired from the world later; false in the test bed

        // Ground-collision family (setId 1,8,10,11,15,17,18,26): the original samples the L2M terrain
        // heightfield (FUN_00592f10). Per the Unity-collider approach we instead raycast straight DOWN
        // against these layers — works in the bench (test planes) and in a real scene (terrain/mesh
        // colliders). UNLIKE fire/smoke, this family uses `dir` DIRECTLY in Unity space (NO ToUnity):
        // empirically dir.y<0 = Unity DOWN here (sparks/drops fall — the real "Funken" dirs {±0.1,-0.1,0}
        // pour down), gravity is Unity −Y, raycast is Unity down. (The orig is a separate Z=altitude terrain
        // subsystem; once it's replaced by colliders, dir maps straight to Unity with y as the vertical.)
        public LayerMask GroundMask = ~0;
        // Start the ray only just ABOVE the particle: enough to catch a single step's fall overshoot, but
        // NOT so high it hits a collider above the particle. (In the bench, ground-family test planes stack
        // in a column — same x/z, different Y — so a tall up-offset would make a spark grab a sibling
        // plane ABOVE it and "land" at spawn. Keep this < the gap between stacked planes.)
        const float GroundProbeUp = 1f;
        const float GroundProbeReach = 8f;  // total ray length (finds the nearest collider below)

        Mesh _mesh = null!;
        bool _meshEmpty = true;    // billboard mesh currently holds no geometry (skip redundant Clear)
        MeshFilter _mf = null!;
        MeshRenderer _mr = null!;
        Transform? _entity;        // for MeshVerts emission (objverts)
        Mesh? _entityMesh;
        Vector3[]? _entityVerts;   // cached copy — Mesh.vertices ALLOCATES a fresh array per call;
                                   // re-copied only when the mesh reference changes
        bool[]? _cellNonEmpty;     // per-cell non-empty map at this type's grid (skip blank/gutter cells)
        int[] _allowedCells = System.Array.Empty<int>();   // RandomCell candidates (band ∩ non-empty)

        // Reused mesh build buffers.
        readonly List<Vector3> _verts = new();
        readonly List<Color32> _cols = new();
        readonly List<Vector2> _uvs = new();
        readonly List<Vector3> _specs = new();   // per-vertex specular rgb → shader TEXCOORD1 (tex*diffuse + spec)
        readonly List<Vector2> _corners = new(); // per-vertex 2D corner offset → shader TEXCOORD2 (view-space billboard)
        readonly List<int> _tris = new();

        const float DriftDamping = 0.9f;        // DAT_00737108
        const float BuoyancyForce = 0.003f;     // gentle +Y rise (the original lift is tiny ~0.0008/frame;
                                                // 0.04 made fireballs shoot up ~15u as a "stream")
        const float DriftCouple = 0.03f;        // DAT_00737034 — emitter drift → particle accel (was 0.1 = too sideways)
        const float GroundDrop = 3f;            // test-bed floor below the emitter (no L2M terrain here)
        const float GravityScale = 2.5f;        // global: the per-frame gravity consts read too weak (slow fall)
        const float SwayAmp = 0.03f;            // horizontal bob amplitude for Sway glyphs (sleep Z / note)

        public int AliveCount { get; private set; }
        public int SetId => _type?.SetId ?? -1;

        void Awake()
        {
            _mf = GetComponent<MeshFilter>();
            _mr = GetComponent<MeshRenderer>();
            _mesh = new Mesh { name = "DigglesParticles" };
            _mesh.MarkDynamic();
            _mf.sharedMesh = _mesh;
            _mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _mr.receiveShadows = false;
            _mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            _mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        }

        public void Configure(ParticleType type, ParticleSystemState.Source src, Transform? entity, Mesh? entityMesh,
            bool[]? cellNonEmpty = null)
        {
            bool typeChanged = _type == null || _type.SetId != type.SetId;
            bool mapChanged = !ReferenceEquals(_cellNonEmpty, cellNonEmpty);
            _type = type;
            _src = src;
            _entity = entity;
            if (!ReferenceEquals(_entityMesh, entityMesh))
            {
                _entityMesh = entityMesh;
                _entityVerts = null;   // invalidate the cached vertex copy
            }
            _cellNonEmpty = cellNonEmpty;
            if (typeChanged)
            {
                _rng = new ParticleLcg((uint)((type.SetId + 1) * 2654435761u));
                _capacity = Mathf.Clamp(src.MaxParticles > 0 ? src.MaxParticles : 256, 8, 4096);
                _pool = new P[_capacity];
                _high = 0;
                _drift = Vector3.zero;
            }
            if (typeChanged || mapChanged) BuildAllowedCells();
        }

        // Precompute the RandomCell candidate cells = the type's sprite band [AtlasRow0, +AtlasRows)
        // intersected with the non-empty cells (so particles never sample blank gutters or the digit
        // rows that sit outside the band). Falls back to the whole band if the map is unavailable.
        void BuildAllowedCells()
        {
            int grid = Mathf.Max(1, _type.AtlasGrid);
            int rows = _type.AtlasRows > 0 ? Mathf.Min(_type.AtlasRows, grid) : grid;
            int cols = _type.AtlasCols > 0 ? Mathf.Min(_type.AtlasCols, grid) : grid;
            int v0 = Mathf.Clamp(_type.AtlasRow0, 0, grid - 1), vEnd = Mathf.Min(v0 + rows, grid);
            int u0 = Mathf.Clamp(_type.AtlasCol0, 0, grid - 1), uEnd = Mathf.Min(u0 + cols, grid);
            var list = new List<int>();
            for (int v = v0; v < vEnd; v++)
                for (int u = u0; u < uEnd; u++)
                {
                    int lin = v * grid + u;
                    if (_cellNonEmpty == null || (lin < _cellNonEmpty.Length && _cellNonEmpty[lin])) list.Add(Pack(u, v));
                }
            if (list.Count == 0)                              // fallback: band ignoring emptiness
                for (int v = v0; v < vEnd; v++) for (int u = u0; u < uEnd; u++) list.Add(Pack(u, v));
            _allowedCells = list.ToArray();
        }

        public void SetWorldPos(Vector3 worldPos) => _worldPos = worldPos;
        public void SetActive(bool active) => _active = active;
        public void SetPaused(bool paused) => _paused = paused;

        // ---- PORT set 43: the MeshDestructor shatter source (see ParticleTypeTable t[43]) ----
        // Precomputed UNITY-WORLD vertex positions (+ optional per-vertex tints) of the dying
        // model's CURRENT frame — precomputed by the retire seam so the object can be freed the
        // same tick, like the native two-stage destroy (FUN_005eae80: shatter now via
        // FUN_004845a0, deferred free via the obj+0x404 list).
        Vector3[]? _shatterVerts;
        Color32[]? _shatterCols;
        public void SetShatterSource(Vector3[] verts, Color32[]? cols)
        { _shatterVerts = verts; _shatterCols = cols; }

        // Debug/test surface: world positions of the live particles (law tests assert the
        // shatter spawns ON the provided vertices; no gameplay reads this).
        public IEnumerable<Vector3> AlivePositions()
        {
            for (int i = 0; i < _high; i++)
                if (_pool[i].Alive) yield return _pool[i].Pos;
        }

        // blow_particlesource wind (ORIG-space vector, e.g. prod_blowright's {0.1 0 0}): the
        // native folds the blow accumulator into the emitter drift +0x24..0x2c and zeroes it
        // (FUN_00498590:303-308); the drift then decays ×0.9/frame (DAT_00737108) and couples
        // ×0.1/frame into live particle velocities (FUN_0049e760:313-326 family) — existing
        // flames lean away and recover on their own. _drift is kept in orig space (ToUnity at
        // the coupling sites), so the raw native vector adds directly.
        public void AddImpulse(Vector3 orig) => _drift += orig;

        // blow_particlesource / create_particlesource (temp) one-shot burst, regardless of the active flag.
        // Routes through the per-setId SpawnInto so FAITHFUL types (e.g. setId 13 magic rings) get their
        // real spawn fields — NOT the legacy template (which gave them the wrong size/colour/seed) — and
        // recomputes AliveCount so the burst renders on the SAME frame (Render early-outs on AliveCount==0,
        // which only the per-type StepN otherwise sets — temps never run StepN's spawn).
        public void Burst(int count)
        {
            for (int i = 0; i < count; i++)
            {
                int s = Alloc();
                if (s < 0) break;
                SpawnInto(s);
            }
            AliveCount = 0;
            for (int i = 0; i < _high; i++) if (_pool[i].Alive) AliveCount++;
        }

        // Spawn one particle into a pre-allocated slot via the FAITHFUL per-setId spawner (mirrors the
        // StepOne dispatch); falls back to the legacy template for un-ported types.
        void SpawnInto(int s)
        {
            switch (_type.SetId)
            {
                case 0: Spawn0(ref _pool[s]); break;
                case 1: Spawn1(ref _pool[s]); break;
                case 2: Spawn2(ref _pool[s]); break;
                case 3: Spawn3(ref _pool[s]); break;
                case 6: Spawn6(ref _pool[s], BurstDrift()); break;
                case 8: Spawn8(ref _pool[s]); break;
                case 10: Spawn10(ref _pool[s]); break;
                case 11: Spawn11(ref _pool[s], BurstDrift()); break;
                case 13: Spawn13(ref _pool[s]); break;
                case 14: Spawn14(ref _pool[s]); break;
                case 15: Spawn15(ref _pool[s]); break;
                case 16: Spawn16(ref _pool[s]); break;
                case 17: Spawn17(ref _pool[s]); break;
                case 18: Spawn18(ref _pool[s]); break;
                case 19: Spawn19(ref _pool[s]); break;
                case 20: Spawn20(ref _pool[s]); break;
                case 21: Spawn21(ref _pool[s]); break;
                case 22: Spawn22(ref _pool[s]); break;
                case 23: Spawn23(ref _pool[s]); break;
                case 26: Spawn26(ref _pool[s]); break;
                case 28: Spawn28(ref _pool[s]); break;
                case 29: Spawn29(s); break;
                case 30: Spawn30(s); break;
                case 32: Spawn32(ref _pool[s]); break;
                case 36: SpawnPoof(s, Poof36V, Poof36U); break;
                case 37: SpawnPoof(s, Poof37V, Poof37U); break;
                case 38: SpawnPoof(s, Poof38V, Poof38U); break;
                case 39: SpawnGlow(s, 0x40007); break;
                case 40: SpawnGlow(s, 0x20005); break;
                case 43: SpawnShatter(ref _pool[s]); break;
                default: SpawnLegacy(ref _pool[s]); break;
            }
        }

        // ===== PORT set 43 — MeshDestructor shatter approximation ==================================
        // The native shatter (FUN_004830a0 via FUN_004845a0) converts the model's CURRENT-frame
        // mesh into textured triangle shards: per-vertex 16-bit decode + world transform
        // (:289-303), adaptive triangle tessellation (FUN_00482950), per-vertex palette colour
        // (:314-327), random symmetric velocity (:167-172 — ((rand·a − b)·c) per component),
        // gravity DAT_00737058 + ground settle (FUN_005ea850). The port approximates the shards
        // as billboard fragments: spawn AT a random precomputed model vertex, tinted by the
        // model's sampled texture colour at that vertex, debris atlas cell, symmetric scatter
        // with a small up-arc, gravity + ground line + bounce in Step43. Tunables are the
        // public Shatter* fields.
        public static float ShatterUpBias = 0.02f;     // unity units/frame added to the launch
        public static float ShatterGroundDrop = 1.2f;  // fragments settle this far below the piece

        void SpawnShatter(ref P p)
        {
            p.Alive = true; p.Bill = 0; p.Age = 0; p.Spin = 0f;
            int n = _shatterVerts?.Length ?? 0;
            if (n > 0)
            {
                int i = _rng.Mod(n);
                p.Pos = _shatterVerts![i];
                p.Color = _shatterCols != null && i < _shatterCols.Length
                    ? _shatterCols[i]
                    : new Color32(0x9f, 0x9f, 0x9f, 0xff);
            }
            else
            {
                p.Pos = _worldPos;
                p.Color = new Color32(0x9f, 0x9f, 0x9f, 0xff);
            }
            // The FUN_004830a0:167-172 law shape: symmetric random per component.
            Vector3 velOrig = new((_rng.Bits(0xf) - 8) / 260f, (_rng.Bits(0xf) - 8) / 260f,
                                  (_rng.Bits(0xf) - 8) / 260f);
            p.Vel = ToUnity(velOrig);
            p.Vel.y += ShatterUpBias;                       // small arc before the gravity pull
            p.Base = p.Size = _type.StartSize * (0.7f + 0.6f * _rng.Frac01());
            p.Spec = new Color32(0, 0, 0, 0);
            p.Cell = (2 << 16) | _rng.Bits(1);              // c_256d debris cells (set 20's row)
            p.Aux = new Vector3(p.Pos.x, _worldPos.y - ShatterGroundDrop, p.Pos.z);
        }

        // Fragment physics: gravity + drag + settle at the piece's ground line, mild shrink,
        // late alpha fade — the model TINT itself is per-particle and never re-rammed (the
        // generic FinishFrame would clobber it with the type constant).
        void Step43()
        {
            AliveCount = 0;
            for (int i = 0; i < _high; i++)
            {
                if (!_pool[i].Alive) continue;
                ref P p = ref _pool[i];
                p.Pos += p.Vel;
                p.Vel += _type.Gravity * GravityScale;
                if (_type.Drag < 1f) p.Vel *= _type.Drag;
                if (p.Pos.y < p.Aux.y && p.Vel.y < 0f)      // the settle line (FUN_005ea850 spirit)
                {
                    p.Pos.y = p.Aux.y;
                    p.Vel.y = -p.Vel.y * 0.3f;
                    p.Vel.x *= 0.5f; p.Vel.z *= 0.5f;
                }
                p.Size = p.Base * (1f - 0.55f * Mathf.Clamp01(p.Age / (float)_type.AgeCap));
                if (p.Age > _type.AgeCap - 16 && p.Color.a >= 16) p.Color.a -= 16;
                p.Age++;
                if (p.Age >= _type.AgeCap) { p.Alive = false; continue; }
                AliveCount++;
            }
        }

        // Frame-shared spawn drift for the smoke types (6/11) when burst (they normally get it per Step).
        Vector3 BurstDrift() => new((_rng.Bits(0xf) - 8) / 140f, (_rng.Bits(0xf) - 8) / 140f, (_rng.Bits(0xf) - 8) / 140f);

        // ---- per-frame simulation ----------------------------------------------------------------

        public void StepFrames(int frames)
        {
            for (int f = 0; f < frames; f++) StepOne();
        }

        void StepOne()
        {
            // Faithful per-type transcriptions (from re/decompiled/.../FUN_*.c) take over as they land.
            // Everything else still runs the OLD hand-tuned template (being replaced one setId at a time).
            switch (_type.SetId)
            {
                case 0: Step0(); return;
                case 1: Step1(); return;
                case 2: Step2(); return;
                case 3: Step3(); return;
                case 6: Step6(); return;
                case 8: Step8(); return;
                case 9: Step9(); return;
                case 10: Step10(); return;
                case 11: Step11(); return;
                case 12: Step12(); return;
                case 13: Step13(); return;
                case 14: Step14(); return;
                case 15: Step15(); return;
                case 16: Step16(); return;
                case 17: Step17(); return;
                case 18: Step18(); return;
                case 19: Step19(); return;
                case 20: Step20(); return;
                case 21: Step21(); return;
                case 22: Step22(); return;
                case 23: Step23(); return;
                case 26: Step26(); return;
                case 28: Step28(); return;
                case 29: Step29(); return;
                case 30: Step30(); return;
                case 32: Step32(); return;
                case 36: Step36(); return;
                case 37: Step37(); return;
                case 38: Step38(); return;
                case 39: Step39(); return;
                case 40: Step40(); return;
                case 43: Step43(); return;
            }

            // ---- legacy hand-tuned template (TEMPORARY, pending faithful transcription) ----
            // A) emitter random-walk drift. The DAMPING runs even without wander so a
            // blow_particlesource impulse (AddImpulse → _drift) decays here too instead of
            // pushing spawns sideways forever (was a no-op while _drift stayed zero).
            if (_type.EmitterWander)
            {
                _drift.x = (_drift.x + _rng.Sym4(800f)) * DriftDamping;
                _drift.y = (_drift.y + _rng.Sym4(800f)) * DriftDamping;
                _drift.z = (_drift.z + _rng.Sym4(800f)) * DriftDamping;
            }
            else _drift *= DriftDamping;

            // B) spawn.
            if (_active && !_paused)
            {
                int n = SpawnCountThisFrame();
                for (int i = 0; i < n; i++) Spawn();
            }

            // C) integrate live particles.
            AliveCount = 0;
            for (int i = 0; i < _high; i++)
            {
                if (!_pool[i].Alive) continue;
                Integrate(ref _pool[i]);
                if (_pool[i].Alive) AliveCount++;
            }
        }

        // ===== FAITHFUL PORT: setId 0 — "Schmiede" forge embers (FUN_0049e760) =====================
        // Direct transcription of the original per-type function. All magnitudes are the ORIGINAL's
        // DAT_ constants (resolved via re/notes/particle_constants.txt), not tuned-by-eye values.
        // Real Tcl call: change_particlesource <o> <s> 0 {0 -0.1 0} {0 0 0} 32 1 0 → dir = 0, 1/frame.
        // 3 phases, exactly as the decompiled fn: A) emitter drift, B) spawn, C) integrate.
        void Step0()
        {
            // A) emitter random-walk drift (damped 0.9; normalizers 8200/9500/8200 = DAT_007376bc/c0/bc).
            _drift.x = (_drift.x + (_rng.Bits(0xf) - 8) / 8200f) * 0.9f;
            _drift.y = (_drift.y + (_rng.Bits(0xf) - 8) / 9500f) * 0.9f;
            _drift.z = (_drift.z + (_rng.Bits(0xf) - 8) / 8200f) * 0.9f;

            // B) spawn num/frame particles (gated by active & !fogged; terrain fog stubbed in the test bed).
            if (_active && !_paused)
            {
                int n = _src != null ? Mathf.Max(0, _src.NumPerFrame) : 0;
                for (int i = 0; i < n; i++) { int s = Alloc(); if (s < 0) break; Spawn0(ref _pool[s]); }
            }

            // C) integrate live particles.
            AliveCount = 0;
            for (int i = 0; i < _high; i++)
            {
                if (!_pool[i].Alive) continue;
                Integrate0(ref _pool[i]);
                if (_pool[i].Alive) AliveCount++;
            }
        }

        // The original particle world is Y-DOWN (negative Y = visually UP); Unity is Y-UP. X/Z match.
        // So every original vertical quantity is negated when applied in Unity.
        static Vector3 ToUnity(Vector3 orig) => new Vector3(orig.x, -orig.y, orig.z);

        // Shared warm/grey age+distance colour ramp (setId 0/2/3/28/29/30…). The original draws
        // texture×diffuse + specular; we set both (specular goes to the shader via TEXCOORD1).
        // diffuse R=G=(255−20·i)·(1−d), B=255·(1−d); specular = (0,0,specB). Orange texture + blue
        // specular = the violet cast (setId 28). specConstBlue: 28 keeps B=255; fire uses 30·i (subtle).
        void WarmRampColour(ref P p, bool specConstBlue)
        {
            float hdist = Mathf.Abs(p.Pos.x - _worldPos.x);
            if (hdist > 0.1f) hdist = 0.1f;
            float bright = (0.1f - hdist) * 8f;                 // brighter near the emitter centre
            int i = Mathf.RoundToInt(Mathf.Clamp(16 - p.Age, 0, 5) * bright);
            float d = Mathf.Clamp01((p.Age - 16) * 0.0625f);    // diffuse fades after age 16
            byte rg = (byte)Mathf.Clamp(Mathf.RoundToInt((255 - 20 * i) * (1f - d)), 0, 255);
            byte b = (byte)Mathf.Clamp(Mathf.RoundToInt(255 * (1f - d)), 0, 255);
            p.Color = new Color32(rg, rg, b, 255);
            byte specB = (byte)(specConstBlue ? 255 : Mathf.Clamp(30 * i, 0, 255));
            p.Spec = new Color32(0, 0, specB, 0);
        }

        void Spawn0(ref P p)
        {
            p.Alive = true;
            p.Age = 0;
            p.Bill = 0;
            // disc spread: angle (rand%360)/180·π, radius (rand%100)/350, y-offset (rand%10)/200 (orig, Y-down).
            float ang = (_rng.U15() % 360) / 180f * Mathf.PI;
            float rad = (_rng.U15() % 100) / 350f;
            float yoff = (_rng.U15() % 10) / 200f;
            p.Pos = _worldPos + ToUnity(new Vector3(Mathf.Cos(ang) * rad, yoff, Mathf.Sin(ang) * rad));
            // velocity = base dir + jitter (±8/800 on x/z; y = −((rand%10)/600 + 0.006)); all orig → ToUnity.
            Vector3 dir = _src != null ? _src.Direction : Vector3.zero;
            Vector3 velOrig = dir + new Vector3((_rng.Bits(0xf) - 8) / 800f,
                                                -((_rng.U15() % 10) / 600f + 0.006f),
                                                (_rng.Bits(0xf) - 8) / 800f);
            p.Vel = ToUnity(velOrig);                 // orig Y-down vel.y (negative = up) → Unity +Y up
            p.Base = 2.15f; p.Size = 2.15f;          // particle +0x28 = 0x4009999a
            p.Spin = _rng.U15() / 5215f;             // +0x30
            p.Cell = _rng.Bits(0x3);                 // +0x3c = rand&3 → u 0..3, v 0 (e_256 row-0 fire cells)
            p.Color = new Color32(255, 255, 255, 255);
        }

        void Integrate0(ref P p)
        {
            p.Pos += p.Vel;                                                   // pos += vel (Unity convention)
            p.Pos.x = (_worldPos.x * 8f + p.Pos.x * 92f) / 100f;             // horizontal pull-to-emitter 8%/frame
            p.Pos.z = (p.Pos.z * 92f + _worldPos.z * 8f) / 100f;             // (keeps the ember cluster coherent)
            p.Spin += 0.2f;                                                   // +0x30 += 0.2 (DAT_00737054)
            p.Vel += ToUnity(_drift) * 0.1f;                                  // emitter drift coupling (DAT_00737034)
            if (p.Age < 16)                                                   // age<0x10: lift term (orig negative=up → +Y)
                p.Vel.y += -((_rng.U15() + 32768f) * -0.0008f * 1.5258789e-05f);

            // colour: EXACT age fade (diffuse ×(1−(age−16)·0.0625)). NOTE: the original also writes a
            // per-frame specular tint from age×horizontal-distance — deferred until the shader carries a
            // separate specular stream; the orange comes from the e_256 texture, white diffuse passes it.
            WarmRampColour(ref p, false);

            p.Age++;
            if (p.Age >= 32) p.Alive = false;                                // ageCap (dist²>maxrange² kill: maxrng unset for type 0)
        }

        // ===== FAITHFUL PORT: setId 2 — small warm fireball (FUN_004a1c50) ==========================
        // Same template as setId 0 with different constants + an age² atlas flipbook. Real Tcl varies by
        // caller (e.g. drache.tcl dir {0.4 0.05 0}); the test uses the static-fireball case (dir 0).
        void Step2()
        {
            _drift.x = (_drift.x + (_rng.Bits(0xf) - 8) / 8200f) * 0.9f;
            _drift.y = (_drift.y + (_rng.Bits(0xf) - 8) / 9500f) * 0.9f;
            _drift.z = (_drift.z + (_rng.Bits(0xf) - 8) / 8200f) * 0.9f;
            if (_active && !_paused)
            {
                int n = _src != null ? Mathf.Max(0, _src.NumPerFrame) : 0;
                for (int i = 0; i < n; i++) { int s = Alloc(); if (s < 0) break; Spawn2(ref _pool[s]); }
            }
            AliveCount = 0;
            for (int i = 0; i < _high; i++)
            {
                if (!_pool[i].Alive) continue;
                Integrate2(ref _pool[i]);
                if (_pool[i].Alive) AliveCount++;
            }
        }

        void Spawn2(ref P p)
        {
            p.Alive = true;
            p.Age = 0;
            p.Bill = 0;
            float ang = (_rng.U15() % 360) / 180f * Mathf.PI;
            float rad = (_rng.U15() % 100) / 850f;             // tighter than type 0 (/350) → smaller ball
            float yoff = (_rng.U15() % 10) / 200f;
            p.Pos = _worldPos + ToUnity(new Vector3(Mathf.Cos(ang) * rad, yoff, Mathf.Sin(ang) * rad));
            Vector3 dir = _src != null ? _src.Direction : Vector3.zero;
            Vector3 velOrig = dir + new Vector3((_rng.Bits(0xf) - 8) / 800f,
                                                -((_rng.U15() % 10) / 600f + 0.008f),
                                                (_rng.Bits(0xf) - 8) / 800f);
            velOrig += dir * (_rng.U15() * 3.0517578e-05f);    // + 0..1× base-vel (0 when dir = 0)
            p.Vel = ToUnity(velOrig);
            p.Base = 1.15f; p.Size = 1.15f;                    // +0x28 = 0x3f933333
            p.Spin = 0f;
            p.Cell = (_rng.U15() % 3) << 16;                   // v = rand%3 (u set by age² in integrate)
            p.Color = new Color32(255, 255, 255, 255);
        }

        void Integrate2(ref P p)
        {
            p.Pos += p.Vel;
            p.Pos.x = (_worldPos.x * 8f + p.Pos.x * 92f) / 100f;
            p.Pos.z = (p.Pos.z * 92f + _worldPos.z * 8f) / 100f;
            int a2 = p.Age * p.Age;                            // atlas u = (31 − age²/32) / 4 (warm column, 7→0)
            int uu = Mathf.Clamp((31 - (a2 >> 5)) >> 2, 0, 15);
            p.Cell = (p.Cell & unchecked((int)0xFFFF0000)) | uu;
            p.Spin += 0.2f;
            p.Vel += ToUnity(_drift) * 0.1f;
            if (p.Age < 6)                                     // lift for the first 6 frames (orig negative = up)
                p.Vel.y += -((_rng.U15() + 32768f) * -0.0008f * 1.5258789e-05f);
            WarmRampColour(ref p, false);
            p.Age++;
            if (p.Age >= 32) p.Alive = false;
        }

        // ===== FAITHFUL PORT: setId 3 — wide/flat fireball (FUN_004a2d30) ===========================
        // Fire family again; deltas: radius /180 (wider), vel.y term /130 (rises harder), size 2.15,
        // drift-couple 0.12, lift for the first 16 frames, cell v = rand&3.
        void Step3()
        {
            _drift.x = (_drift.x + (_rng.Bits(0xf) - 8) / 8200f) * 0.9f;
            _drift.y = (_drift.y + (_rng.Bits(0xf) - 8) / 9500f) * 0.9f;
            _drift.z = (_drift.z + (_rng.Bits(0xf) - 8) / 8200f) * 0.9f;
            if (_active && !_paused)
            {
                int n = _src != null ? Mathf.Max(0, _src.NumPerFrame) : 0;
                for (int i = 0; i < n; i++) { int s = Alloc(); if (s < 0) break; Spawn3(ref _pool[s]); }
            }
            AliveCount = 0;
            for (int i = 0; i < _high; i++)
            {
                if (!_pool[i].Alive) continue;
                Integrate3(ref _pool[i]);
                if (_pool[i].Alive) AliveCount++;
            }
        }

        void Spawn3(ref P p)
        {
            p.Alive = true;
            p.Age = 0;
            p.Bill = 0;
            float ang = (_rng.U15() % 360) / 180f * Mathf.PI;
            float rad = (_rng.U15() % 100) / 180f;             // wider than 0/2 → flatter disc
            float yoff = (_rng.U15() % 10) / 200f;
            p.Pos = _worldPos + ToUnity(new Vector3(Mathf.Cos(ang) * rad, yoff, Mathf.Sin(ang) * rad));
            Vector3 dir = _src != null ? _src.Direction : Vector3.zero;
            Vector3 velOrig = dir + new Vector3((_rng.Bits(0xf) - 8) / 800f,
                                                -((_rng.U15() % 10) / 130f + 0.006f),   // rises harder
                                                (_rng.Bits(0xf) - 8) / 800f);
            p.Vel = ToUnity(velOrig);
            p.Base = 2.15f; p.Size = 2.15f;                    // +0x28 = 0x4009999a
            p.Spin = 0f;
            p.Cell = (_rng.Bits(0x3)) << 16;                   // v = rand&3 (u set by age² in integrate)
            p.Color = new Color32(255, 255, 255, 255);
        }

        void Integrate3(ref P p)
        {
            p.Pos += p.Vel;
            p.Pos.x = (_worldPos.x * 8f + p.Pos.x * 92f) / 100f;
            p.Pos.z = (p.Pos.z * 92f + _worldPos.z * 8f) / 100f;
            int a2 = p.Age * p.Age;
            int uu = Mathf.Clamp((31 - (a2 >> 5)) >> 2, 0, 15);
            p.Cell = (p.Cell & unchecked((int)0xFFFF0000)) | uu;
            p.Spin += 0.2f;
            p.Vel += ToUnity(_drift) * 0.12f;                  // drift couple 0.12 (DAT_00737038)
            if (p.Age < 16)
                p.Vel.y += -((_rng.U15() + 32768f) * -0.0008f * 1.5258789e-05f);
            WarmRampColour(ref p, false);
            p.Age++;
            if (p.Age >= 32) p.Alive = false;
        }

        // ===== FAITHFUL PORT: setId 28 — rising smoke (FUN_004b9150) ================================
        // Point spawn; rises via the same lift term as fire; GROWS (+0.0719/frame); no horizontal pull.
        // Colour uses the same age ramp as fire (deferred → white + fade until the specular stream lands).
        void Step28()
        {
            _drift.x = (_drift.x + (_rng.Bits(0xf) - 8) / 8200f) * 0.9f;
            _drift.y = (_drift.y + (_rng.Bits(0xf) - 8) / 9500f) * 0.9f;
            _drift.z = (_drift.z + (_rng.Bits(0xf) - 8) / 8200f) * 0.9f;
            if (_active && !_paused)
            {
                int n = _src != null ? Mathf.Max(0, _src.NumPerFrame) : 0;
                for (int i = 0; i < n; i++) { int s = Alloc(); if (s < 0) break; Spawn28(ref _pool[s]); }
            }
            AliveCount = 0;
            for (int i = 0; i < _high; i++)
            {
                if (!_pool[i].Alive) continue;
                SmokeIntegrate(ref _pool[i], pull: false);     // 28: no horizontal pull
                if (_pool[i].Alive) AliveCount++;
            }
        }

        void Spawn28(ref P p)
        {
            p.Alive = true;
            p.Age = 0;
            p.Bill = 0;
            p.Pos = _worldPos;                                 // point spawn (pos = emitter)
            SmokeVel(ref p);
            p.Base = 0.15f; p.Size = 0.15f;                    // +0x28 = 0x3e19999a
            p.Spin = _rng.U15() / 5215f;
            p.Cell = _rng.U15() % 3;                           // u = rand%3, v = 0
            p.Color = new Color32(255, 255, 255, 255);
        }

        // ===== FAITHFUL PORT: setId 29 / 30 — violet smoke discs (FUN_004b9dc0 / FUN_004bacb0) =======
        // Same as 28 but disc spawn + a horizontal pull-to-emitter (8%/frame). 29: radius (rand%100)/380,
        // size 1.8. 30: radius (rand%50)/850 (very tight), size 0.5.
        void Step29() => StepSmokeDisc(Spawn29);
        void Step30() => StepSmokeDisc(Spawn30);

        void StepSmokeDisc(System.Action<int> spawnInto)
        {
            _drift.x = (_drift.x + (_rng.Bits(0xf) - 8) / 8200f) * 0.9f;
            _drift.y = (_drift.y + (_rng.Bits(0xf) - 8) / 9500f) * 0.9f;
            _drift.z = (_drift.z + (_rng.Bits(0xf) - 8) / 8200f) * 0.9f;
            if (_active && !_paused)
            {
                int n = _src != null ? Mathf.Max(0, _src.NumPerFrame) : 0;
                for (int i = 0; i < n; i++) { int s = Alloc(); if (s < 0) break; spawnInto(s); }
            }
            AliveCount = 0;
            for (int i = 0; i < _high; i++)
            {
                if (!_pool[i].Alive) continue;
                SmokeIntegrate(ref _pool[i], pull: true);      // 29/30: pull-to-centre
                if (_pool[i].Alive) AliveCount++;
            }
        }

        void Spawn29(int idx) => SmokeDiscSpawn(idx, 100, 380f, 1.8f);
        void Spawn30(int idx) => SmokeDiscSpawn(idx, 50, 850f, 0.5f);

        void SmokeDiscSpawn(int idx, int radMod, float radDiv, float size)
        {
            ref P p = ref _pool[idx];
            p.Alive = true;
            p.Age = 0;
            p.Bill = 0;
            float ang = (_rng.U15() % 360) / 180f * Mathf.PI;
            float rad = (_rng.U15() % radMod) / radDiv;
            float yoff = (_rng.U15() % 10) / 200f;
            p.Pos = _worldPos + ToUnity(new Vector3(Mathf.Cos(ang) * rad, yoff, Mathf.Sin(ang) * rad));
            SmokeVel(ref p);
            p.Base = size; p.Size = size;
            p.Spin = _rng.U15() / 5215f;
            p.Cell = _rng.U15() % 3;
            p.Color = new Color32(255, 255, 255, 255);
        }

        // shared smoke spawn velocity = dir + jitter (±8/800 on x/z; y = −((rand%10)/600 + 0.006)).
        void SmokeVel(ref P p)
        {
            Vector3 dir = _src != null ? _src.Direction : Vector3.zero;
            Vector3 velOrig = dir + new Vector3((_rng.Bits(0xf) - 8) / 800f,
                                                -((_rng.U15() % 10) / 600f + 0.006f),
                                                (_rng.Bits(0xf) - 8) / 800f);
            p.Vel = ToUnity(velOrig);
        }

        // shared smoke integrate (28/29/30): rise via lift, GROW (+0.0719/frame), violet colour, age cap 32.
        void SmokeIntegrate(ref P p, bool pull)
        {
            p.Pos += p.Vel;
            if (pull)
            {
                p.Pos.x = (_worldPos.x * 8f + p.Pos.x * 92f) / 100f;
                p.Pos.z = (p.Pos.z * 92f + _worldPos.z * 8f) / 100f;
            }
            p.Spin += 0.2f;
            p.Vel += ToUnity(_drift) * 0.1f;
            if (p.Age < 16)
                p.Vel.y += -((_rng.U15() + 32768f) * -0.0008f * 1.5258789e-05f);
            p.Size += 0.0718750f;                              // grows over life (_DAT_00737020)
            WarmRampColour(ref p, true);                       // constant blue specular → violet smoke
            p.Age++;
            if (p.Age >= 32) p.Alive = false;
        }

        // ===== FAITHFUL PORT: setId 36/37/38 — energy-burst flipbooks (FUN_004be880 / …d80 / …270) =====
        // Stationary point flash; size 6.5 grows +0.05/frame; atlas cell walks an explicit 9-frame (V,U)
        // table (a_256d @ grid8 — 36 blue / 37 orange / 38 teal glow rows); white; ageCap 9; no physics.
        static readonly int[] Poof36V = { 1, 1, 1, 1, 1, 1, 1, 1, 2 }, Poof36U = { 0, 1, 2, 3, 4, 5, 6, 7, 0 };
        static readonly int[] Poof37V = { 2, 2, 2, 2, 2, 2, 2, 3, 3 }, Poof37U = { 1, 2, 3, 4, 5, 6, 7, 0, 1 };
        static readonly int[] Poof38V = { 3, 3, 3, 3, 3, 3, 4, 4, 0 }, Poof38U = { 2, 3, 4, 5, 6, 7, 0, 1, 0 };

        // frames: how many table entries actually render. 36/37 gate `age<10` → 9 frames; 38 gates
        // `age<9` → it KILLS on age 8 before its 9th (V=0,U=0 = soot/ground) frame ever renders → 8 frames.
        void Step36() => StepPoof(Poof36V, Poof36U, 9);
        void Step37() => StepPoof(Poof37V, Poof37U, 9);
        void Step38() => StepPoof(Poof38V, Poof38U, 8);

        void StepPoof(int[] vt, int[] ut, int frames)
        {
            if (_active && !_paused)
            {
                int n = _src != null ? Mathf.Max(0, _src.NumPerFrame) : 0;
                for (int i = 0; i < n; i++) { int s = Alloc(); if (s < 0) break; SpawnPoof(s, vt, ut); }
            }
            AliveCount = 0;
            for (int i = 0; i < _high; i++)
            {
                if (!_pool[i].Alive) continue;
                IntegratePoof(ref _pool[i], vt, ut, frames);
                if (_pool[i].Alive) AliveCount++;
            }
        }

        void SpawnPoof(int idx, int[] vt, int[] ut)
        {
            ref P p = ref _pool[idx];
            p.Alive = true;
            p.Age = 0;
            p.Bill = 0;
            p.Pos = _worldPos;                                 // stationary point flash
            p.Vel = Vector3.zero;                              // (orig sets vel=dir but never integrates it)
            p.Base = 6.5f; p.Size = 6.5f;                      // +0x28 = 0x40d00000
            p.Spin = 0.7853982f;                               // +0x30 = π/4
            p.Cell = (vt[0] << 16) | ut[0];
            p.Color = new Color32(255, 255, 255, 255);
            p.Spec = new Color32(0, 0, 0, 0);
        }

        void IntegratePoof(ref P p, int[] vt, int[] ut, int frames)
        {
            if (p.Age >= frames) { p.Alive = false; return; }  // kill before the unused table tail renders
            p.Cell = (vt[p.Age] << 16) | ut[p.Age];            // (V<<16)|U packed cell
            p.Size += 0.05f;                                   // grows (_DAT_00737010)
            p.Age++;
        }

        // ===== FAITHFUL PORT: setId 39/40 — long growing glows (FUN_004bf760 / FUN_004bfc30) =========
        // Stationary single fixed cell, size 6.5 grows +0.1/frame, white, age cap 49. NOT a flipbook —
        // 39 = a_256d cell 0x40007 (v4,u7 = circle-in-square), 40 = 0x20005 (v2,u5 = orange glow).
        void Step39() => StepGlow(0x40007);
        void Step40() => StepGlow(0x20005);

        void StepGlow(int fixedCell)
        {
            if (_active && !_paused)
            {
                int n = _src != null ? Mathf.Max(0, _src.NumPerFrame) : 0;
                for (int i = 0; i < n; i++) { int s = Alloc(); if (s < 0) break; SpawnGlow(s, fixedCell); }
            }
            AliveCount = 0;
            for (int i = 0; i < _high; i++)
            {
                if (!_pool[i].Alive) continue;
                IntegrateGlow(ref _pool[i]);
                if (_pool[i].Alive) AliveCount++;
            }
        }

        void SpawnGlow(int idx, int fixedCell)
        {
            ref P p = ref _pool[idx];
            p.Alive = true;
            p.Age = 0;
            p.Bill = 0;
            p.Pos = _worldPos;                                 // stationary
            p.Vel = Vector3.zero;
            p.Base = 6.5f; p.Size = 6.5f;                      // +0x28 = 0x40d00000
            p.Spin = 0.7853982f;                               // π/4
            p.Cell = fixedCell;                                // constant (integrate re-sets the same value)
            p.Color = new Color32(255, 255, 255, 255);
            p.Spec = new Color32(0, 0, 0, 0);
        }

        void IntegrateGlow(ref P p)
        {
            p.Size += 0.1f;                                    // grows (DAT_00737034)
            p.Age++;
            if (p.Age > 48) p.Alive = false;                  // kill at age > 0x30
        }

        // ===== FAITHFUL PORT: setId 9 — drifting smoke puff (FUN_004a7570) ==========================
        // Point spawn, vel = dir (rise is dir-driven; test dir=0 → mostly hovers). Drag (×90/130) + a
        // strong emitter-drift coupling (×4·10/130) with a tiny lift; cell walks b_128d row 5 by age/7;
        // size 1.5 constant; white (no fade); random initial age 0..19; ageCap 112. Drift damping 0.3.
        void Step9()
        {
            _drift.x = (_drift.x + (_rng.Bits(0xf) - 8) / 3200f) * 0.3f;
            _drift.y = (_drift.y + (_rng.Bits(0xf) - 8) / 5500f) * 0.3f;
            _drift.z = (_drift.z + (_rng.Bits(0xf) - 8) / 3200f) * 0.3f;
            if (_active && !_paused)
            {
                int baseN = _src != null ? Mathf.Max(0, _src.NumPerFrame) : 0;
                int n = Mathf.Max(0, baseN - 1 + _rng.Mod(3));   // num ±1
                for (int i = 0; i < n; i++) { int s = Alloc(); if (s < 0) break; Spawn9(ref _pool[s]); }
            }
            AliveCount = 0;
            for (int i = 0; i < _high; i++)
            {
                if (!_pool[i].Alive) continue;
                Integrate9(ref _pool[i]);
                if (_pool[i].Alive) AliveCount++;
            }
        }

        void Spawn9(ref P p)
        {
            p.Alive = true;
            p.Bill = 0;
            p.Pos = _worldPos;                                 // point spawn
            p.Vel = ToUnity(_src != null ? _src.Direction : Vector3.zero);   // no jitter, vel = dir
            p.Base = 1.5f; p.Size = 1.5f;                      // +0x28 = 0x3fc00000
            p.Age = _rng.U15() % 20;                           // random initial age (desync)
            p.Spin = -((_rng.Bits(0xf) - 8) / 140f);
            p.Cell = 5 << 16;                                  // (v5,u0); integrate sets u = age/7
            p.Color = new Color32(255, 255, 255, 255);
            p.Spec = new Color32(0, 0, 0, 0);
        }

        void Integrate9(ref P p)
        {
            p.Pos += p.Vel;
            // drag + emitter-drift coupling (orig: vel = (vel·90 + driftForce·10)/130; driftForce = drift·4
            // with a tiny −0.05 Y lift). ToUnity flips the Y of the orig driftForce.
            Vector3 fOrig = new(_drift.x * 4f, _drift.y * 4f - 0.05f, _drift.z * 4f);
            p.Vel = (p.Vel * 90f + ToUnity(fOrig) * 10f) / 130f;
            p.Spin += p.Spin >= 0f ? 0.1f : -0.1f;             // rotation accelerates away from 0 (DAT_00737034)
            p.Cell = (5 << 16) | (p.Age / 7);                  // b_128d row 5, u walks by age/7
            p.Age++;
            if (p.Age > 111) p.Alive = false;
        }

        // ===== FAITHFUL PORT: setId 12 — smoke cloud that grows in place (FUN_004a9810) =============
        // Point spawn, vel = dir (real Tcl dir=0 → grows in place, doesn't fly). Drift coupling 0.05,
        // size 0.5 grows +0.15/frame, cell walks b_128d row 15 (u = 4 + age/16), white (no fade),
        // random init age 0..18, ageCap 64. Drift damping 0.98 (norm ~6200).
        void Step12()
        {
            _drift.x = (_drift.x + (_rng.Bits(0xf) - 8) / 6200f) * 0.98f;
            _drift.y = (_drift.y + (_rng.Bits(0xf) - 8) / 6200f) * 0.98f;
            _drift.z = (_drift.z + (_rng.Bits(0xf) - 8) / 6200f) * 0.98f;
            if (_active && !_paused)
            {
                int baseN = _src != null ? Mathf.Max(0, _src.NumPerFrame) : 0;
                int n = Mathf.Max(0, baseN - 1 + _rng.Mod(3));
                for (int i = 0; i < n; i++) { int s = Alloc(); if (s < 0) break; Spawn12(ref _pool[s]); }
            }
            AliveCount = 0;
            for (int i = 0; i < _high; i++)
            {
                if (!_pool[i].Alive) continue;
                Integrate12(ref _pool[i]);
                if (_pool[i].Alive) AliveCount++;
            }
        }

        void Spawn12(ref P p)
        {
            p.Alive = true;
            p.Bill = 0;
            p.Pos = _worldPos;
            p.Vel = ToUnity(_src != null ? _src.Direction : Vector3.zero);
            p.Base = 0.5f; p.Size = 0.5f;                      // +0x28 = 0x3f000000
            p.Age = _rng.Bits(7) + _rng.Mod(12);               // random init age 0..18
            p.Spin = -((_rng.Bits(0xf) - 8) / 140f);
            p.Cell = 0xf0007;                                  // (v15,u7); integrate sets u = 4 + age/16
            p.Color = new Color32(255, 255, 255, 255);
            p.Spec = new Color32(0, 0, 0, 0);
        }

        void Integrate12(ref P p)
        {
            p.Pos += p.Vel;
            p.Vel += ToUnity(_drift) * 0.05f;                  // drift coupling
            p.Size += 0.15f;                                   // grows in place (_DAT_0073704c)
            p.Spin += p.Spin >= 0f ? 0.2f : -0.2f;             // rotation accelerates away from 0 (DAT_00737054)
            p.Cell = (15 << 16) | (4 + p.Age / 16);            // b_128d row 15, u 4..7
            p.Age++;
            if (p.Age >= 64) p.Alive = false;                  // ageCap (maxrange dist-kill off in test)
        }

        // ===== FAITHFUL PORT: setId 14 — shimmering magic cloud (FUN_004ab4d0) ======================
        // Sphere spawn (random unit dir × rand·|dir|; test dir=0 → starts collected, then the tiny vel
        // jitter + drag disperse it). Drag 0.98, size 2.0 grows +0.015, slow spin. Cell scans the c_256d
        // grid (v=age/16, u=(age/4)%4). Grey specular = (byte)((64−age)·25) — the byte wrap is the shimmer.
        void Step14()
        {
            _drift.x = (_drift.x + (_rng.Bits(0xf) - 8) / 3200f) * 0.98f;
            _drift.y = (_drift.y + (_rng.Bits(0xf) - 8) / 3200f) * 0.98f;
            _drift.z = (_drift.z + (_rng.Bits(0xf) - 8) / 3200f) * 0.98f;
            if (_active && !_paused)
            {
                int n = _src != null ? Mathf.Max(0, _src.NumPerFrame) : 0;
                for (int i = 0; i < n; i++) { int s = Alloc(); if (s < 0) break; Spawn14(ref _pool[s]); }
            }
            AliveCount = 0;
            for (int i = 0; i < _high; i++)
            {
                if (!_pool[i].Alive) continue;
                Integrate14(ref _pool[i]);
                if (_pool[i].Alive) AliveCount++;
            }
        }

        void Spawn14(ref P p)
        {
            p.Alive = true;
            p.Bill = 0;
            Vector3 r = new(_rng.U15() * 3.0517578e-05f - 0.5f,
                            _rng.U15() * 3.0517578e-05f - 0.5f,
                            _rng.U15() * 3.0517578e-05f - 0.5f);
            float len = r.magnitude;
            Vector3 unit = len > 0f ? r / len : Vector3.zero;
            Vector3 dir = _src != null ? _src.Direction : Vector3.zero;
            float radius = _rng.U15() * 3.0517578e-05f * dir.magnitude;   // rand(0..1)·|dir|
            p.Pos = _worldPos + ToUnity(unit * radius);                   // sphere of radius |dir|
            Vector3 velOrig = new((_rng.Bits(0xf) - 8) / 500f, (_rng.Bits(0xf) - 16) / 500f, (_rng.Bits(0xf) - 8) / 500f);
            p.Vel = ToUnity(velOrig);
            p.Base = 2.0f; p.Size = 2.0f;                                 // +0x28 = 0x40000000
            p.Age = _rng.Bits(7) + _rng.Mod(12);                          // random init age 0..18
            p.Spin = velOrig.x;                                           // spin seed = vel.x
            p.Cell = 0;                                                   // integrate sets it
            p.Color = new Color32(255, 255, 255, 255);                    // preset alpha (70) applied in Render
            p.Spec = new Color32(0, 0, 0, 0);
        }

        void Integrate14(ref P p)
        {
            p.Pos += p.Vel;
            p.Vel *= 0.98f;                                               // drag (DAT_00737120)
            p.Size += 0.015f;                                             // grows (_DAT_00736fe8)
            p.Spin += p.Spin >= 0f ? 0.002f : -0.002f;                    // slow rotation (_DAT_00736fc0)
            p.Cell = ((p.Age / 16) << 16) | ((p.Age / 4) % 4);           // scans c_256d grid
            byte g = (byte)((64 - p.Age) * 25);                           // grey specular, byte-wrap = shimmer
            p.Spec = new Color32(g, g, g, 0);
            p.Age++;
            if (p.Age >= 64) p.Alive = false;                            // ageCap (maxrange dist-kill off in test)
        }

        // ===== FAITHFUL PORT: setId 19 — Staub/settling dust (FUN_004b1480) =========================
        // Static (vel=0). Box spawn scaled by dir (test dir={2 2 4}). Random size (rand/16847·1.8),
        // grows ×1.03/frame for the first 49 frames, then spreads outward along a stored radial vector
        // (when near the emitter plane) else drifts. Grey sprite e_256 row 1, slow spin ±0.01, ageCap 128.
        void Step19()
        {
            _drift.x = (_drift.x + (_rng.Bits(0xf) - 8) / 6200f) * 0.98f;
            _drift.y = (_drift.y + (_rng.Bits(0xf) - 8) / 5500f) * 0.98f;
            _drift.z = (_drift.z + (_rng.Bits(0xf) - 8) / 6200f) * 0.98f;
            if (_active && !_paused)
            {
                int baseN = _src != null ? Mathf.Max(0, _src.NumPerFrame) : 0;
                int n = Mathf.Max(0, baseN - 1 + _rng.Mod(3));
                for (int i = 0; i < n; i++) { int s = Alloc(); if (s < 0) break; Spawn19(ref _pool[s]); }
            }
            AliveCount = 0;
            for (int i = 0; i < _high; i++)
            {
                if (!_pool[i].Alive) continue;
                Integrate19(ref _pool[i]);
                if (_pool[i].Alive) AliveCount++;
            }
        }

        void Spawn19(ref P p)
        {
            p.Alive = true;
            p.Bill = 0;
            Vector3 dir = _src != null ? _src.Direction : Vector3.zero;
            Vector3 offOrig = new(dir.x * (_rng.U15() * 3.0517578e-05f - 0.5f),
                                  -dir.y * (_rng.U15() * 3.0517578e-05f),
                                  dir.z * (_rng.U15() * 3.0517578e-05f - 0.5f));
            p.Pos = _worldPos + ToUnity(offOrig);            // box spread scaled by dir
            p.Vel = Vector3.zero;                            // static
            p.Base = (_rng.U15() / 16847f) * 1.8f;           // random size 0..~3.5
            p.Size = p.Base;
            p.Age = _rng.Bits(7) + _rng.Mod(12);             // random init age 0..18
            p.Spin = (_rng.U15() % 360) / 180f * Mathf.PI;   // random initial rotation
            p.Cell = (1 << 16) | (_rng.U15() % 3);           // e_256 row 1 (grey), u = rand%3
            p.Color = new Color32(255, 255, 255, 255);
            p.Spec = new Color32(0, 0, 0, 0);
            // stored radial spread vector = (horiz dist / 100) · horizontal offset from the emitter
            Vector3 to = p.Pos - _worldPos;
            float f = Mathf.Sqrt(to.x * to.x + to.z * to.z) / 100f;
            p.Aux = new Vector3(f * to.x, 0f, f * to.z);
        }

        void Integrate19(ref P p)
        {
            p.Spin += p.Spin >= 0f ? 0.01f : -0.01f;         // slow rotation (DAT_00736fe0)
            p.Age++;
            if (p.Age < 49) p.Size *= 1.03f;                 // grows (_DAT_00737140)
            else if (p.Pos.y <= _worldPos.y + 0.5f) p.Pos += p.Aux;   // spread outward near the plane
            else p.Pos.y -= 0.005f;                          // else drift down (DAT_00736fcc)
            if (p.Age >= 128) p.Alive = false;               // ageCap (maxrange dist-kill off in test)
        }

        // ===== FAITHFUL PORT: setId 20 — brown dirt cloud (FUN_004b2b50) ============================
        // Box spawn ±0.4, vel = dir + jitter ±0.04, size 1.7 SHRINKS −0.03/frame, drag 0.95, slow spin
        // ±0.01, ageCap 32. Diffuse = olive/brown 0x7f753f (encoded as &DAT_007f753f), c_256d row 2.
        void Step20()
        {
            _drift.x = (_drift.x + (_rng.Bits(0xf) - 8) / 6200f) * 0.98f;
            _drift.y = (_drift.y + (_rng.Bits(0xf) - 8) / 5500f) * 0.98f;
            _drift.z = (_drift.z + (_rng.Bits(0xf) - 8) / 6200f) * 0.98f;
            if (_active && !_paused)
            {
                int baseN = _src != null ? Mathf.Max(0, _src.NumPerFrame) : 0;
                int n = Mathf.Max(0, baseN - 1 + _rng.Mod(3));
                for (int i = 0; i < n; i++) { int s = Alloc(); if (s < 0) break; Spawn20(ref _pool[s]); }
            }
            AliveCount = 0;
            for (int i = 0; i < _high; i++)
            {
                if (!_pool[i].Alive) continue;
                Integrate20(ref _pool[i]);
                if (_pool[i].Alive) AliveCount++;
            }
        }

        void Spawn20(ref P p)
        {
            p.Alive = true;
            p.Bill = 0;
            Vector3 offOrig = new((_rng.Bits(0xf) - 8) / 20f, (_rng.Bits(0xf) - 8) / 20f, (_rng.Bits(0xf) - 8) / 20f);
            p.Pos = _worldPos + ToUnity(offOrig);            // box ±0.4
            Vector3 dir = _src != null ? _src.Direction : Vector3.zero;
            Vector3 velOrig = dir + new Vector3((_rng.Bits(0xf) - 8) / 200f, (_rng.Bits(0xf) - 8) / 200f, (_rng.Bits(0xf) - 8) / 200f);
            p.Vel = ToUnity(velOrig);
            p.Base = 1.7f; p.Size = 1.7f;                    // +0x28 = 0x3fd9999a
            p.Age = _rng.Bits(7) + _rng.Mod(12);
            p.Spin = (_rng.U15() % 80) / 180f + (_rng.U15() % 360) / 180f * Mathf.PI;
            p.Color = new Color32(0x7f, 0x75, 0x3f, 255);    // olive/brown (0x007f753f)
            p.Spec = new Color32(0, 0, 0, 0);
            p.Cell = (2 << 16) | _rng.Bits(1);               // c_256d row 2, u = rand&1
        }

        void Integrate20(ref P p)
        {
            p.Pos += p.Vel;
            p.Size -= 0.03f;                                 // shrinks (_DAT_00736ff8)
            p.Vel *= 0.95f;                                  // drag (DAT_00737114)
            p.Spin += p.Spin >= 0f ? 0.01f : -0.01f;         // slow rotation
            p.Age++;
            if (p.Age >= 32) p.Alive = false;                // ageCap (maxrange dist-kill off in test)
        }

        // ===== FAITHFUL PORT: setId 11 — workshop smoke variant (FUN_004a8bb0) ======================
        // "Rauch" (brauerei/dampfmaschine). A SMOKE type (uses ToUnity, NOT the ground-collision raycast) —
        // it just has the terrain spawn-gate (skipped here). Emitter drift 6200/5500/6200 @0.98; disc spawn
        // (radius (rand%80)/180); vel = dir + frame-shared spawn drift (±8/140) + jitter (±8/400). Per frame
        // drag+drift coupling (vel·90 + driftForce·10)/140, driftForce = drift·4 with a −0.12 Y lift; grows
        // +0.15, spins ±0.02, cell walks a_128d row 15 (u = 4 + age/16). Init age 0..18, ageCap 64.
        void Step11()
        {
            _drift.x = (_drift.x + (_rng.Bits(0xf) - 8) / 6200f) * 0.98f;
            _drift.y = (_drift.y + (_rng.Bits(0xf) - 8) / 5500f) * 0.98f;
            _drift.z = (_drift.z + (_rng.Bits(0xf) - 8) / 6200f) * 0.98f;
            if (_active && !_paused)
            {
                Vector3 spawnDrift = new Vector3((_rng.Bits(0xf) - 8) / 140f,
                                                 (_rng.Bits(0xf) - 8) / 140f,
                                                 (_rng.Bits(0xf) - 8) / 140f);
                int baseN = _src != null ? Mathf.Max(0, _src.NumPerFrame) : 0;
                int n = Mathf.Max(0, baseN - 1 + _rng.Mod(3));
                for (int i = 0; i < n; i++) { int s = Alloc(); if (s < 0) break; Spawn11(ref _pool[s], spawnDrift); }
            }
            AliveCount = 0;
            for (int i = 0; i < _high; i++)
            {
                if (!_pool[i].Alive) continue;
                Integrate11(ref _pool[i]);
                if (_pool[i].Alive) AliveCount++;
            }
        }

        void Spawn11(ref P p, Vector3 spawnDrift)
        {
            p.Alive = true;
            p.Bill = 0;
            float ang = (_rng.U15() % 360) / 180f * Mathf.PI;
            float rad = (_rng.U15() % 80) / 180f;                // disc radius ~0..0.44
            float yoff = (_rng.U15() % 10) / 200f;
            p.Pos = _worldPos + ToUnity(new Vector3(Mathf.Sin(ang) * rad, yoff, Mathf.Cos(ang) * rad));
            Vector3 dir = _src != null ? _src.Direction : Vector3.zero;
            Vector3 jitter = new Vector3((_rng.Bits(0xf) - 8) / 400f, (_rng.Bits(0xf) - 8) / 400f, (_rng.Bits(0xf) - 8) / 400f);
            p.Vel = ToUnity(dir + spawnDrift + jitter);          // smoke → ToUnity (dir.y<0 = up)
            p.Base = 2.5f; p.Size = 2.5f;                        // +0x28 = 0x40200000
            p.Age = _rng.Bits(7) + _rng.Mod(12);                 // random init age 0..18
            p.Spin = -spawnDrift.x;                              // seed = sign-flipped spawn drift x
            p.Cell = 0xf0007;                                    // (v15,u7); integrate sets u = 4 + age/16
            p.Color = new Color32(255, 255, 255, 255);
            p.Spec = new Color32(0, 0, 0, 0);
        }

        void Integrate11(ref P p)
        {
            p.Pos += p.Vel;
            // drag + drift coupling: vel = (vel·90 + driftForce·10)/140; driftForce = drift·4 with a −0.12
            // Y lift (orig negative Y = up). _drift is orig-space → ToUnity the force.
            Vector3 fOrig = new(_drift.x * 4f, _drift.y * 4f - 0.12f, _drift.z * 4f);
            p.Vel = (p.Vel * 90f + ToUnity(fOrig) * 10f) / 140f;
            p.Size += 0.15f;                                     // grows (_DAT_0073704c)
            p.Spin += p.Spin >= 0f ? 0.02f : -0.02f;             // slow rotation (DAT_00736ff0)
            p.Cell = (15 << 16) | (4 + p.Age / 16);              // a_128d row 15, u 4..7 by age/16
            p.Age++;
            if (p.Age >= 64) p.Alive = false;                    // ageCap (maxrange dist-kill off in test)
        }

        // ===== FAITHFUL PORT: setId 13 — magic sparkles with comet trails (FUN_004aa180) ============
        // magie.tcl item cast. No emitter drift. Point spawn; velocity = dir + a random spherical vector
        // (sin θ, sin φ, cos θ)·r, r = (rand%50)/850, θ=rand%360°, φ=rand%180° (sin φ ≥ 0 → a downward
        // bias in the Y-down world →). A two-tier life: a MAIN spark (age 0..24) every
        // frame spawns a small trail CHILD at its current position, drifts down (+0.005/frame), and runs
        // a white→magenta→purple colour ramp; the spark dies at 24. Each CHILD (born at age 25, no
        // sub-children) steers toward its parent (vel = 0.8·vel + 0.2·(parent−pos)), keeps the gold
        // diffuse with a fading white specular core, can twinkle out (~9%/frame), and dies at age 65.
        void Step13()
        {
            if (_active && !_paused)
            {
                int n = _src != null ? Mathf.Max(0, _src.NumPerFrame) : 0;
                for (int i = 0; i < n; i++) { int s = Alloc(); if (s < 0) break; Spawn13(ref _pool[s]); }
            }
            AliveCount = 0;
            for (int i = 0; i < _high; i++)
            {
                if (!_pool[i].Alive) continue;
                Integrate13(i);
                if (_pool[i].Alive) AliveCount++;
            }
        }

        void Spawn13(ref P p)
        {
            p.Alive = true;
            p.Bill = 0;
            p.Parent = 0;                                       // main spark, no parent
            p.Pos = _worldPos;                                  // point spawn
            Vector3 dir = _src != null ? _src.Direction : Vector3.zero;
            float theta = (_rng.U15() % 360) / 180f * Mathf.PI;
            float phi   = (_rng.U15() % 180) / 180f * Mathf.PI; // 0..π → sin φ ≥ 0 (downward bias in Y-down)
            float r     = (_rng.U15() % 50) / 850f;
            Vector3 velOrig = new Vector3(Mathf.Sin(theta) * r + dir.x,
                                          Mathf.Sin(phi)   * r + dir.y,
                                          Mathf.Cos(theta) * r + dir.z);
            p.Vel = ToUnity(velOrig);
            p.Base = 2.0f; p.Size = 2.0f;                       // +0x28 = 0x40000000
            p.Age = _rng.U15() % 10;                            // random init age 0..9
            p.Spin = 0f;
            p.Cell = (3 << 16) | 2;                             // 0x30002 → c_256d (v3,u2)
            p.Color = new Color32(0xff, 0xe0, 0x60, 255);       // gold (overwritten by the ramp while young)
            p.Spec = new Color32(0, 0, 0, 0);
        }

        void Integrate13(int i)
        {
            ref P p = ref _pool[i];
            p.Spin += 0.08f;                                    // +0x30 += _DAT_00737024
            Vector3 dvOrig = new Vector3((_rng.Bits(0xf) - 8) / 6000f,
                                         (_rng.Bits(0xf) - 10) / 6000f,
                                         (_rng.Bits(0xf) - 8) / 6000f);
            p.Vel += ToUnity(dvOrig);
            p.Pos += p.Vel;

            if (p.Parent > 0)                                   // trail child: fading core, steer to parent, twinkle
            {
                byte g = (byte)Mathf.Clamp((64 - p.Age) * 20, 0, 255);
                p.Spec = new Color32(g, g, g, 0);
                if (p.Parent < _high && _pool[p.Parent].Alive)
                {
                    P parent = _pool[p.Parent];
                    if (parent.Age > 0 && parent.Age < 24)      // steer toward a still-young parent
                        p.Vel = (p.Vel * 80f + (parent.Pos - p.Pos) * 20f) / 100f;
                }
                if (_rng.U15() < 3000) { p.Age = 63; p.Size = 0.3f; p.Spec = new Color32(255, 255, 255, 0); }
            }

            p.Color = new Color32(0xff, 0xe0, 0x60, 255);       // gold (overwritten below while young)
            p.Age++;

            if (p.Age < 24)                                     // young main spark: down-drift, ramp, spawn trail child
            {
                byte g = (byte)Mathf.Clamp((32 - p.Age) * 25, 0, 255);
                p.Spec = new Color32(g, g, g, 0);
                p.Vel.y -= 0.005f;                              // orig +0.005 (down in Y-down) → −Y in Unity
                byte R = (byte)Mathf.Clamp((24 - p.Age) * 0x20, 0, 255);
                byte G = (byte)Mathf.Clamp((8  - p.Age) * 0x20, 0, 255);
                byte B = (byte)Mathf.Clamp((24 - p.Age) * 0x10, 0, 255);
                p.Color = new Color32(R, G, B, 255);            // white → magenta → purple
                int c = Alloc();
                if (c >= 0)
                {
                    ref P child = ref _pool[c];
                    child.Alive = true;
                    child.Bill = 0;
                    child.Parent = i;                           // link back to this spark
                    child.Pos = p.Pos;                          // spawn at the spark's current position
                    child.Vel = Vector3.zero;
                    child.Base = 0.3f; child.Size = 0.3f;       // +0x28 = 0x3e99999a
                    child.Age = 25;                             // born "old" → no sub-children
                    child.Spin = 0f;
                    child.Cell = (3 << 16) | 2;                 // 0x30002
                    child.Color = new Color32(0xff, 0xe0, 0x60, 255);
                    child.Spec = new Color32(0, 0, 0, 0);
                }
            }

            if (p.Age >= 65 || p.Age == 24) p.Alive = false;    // main spark dies at 24, trail child at 65
        }

        // ===== FAITHFUL PORT: setId 6 — workshop smoke plume (FUN_004a5080) =========================
        // The common chimney/cauldron smoke (brauerei, dampfhammer, dampfmaschine, …). Emitter drift
        // (3200/5500/3200, damp 0.98). Point spawn at the emitter; velocity = dir + a frame-shared spawn
        // drift (±8/140) + per-particle jitter (±8/400). Per frame: drag + drift coupling
        // (vel·0.9 + driftForce·0.1, driftForce = drift·4 with a −0.05 Y lift), grows +0.15, spins ±0.2,
        // cell walks a_128d row 15 (u 4→7 by age/8). Random init age 0..18, ageCap 32. Original gates
        // spawn on the terrain heightfield (skipped here) and can emit from object verts (not ported).
        void Step6()
        {
            _drift.x = (_drift.x + (_rng.Bits(0xf) - 8) / 3200f) * 0.98f;
            _drift.y = (_drift.y + (_rng.Bits(0xf) - 8) / 5500f) * 0.98f;
            _drift.z = (_drift.z + (_rng.Bits(0xf) - 8) / 3200f) * 0.98f;
            if (_active && !_paused)
            {
                // frame-shared spawn drift, added to every particle's spawn velocity this frame (orig
                // computes fVar18/20/23 once outside the spawn loop).
                Vector3 spawnDrift = new Vector3((_rng.Bits(0xf) - 8) / 140f,
                                                 (_rng.Bits(0xf) - 8) / 140f,
                                                 (_rng.Bits(0xf) - 8) / 140f);
                int baseN = _src != null ? Mathf.Max(0, _src.NumPerFrame) : 0;
                int n = Mathf.Max(0, baseN - 1 + _rng.Mod(3));
                for (int i = 0; i < n; i++) { int s = Alloc(); if (s < 0) break; Spawn6(ref _pool[s], spawnDrift); }
            }
            AliveCount = 0;
            for (int i = 0; i < _high; i++)
            {
                if (!_pool[i].Alive) continue;
                Integrate6(ref _pool[i]);
                if (_pool[i].Alive) AliveCount++;
            }
        }

        void Spawn6(ref P p, Vector3 spawnDrift)
        {
            p.Alive = true;
            p.Bill = 0;
            p.Pos = _worldPos;                                       // point spawn at the emitter
            Vector3 dir = _src != null ? _src.Direction : Vector3.zero;
            Vector3 jitter = new Vector3((_rng.Bits(0xf) - 8) / 400f,
                                         (_rng.Bits(0xf) - 8) / 400f,
                                         (_rng.Bits(0xf) - 8) / 400f);
            p.Vel = ToUnity(dir + spawnDrift + jitter);             // vel = dir + frame drift + per-particle jitter
            p.Base = 2.5f; p.Size = 2.5f;                           // +0x28 = 0x40200000
            p.Age = _rng.Bits(7) + _rng.Mod(12);                    // random init age 0..18
            p.Spin = -spawnDrift.x;                                  // seed = sign-flipped spawn drift x (±0.057)
            p.Cell = 0xf0007;                                        // (v15,u7); integrate sets u = 4 + age/8
            p.Color = new Color32(255, 255, 255, 255);
            p.Spec = new Color32(0, 0, 0, 0);
        }

        void Integrate6(ref P p)
        {
            p.Pos += p.Vel;
            // drag + drift coupling: vel = (vel·90 + driftForce·10)/100; driftForce = drift·4 with a −0.05
            // Y lift (orig negative Y = up). _drift is kept in orig space → ToUnity the force.
            Vector3 fOrig = new(_drift.x * 4f, _drift.y * 4f - 0.05f, _drift.z * 4f);
            p.Vel = (p.Vel * 90f + ToUnity(fOrig) * 10f) / 100f;
            p.Size += 0.15f;                                        // grows (_DAT_0073704c)
            p.Spin += p.Spin >= 0f ? 0.2f : -0.2f;                  // rotation accelerates away from 0 (DAT_00737054)
            p.Cell = (15 << 16) | (4 + p.Age / 8);                  // a_128d row 15, u walks 4..7 by age/8
            p.Age++;
            if (p.Age >= 32) p.Alive = false;                       // ageCap (maxrange dist-kill off in test)
        }

        // ===== FAITHFUL PORT: setId 21 — bath mist / spray (FUN_004b35c0) ===========================
        // The bath steam (Industriebad/Luxusbad/Mittelalterbad). No emitter drift. Box spawn (±8/180);
        // velocity = dir + jitter (x/z ±8/180; y = (rand&15−16)/185 → up in Y-down). Kicks UP, then a
        // constant +0.01/frame (down in Y-down) pulls it back under gravity. Spins ±0.06 from a random
        // initial angle; cell walks d_256d row v=rand&1, u = age/4. Random init age 0..31, ageCap 32.
        void Step21()
        {
            if (_active && !_paused)
            {
                int n = _src != null ? Mathf.Max(0, _src.NumPerFrame) : 0;
                for (int i = 0; i < n; i++) { int s = Alloc(); if (s < 0) break; Spawn21(ref _pool[s]); }
            }
            AliveCount = 0;
            for (int i = 0; i < _high; i++)
            {
                if (!_pool[i].Alive) continue;
                Integrate21(ref _pool[i]);
                if (_pool[i].Alive) AliveCount++;
            }
        }

        void Spawn21(ref P p)
        {
            p.Alive = true;
            p.Bill = 0;
            Vector3 offOrig = new((_rng.Bits(0xf) - 8) / 180f, (_rng.Bits(0xf) - 8) / 180f, (_rng.Bits(0xf) - 8) / 180f);
            p.Pos = _worldPos + ToUnity(offOrig);               // box spawn ±8/180
            Vector3 dir = _src != null ? _src.Direction : Vector3.zero;
            Vector3 velOrig = new(dir.x + (_rng.Bits(0xf) - 8) / 180f,
                                  dir.y + (_rng.Bits(0xf) - 16) / 185f,   // (rand&15−16) = −16..−1 → up in Y-down
                                  dir.z + (_rng.Bits(0xf) - 8) / 180f);
            p.Vel = ToUnity(velOrig);
            p.Base = 2.5f; p.Size = 2.5f;                       // +0x28 = 0x40200000
            p.Age = _rng.U15() & 0x1f;                          // random init age 0..31
            p.Spin = (_rng.U15() & 0x7fff) / 5215f;             // random initial rotation 0..2π
            p.Cell = (_rng.Bits(1)) << 16;                      // v = rand&1, u = 0 (integrate sets u = age/4)
            p.Color = new Color32(255, 255, 255, 255);
            p.Spec = new Color32(0, 0, 0, 0);
        }

        void Integrate21(ref P p)
        {
            p.Spin += p.Spin >= 0f ? 0.06f : -0.06f;            // rotation accelerates away from 0 (_DAT_00737018)
            p.Pos += p.Vel;
            p.Cell = (p.Cell & unchecked((int)0xFFFF0000)) | (p.Age / 4);   // u walks 0..7 by age/4
            p.Vel.y -= 0.01f;                                   // orig +0.01 (down in Y-down) → gravity (DAT_00736fe0)
            p.Age++;
            if (p.Age >= 32) p.Alive = false;                   // ageCap (maxrange dist-kill off in test)
        }

        // ===== FAITHFUL PORT: setId 22 — big grey steam jets (FUN_004b3fd0) =========================
        // Large (5.5) grey d_256d spray-streak sprites. No emitter drift. Each spawn attempt only fires
        // ~25% of the time (rng<0x2000). Box spawn (x/y ±8·0.125, z ±8·0.2); vel = dir + jitter (x/z /540,
        // y /340). Per frame a constant +0.008/frame (down in Y-down) sinks it. The cell PING-PONGS: an
        // index local_c counts 7→0 over age 0..15 then 0→8 over age 16..32, mapped to d_256d (grid 4)
        // (v = local_c/4 + 2, u = local_c%4) → streak cells 8..15. Const grey diffuse+specular. ageCap 32.
        void Step22()
        {
            if (_active && !_paused)
            {
                int n = _src != null ? Mathf.Max(0, _src.NumPerFrame) : 0;
                for (int i = 0; i < n; i++)
                {
                    if (_rng.U15() >= 0x2000) continue;          // ~25% gated spawn (rng < 0x2000)
                    int s = Alloc(); if (s < 0) break; Spawn22(ref _pool[s]);
                }
            }
            AliveCount = 0;
            for (int i = 0; i < _high; i++)
            {
                if (!_pool[i].Alive) continue;
                Integrate22(ref _pool[i]);
                if (_pool[i].Alive) AliveCount++;
            }
        }

        void Spawn22(ref P p)
        {
            p.Alive = true;
            p.Bill = 0;
            Vector3 offOrig = new((_rng.Bits(0xf) - 8) * 0.125f, (_rng.Bits(0xf) - 8) * 0.125f, (_rng.Bits(0xf) - 8) * 0.2f);
            p.Pos = _worldPos + ToUnity(offOrig);                // box spawn (x/y ±8·0.125, z ±8·0.2)
            Vector3 dir = _src != null ? _src.Direction : Vector3.zero;
            Vector3 velOrig = new(dir.x + (_rng.Bits(0xf) - 8) / 540f,
                                  dir.y + (_rng.Bits(0xf) - 8) / 340f,
                                  dir.z + (_rng.Bits(0xf) - 8) / 540f);
            p.Vel = ToUnity(velOrig);
            p.Base = 5.5f; p.Size = 5.5f;                        // +0x28 = 0x40b00000
            p.Age = 0;                                           // NOT random (gated spawn desyncs them)
            p.Spin = 0.7853982f;                                 // +0x30 = π/4 (0x3f490fdb)
            p.Color = new Color32(0x7f, 0x7f, 0x7f, 255);        // const grey 0x7f7f7f7f
            p.Spec = new Color32(0x7f, 0x7f, 0x7f, 0);           // const grey specular
            p.Cell = 2 << 16;                                    // 0x20000 (v2,u0); integrate ping-pongs it
        }

        void Integrate22(ref P p)
        {
            p.Pos += p.Vel;
            p.Vel.y -= 0.008f;                                   // orig +0.008 (down in Y-down) (_DAT_00736fdc)
            int localC = p.Age < 16 ? 7 - p.Age / 2 : (p.Age - 16) / 2;   // ping-pong 7→0 then 0→8
            int v = localC / 4 + 2, u = localC % 4;
            p.Cell = (v << 16) | u;                              // d_256d streak cells 8..15
            p.Age++;
            if (p.Age > 32) p.Alive = false;                     // ageCap (kill at age > 0x20)
        }

        // ===== FAITHFUL PORT: setId 23 — grey rising bubbles (FUN_004b47c0) =========================
        // Grey d_256d ORB sprite (grid 8 cell (v2,u0) = cell 16) rising from a surface. No emitter drift.
        // Gated ~25% spawn (rng<0x2000). Box spawn (x/z ±8·0.125) with a +4 Y offset (orig down → at the
        // water surface below the emitter). vel = dir (no jitter). Random size rand/23768 + 0.5. Per frame
        // vel.y −0.008 (up in Y-down → rises), spins ±0.002 (invisible on the round orb). Const grey
        // diffuse + specular, fixed cell. ageCap 32.
        void Step23()
        {
            if (_active && !_paused)
            {
                int n = _src != null ? Mathf.Max(0, _src.NumPerFrame) : 0;
                for (int i = 0; i < n; i++)
                {
                    if (_rng.U15() >= 0x2000) continue;          // ~25% gated spawn
                    int s = Alloc(); if (s < 0) break; Spawn23(ref _pool[s]);
                }
            }
            AliveCount = 0;
            for (int i = 0; i < _high; i++)
            {
                if (!_pool[i].Alive) continue;
                Integrate23(ref _pool[i]);
                if (_pool[i].Alive) AliveCount++;
            }
        }

        void Spawn23(ref P p)
        {
            p.Alive = true;
            p.Bill = 0;
            Vector3 offOrig = new((_rng.Bits(0xf) - 8) * 0.125f,
                                  (_rng.Bits(0xf) - 8) * 0.125f + 4f,    // +4 = down to the water surface (orig Y-down)
                                  (_rng.Bits(0xf) - 8) * 0.125f);
            p.Pos = _worldPos + ToUnity(offOrig);
            p.Vel = ToUnity(_src != null ? _src.Direction : Vector3.zero);   // vel = dir (no jitter)
            p.Base = (_rng.U15() & 0x7fff) / 23768f + 0.5f;     // random size 0.5..~1.88
            p.Size = p.Base;
            p.Age = 0;
            p.Spin = 0.7853982f;                                 // +0x30 = π/4
            p.Color = new Color32(0x7f, 0x7f, 0x7f, 255);        // const grey 0x7f7f7f7f
            p.Spec = new Color32(0x7f, 0x7f, 0x7f, 0);
            p.Cell = 2 << 16;                                    // 0x20000 (v2,u0) = the orb (grid-8 cell 16)
        }

        void Integrate23(ref P p)
        {
            p.Pos += p.Vel;
            p.Vel.y += 0.008f;                                   // orig −0.008 (up in Y-down → rises) (_DAT_00736fdc)
            p.Spin += p.Spin >= 0f ? 0.002f : -0.002f;           // slow rotation (_DAT_00736fc0)
            p.Age++;
            if (p.Age > 32) p.Alive = false;                     // ageCap (kill at age > 0x20)
        }

        // ===== FAITHFUL PORT: setId 15 — liquid drops falling to the ground (FUN_004ac020) ==========
        // GROUND-COLLISION family (terrain → Unity colliders). Tiny disc spawn at the emitter; vel = dir
        // (used DIRECTLY in Unity space — these types don't use the fire/smoke ToUnity map). Falls at a
        // constant velocity with a tiny horizontal splatter, spins by its speed. When a downward raycast
        // finds ground at/above the particle it LANDS: snaps to the surface, swaps to the splat cell, sets
        // the landed flag (the fall stops) and then RESTS, ageing normally until it shrinks (age>50) and
        // dies (age>95). b_128d grid 16: drop cells (v6, u8..13), splat (v6, u14/15).
        void Step15()
        {
            if (_active && !_paused)
            {
                int n = _src != null ? Mathf.Max(0, _src.NumPerFrame) : 0;
                for (int i = 0; i < n; i++) { int s = Alloc(); if (s < 0) break; Spawn15(ref _pool[s]); }
            }
            AliveCount = 0;
            for (int i = 0; i < _high; i++)
            {
                if (!_pool[i].Alive) continue;
                Integrate15(ref _pool[i]);
                if (_pool[i].Alive) AliveCount++;
            }
        }

        void Spawn15(ref P p)
        {
            p.Alive = true;
            p.Bill = 0;                                          // 0 = falling, 1 = landed (orig +0x40 flag)
            float ang = (_rng.U15() % 360) / 180f * Mathf.PI;
            float ang2 = (_rng.U15() % 360) / 180f * Mathf.PI;
            float r = (_rng.U15() % 5) / 180f;                   // tiny spread (~0.02)
            p.Pos = _worldPos + new Vector3(Mathf.Sin(ang) * r, Mathf.Sin(ang2) * r, Mathf.Cos(ang) * r);
            p.Vel = _src != null ? _src.Direction : Vector3.zero;   // dir DIRECT (ground family Unity-space; dir.y<0 = down)
            p.Base = 0.6f; p.Size = 0.6f;                        // +0x28 = 0x3f19999a
            p.Age = _rng.U15() & 0x1f;                           // random init age 0..31
            p.Spin = 0f;
            p.Cell = (6 << 16) | (8 + _rng.Mod(6));              // b_128d grid16 drop cells (v6, u8..13)
            p.Color = new Color32(255, 255, 255, 255);
            p.Spec = new Color32(0, 0, 0, 0);
        }

        void Integrate15(ref P p)
        {
            p.Age++;
            if (p.Bill == 0)                                     // falling
            {
                p.Pos += p.Vel;
                p.Vel.x += _rng.U15() * 3.0517578e-05f * 0.01f;  // tiny horizontal splatter (orig 0.01·rand01)
                p.Vel.z += _rng.U15() * 3.0517578e-05f * 0.01f;
                p.Spin += p.Vel.magnitude;                       // spin by speed (orig += |vel|)
                if (GroundUnder(p.Pos, out float gy, out _) && p.Pos.y <= gy)
                {
                    p.Pos.y = gy;                                // settle on the surface (orig: push up to it)
                    p.Vel = Vector3.zero;                        // stop (also: render interp won't drift it)
                    p.Bill = 1;                                  // landed → the fall block above stops running
                    p.Cell = (6 << 16) | (14 + _rng.Bits(1));    // splat cell (v6, u14/15)
                    // NOTE: orig only force-ages to 100 (instant death) when it CAN'T settle in 40 steps
                    // (deep underground); the normal landing keeps ageing → the splat rests then fades.
                }
            }
            // landed (Bill==1) or still falling: age out — shrink late, die at the cap. A settled drop
            // sits on the ground (vel no longer applied) until it ages away.
            if (p.Age > 50) p.Size *= 0.98f;                     // shrink late (DAT_00737108)
            if (p.Age > 95) p.Alive = false;                     // ageCap (kill at age > 0x5f)
        }

        // ===== FAITHFUL PORT: setId 18 — bouncing sparks (FUN_004af7e0) =============================
        // GROUND-COLLISION family. Yellow b_128d sparks (grid 8 cell (v4,u1)). Small x spawn spread; vel =
        // dir + jitter (x/430, y/435, z/337). Gravity −0.01/step; oriented along velocity (spin = π/4 −
        // atan2(vel.y,vel.x)). On a downward ground hit with bounces left (Aux.x, starts 4) and speed²>0.01
        // it REFLECTS off the collider normal (Vector3.Reflect) damped ×0.7 — replacing the original's
        // terrain-slope estimation (it sampled neighbour cells; the Unity normal gives it for free). When
        // bounces run out / too slow it settles (Vel=0). Yellow fades (32−age)·7; red specular flickers.
        // ageCap 32.
        void Step18()
        {
            if (_active && !_paused)
            {
                int n = _src != null ? Mathf.Max(0, _src.NumPerFrame) : 0;
                for (int i = 0; i < n; i++) { int s = Alloc(); if (s < 0) break; Spawn18(ref _pool[s]); }
            }
            AliveCount = 0;
            for (int i = 0; i < _high; i++)
            {
                if (!_pool[i].Alive) continue;
                Integrate18(ref _pool[i]);
                if (_pool[i].Alive) AliveCount++;
            }
        }

        void Spawn18(ref P p)
        {
            p.Alive = true;
            p.Bill = 0;
            p.Pos = _worldPos + new Vector3((_rng.Bits(0xf) - 8) * 0.0078125f, 0f, 0f);   // small x spread
            Vector3 dir = _src != null ? _src.Direction : Vector3.zero;   // DIRECT (ground family Unity-space; dir.y<0 = down)
            p.Vel = dir + new Vector3((_rng.Bits(0xf) - 8) / 430f, (_rng.Bits(0xf) - 8) / 435f, (_rng.Bits(0xf) - 8) / 337f);
            p.Base = 0.6f; p.Size = 0.6f;                        // +0x28 = 0x3f19999a
            p.Age = _rng.U15() & 0x1f;                           // random init age 0..31
            p.Spin = 0.7853982f;
            p.Color = new Color32(255, 255, 0, 255);             // yellow 0xffff00
            p.Spec = new Color32(0, 0, 0, 0);
            p.Cell = (4 << 16) | 1;                              // b_128d grid8 (v4, u1)
            p.Aux = new Vector3(4f, 0f, 0f);                     // bounce count (orig +0x24)
        }

        void Integrate18(ref P p)
        {
            if (p.Bill == 0)                                     // airborne / bouncing
            {
                p.Pos += p.Vel;
                p.Vel.y -= 0.01f;                                // gravity (Unity down) (_DAT_00736fe0)
                p.Spin = 0.7853982f - Mathf.Atan2(p.Vel.y, p.Vel.x);   // orient along velocity (DAT_007370d0=π/4)
                if (GroundUnder(p.Pos, out float gy, out Vector3 nrm) && p.Pos.y <= gy)
                {
                    if (p.Aux.x > 0f && p.Vel.sqrMagnitude > 0.01f)   // bounce (DAT_00736fe4 speed² threshold)
                    {
                        p.Vel = Vector3.Reflect(p.Vel, nrm) * 0.3f;   // reflect + heavy damp (orig 0.7 flung
                        // sparks up on a flat floor; 0.3 keeps the bounce low on the bench)
                        p.Aux.x -= 1f;
                        p.Pos.y = gy + 0.01f;                         // nudge above the surface
                    }
                    else { p.Pos.y = gy; p.Vel = Vector3.zero; p.Bill = 1; }   // settle
                }
            }
            byte yc = (byte)Mathf.Clamp((32 - p.Age) * 7, 0, 255);   // yellow fade (R=G=(32−age)·7, B=0)
            p.Color = new Color32(yc, yc, 0, 255);
            byte sr = (byte)(_rng.U15() & 0x3f);                 // flickering red specular (orig (rand&0x3f)<<16)
            p.Spec = new Color32(sr, 0, 0, 0);
            p.Age++;
            if (p.Age > 32) p.Alive = false;                     // ageCap (kill at age > 0x20)
        }

        // ===== FAITHFUL PORT: setId 8 — settling dust (FUN_004a66c0) =================================
        // GROUND-COLLISION family (tempel "Blutfontänen"). Small (0.4) white b_128d sprites (grid 16, cell
        // v=rand&3, u=rand&0xf). Tight sphere spawn (radius 0.02); vel = dir + jitter, with vel.y DOWN-biased
        // ((rand&15−20)/180 → always negative) so the dust drifts down without gravity. Spins by speed; on a
        // ground hit it sticks (snap + Vel=0). Shrinks after age 50, dies after 95 (same tail as setId 15).
        void Step8()
        {
            if (_active && !_paused)
            {
                int n = _src != null ? Mathf.Max(0, _src.NumPerFrame) : 0;
                for (int i = 0; i < n; i++) { int s = Alloc(); if (s < 0) break; Spawn8(ref _pool[s]); }
            }
            AliveCount = 0;
            for (int i = 0; i < _high; i++)
            {
                if (!_pool[i].Alive) continue;
                Integrate8(ref _pool[i]);
                if (_pool[i].Alive) AliveCount++;
            }
        }

        void Spawn8(ref P p)
        {
            p.Alive = true;
            p.Bill = 0;
            float ang = (_rng.U15() % 360) / 180f * Mathf.PI;
            float ang2 = (_rng.U15() % 360) / 180f * Mathf.PI;
            p.Pos = _worldPos + new Vector3(Mathf.Sin(ang) * 0.02f, Mathf.Sin(ang2) * 0.02f, Mathf.Cos(ang) * 0.02f);
            Vector3 dir = _src != null ? _src.Direction : Vector3.zero;   // DIRECT (ground family; dir.y<0 = down)
            p.Vel = dir + new Vector3((_rng.Bits(0xf) - 8) / 180f,
                                      (_rng.Bits(0xf) - 20) / 180f,   // (rand&15−20) = −20..−5 → DOWN-biased
                                      (_rng.Bits(0xf) - 8) / 180f);
            p.Base = 0.4f; p.Size = 0.4f;                        // +0x28 = 0x3ecccccd
            p.Age = _rng.U15() & 0x1f;                           // random init age 0..31
            p.Spin = 0f;
            p.Color = new Color32(255, 255, 255, 255);
            p.Spec = new Color32(0, 0, 0, 0);
            p.Cell = ((int)(_rng.U15() & 3) << 16) | (int)(_rng.U15() & 0xf);   // b_128d grid16, v0-3 u0-15
        }

        void Integrate8(ref P p)
        {
            if (p.Bill == 0)
            {
                p.Spin += p.Vel.magnitude;                       // spin by speed
                p.Pos += p.Vel;
                if (GroundUnder(p.Pos, out float gy, out _) && p.Pos.y <= gy)
                {
                    p.Pos.y = gy; p.Vel = Vector3.zero; p.Bill = 1;   // stick (no bounce)
                }
            }
            if (p.Age > 50) p.Size *= 0.98f;                     // shrink late (DAT_00737108)
            p.Age++;
            if (p.Age > 95) p.Alive = false;                     // ageCap (kill at age > 0x5f)
        }

        // ===== FAITHFUL PORT: setId 10 — droplet drip (FUN_004a7eb0) ================================
        // GROUND-COLLISION family. Spawns just ONE particle per frame (no count loop) — a slow drip. Point
        // spawn; vel = dir + tiny horizontal jitter (±8/450) with vel.y = dir.y exactly (no bias, no
        // gravity), so it falls at the dir velocity. Small (0.4) white b_128d sprite (grid 16 row 6,
        // u=rand%6). Sticks on a ground hit; shrinks after 50, dies after 95 (shares Integrate8's tail).
        void Step10()
        {
            if (_active && !_paused)
            {
                int s = Alloc(); if (s >= 0) Spawn10(ref _pool[s]);   // exactly one per frame (orig: no loop)
            }
            AliveCount = 0;
            for (int i = 0; i < _high; i++)
            {
                if (!_pool[i].Alive) continue;
                Integrate8(ref _pool[i]);                            // same stick/shrink/age-out as setId 8
                if (_pool[i].Alive) AliveCount++;
            }
        }

        void Spawn10(ref P p)
        {
            p.Alive = true;
            p.Bill = 0;
            p.Pos = _worldPos;                                       // point spawn
            Vector3 dir = _src != null ? _src.Direction : Vector3.zero;   // DIRECT (ground family; dir.y<0 = down)
            p.Vel = new Vector3(dir.x + (_rng.Bits(0xf) - 8) / 450f, dir.y, dir.z + (_rng.Bits(0xf) - 8) / 450f);
            p.Base = 0.4f; p.Size = 0.4f;                            // +0x28 = 0x3ecccccd
            p.Age = _rng.U15() & 0x1f;                               // random init age 0..31
            p.Spin = 0f;
            p.Color = new Color32(255, 255, 255, 255);
            p.Spec = new Color32(0, 0, 0, 0);
            p.Cell = (6 << 16) | (int)(_rng.U15() % 6);              // b_128d grid16 row 6, u 0..5
        }

        // ===== FAITHFUL PORT: setId 1 — ground debris chunks (FUN_004a07d0) =========================
        // GROUND-COLLISION family. Big (2.5) white a_256d sprites (grid 8 row 0, u=rand&7). Box spawn
        // (±8/30); vel = dir + jitter (x/30, y/35, z/40). NO gravity and NO bounce — it flies in a straight
        // line and STICKS where it hits the ground (the orig stops it when its speed² drops below the
        // threshold). Spins by its speed. ageCap 100.
        void Step1()
        {
            if (_active && !_paused)
            {
                int n = _src != null ? Mathf.Max(0, _src.NumPerFrame) : 0;
                for (int i = 0; i < n; i++) { int s = Alloc(); if (s < 0) break; Spawn1(ref _pool[s]); }
            }
            AliveCount = 0;
            for (int i = 0; i < _high; i++)
            {
                if (!_pool[i].Alive) continue;
                Integrate1(ref _pool[i]);
                if (_pool[i].Alive) AliveCount++;
            }
        }

        void Spawn1(ref P p)
        {
            p.Alive = true;
            p.Bill = 0;
            Vector3 dir = _src != null ? _src.Direction : Vector3.zero;   // DIRECT (ground family; dir.y<0 = down)
            p.Pos = _worldPos + new Vector3((_rng.Bits(0xf) - 8) / 30f, (_rng.Bits(0xf) - 8) / 30f, (_rng.Bits(0xf) - 8) / 30f);
            p.Vel = dir + new Vector3((_rng.Bits(0xf) - 8) / 30f, (_rng.Bits(0xf) - 8) / 35f, (_rng.Bits(0xf) - 8) / 40f);
            p.Base = 2.5f; p.Size = 2.5f;                        // +0x28 = 0x40200000
            p.Age = _rng.U15() & 0x1f;                           // random init age 0..31
            p.Spin = 0f;
            p.Color = new Color32(255, 255, 255, 255);
            p.Spec = new Color32(0, 0, 0, 0);
            p.Cell = _rng.Bits(0x7);                             // a_256d grid8 row 0, u 0..7
        }

        void Integrate1(ref P p)
        {
            if (p.Bill == 0)                                     // airborne
            {
                p.Spin += p.Vel.magnitude;                       // spin by speed (orig += |vel|)
                p.Vel.y -= 0.02f;                                // gravity (Unity down). Orig 1 has NONE (flew
                // straight, stuck on terrain); on the flat collider bench the big Y-jitter (±0.23) leaves
                // some chunks drifting up/sideways, so add gravity so they all fall and land.
                p.Pos += p.Vel;
                if (GroundUnder(p.Pos, out float gy, out _) && p.Pos.y <= gy)
                {
                    p.Pos.y = gy; p.Vel = Vector3.zero; p.Bill = 1;   // stick where it lands (no bounce)
                }
            }
            p.Age++;
            if (p.Age > 100) p.Alive = false;                    // ageCap (kill at age > 0x64)
        }

        // ===== FAITHFUL PORT: setId 17 — wood shavings / Späne (FUN_004ada30) =======================
        // GROUND-COLLISION family. Brown b_128d sprites (grid 8 row 5, u=rand&7). Point spawn; velocity is
        // a CONE aligned to the emitter dir: a spread (f17,f22) rotated by the dir's azimuth (atan2(z,x)+π),
        // f22 scaled by sqrt(1−f17·10). Random size (rand%10)/25 + 0.8; spins at a fixed random rate. Falls
        // (gravity −Y), bounces off the collider normal (×0.7) up to Aux.x=4 times, then SETTLES (Vel=0,
        // landed, size ×0.75). White diffuse (brown comes from the texture). Long life: ageCap 100.
        void Step17()
        {
            if (_active && !_paused)
            {
                int n = _src != null ? Mathf.Max(0, _src.NumPerFrame) : 0;
                for (int i = 0; i < n; i++) { int s = Alloc(); if (s < 0) break; Spawn17(ref _pool[s]); }
            }
            AliveCount = 0;
            for (int i = 0; i < _high; i++)
            {
                if (!_pool[i].Alive) continue;
                Integrate17(ref _pool[i]);
                if (_pool[i].Alive) AliveCount++;
            }
        }

        void Spawn17(ref P p)
        {
            p.Alive = true;
            p.Bill = 0;
            p.Pos = _worldPos;                                   // point spawn
            Vector3 dir = _src != null ? _src.Direction : Vector3.zero;   // DIRECT (ground family; dir.y<0 = down)
            float f17 = (_rng.Bits(0xf) - 8) / 80f;              // spread comp 1
            float f21 = (_rng.Bits(0xf) - 8) / 85f;              // vel.y spread
            float f22 = ((_rng.Bits(0xf) - 8) / 87f) * Mathf.Sqrt(Mathf.Max(0f, 1f - f17 * 10f));   // spread comp 2
            float ang = Mathf.Atan2(dir.z, dir.x) + Mathf.PI;    // dir azimuth + π
            float s = Mathf.Sin(ang), c = Mathf.Cos(ang);
            p.Vel = new Vector3((s * f17 - f22 * c) + dir.x,     // spread rotated into the dir frame (a cone around dir)
                                dir.y + f21,
                                2f * (f22 * s + c * f17) + dir.z);
            p.Base = (_rng.U15() % 10) / 25f + 0.8f;             // random size 0.8..1.16
            p.Size = p.Base;
            p.Age = _rng.U15() & 0x1f;                           // random init age 0..31
            p.Spin = (_rng.U15() & 0x7fff) / 23768f;             // fixed random rotation
            p.Color = new Color32(255, 255, 255, 255);           // white (brown from the b_128d texture)
            p.Spec = new Color32(0, 0, 0, 0);
            p.Cell = (5 << 16) | _rng.Bits(0x7);                 // b_128d grid8 row 5, u 0..7
            p.Aux = new Vector3(4f, 0f, 0f);                     // bounce count (orig +0x24)
        }

        void Integrate17(ref P p)
        {
            if (p.Bill == 0)                                     // airborne / bouncing
            {
                p.Pos += p.Vel;
                p.Vel.y -= 0.01f;                                // gravity (Unity down). NOTE: orig 17 has
                // NO gravity (it relied on bumpy/bounded terrain to catch shavings); on a flat collider
                // they'd fly up forever after a bounce, so we add gravity here so they settle.
                if (GroundUnder(p.Pos, out float gy, out Vector3 nrm) && p.Pos.y <= gy)
                {
                    if (p.Aux.x > 0f && p.Vel.sqrMagnitude > 0.01f)   // bounce
                    {
                        p.Vel = Vector3.Reflect(p.Vel, nrm) * 0.3f;   // reflect + heavy damp (orig 0.7 flung
                        // them up on a flat floor; wood barely bounces — 0.3 reads right on the bench)
                        p.Aux.x -= 1f;
                        p.Pos.y = gy + 0.01f;
                    }
                    else { p.Pos.y = gy; p.Vel = Vector3.zero; p.Bill = 1; p.Size *= 0.75f; }   // settle + shrink (DAT_007370c0)
                }
            }
            p.Age++;
            if (p.Age > 100) p.Alive = false;                    // ageCap (kill at age > 0x64)
        }

        // ===== FAITHFUL PORT: setId 26 — wood shavings variant (FUN_004b6440) =======================
        // Identical to setId 17 (same dir-aligned cone spawn, size, white/brown b_128d, gravity-less orig,
        // settle+shrink, ageCap 100, shares Integrate17) EXCEPT: cell is b_128d row 6 (u=rand%6) and it
        // bounces only ONCE (Aux.x = 1, vs 17's 4).
        void Step26()
        {
            if (_active && !_paused)
            {
                int n = _src != null ? Mathf.Max(0, _src.NumPerFrame) : 0;
                for (int i = 0; i < n; i++) { int s = Alloc(); if (s < 0) break; Spawn26(ref _pool[s]); }
            }
            AliveCount = 0;
            for (int i = 0; i < _high; i++)
            {
                if (!_pool[i].Alive) continue;
                Integrate17(ref _pool[i]);                       // same bounce/settle/age-out as setId 17
                if (_pool[i].Alive) AliveCount++;
            }
        }

        void Spawn26(ref P p)
        {
            Spawn17(ref p);                                      // identical spawn…
            p.Cell = (6 << 16) | (int)(_rng.U15() % 6);          // …except b_128d row 6, u 0..5
            p.Aux = new Vector3(1f, 0f, 0f);                     // …and a single bounce (orig +0x24 = 1)
        }

        // ===== FAITHFUL PORT: setId 32 — big sparkle flash (FUN_004bc2a0) ===========================
        // A sporadic burst: ~1/30 chance PER FRAME (rng%30==1) the whole `num` spawns at once. Point
        // spawn; vel = dir + a random spherical vector (sinθ, sinφ, cosθ)·r, r=(rand%50)/850 (sinφ≥0 →
        // downward bias). Each spark GROWS fast (+1.6/frame for 6 frames → ~11.6) then SHRINKS (−1.6),
        // spins +0.08, flickers its cell (rand&3 = c_256d row 0) every frame, turns white after spawn
        // (spawn gold 0xffe060). Very short life: ageCap 12. The self-gate makes it flash intermittently
        // (no harness pulse needed). setId 32 is unused by shipped scripts → representative test num.
        void Step32()
        {
            if (_active && !_paused && _rng.U15() % 30 == 1)     // ~3.3%/frame burst gate
            {
                int n = _src != null ? Mathf.Max(0, _src.NumPerFrame) : 0;
                for (int i = 0; i < n; i++) { int s = Alloc(); if (s < 0) break; Spawn32(ref _pool[s]); }
            }
            AliveCount = 0;
            for (int i = 0; i < _high; i++)
            {
                if (!_pool[i].Alive) continue;
                Integrate32(ref _pool[i]);
                if (_pool[i].Alive) AliveCount++;
            }
        }

        void Spawn32(ref P p)
        {
            p.Alive = true;
            p.Bill = 0;
            p.Pos = _worldPos;                                   // point spawn
            Vector3 dir = _src != null ? _src.Direction : Vector3.zero;
            float theta = (_rng.U15() % 360) / 180f * Mathf.PI;
            float phi   = (_rng.U15() % 180) / 180f * Mathf.PI;  // sin φ ≥ 0 → downward bias in Y-down
            float r     = (_rng.U15() % 50) / 850f;
            Vector3 velOrig = new Vector3(Mathf.Sin(theta) * r + dir.x,
                                          Mathf.Sin(phi)   * r + dir.y,
                                          Mathf.Cos(theta) * r + dir.z);
            p.Vel = ToUnity(velOrig);
            p.Base = 2.0f; p.Size = 2.0f;                        // +0x28 = 0x40000000
            p.Age = 0;
            p.Spin = 0f;
            p.Cell = _rng.Bits(0x3);                             // (v0, u=rand&3) c_256d row 0
            p.Color = new Color32(0xff, 0xe0, 0x60, 255);        // gold (integrate turns it white)
            p.Spec = new Color32(0, 0, 0, 0);
        }

        void Integrate32(ref P p)
        {
            p.Spin += 0.08f;                                     // +0x30 += _DAT_00737024
            p.Size += p.Age < 6 ? 1.6f : -1.6f;                  // grow 6 frames (→~11.6) then shrink (_DAT_007371f8)
            p.Cell = _rng.Bits(0x3);                             // flicker among c_256d row-0 cells each frame
            p.Color = new Color32(255, 255, 255, 255);           // white after spawn
            p.Age++;
            if (p.Age > 12) p.Alive = false;                     // ageCap (kill at age > 0xc)
        }

        // ===== FAITHFUL PORT: setId 16 — warm rising spark cone (FUN_004acee0) ======================
        // No emitter drift. Original gates spawn on the terrain heightfield (sparks only emit when the
        // emitter sits against a solid cell, FUN_00592f10 ≥ 0x81) — that gate is skipped in the test bed
        // (no L2M terrain). Cylinder spawn: radius from dir.x, vertical column from dir.y. Per-frame the
        // velocity does a tiny random walk (slight up-bias), the specular is a grey core (48−age)·5 that
        // fades with age, and ~9%/frame the spark is force-aged to death (the twinkle). setId 16 is NOT
        // referenced by any shipped script → the test dir is a representative value, not from Tcl.
        void Step16()
        {
            if (_active && !_paused)
            {
                int n = _src != null ? Mathf.Max(0, _src.NumPerFrame) : 0;
                for (int i = 0; i < n; i++) { int s = Alloc(); if (s < 0) break; Spawn16(ref _pool[s]); }
            }
            AliveCount = 0;
            for (int i = 0; i < _high; i++)
            {
                if (!_pool[i].Alive) continue;
                Integrate16(ref _pool[i]);
                if (_pool[i].Alive) AliveCount++;
            }
        }

        void Spawn16(ref P p)
        {
            p.Alive = true;
            p.Bill = 0;
            Vector3 dir = _src != null ? _src.Direction : Vector3.zero;
            float ang = (_rng.U15() % 360) / 180f * Mathf.PI;        // (rand%360)·π/180
            float rad = (_rng.U15() % 100) * (dir.x / 100f);         // radius from dir.x (DAT_0073760c=100)
            float h   = (_rng.U15() % 100) * (dir.y / 100f);         // vertical column from dir.y
            p.Pos = _worldPos + ToUnity(new Vector3(Mathf.Sin(ang) * rad, h, Mathf.Cos(ang) * rad));
            // vel: x/z ±8/880, y = (rand&15 − 16)/885 (range −16..−1 → negative = UP in Y-down)
            Vector3 velOrig = new Vector3((_rng.Bits(0xf) - 8) / 880f,
                                          (_rng.Bits(0xf) - 16) / 885f,
                                          (_rng.Bits(0xf) - 8) / 880f);
            p.Vel = ToUnity(velOrig);
            p.Base = 0.4f; p.Size = 0.4f;                            // +0x28 = 0x3ecccccd
            p.Age = _rng.U15() % 10;                                 // random init age 0..9
            p.Spin = 0f;
            p.Cell = (3 << 16) | 2;                                  // 0x30002 → c_256d (v3,u2), static
            p.Color = new Color32(0xff, 0xe0, 0x60, 255);            // gold 0xffe060
            p.Spec = new Color32(0, 0, 0, 0);
        }

        void Integrate16(ref P p)
        {
            p.Spin += 0.08f;                                         // +0x30 += _DAT_00737024
            // velocity random walk (±/6000, y biased −10 → slight up-drift in Y-down)
            Vector3 dvOrig = new Vector3((_rng.Bits(0xf) - 8) / 6000f,
                                         (_rng.Bits(0xf) - 10) / 6000f,
                                         (_rng.Bits(0xf) - 8) / 6000f);
            p.Vel += ToUnity(dvOrig);
            p.Pos += p.Vel;                                          // pos += vel·XMM2 (XMM2≈1, as other types)
            byte g = (byte)Mathf.Clamp((48 - p.Age) * 5, 0, 255);    // grey specular core, fades with age
            p.Spec = new Color32(g, g, g, 0);
            if (_rng.U15() < 3000)                                   // ~9%/frame: force death this step (twinkle)
            {
                p.Age = 47; p.Size = 0.5f; p.Spec = new Color32(255, 255, 255, 0);
            }
            p.Color = new Color32(0xff, 0xe0, 0x60, 255);            // diffuse re-set to gold each frame
            p.Age++;
            if (p.Age > 47) p.Alive = false;                        // ageCap (kill at age > 0x2f)
        }

        int SpawnCountThisFrame()
        {
            int baseN = _src != null ? Mathf.Max(0, _src.NumPerFrame) : 0;
            switch (_type.CountMode)
            {
                case SpawnCount.Single: return 1;
                case SpawnCount.JitteredPlusMinus1: return Mathf.Max(0, baseN + _rng.Mod(3) - 1);
                case SpawnCount.GatedProb: return _rng.Unit() < _type.GateProb ? Mathf.Max(1, baseN) : 0;
                default: return baseN;
            }
        }

        // Legacy template spawn — self-allocs (the un-ported-type StepOne path). Burst/SpawnInto call
        // SpawnLegacy(ref) directly on a pre-allocated slot.
        void Spawn()
        {
            int idx = Alloc();
            if (idx < 0) return;
            SpawnLegacy(ref _pool[idx]);
        }

        void SpawnLegacy(ref P p)
        {
            p.Alive = true;
            p.Age = 0;
            p.Base = _type.SizeJitter > 0f
                ? _type.StartSize * (1f + (_rng.Unit() * 2f - 1f) * _type.SizeJitter)
                : _type.StartSize;
            p.Size = p.Base;
            // π/4 base: the render billboard subtracts π/4 (faithful types seed spin=π/4 ⇒ upright), so
            // the legacy template must seed π/4 too or its sprites (setId 4 swirl, 7 spray, …) tilt 45°.
            p.Spin = 0.7853982f + _rng.Unit() * 0.05f;
            p.Bill = (byte)(_type.Bill == Billboard.VelocityStretched ? 1 : 0);

            Vector3 offset = SpawnOffset();
            p.Pos = _worldPos + offset;
            p.Aux = _worldPos;                       // remember the emitter centre for custom kinds

            Vector3 dir = _src != null ? _src.Direction : Vector3.zero;
            Vector3 jit = new(
                _rng.Sym4(800f) * _type.VelJitter.x * 100f,
                _rng.Sym4(800f) * _type.VelJitter.y * 100f,
                _rng.Sym4(800f) * _type.VelJitter.z * 100f);
            p.Vel = dir + jit;

            p.Cell = InitialCell();
            p.Color = ColourAt(0);

            // Custom-kind spawn tweaks.
            switch (_type.Dispatch)
            {
                case DispatchKind.Swirl:
                case DispatchKind.Spiral:
                    p.Aux = new Vector3(_rng.Angle(), _worldPos.y, p.Pos.y - _worldPos.y);
                    break;
                case DispatchKind.GroundCollision:
                    p.Aux.y = _worldPos.y - GroundDrop;   // floor below the emitter so particles FALL, not stick
                    p.Vel += new Vector3(_rng.Sym4(200f), -0.06f, _rng.Sym4(200f));  // initial downward+small outward spray
                    break;
            }
        }

        Vector3 SpawnOffset()
        {
            switch (_type.Shape)
            {
                case SpawnShape.Disc:
                {
                    float a = _rng.Angle(), r = _type.SpreadRadius * _rng.Frac01();
                    return new Vector3(Mathf.Cos(a) * r, (_rng.Mod(10)) / 200f, Mathf.Sin(a) * r);
                }
                case SpawnShape.Box:
                    return new Vector3(
                        _rng.Sym4(8f) * _type.SpreadBox.x, _rng.Sym4(8f) * _type.SpreadBox.y, _rng.Sym4(8f) * _type.SpreadBox.z);
                case SpawnShape.Sphere:
                {
                    Vector3 d = new(_rng.Sym4(8f), _rng.Sym4(8f), _rng.Sym4(8f));
                    return d.normalized * (_type.SpreadRadius * _rng.Frac01());
                }
                case SpawnShape.Cone:
                {
                    float a = _rng.Angle(), r = _type.SpreadRadius * _rng.Frac01();
                    return new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                }
                case SpawnShape.MeshVerts:
                    return MeshVertOffset();
                default:
                    return Vector3.zero;
            }
        }

        Vector3 MeshVertOffset()
        {
            if (_entityMesh != null && _entity != null)
            {
                // Mesh.vertices ALLOCATES a fresh Vector3[] on every access — this ran per
                // SPAWNED PARTICLE (a steady multi-KB/frame GC churn per objverts source).
                // Cache the copy; Configure invalidates it when the mesh reference changes.
                var verts = _entityVerts ??= _entityMesh.vertices;
                if (verts.Length > 0)
                {
                    var local = verts[_rng.Mod(verts.Length)];
                    return _entity.TransformPoint(local) - _worldPos;
                }
            }
            Vector3 d = new(_rng.Sym4(8f), _rng.Sym4(8f), _rng.Sym4(8f));
            return d.normalized * 0.3f;
        }

        void Integrate(ref P p)
        {
            switch (_type.Dispatch)
            {
                case DispatchKind.Swirl:           IntegrateSwirl(ref p); break;
                case DispatchKind.Spiral:          IntegrateSpiral(ref p); break;
                case DispatchKind.GroundCollision: IntegrateGround(ref p); break;
                case DispatchKind.Flock:           IntegrateFlock(ref p); break;
                case DispatchKind.Rope:    IntegrateStandard(ref p); break;   // chain TODO; behaves as standard
                default:                   IntegrateStandard(ref p); break;
            }
        }

        void IntegrateStandard(ref P p)
        {
            p.Pos += p.Vel;
            p.Vel += _type.Gravity * GravityScale;
            if (_type.Buoyancy && p.Age < _type.BuoyancyFrames) p.Vel.y += BuoyancyForce;
            if (_type.Drag < 1f) p.Vel *= _type.Drag;
            if (_type.EmitterWander) p.Vel += _drift * DriftCouple;
            if (_type.Sway) p.Pos.x += Mathf.Cos(p.Age * 0.25f) * SwayAmp;   // gentle bob (Z / note glyph)
            FinishFrame(ref p);
        }

        void IntegrateSwirl(ref P p)
        {
            // radius grows with age; sweep angle by age — an expanding ring (type 4).
            float ang = p.Aux.x + p.Age * 0.5f;
            float rad = p.Age * 0.06f;
            p.Pos = new Vector3(_worldPos.x + Mathf.Cos(ang) * rad, _worldPos.y + p.Age * 0.01f, _worldPos.z + Mathf.Sin(ang) * rad);
            FinishFrame(ref p);
        }

        void IntegrateSpiral(ref P p)
        {
            // sinking helix (type 31).
            float ang = p.Aux.x + p.Age * 0.4f;
            float rad = p.Age * 0.05f;
            p.Pos = new Vector3(_worldPos.x + Mathf.Cos(ang) * rad, _worldPos.y - p.Age * 0.02f, _worldPos.z + Mathf.Sin(ang) * rad);
            FinishFrame(ref p);
        }

        void IntegrateGround(ref P p)
        {
            p.Pos += p.Vel;
            p.Vel += _type.Gravity * GravityScale;
            if (_type.Drag < 1f) p.Vel *= _type.Drag;
            // bounce off the spawn plane (terrain heightfield approximation until L2M is wired).
            if (p.Pos.y < p.Aux.y && p.Vel.y < 0f)
            {
                p.Pos.y = p.Aux.y;
                p.Vel.y = -p.Vel.y * 0.3f;
                p.Vel.x *= 0.5f; p.Vel.z *= 0.5f;
            }
            FinishFrame(ref p);
        }

        void IntegrateFlock(ref P p)
        {
            // wander + cohesion toward the emitter centre — a loose elf-aura swarm (type 33).
            p.Vel += new Vector3(_rng.Sym4(400f), _rng.Sym4(400f), _rng.Sym4(400f));
            Vector3 toCenter = _worldPos - p.Pos;
            p.Vel += toCenter * 0.02f;
            p.Vel *= 0.92f;
            p.Pos += p.Vel;
            FinishFrame(ref p);
        }

        // Shared tail: colour-from-age, flipbook, size-over-life, age++, kill.
        void FinishFrame(ref P p)
        {
            if (_type.Atlas == AtlasMode.FlipbookByAge && _type.FlipFrames is { Length: > 0 })
                p.Cell = _type.FlipFrames[Mathf.Min(p.Age, _type.FlipFrames.Length - 1)];

            p.Size = p.Base * SizeMul(p.Age);
            p.Color = ColourAt(p.Age);
            p.Age++;
            if (p.Age >= _type.AgeCap) p.Alive = false;
        }

        float SizeMul(int age)   // size curve as a multiplier of the per-particle base size
        {
            float t = _type.AgeCap > 0 ? Mathf.Clamp01(age / (float)_type.AgeCap) : 0f;
            return _type.Size switch
            {
                SizeMode.Grow            => 0.5f + 0.8f * t,
                SizeMode.Shrink          => 1f - 0.9f * t,
                SizeMode.PulseGrowShrink => t < 0.4f ? 0.4f + 1.5f * t : 1f - (t - 0.4f) / 0.6f,
                _                        => 1f,
            };
        }

        // Diffuse(+specular folded into one additive draw colour) from age. The original computes this
        // per-frame inside each type fn; here we reproduce the two families + the const/flat cases.
        Color32 ColourAt(int age)
        {
            float t = _type.AgeCap > 0 ? Mathf.Clamp01(age / (float)_type.AgeCap) : 0f;
            float capA = _type.Alpha / 255f;
            switch (_type.Colour)
            {
                case ColourMode.WarmRamp:
                {
                    // bright warm core early → orange → dark; warm glow is the specular add (here folded).
                    Color c = t < 0.5f
                        ? Color.Lerp(new Color(1f, 0.95f, 0.75f), new Color(1f, 0.55f, 0.18f), t / 0.5f)
                        : Color.Lerp(new Color(1f, 0.55f, 0.18f), new Color(0.5f, 0.15f, 0.04f), (t - 0.5f) / 0.5f);
                    c.a = (1f - t) * capA;
                    return c;
                }
                case ColourMode.GreyRamp:
                {
                    float a = (t < 0.2f ? t / 0.2f : (1f - t) / 0.8f) * capA;
                    return new Color(0.85f, 0.85f, 0.85f, a);
                }
                case ColourMode.ConstantArgb:
                {
                    Color c = Argb(_type.ConstArgb);
                    c.a *= (1f - t) * capA;
                    return c;
                }
                default: // FlatWhite
                    return new Color(1f, 1f, 1f, (1f - t) * capA);
            }
        }

        int InitialCell()
        {
            int grid = Mathf.Max(1, _type.AtlasGrid);
            switch (_type.Atlas)
            {
                case AtlasMode.FixedCell:
                case AtlasMode.DirIndexed:
                    return Pack(Mathf.Clamp(_type.FixedU, 0, grid - 1), Mathf.Clamp(_type.FixedV, 0, grid - 1));
                case AtlasMode.FlipbookByAge:
                    return _type.FlipFrames is { Length: > 0 } ? _type.FlipFrames[0] : Pack(_type.FixedU, _type.FixedV);
                default: // RandomCell — pick from the precomputed band ∩ non-empty candidates.
                    return _allowedCells.Length > 0 ? _allowedCells[_rng.Mod(_allowedCells.Length)] : Pack(0, 0);
            }
        }

        static int Pack(int u, int v) => (u & 0xFFFF) | (v << 16);

        // Raycast straight down to find the Unity-collider ground under a particle (replaces the L2M
        // terrain height sample FUN_00592f10 for the ground-collision family). Returns the ground Y.
        bool GroundUnder(Vector3 pos, out float groundY, out Vector3 normal)
        {
            if (Physics.Raycast(pos + Vector3.up * GroundProbeUp, Vector3.down, out var hit,
                                GroundProbeReach, GroundMask, QueryTriggerInteraction.Ignore))
            {
                groundY = hit.point.y; normal = hit.normal; return true;
            }
            groundY = 0f; normal = Vector3.up; return false;
        }

        // ---- pool ---------------------------------------------------------------------------------

        int Alloc()
        {
            for (int i = 0; i < _high; i++)
                if (!_pool[i].Alive) return i;
            if (_high < _capacity) return _high++;
            return -1; // full
        }

        // ---- render -------------------------------------------------------------------------------

        // Rebuild the camera-facing billboard mesh in world space. One additive draw per emitter
        // (the original kept a per-emitter vertex/index buffer too). The mesh holder GO stays at world
        // origin so these world-space verts render 1:1.
        public void Render(Camera cam, Material? material, float sizeScale, bool atlasVFlip, float inset, float interp)
        {
            if (material != null && _mr.sharedMaterial != material) _mr.sharedMaterial = material;

            // Never draw the billboard mesh with a NULL material: Unity paints the whole burst with
            // the magenta missing-material shader. The set-43 shatter dirt burst (and any set whose
            // presenter material didn't resolve this frame) must be dirt or NOTHING, never pink — so
            // drop the geometry and skip; the emitter picks a material up on a later frame.
            if (_mr.sharedMaterial == null)
            {
                if (!_meshEmpty) { _mesh.Clear(); _meshEmpty = true; }
                return;
            }

            if (cam == null || AliveCount == 0)
            {
                // Idle emitter (fog-gated torch, drained burst): skip the redundant per-frame
                // Mesh.Clear/list churn once the mesh is already empty.
                if (!_meshEmpty) { _mesh.Clear(); _meshEmpty = true; }
                return;
            }
            _meshEmpty = false;
            _verts.Clear(); _cols.Clear(); _uvs.Clear(); _specs.Clear(); _corners.Clear(); _tris.Clear();

            int grid = Mathf.Max(1, _type.AtlasGrid);
            // atlas cell sampling width as a fraction of the cell. Original = 0.875 (DAT_007370fc) but it
            // crops sprites that fill their cell; tunable (1.0 = full cell, no crop).

            for (int i = 0; i < _high; i++)
            {
                ref P p = ref _pool[i];
                if (!p.Alive) continue;
                float half = Mathf.Max(0.001f, p.Size * sizeScale * 0.5f);
                // Original billboard (FUN_00497c60) builds the quad from corner offsets (±sin, ±cos) of
                // +0x30, so spin = π/4 ⇒ an AXIS-ALIGNED quad (and spin = 0 ⇒ a 45° diamond). Our quad is
                // axis-aligned at rotation 0, so subtract π/4 to match the original's convention exactly.
                // (Invisible for round/randomly-spun sprites; it un-tilts fixed-angle ones like setId 22.)
                float a = p.Spin - 0.7853982f;
                float cs = Mathf.Cos(a), sn = Mathf.Sin(a);

                // Vertex = particle CENTRE (world); the 2D corner offset (rotated by spin) goes to the
                // shader, which adds it in VIEW space — so the quad always faces the rendering camera
                // (gameplay / cutscene / test), not a single cached one. (Original rotates by +0x30.)
                // Smooth the low-rate (fpsRef) sim for display: render between sim steps by walking back
                // along the velocity by the un-elapsed fraction (interp = time into the next step ∈ [0,1]).
                // pos += vel each step, so one step ago the centre was ~Pos − Vel. Landed/static particles
                // zero their Vel so they don't drift.
                Vector3 c = p.Pos - p.Vel * (1f - interp);
                int b = _verts.Count;
                _verts.Add(c); _verts.Add(c); _verts.Add(c); _verts.Add(c);
                _corners.Add(new Vector2(-half * cs + half * sn, -half * sn - half * cs));
                _corners.Add(new Vector2( half * cs + half * sn,  half * sn - half * cs));
                _corners.Add(new Vector2( half * cs - half * sn,  half * sn + half * cs));
                _corners.Add(new Vector2(-half * cs - half * sn, -half * sn + half * cs));
                // Apply the per-type preset alpha (material brightness cap, +0x34810) on top of the
                // per-particle alpha; the shader scales the whole contribution by color.a.
                Color32 vc = p.Color;
                vc.a = (byte)(p.Color.a * _type.Alpha / 255);
                _cols.Add(vc); _cols.Add(vc); _cols.Add(vc); _cols.Add(vc);
                Vector3 sp = new(p.Spec.r / 255f, p.Spec.g / 255f, p.Spec.b / 255f);
                _specs.Add(sp); _specs.Add(sp); _specs.Add(sp); _specs.Add(sp);

                int u = p.Cell & 0xFFFF, v = (p.Cell >> 16) & 0xFFFF;
                float uMin = u / (float)grid, uMax = (u + inset) / grid;
                float vA = 1f - v / (float)grid, vB = 1f - (v + inset) / grid;  // image-top at V=1 (bottom-up tga)
                if (atlasVFlip) { vA = v / (float)grid; vB = (v + inset) / grid; }
                _uvs.Add(new Vector2(uMin, vB));
                _uvs.Add(new Vector2(uMax, vB));
                _uvs.Add(new Vector2(uMax, vA));
                _uvs.Add(new Vector2(uMin, vA));

                _tris.Add(b); _tris.Add(b + 1); _tris.Add(b + 2);
                _tris.Add(b); _tris.Add(b + 2); _tris.Add(b + 3);
            }

            _mesh.Clear();
            _mesh.SetVertices(_verts);
            _mesh.SetColors(_cols);
            _mesh.SetUVs(0, _uvs);
            _mesh.SetUVs(1, _specs);
            _mesh.SetUVs(2, _corners);
            _mesh.SetTriangles(_tris, 0);
            _mesh.RecalculateBounds();
            // verts are particle centres; the quad extent is added in the shader, so pad the bounds
            // generously so billboards near the screen edge aren't culled.
            var bnds = _mesh.bounds; bnds.Expand(8f); _mesh.bounds = bnds;
        }

        static Color Argb(uint argb) => new(
            ((argb >> 16) & 0xFF) / 255f, ((argb >> 8) & 0xFF) / 255f, (argb & 0xFF) / 255f, ((argb >> 24) & 0xFF) / 255f);

        void OnDestroy()
        {
            if (_mesh != null) Destroy(_mesh);
        }
    }
}
