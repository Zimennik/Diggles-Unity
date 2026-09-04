#nullable enable

namespace Diggles.Runtime.Particles
{
    using UnityEngine;

    // The original particle engine's random source: a 32-bit LCG (FUN_*: x = x*0x343fd + 0x269ec3),
    // sampled as (x >> 16) & mask. The per-type spawn/integrate functions pull angles, radii, jitter
    // and atlas cells from it through a handful of fixed bit-field idioms — reproduced here so the
    // CPU port has the same statistical character. The original keeps one GLOBAL state word; we give
    // each emitter its own seed (visual character is identical; exact reproduction is impossible
    // anyway under Unity's variable frame timing).
    public struct ParticleLcg
    {
        uint _s;

        public ParticleLcg(uint seed) => _s = seed == 0u ? 0x2545F491u : seed;

        public uint Next()
        {
            _s = _s * 0x343FDu + 0x269EC3u;
            return _s;
        }

        // (x >> 16) & mask — the raw idiom.
        public int Bits(uint mask) => (int)((Next() >> 16) & mask);

        // Unsigned 15-bit sample, the basis for angles/radii/cells.
        public int U15() => (int)((Next() >> 16) & 0x7FFFu);

        // Symmetric small jitter: ((x>>16 & 0xf) - 8) / norm → roughly [-8/norm, +7/norm].
        public float Sym4(float norm) => (Bits(0xF) - 8) / norm;

        // Angle 0..2π: (x>>16 & 0x7fff) % 360 / 180 * π.
        public float Angle() => (U15() % 360) * (Mathf.PI / 180f);

        // Radius/length fraction 0..1.
        public float Frac01() => (U15() % 100) / 100f;

        // U15 % m.
        public int Mod(int m) => m <= 0 ? 0 : U15() % m;

        // Uniform 0..1 over the full 15-bit range.
        public float Unit() => U15() / 32767f;
    }
}
