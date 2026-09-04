#nullable enable

namespace Diggles.Tests
{
    using Diggles.Runtime.World;
    using NUnit.Framework;

    // Proves the faithful MT19937 (worldgen phase 4, B7) reproduces the original engine's RNG
    // (FUN_0049c670 genrand + FUN_004454d0 sgenrand) EXACTLY, and that the System.Random drop-in
    // overrides reproduce the native draw idioms the port's worldgen call sites were written against.
    //
    // Reference vectors were produced by an INDEPENDENT oracle: two separate transcriptions — the
    // canonical 1997 "mt19937.c" (textbook) and a literal transcription of the Ghidra decompile —
    // agree bit-for-bit over 1300+ draws across the reload boundary for seeds 0x1105 / 12345 / 1
    // (cross-check script: scratchpad/mt_crosscheck.py). If Mt19937 matches these, it is the exact
    // 1997 MT19937 the shipped exe uses.
    public sealed class Mt19937Tests
    {
        // sgenrand(0x1105) — the NATIVE seed (0x1105 == 4357 == the canonical 1997 default) — first 16.
        static readonly uint[] Seed0x1105_First16 =
        {
            0xaae64ac3u, 0x5e7c47dau, 0xb98e8121u, 0xb010f327u, 0x92da86b4u, 0xcf8f28c4u,
            0x4565c353u, 0xd6781b71u, 0x232a4514u, 0xf51bb58bu, 0x2db9402fu, 0xf3f75ff7u,
            0x4ed2bd39u, 0xc8190e8cu, 0xc4b2be54u, 0xd9866819u,
        };
        static readonly uint[] Seed12345_First8 =
        {
            0xd00d52b0u, 0x2124971du, 0x41380277u, 0xc68f36ccu,
            0x06d200fdu, 0x98354598u, 0xaa742811u, 0xc92759beu,
        };
        static readonly uint[] Seed1_First8 =
        {
            0xd3f6b9e5u, 0xef284157u, 0x0d3639c7u, 0x22f28abfu,
            0xc8061f7eu, 0x57974ebcu, 0xd0d92ee3u, 0xe0db49c9u,
        };

        [Test]
        public void Genrand_MatchesReferenceVector_NativeSeed0x1105()
        {
            var mt = new Mt19937(0x1105);
            for (int i = 0; i < Seed0x1105_First16.Length; i++)
                Assert.AreEqual(Seed0x1105_First16[i], mt.NextUInt32(), $"draw {i}");
        }

        [Test]
        public void Genrand_MatchesReferenceVector_OtherSeeds()
        {
            var a = new Mt19937(12345u);
            for (int i = 0; i < Seed12345_First8.Length; i++)
                Assert.AreEqual(Seed12345_First8[i], a.NextUInt32(), $"seed 12345 draw {i}");

            var b = new Mt19937(1u);
            for (int i = 0; i < Seed1_First8.Length; i++)
                Assert.AreEqual(Seed1_First8[i], b.NextUInt32(), $"seed 1 draw {i}");
        }

        [Test]
        public void DefaultCtor_EqualsNativeLazySeed0x1105()
        {
            // The native lazily seeds sgenrand(0x1105) on first genrand (FUN_0049c670:33). The
            // parameterless ctor must therefore equal the explicit 0x1105 stream.
            var lazy = new Mt19937();
            var seeded = new Mt19937(0x1105);
            for (int i = 0; i < 32; i++)
                Assert.AreEqual(seeded.NextUInt32(), lazy.NextUInt32(), $"draw {i}");
            Assert.AreEqual(0x1105u, Mt19937.NativeSeed);
        }

        [Test]
        public void Genrand_ReloadBoundary_Draw624()
        {
            // After 624 draws the state array regenerates (mti wraps at N=624); draw #624 must be the
            // first output of the second reload block — exercises the FUN_0049c670 regenerate path twice.
            var mt = new Mt19937(0x1105);
            for (int i = 0; i < 624; i++) mt.NextUInt32();
            Assert.AreEqual(0x3e44719du, mt.NextUInt32(), "draw 624 (post second reload)");
        }

        [Test]
        public void Next_ReturnsRawGenrand_LowBitsMatchNativeMask()
        {
            // The port consumes the raw draw as `Rng.Next & 0x7fff` (== native `genrand & 0x7fff`).
            var raw = new Mt19937(0x1105);
            var viaNext = new Mt19937(0x1105);
            for (int i = 0; i < 16; i++)
            {
                uint g = raw.NextUInt32();
                int n = viaNext.Next();
                Assert.AreEqual(unchecked((int)g), n, $"raw draw {i}");
                Assert.AreEqual((int)(g & 0x7fffu), n & 0x7fff, $"low-15 draw {i}");
            }
        }

        [Test]
        public void NextDouble_IsNativeDice_Genrand0x7fffOver32768()
        {
            // Native selection dice: r = (genrand & 0x7fff) / 32768.0 (FUN_005fac90:641-642).
            var dice = new Mt19937(0x1105);
            var raw = new Mt19937(0x1105);
            for (int i = 0; i < 16; i++)
            {
                double d = dice.NextDouble();
                double expected = (raw.NextUInt32() & 0x7fffu) / 32768.0;
                Assert.AreEqual(expected, d, 1e-12, $"dice draw {i}");
                Assert.GreaterOrEqual(d, 0.0);
                Assert.Less(d, 1.0);
            }
        }

        [Test]
        public void NextBounded_IsNativeModulo_AndInRange()
        {
            // Native bounded pick: (genrand & 0x7fff) % n — one draw, result in [0, n).
            var bounded = new Mt19937(0x1105);
            var raw = new Mt19937(0x1105);
            foreach (int n in new[] { 1, 2, 7, 100, 128, 4096 })
            {
                int v = bounded.Next(n);
                int expected = (int)((raw.NextUInt32() & 0x7fffu) % (uint)n);
                Assert.AreEqual(expected, v, $"Next({n})");
                Assert.GreaterOrEqual(v, 0);
                Assert.Less(v, n);
            }
            Assert.AreEqual(0, new Mt19937(0x1105).Next(0), "Next(0) == 0 (System.Random contract)");
        }

        [Test]
        public void IsDropInFor_SystemRandom()
        {
            // Assignable to the System.Random-typed worldgen fields; overrides dispatch virtually.
            System.Random r = new Mt19937(0x1105);
            Assert.AreEqual(Seed0x1105_First16[0], unchecked((uint)r.Next()));
        }

        [Test]
        public void CreateSessionStream_IsReproducible_AndDecorrelatesPerSession()
        {
            // Same session seed → identical stream (reproducible for a harness / a re-dig of the
            // same session). Different session seeds → different streams (per-session cave variety
            // preserved — the point of the prewarm, vs a fixed MT(0x1105) that repeats every run).
            uint[] Draws(System.Random rng, int n)
            {
                var o = new uint[n];
                for (int i = 0; i < n; i++) o[i] = unchecked((uint)rng.Next());
                return o;
            }

            var repA = Draws(Mt19937.CreateSessionStream(1000), 8);
            var repB = Draws(Mt19937.CreateSessionStream(1000), 8);
            Assert.AreEqual(repA, repB, "same session seed must reproduce the same stream");

            var s1 = Draws(Mt19937.CreateSessionStream(1000), 8);
            var s2 = Draws(Mt19937.CreateSessionStream(99999), 8);
            Assert.AreNotEqual(s1, s2, "different session seeds must decorrelate (variety)");
        }
    }
}
