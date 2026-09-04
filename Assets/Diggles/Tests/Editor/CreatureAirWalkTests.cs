#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using Diggles.Runtime;
    using Diggles.Runtime.Navigation;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class CreatureAirWalkTests
    {
        private const int Width = 4;
        private const int Height = 3;
        private const int FloorSubY = 8;
        private const int PitStartSubX = 10;
        private const int PitEndSubX = 15;

        [Test]
        public void HamsterMetaWalk_CarvedPit_RefusesUnsupportedFloorStep()
        {
            var root = new GameObject(nameof(HamsterMetaWalk_CarvedPit_RefusesUnsupportedFloorStep));
            try
            {
                byte[] subGrid = BuildFloorBand();
                var terrain = root.AddComponent<DiggableTerrain>();
                terrain.Nav = new NavWorld(
                    Width,
                    Height,
                    new uint[Width * Height],
                    subGrid,
                    new BdbModel(),
                    deriveImmediately: false);
                var navService = root.AddComponent<NavWorldService>();

                var body = new GameObject("Hamster");
                body.transform.SetParent(root.transform, false);
                var controller = body.AddComponent<DigglesAnimController>();
                Vector3 start = ToWorld(root.transform, new Vector3(0.5f, 1f, 5f));
                Vector3 goal = ToWorld(root.transform, new Vector3(3f, 1f, 5f));
                body.transform.position = start;

                int supportProbeCount = 0;
                bool ProbeFloorSupport(Vector3 worldPosition)
                {
                    supportProbeCount++;
                    return navService.HasFloorSupportWorld(worldPosition);
                }

                var walk = new MetaWalkAction(
                    controller,
                    body.transform,
                    new List<Vector3> { goal },
                    1,
                    -1,
                    0,
                    ProbeFloorSupport);

                CarvePit(subGrid);

                bool running = true;
                int tickCount = 0;
                while (running && tickCount++ < 512)
                {
                    running = walk.Tick(0.1d);
                    Assert.That(
                        navService.HasFloorSupportWorld(body.transform.position),
                        Is.True,
                        $"hamster occupied an air cell on floor leg tick {tickCount}");
                }

                Vector3 stoppedNative = RuntimeWorldSpawner.LocalToNative(
                    root.transform.InverseTransformPoint(body.transform.position));
                Assert.That(running, Is.False, "hamster floor leg did not terminate at the pit");
                Assert.That(walk.ExitResult, Is.EqualTo(3), "pit must terminate the floor leg with WS_NOPASSAGE");
                Assert.That(stoppedNative.x, Is.LessThan(PitStartSubX * 0.125f));
                Assert.That(supportProbeCount, Is.GreaterThan(0));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void WormMove_WallAxisTraversal_RemainsOutsideFloorSupportGate()
        {
            var context = new DigglesTclContext(new TclInterpreter());
            var worm = new DigglesTclEntity
            {
                Position = new Vector3(2f, 1f, 7f),
                Rotation = new Vector3(0.2f, 1.25f, -0.15f),
            };
            Vector3 target = new Vector3(2f, 3f, 7f);
            Vector3 surfaceRotation = worm.Rotation;
            var move = new MoveAction(context, worm, target, 0.03f);

            Assert.That(move, Is.Not.InstanceOf<IWalkResultCarrier>());

            bool running = true;
            int tickCount = 0;
            float previousY = worm.Position.y;
            while (running && tickCount++ < 256)
            {
                running = move.Tick(0.1d);
                Assert.That(worm.Position.x, Is.EqualTo(2f).Within(1e-6f));
                Assert.That(worm.Position.z, Is.EqualTo(7f).Within(1e-6f));
                Assert.That(worm.Position.y, Is.GreaterThanOrEqualTo(previousY));
                Assert.That(worm.Rotation, Is.EqualTo(surfaceRotation));
                previousY = worm.Position.y;
            }

            Assert.That(running, Is.False, "worm CAct_Move-compatible surface traversal did not finish");
            Assert.That(worm.Position, Is.EqualTo(target));
        }

        private static byte[] BuildFloorBand()
        {
            var subGrid = new byte[Width * Height * 64];
            for (int subX = 0; subX < Width * 8; subX++)
                SetSubCell(subGrid, subX, FloorSubY, (byte)(NavWorld.SubWalk | NavWorld.SubFloor));
            return subGrid;
        }

        private static void CarvePit(byte[] subGrid)
        {
            for (int subX = PitStartSubX; subX <= PitEndSubX; subX++)
                SetSubCell(subGrid, subX, FloorSubY, 0);
        }

        private static void SetSubCell(byte[] subGrid, int subX, int subY, byte value)
        {
            int tileIndex = (subY >> 3) * Width + (subX >> 3);
            int subIndex = tileIndex * 64 + ((subY & 7) << 3) + (subX & 7);
            subGrid[subIndex] = value;
        }

        private static Vector3 ToWorld(Transform root, Vector3 nativePosition) =>
            root.TransformPoint(RuntimeWorldSpawner.NativeToLocal(nativePosition));
    }
}
