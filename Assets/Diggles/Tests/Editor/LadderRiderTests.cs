#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using Diggles.Runtime;
    using Diggles.Runtime.Navigation;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    // Vertical-ladder rider laws. Native evidence:
    // • FUN_00607000:49-54 reselects/reasserts the ladder rung every transport tick;
    // • FUN_00607000:48 dispatches riders to FUN_0060bc70, not CAct_Climb;
    // • FUN_0060bc70: up = rm(slot 0x0e) × 0.1/tick; down = v × 0.1 then
    // v += 0.25, capped at 2.5; FUN_0060c3c0 resets v for every registered ride;
    // • FUN_0060bb30 reasserts slot 0x0e/0x0f from the factual rider animation each tick.
    public sealed class LadderRiderTests
    {
        const float LadderUpRootMotion = 28.2918701171875f;
        const float WallUpRootMotion = 8.357107162475586f;
        const float WallDownRootMotion = 8.29141902923584f;
        const float WallLeftRootMotion = 8.288270950317383f;
        const float WallRightRootMotion = 8.306541442871094f;

        readonly List<GameObject> _gameObjects = new();
        readonly List<Mesh> _meshes = new();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject gameObject in _gameObjects)
                if (gameObject != null) Object.DestroyImmediate(gameObject);
            foreach (Mesh mesh in _meshes)
                if (mesh != null) Object.DestroyImmediate(mesh);
            _gameObjects.Clear();
            _meshes.Clear();
        }

        [Test]
        public void RungPresentation_ReassertsAgainstFactualCurrentAnim_NotSelectionCache()
        {
            var entity = new DigglesTclEntity();
            var ladder = new LadderLogicState();

            Assert.IsTrue(DigglesTclEngine.TryReassertLadderPresentationState(
                entity,
                ladder,
                "leiter_d",
                out string clip));
            Assert.AreEqual("leiter_d.standard", clip);
            Assert.AreEqual(clip, entity.CurrentAnim);
            Assert.AreEqual((int)DigglesAnimator.AnimMode.Still, entity.CurrentAnimMode);

            Assert.IsFalse(DigglesTclEngine.TryReassertLadderPresentationState(
                entity,
                ladder,
                "leiter_d",
                out _),
                "a factual match is a no-op, so the selected still frame is not restarted");

            entity.CurrentAnim = "leiter_a.standard";
            Assert.AreEqual("leiter_d", ladder.CurrentPresentation,
                "the selection cache intentionally still names the same rung");
            Assert.IsTrue(DigglesTclEngine.TryReassertLadderPresentationState(
                entity,
                ladder,
                "leiter_d",
                out clip),
                "FUN_00607000:49-54 reasserts after another writer replaces the actual clip");
            Assert.AreEqual("leiter_d.standard", entity.CurrentAnim);
            Assert.AreEqual((int)DigglesAnimator.AnimMode.Still, entity.CurrentAnimMode);
        }

        [Test]
        public void LadderUpVsWallClimb_UsesNativeMannSpeedRatio()
        {
            var (_, controller, _) = CreateRig();

            float ladderSpeed = controller.NativeLadderUpSpeed(0);
            float wallSpeed = controller.NativeClimbSpeed(0);
            float expectedWall = 0.25f * (
                WallUpRootMotion
                + WallDownRootMotion
                + WallLeftRootMotion
                + WallRightRootMotion) * DigglesAnimController.NativeArcToWorld;

            Assert.AreEqual(
                LadderUpRootMotion * DigglesAnimController.NativeArcToWorld,
                ladderSpeed,
                1e-6f,
                "FUN_0060bc70 uses slot 0x0e root motion directly for vertical-ladder ascent");
            Assert.AreEqual(expectedWall, wallSpeed, 1e-6f,
                "wall CAct_Climb keeps FUN_00418db0's quarter-sum law");
            Assert.AreEqual(3.4042153f, ladderSpeed / wallSpeed, 1e-5f,
                "mann ladder ascent must remain about 3.4x wall climbing, never the old average");
        }

        [Test]
        public void LadderAscent_ReassertsOverwrittenLoop_WithoutChangingPerTickSpeed()
        {
            var (body, controller, animator) = CreateRig();
            body.transform.position = Vector3.down * 10f;
            var action = CreateAction(controller, body.transform, Vector3.zero);
            float expectedStep = LadderUpRootMotion
 * DigglesAnimController.NativeArcToWorld
 * LadderRiderMover.NativeTickSeconds;

            for (int tick = 0; tick < 8; tick++)
            {
                if (tick == 4)
                {
                    animator.SetAnim("standard", 0, DigglesAnimator.AnimMode.Loop);
                    Assert.AreEqual("standard", animator.CurrentAnimName);
                }

                Vector3 before = body.transform.position;
                int restartsBeforeTick = animator.RestartCount;
                Assert.IsTrue(action.Tick(LadderRiderMover.NativeTickSeconds));
                Assert.AreEqual(expectedStep, Vector3.Distance(before, body.transform.position), 1e-5f,
                    $"ascent step {tick} must retain the native slot-0x0e speed");
                Assert.AreEqual("leiter_hoch_loop", animator.CurrentAnimName,
                    "FUN_0060bb30 reasserts the factual UP loop each rider tick");
                if (tick > 0 && tick != 4)
                    Assert.AreEqual(restartsBeforeTick, animator.RestartCount,
                        "a matching factual loop reassert must not rewind the animation");
                else if (tick == 4)
                    Assert.AreEqual(restartsBeforeTick + 1, animator.RestartCount,
                        "an overwritten factual loop is restored exactly once");
            }
        }

        [Test]
        public void LadderDescent_UsesNativeSteppedAccelerationAndCap()
        {
            var mover = new LadderRiderMover(0f, 10f, 1f);

            Assert.IsTrue(mover.Tick(0.1f));
            Assert.AreEqual(0f, mover.CurrentCoordinate, 1e-6f,
                "the first down tick moves by the entry velocity, initially zero");
            Assert.AreEqual(0.25f, mover.DownVelocity, 1e-6f);

            Assert.IsTrue(mover.Tick(0.1f));
            Assert.AreEqual(0.025f, mover.CurrentCoordinate, 1e-6f);
            Assert.AreEqual(0.5f, mover.DownVelocity, 1e-6f);

            for (int tick = 0; tick < 20; tick++) mover.Tick(0.1f);
            Assert.AreEqual(2.5f, mover.DownVelocity, 1e-6f,
                "DAT_007373d8 caps the native down velocity at 2.5");
        }

        [Test]
        public void RiderTick_HardSnapsToLadderPlane_AndFixedFacing()
        {
            var (body, controller, animator) = CreateRig();
            Vector3 anchor = new(1f, 0f, 2f);
            Quaternion facing = Quaternion.Euler(0f, 137f, 0f);
            body.transform.position = new Vector3(3f, -2f, 5f);
            var action = new LadderRiderAction(
                controller,
                body.transform,
                anchor,
                Vector3.down,
                anchor + Vector3.down * 4f,
                facing,
                0);

            Assert.IsTrue(action.Tick(LadderRiderMover.NativeTickSeconds));

            Assert.AreEqual(new Vector3(1f, -2f, 2f), body.transform.position,
                "FUN_0060bc70 snaps lateral/depth coordinates to ladder.x / ladder.z+1 each tick");
            Assert.AreEqual(0f, Quaternion.Angle(facing, body.transform.rotation), 1e-5f,
                "the rider keeps FUN_005cd8e0:222's fixed native (0,pi,0) facing");
            Assert.AreEqual("leiter_runter_loop", animator.CurrentAnimName);
        }

        [Test]
        public void RepeatedDownUpCycles_ResetRiderState_AndKeepIdenticalPace()
        {
            var (body, controller, animator) = CreateRig();
            List<float>? firstDown = null;
            List<float>? firstUp = null;

            for (int cycle = 0; cycle < 3; cycle++)
            {
                animator.PlaybackSpeed = 0.2f;
                List<float> down = RunRide(
                    CreateAction(controller, body.transform, Vector3.down * 4f),
                    body.transform);
                Assert.AreEqual(1f, animator.PlaybackSpeed, 1e-6f,
                    "the dedicated rider starts at the native cadence, independent of prior actions");

                animator.PlaybackSpeed = 2.5f;
                List<float> up = RunRide(
                    CreateAction(controller, body.transform, Vector3.zero),
                    body.transform);
                Assert.AreEqual(1f, animator.PlaybackSpeed, 1e-6f);

                if (firstDown == null)
                {
                    firstDown = down;
                    firstUp = up;
                }
                else
                {
                    AssertTrajectory(firstDown, down, $"down cycle {cycle}");
                    AssertTrajectory(firstUp!, up, $"up cycle {cycle}");
                }
            }
        }

        [Test]
        public void ContinuousRide_BridgesTaggedFloorCrossings_ForSameLadderOnly()
        {
            const int source = 81;
            var path = new List<NavWaypoint>
            {
                new(Vector3.zero, false),
                new(Vector3.down, true, transportKind: NavTransportKind.Ladder,
                    transportSourceHandle: source),
                new(Vector3.down * 2f, false, transportKind: NavTransportKind.Ladder,
                    transportSourceHandle: source),
                new(Vector3.down * 3f, true, transportKind: NavTransportKind.Ladder,
                    transportSourceHandle: source),
                new(Vector3.down * 4f, false, transportKind: NavTransportKind.Ladder,
                    transportSourceHandle: source),
                new(Vector3.down * 5f, false),
            };

            Assert.AreEqual(5, LadderRiderAction.FindContinuousRideEnd(path, 1),
                "one rider span includes same-source ladder floors between wall bands and its exit floor");
            Assert.IsFalse(path[2].IsWall,
                "Calc1/FloorPathMap classification stays FLOOR; only ladder provenance is retained");
        }

        [Test]
        public void FloorWaypoint_RetainsOnlyLadderTransportProvenance()
        {
            const int width = 4;
            const int height = 4;
            const int floorX = 10;
            const int floorY = 10;
            const int elevatorFloorX = 20;
            var subGrid = new byte[width * height * 64];
            int floorTile = (floorY >> 3) * width + (floorX >> 3);
            int floorSub = floorTile * 64 + ((floorY & 7) << 3) + (floorX & 7);
            subGrid[floorSub] = NavWorld.SubWalk | NavWorld.SubFloor;
            int elevatorTile = (floorY >> 3) * width + (elevatorFloorX >> 3);
            int elevatorSub = elevatorTile * 64
                + ((floorY & 7) << 3)
                + (elevatorFloorX & 7);
            subGrid[elevatorSub] = NavWorld.SubWalk | NavWorld.SubFloor;
            var nav = new NavWorld(
                width,
                height,
                new uint[width * height],
                subGrid,
                new BdbModel());
            nav.SetInfluence(
                81,
                floorX,
                floorY,
                floorX,
                floorY,
                -20,
                addsClimbSurface: true,
                transportKind: NavTransportKind.Ladder,
                climbX0: floorX,
                climbY0: floorY,
                climbX1: floorX,
                climbY1: floorY);
            nav.SetInfluence(
                82,
                elevatorFloorX,
                floorY,
                elevatorFloorX,
                floorY,
                -20,
                addsClimbSurface: false,
                transportKind: NavTransportKind.Elevator);

            NavWaypoint waypoint = NavWorldService.ClassifyWaypoint(
                nav,
                Vector3.zero,
                floorX,
                floorY);

            Assert.IsFalse(waypoint.IsWall,
                "the additive ladder overlay must not erase the terrain FLOOR bit");
            Assert.AreEqual(NavTransportKind.Ladder, waypoint.TransportKind);
            Assert.AreEqual(81, waypoint.TransportSourceHandle,
                "the source survives so BuildWalkSegments can keep one continuous rider");

            NavWaypoint elevatorFloor = NavWorldService.ClassifyWaypoint(
                nav,
                Vector3.right,
                elevatorFloorX,
                floorY);
            Assert.IsFalse(elevatorFloor.IsWall);
            Assert.AreEqual(NavTransportKind.None, elevatorFloor.TransportKind,
                "floor provenance is ladder-specific; the known elevator-shaft asymmetry is untouched");
            Assert.AreEqual(-1, elevatorFloor.TransportSourceHandle);
        }

        (GameObject body, DigglesAnimController controller, DigglesAnimator animator) CreateRig()
        {
            var body = new GameObject("ladder-rider-body");
            _gameObjects.Add(body);
            var model = new GameObject("Model");
            _gameObjects.Add(model);
            model.transform.SetParent(body.transform, false);
            var animator = model.AddComponent<DigglesAnimator>();
            var controller = model.AddComponent<DigglesAnimController>();
            controller.Animator = animator;

            animator.AddClip(CreateClip("standard", 2));
            animator.AddClip(CreateClip("leiter_hoch_loop", 7, LadderUpRootMotion));
            animator.AddClip(CreateClip("leiter_runter_loop", 3, 7.066395282745361f));
            animator.AddClip(CreateClip("kletter_hoch_loop", 19, WallUpRootMotion));
            animator.AddClip(CreateClip("kletter_runter_loop", 19, WallDownRootMotion));
            animator.AddClip(CreateClip("kletter_links_loop", 19, WallLeftRootMotion));
            animator.AddClip(CreateClip("kletter_rechts_loop", 19, WallRightRootMotion));

            controller.SetTables(
                new Dictionary<string, string> { ["standard"] = "standard" },
                new Dictionary<int, Dictionary<string, string>>
                {
                    [0] = new()
                    {
                        ["ladder_climb_up"] = "leiter_hoch_loop",
                        ["ladder_climb_down"] = "leiter_runter_loop",
                        ["climb_up"] = "kletter_hoch_loop",
                        ["climb_down"] = "kletter_runter_loop",
                        ["climb_left"] = "kletter_links_loop",
                        ["climb_right"] = "kletter_rechts_loop",
                    },
                });
            return (body, controller, animator);
        }

        DigglesAnimClip CreateClip(string name, int frameCount, float rootMotion = 0f)
        {
            var clip = new DigglesAnimClip
            {
                Name = name,
                Frames = new Mesh[frameCount],
                MotionOffset = Vector3.up * rootMotion,
            };
            for (int frame = 0; frame < frameCount; frame++)
            {
                clip.Frames[frame] = new Mesh();
                _meshes.Add(clip.Frames[frame]);
            }
            return clip;
        }

        static LadderRiderAction CreateAction(
            DigglesAnimController controller,
            Transform body,
            Vector3 target)
        {
            return new LadderRiderAction(
                controller,
                body,
                Vector3.zero,
                Vector3.down,
                target,
                Quaternion.identity,
                0);
        }

        static List<float> RunRide(LadderRiderAction action, Transform body)
        {
            var trajectory = new List<float>();
            for (int tick = 0; tick < 500; tick++)
            {
                bool running = action.Tick(LadderRiderMover.NativeTickSeconds);
                trajectory.Add(-body.position.y);
                if (!running) return trajectory;
            }
            Assert.Fail("ladder ride did not finish within 500 native ticks");
            return trajectory;
        }

        static void AssertTrajectory(
            IReadOnlyList<float> expected,
            IReadOnlyList<float> actual,
            string message)
        {
            Assert.AreEqual(expected.Count, actual.Count, $"{message}: tick count changed");
            for (int tick = 0; tick < expected.Count; tick++)
                Assert.AreEqual(expected[tick], actual[tick], 1e-5f,
                    $"{message}: displacement changed at tick {tick}");
        }
    }
}
