#nullable enable

namespace Diggles.Runtime.Particles
{
    using UnityEngine;

    // The 43-entry RE-truth table (setId 0..42; setId 5 is inert — the original has no update fn for it).
    // Render facts (Tex/RenderMode/Alpha/AtlasGrid) are exact from Particle.cpp (FUN_00495f60 / FUN_00498360
    // + the integrator size-tier switch). Behaviour fields come from the 42 per-type update functions
    // (see Docs/PARTICLE_SYSTEM_REWRITE.md §4 + re/notes/particle_constants.txt). Motion magnitudes are the
    // extracted per-frame constants where known; a few are first-pass values flagged for Play calibration.
    // Vectors are in the original particle convention (same as the Tcl relpos/dir args); the presenter maps
    // them to Unity world via a single conversion + FpsRef/SizeScale knobs.
    public static class ParticleTypeTable
    {
        public static readonly ParticleType[] All = Build();

        public static ParticleType Get(int setId) =>
            setId >= 0 && setId < All.Length ? All[setId] : All[5]; // 5 = inert fallback

        // Common warm/grey/ember ARGB constants written by the per-type colour ramps.
        const uint Gold   = 0xFFFFE060; // 0xffe060 — fire-orange/gold (types 13,16,32,33,41)
        const uint Yellow = 0xFFFFFF00; // sparks (type 18)
        const uint Olive  = 0xFF7F753F; // dust/dirt (type 20)
        const uint Ember  = 0xFFB29567; // muted ember-brown (type 42)
        const uint Grey   = 0x7F7F7F7F; // half-bright grey (types 7,22,23)

        static ParticleType[] Build()
        {
            var t = new ParticleType[44];   // 0..42 native; 43 = PORT-ONLY shatter (below)

            // 0 — smith ember, disc, rising, warm ramp
            t[0] = new ParticleType { SetId=0, Tex=ParticleTex.E256, RenderMode=0x10, Alpha=100, AtlasGrid=4,
                Shape=SpawnShape.Sphere, SpreadRadius=0.25f, VelJitter=new(0.02f,0.02f,0.02f),
                Atlas=AtlasMode.RandomCell, AtlasRows=1, Colour=ColourMode.WarmRamp, StartSize=2.6f, SizeJitter=0.2f, Size=SizeMode.Grow,
                Buoyancy=true, BuoyancyFrames=12, AgeCap=32, DistanceKill=true };   // dense round fireball

            // 1 — ground spark/splash (heightfield)
            t[1] = new ParticleType { SetId=1, Tex=ParticleTex.A256d, RenderMode=0x0f, Alpha=255, AtlasGrid=8,
                Dispatch=DispatchKind.GroundCollision, Shape=SpawnShape.Box, SpreadBox=new(0.1f,0.1f,0.1f),
                VelJitter=new(0.06f,0.06f,0.06f), Atlas=AtlasMode.RandomCell, AtlasRows=1, StartSize=2.5f, Size=SizeMode.Grow,
                Gravity=new(0,-0.01f,0), AgeCap=100, GroundInteract=true };

            // 2 — warm ember glow, disc, buoyant, age² flipbook
            t[2] = new ParticleType { SetId=2, Tex=ParticleTex.A128d, RenderMode=0x10, Alpha=180, AtlasGrid=16,
                Shape=SpawnShape.Sphere, SpreadRadius=0.18f, VelJitter=new(0.02f,0.02f,0.02f),
                Atlas=AtlasMode.FlipbookByAge, FlipFrames=new[]{6,22,38,54}, Colour=ColourMode.WarmRamp, StartSize=2.0f, SizeJitter=0.15f, Size=SizeMode.Grow,
                Buoyancy=true, BuoyancyFrames=6, AgeCap=32, DistanceKill=true };   // small dense fireball

            // 3 — smith fire (bigger 2), longer plume
            t[3] = new ParticleType { SetId=3, Tex=ParticleTex.A128d, RenderMode=0x10, Alpha=170, AtlasGrid=16,
                Shape=SpawnShape.Sphere, SpreadRadius=0.25f, VelJitter=new(0.02f,0.02f,0.02f),
                Atlas=AtlasMode.FlipbookByAge, FlipFrames=new[]{7,23,39,55}, Colour=ColourMode.WarmRamp, StartSize=2.8f, SizeJitter=0.15f, Size=SizeMode.Grow,
                Buoyancy=true, BuoyancyFrames=16, AgeCap=32, DistanceKill=true };   // dense round fireball

            // 4 — single "Z" glyph above a sleeping gnome: bobs up, grows, fades (NOT a swirl).
            t[4] = new ParticleType { SetId=4, Tex=ParticleTex.B128d, RenderMode=0x0f, Alpha=255, AtlasGrid=8,
                Shape=SpawnShape.Point, CountMode=SpawnCount.GatedProb, GateProb=0.02f,
                Atlas=AtlasMode.FixedCell, FixedU=0, FixedV=4, StartSize=1.5f, Size=SizeMode.Grow,
                Buoyancy=true, BuoyancyFrames=8, Sway=true, AgeCap=64 };   // slow bobbing rise

            // 5 — INERT (no update fn). Kept so indexing is dense.
            t[5] = new ParticleType { SetId=5, Tex=ParticleTex.A128d, RenderMode=0x10, Alpha=255, AtlasGrid=16,
                Dispatch=DispatchKind.Inert };

            // 6 — smoke from mesh verts
            t[6] = new ParticleType { SetId=6, Tex=ParticleTex.A128d, RenderMode=0x0f, Alpha=255, AtlasGrid=16,
                Shape=SpawnShape.MeshVerts, CountMode=SpawnCount.JitteredPlusMinus1, VelJitter=new(0.04f,0.04f,0.04f),
                Atlas=AtlasMode.RandomCell, AtlasRow0=14, AtlasRows=2, AtlasCol0=4, AtlasCols=4, StartSize=2.5f, Size=SizeMode.Grow, Drag=0.9f, Buoyancy=true, BuoyancyFrames=4,
                EmitterWander=true, AgeCap=32, DistanceKill=true };

            // 7 — a few droplets falling in a near-vertical arc (screen billboard, gravity; NOT a fountain).
            t[7] = new ParticleType { SetId=7, Tex=ParticleTex.D256d, RenderMode=0x0f, Alpha=200, AtlasGrid=4,
                Shape=SpawnShape.Box, SpreadBox=new(0.1f,0.1f,0.1f), CountMode=SpawnCount.GatedProb, GateProb=0.12f,
                VelJitter=new(0.03f,0.02f,0.03f), Atlas=AtlasMode.DirIndexed, FixedV=2, Colour=ColourMode.ConstantArgb, ConstArgb=Grey,
                StartSize=4.5f, Gravity=new(0,-0.015f,0), AgeCap=32, DistanceKill=true };

            // 8 — dust puff, sphere, ground-stick
            t[8] = new ParticleType { SetId=8, Tex=ParticleTex.B128d, RenderMode=0x0f, Alpha=255, AtlasGrid=16,
                Dispatch=DispatchKind.GroundCollision, Shape=SpawnShape.Point,
                VelJitter=new(0.03f,0,0.03f), Atlas=AtlasMode.RandomCell, AtlasRow0=0, AtlasRows=2, StartSize=0.5f, Size=SizeMode.Grow,
                Colour=ColourMode.ConstantArgb, ConstArgb=0xFFC03028,   // dark blood-red (was bright pink)
                Gravity=new(0,-0.01f,0), AgeCap=95, GroundInteract=true };

            // 9 — smoke puff (point), drag+buoyancy, age/7 flipbook
            t[9] = new ParticleType { SetId=9, Tex=ParticleTex.B128d, RenderMode=0x0f, Alpha=255, AtlasGrid=16,
                Shape=SpawnShape.Sphere, SpreadRadius=0.2f, CountMode=SpawnCount.JitteredPlusMinus1,
                Atlas=AtlasMode.RandomCell, AtlasRow0=0, AtlasRows=2, Colour=ColourMode.WarmRamp, StartSize=2.0f, SizeJitter=0.2f, Size=SizeMode.Grow,
                Drag=0.9f, Buoyancy=true, BuoyancyFrames=8, AgeCap=111, DistanceKill=true };   // small fireball (sphere)

            // 10 — droplet, 1/frame, ground-bounce, on-impact atlas swap
            t[10] = new ParticleType { SetId=10, Tex=ParticleTex.B128d, RenderMode=0x0f, Alpha=255, AtlasGrid=16,
                Dispatch=DispatchKind.GroundCollision, Shape=SpawnShape.Point, CountMode=SpawnCount.Single,
                VelJitter=new(0.06f,0,0.06f), Atlas=AtlasMode.RandomCell, AtlasRow0=4, AtlasRows=2, StartSize=0.4f, Size=SizeMode.Grow,
                Colour=ColourMode.ConstantArgb, ConstArgb=0xFF9098A8,   // grey drops (was red-chip pink)
                Gravity=new(0,-0.008f,0), AgeCap=95, GroundInteract=true };

            // 11 — smoke cloud: appears, slowly rises, grows in volume, fades (NOT a ground fountain).
            t[11] = new ParticleType { SetId=11, Tex=ParticleTex.A128d, RenderMode=0x0f, Alpha=255, AtlasGrid=16,
                Shape=SpawnShape.Disc, SpreadRadius=0.35f, CountMode=SpawnCount.JitteredPlusMinus1, VelJitter=new(0.04f,0.04f,0.04f),
                Atlas=AtlasMode.RandomCell, AtlasRow0=14, AtlasRows=2, AtlasCol0=4, AtlasCols=4, Colour=ColourMode.GreyRamp,
                StartSize=2.0f, Size=SizeMode.Grow, Drag=0.95f, Buoyancy=true, BuoyancyFrames=24, AgeCap=48, DistanceKill=true };

            // 12 — drift smoke (flipbook), grows, screen-aligned
            t[12] = new ParticleType { SetId=12, Tex=ParticleTex.A128d, RenderMode=0x0f, Alpha=255, AtlasGrid=16,
                Shape=SpawnShape.Point, CountMode=SpawnCount.JitteredPlusMinus1,
                Atlas=AtlasMode.RandomCell, AtlasRow0=14, AtlasRows=2, AtlasCol0=4, AtlasCols=4, Colour=ColourMode.GreyRamp, StartSize=1.0f, Size=SizeMode.Grow,
                AgeCap=64, DistanceKill=true };   // small smoke puff: grows in place + fades (no sideways drift)

            // 13 — magic white-blue-purple sparkles scattering DOWN out of a point, varied, twinkling.
            t[13] = new ParticleType { SetId=13, Tex=ParticleTex.C256d, RenderMode=0x10, Alpha=180, AtlasGrid=4,
                Shape=SpawnShape.Sphere, SpreadRadius=0.4f, CountMode=SpawnCount.JitteredPlusMinus1,
                Atlas=AtlasMode.RandomCell, Colour=ColourMode.ConstantArgb, ConstArgb=0xFFB0A8FF,
                StartSize=1.2f, Size=SizeMode.Shrink, Gravity=new(0,-0.01f,0), AgeCap=48, DistanceKill=true };

            // 14 — radial burst (decel), sphere, grey fade
            t[14] = new ParticleType { SetId=14, Tex=ParticleTex.C256d, RenderMode=0x10, Alpha=70, AtlasGrid=4,
                Shape=SpawnShape.Sphere, SpreadRadius=0.5f, VelJitter=new(0,0.05f,0),
                Atlas=AtlasMode.RandomCell, AtlasRows=1, Colour=ColourMode.GreyRamp, StartSize=2.0f, Size=SizeMode.Grow,
                Drag=0.92f, AgeCap=64, DistanceKill=true };

            // 15 — drops of yellow liquid falling vertically to the ground.
            t[15] = new ParticleType { SetId=15, Tex=ParticleTex.B128d, RenderMode=0x0f, Alpha=200, AtlasGrid=16,
                Dispatch=DispatchKind.GroundCollision, Shape=SpawnShape.Disc, SpreadRadius=0.1f,
                Atlas=AtlasMode.RandomCell, AtlasRow0=4, AtlasRows=2, Colour=ColourMode.ConstantArgb, ConstArgb=Yellow,
                StartSize=0.6f, Gravity=new(0,-0.012f,0), AgeCap=95, GroundInteract=true };

            // 16 — warm rising cone + end-flash
            t[16] = new ParticleType { SetId=16, Tex=ParticleTex.C256d, RenderMode=0x10, Alpha=100, AtlasGrid=4,
                Shape=SpawnShape.Cone, SpreadRadius=0.2f, ConeAngle=18f, VelJitter=new(0.04f,0.1f,0.04f),
                Atlas=AtlasMode.FixedCell, FixedU=2, FixedV=3, Colour=ColourMode.WarmRamp, ConstArgb=Gold,
                StartSize=0.4f, Size=SizeMode.Grow, Buoyancy=true, BuoyancyFrames=8, AgeCap=47 };

            // 17 — Späne/shavings (terrain bounce, dir-fan)
            t[17] = new ParticleType { SetId=17, Tex=ParticleTex.B128d, RenderMode=0x0f, Alpha=255, AtlasGrid=8,
                Dispatch=DispatchKind.GroundCollision, Shape=SpawnShape.Cone, SpreadRadius=0.15f, ConeAngle=35f,
                Atlas=AtlasMode.RandomCell, AtlasRow0=5, AtlasRows=2, StartSize=0.5f, Gravity=new(0,-0.01f,0),   // brown wood shavings
                AgeCap=100, GroundInteract=true };

            // 18 — Funken/sparks (terrain bounce, yellow fade)
            t[18] = new ParticleType { SetId=18, Tex=ParticleTex.B128d, RenderMode=0x10, Alpha=170, AtlasGrid=8,
                Dispatch=DispatchKind.GroundCollision, Shape=SpawnShape.Point, VelJitter=new(0.02f,0.02f,0.02f),
                Atlas=AtlasMode.FixedCell, FixedU=5, FixedV=3, Colour=ColourMode.WarmRamp, ConstArgb=Yellow,
                StartSize=0.6f, Gravity=new(0,-0.01f,0), AgeCap=32, GroundInteract=true };

            // 19 — Staub/dust (box, falls, long life)
            t[19] = new ParticleType { SetId=19, Tex=ParticleTex.E256, RenderMode=0x0f, Alpha=100, AtlasGrid=4,
                Shape=SpawnShape.Box, SpreadBox=new(0.1f,0.1f,0.1f), VelJitter=new(0.05f,0.05f,0.05f),
                Atlas=AtlasMode.RandomCell, AtlasRow0=1, AtlasRows=1, StartSize=0.8f, Gravity=new(0,-0.005f,0),
                AgeCap=128, DistanceKill=true };

            // 20 — dust/dirt cloud (brown, shrinks, slow spin)
            t[20] = new ParticleType { SetId=20, Tex=ParticleTex.C256d, RenderMode=0x0f, Alpha=100, AtlasGrid=4,
                Shape=SpawnShape.Box, SpreadBox=new(0.15f,0.15f,0.15f), CountMode=SpawnCount.JitteredPlusMinus1,
                VelJitter=new(0.05f,0.05f,0.05f), Atlas=AtlasMode.RandomCell, AtlasRows=1,
                Colour=ColourMode.ConstantArgb, ConstArgb=Olive, StartSize=1.7f, Size=SizeMode.Shrink, Drag=0.92f,
                EmitterWander=true, AgeCap=32, DistanceKill=true };

            // 21 — mist/spray drift (big white)
            t[21] = new ParticleType { SetId=21, Tex=ParticleTex.D256d, RenderMode=0x0f, Alpha=170, AtlasGrid=8,
                Shape=SpawnShape.Sphere, SpreadRadius=0.3f, Atlas=AtlasMode.RandomCell, AtlasRows=2,
                StartSize=2.5f, SizeJitter=0.3f, Buoyancy=true, BuoyancyFrames=6, Gravity=new(0,-0.02f,0),
                AgeCap=32, DistanceKill=true };   // appears as a sphere, kicks up, then falls under gravity

            // 22 — rising steam (sparse, large grey, ping-pong flipbook)
            t[22] = new ParticleType { SetId=22, Tex=ParticleTex.D256d, RenderMode=0x0f, Alpha=200, AtlasGrid=4,
                Shape=SpawnShape.Box, SpreadBox=new(0.2f,0.2f,0.2f), CountMode=SpawnCount.GatedProb, GateProb=0.25f,
                Atlas=AtlasMode.RandomCell, AtlasRows=1, Colour=ColourMode.ConstantArgb, ConstArgb=Grey, StartSize=5.5f,
                Gravity=new(0,-0.015f,0), AgeCap=32 };   // crumbles downward (atlas region TBC)

            // 23 — grey puff (gated ¼, gravity)
            t[23] = new ParticleType { SetId=23, Tex=ParticleTex.D256d, RenderMode=0x0f, Alpha=200, AtlasGrid=8,
                Shape=SpawnShape.Box, SpreadBox=new(0.4f,0.02f,0.4f), CountMode=SpawnCount.GatedProb, GateProb=0.25f,
                Atlas=AtlasMode.FixedCell, FixedU=0, FixedV=2, Colour=ColourMode.ConstantArgb, ConstArgb=Grey,
                StartSize=1.2f, SizeJitter=0.5f, Buoyancy=true, BuoyancyFrames=32, AgeCap=32 };   // bubbles rising from a surface

            // 24 — cell-anim sheet (buoyant), wall-confine
            t[24] = new ParticleType { SetId=24, Tex=ParticleTex.D256d, RenderMode=0x0f, Alpha=170, AtlasGrid=8,
                Shape=SpawnShape.Box, SpreadBox=new(0.1f,0.1f,0.2f), VelJitter=new(0.05f,0.05f,0.05f),
                Atlas=AtlasMode.RandomCell, AtlasRows=2, StartSize=2.5f, Buoyancy=true, BuoyancyFrames=6,
                AgeCap=32, DistanceKill=true };

            // 25 — beam/rope (custom; Verlet chain → LineRenderer)
            t[25] = new ParticleType { SetId=25, Tex=ParticleTex.B128d, RenderMode=0x10, Alpha=220, AtlasGrid=8,
                Dispatch=DispatchKind.Rope, Atlas=AtlasMode.FixedCell, FixedU=6, FixedV=4, StartSize=1.3f,
                Size=SizeMode.Shrink, AgeCap=8 };

            // 26 — Späne/shavings (terrain bounce, dir-fan)
            t[26] = new ParticleType { SetId=26, Tex=ParticleTex.B128d, RenderMode=0x0f, Alpha=255, AtlasGrid=8,
                Dispatch=DispatchKind.GroundCollision, Shape=SpawnShape.Cone, SpreadRadius=0.1f, ConeAngle=35f,
                Atlas=AtlasMode.RandomCell, AtlasRow0=5, AtlasRows=2, StartSize=0.5f, Gravity=new(0,-0.01f,0),   // brown wood shavings
                AgeCap=100, GroundInteract=true };

            // 27 — warm smoke from mesh verts
            t[27] = new ParticleType { SetId=27, Tex=ParticleTex.A128d, RenderMode=0x10, Alpha=180, AtlasGrid=16,
                Shape=SpawnShape.Sphere, SpreadRadius=0.2f, Atlas=AtlasMode.RandomCell, AtlasRow0=0, AtlasRows=4, AtlasCol0=4, AtlasCols=4,
                Colour=ColourMode.WarmRamp, StartSize=2.0f, SizeJitter=0.2f, Size=SizeMode.Grow,
                Buoyancy=true, BuoyancyFrames=10, AgeCap=32, DistanceKill=true };   // small fire sphere

            // 28 — rising grey smoke (small, point)
            t[28] = new ParticleType { SetId=28, Tex=ParticleTex.E256, RenderMode=0x10, Alpha=170, AtlasGrid=4,
                Shape=SpawnShape.Sphere, SpreadRadius=0.3f, Atlas=AtlasMode.RandomCell, AtlasRow0=1, AtlasRows=1,
                Colour=ColourMode.ConstantArgb, ConstArgb=0xFF9866C8, StartSize=1.6f, SizeJitter=0.3f, Size=SizeMode.Grow,
                Buoyancy=true, BuoyancyFrames=16, AgeCap=32, DistanceKill=true };   // purple smoke sphere

            // 29 — rising grey smoke (disc, pull-to-center)
            t[29] = new ParticleType { SetId=29, Tex=ParticleTex.E256, RenderMode=0x10, Alpha=170, AtlasGrid=4,
                Shape=SpawnShape.Sphere, SpreadRadius=0.4f, Atlas=AtlasMode.RandomCell, AtlasRow0=1, AtlasRows=1,
                Colour=ColourMode.ConstantArgb, ConstArgb=0xFF9866C8, StartSize=1.8f, SizeJitter=0.3f, Size=SizeMode.Grow,
                Buoyancy=true, BuoyancyFrames=16, AgeCap=32, DistanceKill=true };   // purple smoke sphere (≈28)

            // 30 — rising grey smoke (disc, smaller radius)
            t[30] = new ParticleType { SetId=30, Tex=ParticleTex.E256, RenderMode=0x10, Alpha=170, AtlasGrid=4,
                Shape=SpawnShape.Disc, SpreadRadius=0.3f, VelJitter=new(0.05f,0.05f,0.05f),
                Atlas=AtlasMode.RandomCell, AtlasRow0=1, AtlasRows=1, Colour=ColourMode.GreyRamp, StartSize=0.5f, Size=SizeMode.Grow,
                Buoyancy=true, BuoyancyFrames=16, EmitterWander=true, AgeCap=32, DistanceKill=true };

            // 31 — single music-note glyph rising + bobbing (like the Z, type 4).
            t[31] = new ParticleType { SetId=31, Tex=ParticleTex.B128d, RenderMode=0x0f, Alpha=255, AtlasGrid=8,
                Shape=SpawnShape.Point, CountMode=SpawnCount.GatedProb, GateProb=0.02f,
                Atlas=AtlasMode.FixedCell, FixedU=5, FixedV=4, StartSize=1.5f, Size=SizeMode.Grow,
                Buoyancy=true, BuoyancyFrames=8, Sway=true, AgeCap=64 };

            // 32 — one big flash that pulses (sparse, not a cloud of small ones).
            t[32] = new ParticleType { SetId=32, Tex=ParticleTex.C256d, RenderMode=0x10, Alpha=200, AtlasGrid=4,
                Shape=SpawnShape.Point, CountMode=SpawnCount.GatedProb, GateProb=0.012f,
                Atlas=AtlasMode.RandomCell, AtlasRows=1, Colour=ColourMode.WarmRamp, ConstArgb=Gold,
                StartSize=5.0f, Size=SizeMode.PulseGrowShrink, AgeCap=16, DistanceKill=true };

            // 33 — magic burst: mixed sparkles drifting gently DOWN (was a static hovering flock).
            t[33] = new ParticleType { SetId=33, Tex=ParticleTex.C256d, RenderMode=0x10, Alpha=180, AtlasGrid=4,
                Shape=SpawnShape.Sphere, SpreadRadius=0.5f, CountMode=SpawnCount.JitteredPlusMinus1,
                Atlas=AtlasMode.RandomCell, Colour=ColourMode.ConstantArgb, ConstArgb=0xFFB0A8FF,
                StartSize=1.6f, SizeJitter=0.5f, Size=SizeMode.Shrink, Gravity=new(0,-0.012f,0), AgeCap=48, DistanceKill=true };

            // 34 — fireball (was grey smoke).
            t[34] = new ParticleType { SetId=34, Tex=ParticleTex.E256, RenderMode=0x10, Alpha=170, AtlasGrid=4,
                Shape=SpawnShape.Sphere, SpreadRadius=0.3f, Atlas=AtlasMode.RandomCell, AtlasRow0=0, AtlasRows=1,
                Colour=ColourMode.WarmRamp, StartSize=3.0f, SizeJitter=0.2f, Size=SizeMode.Grow,
                Buoyancy=true, BuoyancyFrames=12, AgeCap=32, DistanceKill=true };

            // 35 — smoke (was a white downward stream).
            t[35] = new ParticleType { SetId=35, Tex=ParticleTex.A128d, RenderMode=0x0f, Alpha=200, AtlasGrid=16,
                Shape=SpawnShape.Box, SpreadBox=new(0.2f,0.1f,0.2f), CountMode=SpawnCount.JitteredPlusMinus1,
                Atlas=AtlasMode.RandomCell, AtlasRow0=14, AtlasRows=2, AtlasCol0=4, AtlasCols=4, Colour=ColourMode.GreyRamp,
                StartSize=3.0f, SizeJitter=0.3f, Size=SizeMode.Grow, Drag=0.92f, AgeCap=40, DistanceKill=true };

            // 36/37/38 — stationary expanding 9-frame poof bursts (explicit flipbook tables, 8×8)
            t[36] = Poof(36, new[]{  8, 9,10,11,12,13,14,15,16 });   // V=1row col0..7, then V=2 col0
            t[37] = Poof(37, new[]{ 17,18,19,20,21,22,23,24,25 });   // V=2 col1..7, then V=3 col0..1
            t[38] = Poof(38, new[]{ 26,27,28,29,30,31,32,33, 0 });   // V=3 col2..7, V=4 col0..1, then V=0 col0

            // 39/40 — animated energy bursts (teal-ring / orange-glow flipbooks); were static glows.
            t[39] = Poof(39, new[]{ 24,25,26,27,28,29,30,31 });   // teal ring expand
            t[40] = Poof(40, new[]{ 16,17,18,19,20,21,22,23 });   // orange glow explosion

            // 41 — many WHITE flashes, emitted in bursts, spread out (was clustered gold).
            t[41] = new ParticleType { SetId=41, Tex=ParticleTex.C256d, RenderMode=0x10, Alpha=200, AtlasGrid=4,
                Shape=SpawnShape.Sphere, SpreadRadius=0.6f, CountMode=SpawnCount.GatedProb, GateProb=0.1f,
                Atlas=AtlasMode.RandomCell, AtlasRows=1, Colour=ColourMode.ConstantArgb, ConstArgb=0xFFFFFFFF,
                StartSize=2.0f, SizeJitter=0.3f, Size=SizeMode.PulseGrowShrink, AgeCap=12, DistanceKill=true };

            // 42 — a small dust sphere that grows and fades (was fire).
            t[42] = new ParticleType { SetId=42, Tex=ParticleTex.E256, RenderMode=0x0f, Alpha=150, AtlasGrid=4,
                Shape=SpawnShape.Sphere, SpreadRadius=0.3f, CountMode=SpawnCount.JitteredPlusMinus1,
                Atlas=AtlasMode.RandomCell, AtlasRow0=1, AtlasRows=1, Colour=ColourMode.ConstantArgb, ConstArgb=0xFFA89880,
                StartSize=1.5f, SizeJitter=0.3f, Size=SizeMode.Grow, AgeCap=63, DistanceKill=true };

            // 43 — PORT-ONLY sprite-burst shatter — DORMANT INFRA since round 6: the
            // billboard rendition lost the mesh geometry (user: 
            //), so the LIVE shatter is now the true geometry effect
            // (MeshShatterEffect — real triangle shards of the current-frame mesh with the
            // model's own material/UVs, per the MeshDestructor law FUN_004830a0 /
            // FUN_00482950 / FUN_005ea850). This set + CreateTempShatter/SetShatterSource
            // remain as tested sprite-burst infrastructure (possible accents: distant kills,
            // dust over the shards) but are NOT wired to any live path.
            t[43] = new ParticleType { SetId=43, Tex=ParticleTex.C256d, RenderMode=0x0f, Alpha=255, AtlasGrid=4,
                Shape=SpawnShape.MeshVerts, Atlas=AtlasMode.RandomCell, AtlasRow0=2, AtlasRows=1,
                Colour=ColourMode.ConstantArgb, ConstArgb=0xFF9F9F9F,
                StartSize=0.55f, Size=SizeMode.Shrink, Gravity=new(0,-0.012f,0), Drag=0.985f,
                AgeCap=72, DistanceKill=true };

            return t;
        }

        static ParticleType Poof(int id, int[] frames) => new ParticleType {
            SetId=id, Tex=ParticleTex.A256d, RenderMode=0x10, Alpha=100, AtlasGrid=8,
            Shape=SpawnShape.Point, CountMode=SpawnCount.GatedProb, GateProb=0.06f,   // sparse so the flipbook reads as an animation, not an overlaid blur
            Atlas=AtlasMode.FlipbookByAge, FlipFrames=frames,
            StartSize=6.5f, Size=SizeMode.Grow, AgeCap=frames.Length + 1 };
    }
}
