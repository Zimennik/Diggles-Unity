#nullable enable

namespace Diggles.Tests
{
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    // Fog-of-war core laws — faithful port of CFogOfWar (Diggles.exe 0057dfc0..00580610;
    // Docs/FOG_OF_WAR_RE.md). Byte encoding: 0 = black never-seen, 1 = gray explored floor,
    // 2..0xFE fading −4/update, 0xFF currently visible. Laws under test:
    // • reveal = radial raycast that penetrates exactly 3 tiles into solid rock (fill 0xF);
    // • negative set_fogofwar sizes → RemoveFogBoxNoCollision (reveals through rock);
    // • FadeBlocks: −4 per update, clamps at the gray floor 1, leaves the fade list;
    // • set_fow_begin: rows above the line are permanently lit and never re-fog;
    // • remove_black_fog: pure 0→1 flip, never touches bytes ≥ 1;
    // • GetMaxFow sampling: max over 7 rect samples, ≥0xE3 early-outs to 0xFF;
    // • UpdateRevealer: sizeX == -1 disabled; refresh on tile crossing or the mod-6 slot.
    public sealed class FogOfWarTests
    {
        const int W = 64, H = 64;
        int[]? _fill;   // per-tile fill nibble; null cell = 0 (open)

        FogOfWarState Make()
        {
            _fill = new int[W * H];
            return new FogOfWarState(W, H,
                (x, y) => (uint)x < W && (uint)y < H ? _fill![y * W + x] : 15);
        }

        void FillRect(int l, int t, int r, int b, int v)
        {
            for (int y = t; y <= b; y++)
                for (int x = l; x <= r; x++)
                    _fill![y * W + x] = v;
        }

        static void Reveal(FogOfWarState fog, int tx, int ty, int sizeTiles, int owner = 0)
            => fog.RemoveFog(tx * 8, ty * 8, sizeTiles * 8, sizeTiles * 8, owner, 0f, 1f);

        [Test]
        public void FreshState_UsesNativeVirginFowBeginRows()
        {
            FogOfWarState fog = Make();

            Assert.That(fog.CaptureSaveState().FowBegin, Is.All.EqualTo(-1));
        }

        [Test]
        public void RadialReveal_OpensAroundCenter_AndStopsThreeTilesIntoRock()
        {
            var fog = Make();
            // Solid wall x ∈ [40..63] (fill 0xF); open ground elsewhere.
            FillRect(40, 0, W - 1, H - 1, 0xF);
            Reveal(fog, 32, 32, 12);

            Assert.That(fog.FogAt(32, 32, 0), Is.EqualTo(0xFF), "center visible");
            Assert.That(fog.FogAt(36, 32, 0), Is.EqualTo(0xFF), "open ground visible");
            // Rays enter the wall at x=40 and run 3 tiles into it (pen counter 1→3):
            Assert.That(fog.FogAt(40, 32, 0), Is.EqualTo(0xFF), "rock face visible");
            Assert.That(fog.FogAt(41, 32, 0), Is.EqualTo(0xFF), "1 tile into rock");
            // …but never past the penetration depth:
            Assert.That(fog.FogAt(44, 32, 0), Is.EqualTo(0), "4 tiles into rock stays BLACK");
            Assert.That(fog.FogAt(63, 32, 0), Is.EqualTo(0), "far side of the wall stays BLACK");
        }

        [Test]
        public void NoCollisionBox_RevealsThroughSolidRock()
        {
            var fog = Make();
            FillRect(0, 0, W - 1, H - 1, 0xF);                    // world of solid rock
            fog.RemoveFogBoxNoCollision(32 * 8, 32 * 8, 10 * 8, 10 * 8, 0);
            Assert.That(fog.FogAt(32, 32, 0), Is.EqualTo(0xFF));
            Assert.That(fog.FogAt(39, 39, 0), Is.EqualTo(0xFF), "deep inside rock — FogRemover law");
            Assert.That(fog.FogAt(50, 32, 0), Is.EqualTo(0), "outside the box stays BLACK");
        }

        [Test]
        public void FadeBlocks_DecaysByFour_ClampsAtGrayFloor()
        {
            var fog = Make();
            Reveal(fog, 32, 32, 4);
            Assert.That(fog.FogAt(32, 32, 0), Is.EqualTo(0xFF));

            // One update with no revealers: 0xFF − 4 = 0xFB.
            fog.BeginUpdate(0); fog.EndUpdate();
            Assert.That(fog.FogAt(32, 32, 0), Is.EqualTo(0xFB));

            // Enough updates to hit the floor: byte must clamp at exactly 1 (gray), never 0.
            for (int i = 0; i < 80; i++) { fog.BeginUpdate(0); fog.EndUpdate(); }
            Assert.That(fog.FogAt(32, 32, 0), Is.EqualTo(1), "explored floor is 1, not black");
        }

        [Test]
        public void FowBegin_SkyBandIsPermanentlyLit_AndNeverFades()
        {
            var fog = Make();
            fog.SetFowBegin(20, 0);
            // The native floods rows [minRow+5, begin) full-width minus the right 2 cols.
            Assert.That(fog.FogAt(10, 10, 0), Is.EqualTo(0xFF), "sky band revealed");
            Assert.That(fog.FogAt(10, 25, 0), Is.EqualTo(0), "below the line stays black");

            // A reveal above the line must not enter the fade list → stays 0xFF forever.
            for (int i = 0; i < 80; i++) { fog.BeginUpdate(0); fog.EndUpdate(); }
            Assert.That(fog.FogAt(10, 10, 0), Is.EqualTo(0xFF), "sky never re-fogs");

            // A reveal below the line fades normally.
            Reveal(fog, 32, 40, 4);
            for (int i = 0; i < 80; i++) { fog.BeginUpdate(0); fog.EndUpdate(); }
            Assert.That(fog.FogAt(32, 40, 0), Is.EqualTo(1), "below the line fades to gray");
        }

        [Test]
        public void RemoveBlackFog_FlipsZeroToOneOnly()
        {
            var fog = Make();
            Reveal(fog, 10, 10, 2);                                // some tiles now 0xFF
            fog.RemoveBlackFog(0, 5, 5, 15, 15);
            Assert.That(fog.FogAt(14, 14, 0), Is.EqualTo(1), "black → gray");
            Assert.That(fog.FogAt(10, 10, 0), Is.EqualTo(0xFF), "visible byte untouched");
            Assert.That(fog.FogAt(30, 30, 0), Is.EqualTo(0), "outside rect untouched");
            // Grayed tiles are not in the fade list — nothing changes on update.
            fog.BeginUpdate(0); fog.EndUpdate();
            Assert.That(fog.FogAt(14, 14, 0), Is.EqualTo(1));
        }

        [Test]
        public void SampleMaxFow_SevenSamples_EarlyOutToFullVisible()
        {
            var fog = Make();
            Reveal(fog, 32, 32, 3);
            Assert.That(fog.SampleMaxFow(32, 32, 32, 32, 0), Is.EqualTo(0xFF),
                "a ≥0xE3 sample early-outs to 0xFF");
            Assert.That(fog.SampleMaxFow(2, 2, 2, 2, 0), Is.EqualTo(0), "unseen tile samples 0");

            // Fade below 0xE3, then the sampler returns the raw max byte.
            for (int i = 0; i < 10; i++) { fog.BeginUpdate(0); fog.EndUpdate(); }
            int v = fog.SampleMaxFow(32, 32, 32, 32, 0);
            Assert.That(v, Is.EqualTo(0xFF - 10 * 4), "raw fading byte (0xFF − 10·4)");
        }

        [Test]
        public void UpdateRevealer_MinusOneDisables_OwnersIsolated()
        {
            var fog = Make();
            float lx = 0, ly = 0;

            fog.BeginUpdate(0);
            // sizeX == -1 → no reveal at all (set_fogofwar this -1 -1).
            fog.UpdateRevealer(32f, 32f, ref lx, ref ly, -1, -1, owner: 0, slotId: 0);
            fog.EndUpdate();
            Assert.That(fog.FogAt(32, 32, 0), Is.EqualTo(0));

            // A fresh revealer (last pos = origin, native zero-filled obj+0x26c) crosses a tile
            // on its first update → reveals immediately, for its owner only.
            lx = 0; ly = 0;
            fog.BeginUpdate(0);
            fog.UpdateRevealer(32f, 32f, ref lx, ref ly, 14 * 8, 8 * 8, owner: 2, slotId: 0);
            fog.EndUpdate();
            Assert.That(fog.FogAt(32, 32, 2), Is.EqualTo(0xFF), "owner 2 lane revealed");
            Assert.That(fog.FogAt(32, 32, 0), Is.EqualTo(0), "owner 0 lane untouched");
        }

        [Test]
        public void UpdateRevealer_RefreshesOnTileCrossing_OrModSixSlot()
        {
            var fog = Make();
            float lx = 0, ly = 0;

            // Prime: reveal at (20,20) — establishes last pos.
            fog.BeginUpdate(0);
            fog.UpdateRevealer(20f, 20f, ref lx, ref ly, 6 * 8, 6 * 8, 0, slotId: 3);
            fog.EndUpdate();
            Assert.That(fog.FogAt(20, 20, 0), Is.EqualTo(0xFF));

            // Same tile, wrong slot phase → no re-reveal ran (fade proceeds).
            fog.BeginUpdate(0);
            bool ran = fog.UpdateRevealer(20.05f, 20.05f, ref lx, ref ly, 6 * 8, 6 * 8, 0, slotId: 3);
            fog.EndUpdate();
            Assert.That(ran, Is.False, "no boundary crossing, slot not due");
            Assert.That(fog.FogAt(20, 20, 0), Is.EqualTo(0xFB), "faded while revealer idle");

            // Crossing into a new tile refreshes immediately regardless of the slot.
            fog.BeginUpdate(0);
            ran = fog.UpdateRevealer(23f, 20f, ref lx, ref ly, 6 * 8, 6 * 8, 0, slotId: 3);
            fog.EndUpdate();
            Assert.That(ran, Is.True, "tile crossing forces the reveal");
            Assert.That(fog.FogAt(23, 20, 0), Is.EqualTo(0xFF));
        }

        [Test]
        public void Simulation_TransportLogicRevealsElevatorAndLadderShafts()
        {
            var fog = Make();
            var context = DigglesTclEngine.Register(new TclInterpreter());
            context.FogOfWar = fog;
            var aux = new byte[BdbModel.AuxStride];
            for (int index = 0; index < 64; index++) aux[index] = 11;
            var bdb = new BdbModel
            {
                Header = new BdbHeader { NBlocks = 1 },
                PerBlockAux = aux,
            };
            var nav = new Diggles.Runtime.Navigation.NavWorld(
                W,
                H,
                new uint[W * H],
                new byte[W * H * 64],
                bdb);
            var terrainObject = new GameObject("fog-transport-terrain");
            var terrain = terrainObject.AddComponent<DiggableTerrain>();
            terrain.Nav = nav;
            context.Terrain = terrain;

            try
            {
                var elevator = new DigglesTclEntity
                {
                    Class = new DigglesClass { Name = "FogElevator", Kind = "elevator" },
                    Owner = 0,
                    Position = new Vector3(16f, 16f, 8f),
                    FowSizeX = 8,
                    FowSizeY = 8,
                    BuildupComplete = true,
                    TransportLogic = new TransportLogicState
                    {
                        Elevator = new ElevatorLogicState { MaxRange = 8f },
                    },
                };
                var ladder = new DigglesTclEntity
                {
                    Class = new DigglesClass { Name = "FogLadder", Kind = "ladder" },
                    Owner = 0,
                    Position = new Vector3(48f, 40f, 8f),
                    FowSizeX = 8,
                    FowSizeY = 8,
                    BuildupComplete = true,
                    TransportLogic = new TransportLogicState
                    {
                        Ladder = new LadderLogicState { Range = 8f },
                    },
                };
                context.RegisterEntity(elevator);
                context.RegisterEntity(ladder);

                var simulation = new DigglesGameSim(context);
                for (int i = 0; i < 4; i++) simulation.Step(0.1);

                Assert.That(fog.FogAt(16, 23, 0), Is.EqualTo(0xFF),
                    "the elevator must reveal down its projected shaft, beyond its radial footprint");
                Assert.That(fog.FogAt(48, 30, 0), Is.EqualTo(0xFF),
                    "the ladder must reveal up its projected climb span, beyond its radial footprint");
            }
            finally
            {
                Object.DestroyImmediate(terrainObject);
            }
        }

        [Test]
        public void RevealAll_FloodsEveryLane()
        {
            var fog = Make();
            fog.Enabled = false;                 // -nofow: the pending flag floods on BeginUpdate
            fog.BeginUpdate(0); fog.EndUpdate();
            Assert.That(fog.FogAt(1, 1, 0), Is.EqualTo(0xFF));
            Assert.That(fog.FogAt(60, 60, 7), Is.EqualTo(0xFF), "all 8 lanes revealed");
        }
    }
}
