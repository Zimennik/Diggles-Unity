#nullable enable

namespace Diggles.Runtime.World
{
    using System;

    /// <summary>
    /// Faithful C# port of the original Diggles game-wide RNG: the Matsumoto-Nishimura MT19937
    /// (the 1997 reference "mt19937.c"), as it was compiled into the shipped engine.
    ///
    /// Decompile ground truth:
    /// • FUN_004454d0 (sgenrand) — the classic 1997 seeder, TWO 69069-multiplies per word:
    /// mt[i] = seed & 0xffff0000; seed = 69069*seed + 1;
    /// mt[i] |= (seed & 0xffff0000) >> 16; seed = 69069*seed + 1;
    /// multiplier 0x10dcd == 69069, N = 0x270 == 624, sets mti (DAT_00768350) = 624.
    /// (This is NOT the 2002 `init_genrand` "mt[0]=seed; mt[i]=1812433253*(…)+i" form —
    /// the shipped exe uses the older two-multiply sgenrand.)
    /// • FUN_0049c670 (genrand) — the 1997 generator over shared state DAT_00767990[624],
    /// index DAT_00768350. LAZILY seeds with the CONSTANT 0x1105 (== 4357, the canonical
    /// 1997 default) at:33 when mti == N+1 (0x271). N=624, M=397 (0x18d), UPPER=0x80000000,
    /// LOWER=0x7fffffff, mag01[1]=MATRIX_A=0x9908b0df (@DAT_00724484+4; mag01[0]=0), tempering
    /// shifts 11 / 7&0x9d2c5680 / 15&0xefc60000 / 18. (The decompile writes the two middle
    /// tempering steps as (y & 0xff3a58ad)<<7 and (y & 0xffffdf8c)<<15 — algebraically identical
    /// to (y<<7)&0x9d2c5680 / (y<<15)&0xefc60000, since the extra mask bits shift out of 32-bit.)
    ///
    /// Native usage: ONE shared MT advanced by EVERY engine system (map-init FUN_00598f10 alone
    /// burns ~512x640 draws), so at worldgen time the stream is heavily decorrelated. The port
    /// cannot reproduce that cross-system interleaving (our other systems roll their own RNGs and
    /// neither memory dump captured a live worldgen trace), so bit-exact reproduction of a SPECIFIC
    /// original playthrough is UNACHIEVABLE. What this class delivers: the faithful algorithm, the
    /// native seed, native draw semantics, and — via <see cref="CreateSessionStream"/> — a bounded
    /// per-session prewarm that emulates the shared-stream decorrelation so each playthrough still
    /// varies (a fixed MT(0x1105) worldgen-only stream would reintroduce the "same caves" bug).
    ///
    /// Subclasses <see cref="System.Random"/> so it is a drop-in for the System.Random-typed worldgen
    /// fields (LevelGenerator.Rng, DigglesTclContext.StoryRng, FreeRectFit/DrawStone params). The
    /// overrides reproduce the EXACT native draw idioms the port's call sites were written against:
    /// • Next → raw 32-bit genrand (the port masks it: `Rng.Next &amp; 0x7fff`).
    /// • Next(n) → native bounded pick `(genrand &amp; 0x7fff) % n` (one draw; [0,n)).
    /// • NextDouble → native selection dice `(genrand &amp; 0x7fff) / 32768.0` in [0,1).
    /// </summary>
    public sealed class Mt19937 : System.Random
    {
        const int N = 624;                 // FUN_0049c670: 0x270
        const int M = 397;                 // 0x18d
        const uint MatrixA = 0x9908b0dfu;  // mag01[1] @ DAT_00724484+4 (mag01[0] = 0)
        const uint UpperMask = 0x80000000u;
        const uint LowerMask = 0x7fffffffu;

        /// <summary>The native lazy default seed (FUN_0049c670:33 → sgenrand(0x1105)). 0x1105 == 4357.</summary>
        public const uint NativeSeed = 0x1105u;

        readonly uint[] _mt = new uint[N];
        int _mti;

        [Serializable]
        public sealed class SaveState
        {
            public uint[] Values = Array.Empty<uint>();
            public int Index;
        }

        /// <summary>Native const-seed stream, exactly as the shipped engine lazily boots it.</summary>
        public Mt19937() : this(NativeSeed) { }

        /// <summary>Deterministic stream from an explicit seed (harness/tests; faithful sgenrand).</summary>
        public Mt19937(uint seed) { SGenrand(seed); }

        /// <summary>
        /// int overload so `new Mt19937(seed)` is a drop-in for the existing `new System.Random(seed)`
        /// deterministic-test seed sites. Seeds the same 32-bit pattern.
        /// </summary>
        public Mt19937(int seed) : this(unchecked((uint)seed)) { }

        public SaveState CaptureSaveState() => new()
        {
            Values = (uint[])_mt.Clone(),
            Index = _mti,
        };

        public void RestoreSaveState(SaveState state)
        {
            if (state.Values == null || state.Values.Length != N || state.Index < 0 || state.Index > N)
                throw new ArgumentException("Saved MT19937 state is invalid.", nameof(state));
            Array.Copy(state.Values, _mt, N);
            _mti = state.Index;
        }

        // FUN_004454d0 — sgenrand. Sets _mti = N so the first draw regenerates the block (identical
        // to the native's lazy sgenrand-then-fall-through-to-reload path, which also leaves mti = N).
        void SGenrand(uint seed)
        {
            for (int i = 0; i < N; i++)
            {
                _mt[i] = seed & 0xffff0000u;
                seed = 69069u * seed + 1u;             // 0x10dcd
                _mt[i] |= (seed & 0xffff0000u) >> 16;
                seed = 69069u * seed + 1u;
            }
            _mti = N;
        }

        /// <summary>
        /// FUN_0049c670 — genrand: the raw 32-bit MT19937 output. This is THE faithful primitive;
        /// every other method derives from it. The port's `Rng.Next &amp; 0x7fff` idiom takes the
        /// low 15 bits of this value (== the native `genrand &amp; 0x7fff`).
        /// </summary>
        public uint NextUInt32()
        {
            if (_mti >= N)                              // regenerate the whole array (FUN_0049c670:31+)
            {
                uint y;
                int kk;
                for (kk = 0; kk < N - M; kk++)          // loop 1: kk = 0..226 (:35-66)
                {
                    y = (_mt[kk] & UpperMask) | (_mt[kk + 1] & LowerMask);
                    _mt[kk] = _mt[kk + M] ^ (y >> 1) ^ ((y & 1u) * MatrixA);
                }
                for (; kk < N - 1; kk++)                // loop 2: kk = 227..622 (:67-102)
                {
                    y = (_mt[kk] & UpperMask) | (_mt[kk + 1] & LowerMask);
                    _mt[kk] = _mt[kk + (M - N)] ^ (y >> 1) ^ ((y & 1u) * MatrixA);
                }
                y = (_mt[N - 1] & UpperMask) | (_mt[0] & LowerMask);   // last element (:104-105)
                _mt[N - 1] = _mt[M - 1] ^ (y >> 1) ^ ((y & 1u) * MatrixA);
                _mti = 0;
            }

            uint v = _mt[_mti++];
            v ^= v >> 11;                               // tempering (:11)
            v ^= (v << 7) & 0x9d2c5680u;
            v ^= (v << 15) & 0xefc60000u;
            v ^= v >> 18;
            return v;
        }

        // ===================== System.Random drop-in overrides =====================

        /// <summary>Raw 32-bit genrand as int. The port always masks it (`Next &amp; 0x7fff`), so the
        /// (possibly negative) sign is irrelevant — the low 15 bits equal the native `genrand &amp; 0x7fff`.</summary>
        public override int Next() => unchecked((int)NextUInt32());

        /// <summary>Native bounded pick `(genrand &amp; 0x7fff) % maxValue` — ONE draw, result in [0,maxValue).
        /// Matches the worldgen modulo idiom (FUN_005862c0:89-95, LevelGenerator.cs:1048) and preserves
        /// the port's draw-count discipline in FreeRectFit / the utemp placement (DigglesTclEngine.cs:4602).</summary>
        public override int Next(int maxValue)
        {
            if (maxValue <= 0) return 0;                // System.Random contract: Next(0) == 0
            return (int)((NextUInt32() & 0x7fffu) % (uint)maxValue);
        }

        /// <summary>Drop-in for the two-arg form (NOT on the worldgen path — kept for contract fidelity).
        /// Uses the full 32-bit draw modulo the range so any range width is handled.</summary>
        public override int Next(int minValue, int maxValue)
        {
            if (maxValue <= minValue) return minValue;
            uint range = (uint)((long)maxValue - minValue);
            return minValue + (int)(NextUInt32() % range);
        }

        /// <summary>Native selection dice `(genrand &amp; 0x7fff) / 32768.0` in [0,1) — ONE draw. This is
        /// the exact form the priority draw compares against (FUN_005fac90:641-642; DigglesTclEngine.cs:4280
        /// `weight &lt; StoryRng.NextDouble`). 15-bit resolution, faithful to the native.</summary>
        public override double NextDouble() => (NextUInt32() & 0x7fffu) * (1.0 / 32768.0);

        /// <summary>Keep the protected base sampler consistent with NextDouble for any base-class path.</summary>
        protected override double Sample() => NextDouble();

        /// <summary>Fill from raw genrand words (not on the worldgen path; provided for drop-in completeness).</summary>
        public override void NextBytes(byte[] buffer)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            int i = 0;
            for (; i + 4 <= buffer.Length; i += 4)
            {
                uint r = NextUInt32();
                buffer[i] = (byte)r;
                buffer[i + 1] = (byte)(r >> 8);
                buffer[i + 2] = (byte)(r >> 16);
                buffer[i + 3] = (byte)(r >> 24);
            }
            if (i < buffer.Length)
            {
                uint r = NextUInt32();
                for (; i < buffer.Length; i++) { buffer[i] = (byte)r; r >>= 8; }
            }
        }

        // ===================== per-session decorrelation =====================

        // The shared-stream decorrelation emulation. Native variety comes from every engine system
        // advancing the ONE shared MT before worldgen samples it; the port has no such interleaving,
        // so a bare MT(0x1105) worldgen-only stream would regenerate the SAME caves every playthrough
        //. Instead we keep the native algorithm + base seed and DECORRELATE by
        // discarding a BOUNDED, per-session number of draws up front — a stand-in for "how far the
        // shared stream had advanced by the time this level generated."
        const int PrewarmBase = N;          // always skip at least one full reload block
        const int PrewarmSpan = 1 << 16;    // bound: at most ~65k discards (sub-millisecond, one-time)

        /// <summary>
        /// Builds the LIVE worldgen stream: the native const seed 0x1105, then a bounded per-session
        /// prewarm derived from <paramref name="sessionSeed"/> (the old GUID/TickCount entropy). Same
        /// session seed → same stream (reproducible); different sessions decorrelate → per-session
        /// cave variety is preserved. For a PINNED deterministic stream (tests/harness) use
        /// `new Mt19937(seed)` directly (no prewarm).
        /// </summary>
        public static Mt19937 CreateSessionStream(int sessionSeed)
        {
            var mt = new Mt19937(NativeSeed);
            int prewarm = PrewarmBase + (int)((uint)sessionSeed % PrewarmSpan);
            for (int i = 0; i < prewarm; i++) mt.NextUInt32();
            return mt;
        }
    }
}
