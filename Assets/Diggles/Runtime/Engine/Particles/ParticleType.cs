#nullable enable

namespace Diggles.Runtime.Particles
{
    using UnityEngine;

    // The 6 atlas textures, in the engine's load order (FUN_004929b0); the value == the preset's
    // +0x34808 texIndex. One shared Material per value is built by the presenter.
    public enum ParticleTex { A128d = 0, B128d = 1, C256d = 2, E256 = 3, D256d = 4, A256d = 5 }

    // How new particles are positioned around the emitter each spawn.
    public enum SpawnShape { Point, Box, Disc, Cone, Sphere, MeshVerts }

    // How a particle picks its atlas cell (low16=U tile, high16=V tile; grid = AtlasGrid).
    public enum AtlasMode { FixedCell, RandomCell, FlipbookByAge, DirIndexed }

    // Per-frame diffuse/specular colour evolution. The preset has NO colour — it is computed from age.
    public enum ColourMode { FlatWhite, ConstantArgb, WarmRamp, GreyRamp }

    public enum Billboard { Screen, VelocityStretched }

    // Spawn-count-per-frame behaviour. Base count = the Tcl <num_per_frame> (source.NumPerFrame).
    public enum SpawnCount { Fixed, JitteredPlusMinus1, Single, GatedProb }

    public enum SizeMode { Constant, Grow, Shrink, PulseGrowShrink }

    // Which emitter implementation drives this type. Most are stock Shuriken config; the rest are
    // custom CPU updaters that render through a PS via SetParticles (or a LineRenderer for Rope).
    public enum DispatchKind { Shuriken, ShurikenSubEmitter, GroundCollision, Swirl, Spiral, Rope, Flock, Inert }

    // One particle SET's full RE-derived spec. The four "preset" fields (Tex/RenderMode/Alpha/AtlasGrid)
    // are baked-in facts from Particle.cpp; the rest describe the per-type hand-coded behaviour. Authored
    // in code (ParticleTypeTable) — this is reverse-engineered fact, not designer content. Fields carry
    // sensible defaults so each table row only states its deltas. Per-frame motion constants come from
    // re/notes/particle_constants.txt and are scaled to per-second by FpsRef at build time.
    public sealed class ParticleType
    {
        // ---- baked preset (exact) ----
        public int SetId;
        public ParticleTex Tex = ParticleTex.C256d;
        public int RenderMode = 0x0f;       // 0x0f or 0x10 (both additive in practice)
        public byte Alpha = 255;            // material brightness cap 70..255
        public int AtlasGrid = 4;           // 4 / 8 / 16 (atlas sampled as N×N)

        public DispatchKind Dispatch = DispatchKind.Shuriken;

        // ---- spawn ----
        public SpawnShape Shape = SpawnShape.Point;
        public float SpreadRadius = 0f;     // Disc/Sphere/Cone radius (native units)
        public Vector3 SpreadBox = Vector3.zero;   // Box half-extents (native units)
        public float ConeAngle = 12f;
        public SpawnCount CountMode = SpawnCount.Fixed;
        public float GateProb = 1f;         // GatedProb: per-frame accept chance (0..1)
        public Vector3 VelJitter = Vector3.zero;   // ± random added to the base dir (native units/frame)

        // ---- visual ----
        public AtlasMode Atlas = AtlasMode.RandomCell;
        public int FixedU, FixedV;          // FixedCell
        public int AtlasRow0 = 0;           // RandomCell: first row of the sprite band (v from image top)
        public int AtlasRows = -1;          // RandomCell: number of rows in the band (-1 = whole grid)
        public int AtlasCol0 = 0;           // RandomCell: first column of the band (u) — for half-row splits
        public int AtlasCols = -1;          // RandomCell: number of columns in the band (-1 = whole width)
        public int[]? FlipFrames;           // FlipbookByAge: explicit frame indices (frame = v*grid + u)
        public Billboard Bill = Billboard.Screen;
        public ColourMode Colour = ColourMode.FlatWhite;
        public uint ConstArgb = 0xFFFFFFFF; // ConstantArgb
        public float StartSize = 1f;        // particle +0x28, native world units
        public float SizeJitter = 0f;       // per-particle start-size variation (fraction 0..1)
        public SizeMode Size = SizeMode.Constant;
        public bool Sway;                   // gentle horizontal bob while rising (sleep Z / note glyph)

        // ---- motion (per-FRAME values; emitter scales by FpsRef) ----
        public Vector3 Gravity = Vector3.zero;   // per-frame velocity delta (native; -Y = down)
        public float Drag = 1f;             // per-frame velocity retention (1 = none, <1 = drag)
        public bool Buoyancy;               // initial upward kick for the first BuoyancyFrames
        public int BuoyancyFrames;
        public bool EmitterWander;          // emitter random-walk drift coupled into particle accel

        // ---- lifetime ----
        public int AgeCap = 32;             // frames before death
        public bool DistanceKill;           // honour the source max_range² (+0x60)
        public bool GroundInteract;         // samples the terrain heightfield (handled by GroundCollisionEmitter)
    }

    // Per-texture atlas info derived from the grid the type samples it at (4/8/16). The same.tga can be
    // sampled at different grids by different types, so the grid lives on ParticleType, not here.
    public static class ParticleTexFiles
    {
        // The atlas tile grid is fixed PER TEXTURE (determined from the real.tga content via
        // re/scripts/detect_particle_grids.py — gutter analysis + the RE'd max cell row, e.g. type 6
        // samples a_128d at v=15 ⇒ 16×16). It is NOT the per-type size-tier (4/8/16 = billboard size).
        public static int GridFor(ParticleTex t) => t switch
        {
            ParticleTex.A128d => 16,  // tiny dot/sparkle grid (cells used up to v=15)
            ParticleTex.B128d => 8,   // 8×8 chips/shavings
            ParticleTex.C256d => 4,   // 4×4 smoke puffs
            ParticleTex.E256  => 4,   // 4×4 sparks (+ digit cells in lower rows)
            ParticleTex.D256d => 4,   // 4×4 sparkles (+ digit cells)
            ParticleTex.A256d => 8,   // 8×8 soot (+ digit cells)
            _ => 4,
        };

        // texIndex -> on-disk filename under <DataRoot>/Texture/Misc (engine load order).
        public static string FileFor(ParticleTex t) => t switch
        {
            ParticleTex.A128d => "particle_a_128d.tga",
            ParticleTex.B128d => "particle_b_128d.tga",
            ParticleTex.C256d => "particle_c_256d.tga",
            ParticleTex.E256  => "particle_e_256.tga",
            ParticleTex.D256d => "particle_d_256d.tga",
            ParticleTex.A256d => "particle_a_256d.tga",
            _ => "particle_c_256d.tga",
        };
    }
}
