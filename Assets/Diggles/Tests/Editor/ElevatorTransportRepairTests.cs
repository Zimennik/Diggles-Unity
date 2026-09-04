#nullable enable

namespace Diggles.Tests
{
    using Diggles.Runtime;
    using Diggles.Runtime.Navigation;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class ElevatorTransportRepairTests
    {
        private const int Width = 32;
        private const int Height = 48;
        private const int ShaftCenterX = 124;
        private const int ShaftHeadY = 143;
        private const int ShaftEndY = 224;

        private GameObject? _terrainObject;

        [TearDown]
        public void TearDown()
        {
            if (_terrainObject != null) Object.DestroyImmediate(_terrainObject);
            _terrainObject = null;
        }

        [TestCase("Aufzug")]
        [TestCase("Dampfaufzug")]
        [TestCase("Kristallaufzug")]
        public void PlacementTable_AllElevatorVariants_ShaftGreenElsewhereRed(string className)
        {
            (TclInterpreter interpreter, DigglesTclContext context) = Host();
            DiggableTerrain terrain = AttachTerrain(context, MakeWorld(hasShaft: true));
            DefineElevator(interpreter, className);
            DigglesTclEntity drive = Spawn(interpreter, context, className);
            DigglesTclEntity cabin = Spawn(interpreter, context, className + "Cabin");
            drive.Owner = context.LocalPlayer;
            int registeredTrack = className == "Kristallaufzug" ? 0 : 1;
            interpreter.Eval(
                $"create_elevatorlogic {drive.Handle} {cabin.Handle} {registeredTrack}");
            SetVariantProperties(interpreter, drive.Handle, className);
            interpreter.Eval($"set_boxed {drive.Handle} 1");

            Assert.AreEqual(
                className == "Kristallaufzug" ? 1 : 4,
                drive.TransportLogic!.Elevator!.RiderCapacity);
            Assert.AreEqual(
                registeredTrack == 1,
                drive.TransportLogic!.Elevator!.UsesRegisteredTrack);

            PlacementController placement = context.Placement!;
            Assert.IsTrue(placement.OrderBuild(drive.Handle));
            placement.UpdateCursor(new Vector3(16f, 16.625f, 8f));

            Assert.IsTrue(placement.Valid, placement.LastReject);
            Assert.IsFalse(placement.AsBox);
            Assert.AreEqual(15.5f, placement.GhostPos.x, 1e-5f);
            Assert.AreEqual(17.5f, placement.GhostPos.y, 1e-5f);
            Assert.AreEqual(15f, placement.GhostPos.z, 1e-5f);

            terrain.Nav = MakeWorld(hasShaft: false);
            placement.UpdateCursor(new Vector3(16f, 16.625f, 15f));

            Assert.IsFalse(placement.Valid);
            Assert.IsFalse(placement.AsBox, "elevator mode 3 has no ordinary crate fallback");
            StringAssert.Contains("elevator vertical shaft", placement.LastReject ?? "");
        }

        [TestCase("Aufzug")]
        [TestCase("Dampfaufzug")]
        [TestCase("Kristallaufzug")]
        public void ProductTable_ElevatorKindProducesBoxedCrate_AndHidesCabin(string className)
        {
            (TclInterpreter interpreter, DigglesTclContext context) = Host();
            DefineElevator(interpreter, className);
            interpreter.Eval(@"
                set BOXED_CLASSES {production energy store elevator protection}
                proc produce_boxed {className} {
                    global BOXED_CLASSES
                    set product [new $className]
                    set_buildupstate $product 1
                    if {[lsearch $BOXED_CLASSES [get_class_type $className]] >= 0} {
                        set_boxed $product 1
                    }
                    return $product
                }");

            DigglesTclEntity product = context.ResolveEntity(
                interpreter.Eval($"produce_boxed {className}"));
            Assert.AreEqual("elevator", interpreter.Eval($"get_class_type {className}"));
            Assert.AreEqual("1", interpreter.Eval($"get_boxed {product.Handle}"));
            Assert.AreEqual(10, product.NativeObjectType);
            Assert.IsFalse(product.BuildupComplete);
            product.NativeObjectType = 0;
            interpreter.Eval($"set_boxed {product.Handle} 1");
            Assert.AreEqual(10, product.NativeObjectType,
                "boxing repairs the type-0 fresh-product metadata present in 2b11057");

            DigglesTclEntity cabin = Spawn(interpreter, context, className + "Cabin");
            int registeredTrack = className == "Kristallaufzug" ? 0 : 1;
            interpreter.Eval(
                $"create_elevatorlogic {product.Handle} {cabin.Handle} {registeredTrack}");
            Assert.AreEqual("0", interpreter.Eval($"get_visibility {cabin.Handle}"),
                "FUN_0060a2c0 hides the cabin while its drive is boxed");
            product.TransportLogic!.Elevator!.CurrentPresentation = "lift_still";
            interpreter.Eval($"set_boxed {product.Handle} 1");
            Assert.IsEmpty(product.TransportLogic!.Elevator!.CurrentPresentation,
                "boxing invalidates the lift clip cache before the crate is displayed");
            product.CurrentAnim = "kiste.standard";
            DigglesTclEngine.TickTransportLogic(context, 0.1);
            Assert.AreEqual("kiste.standard", product.CurrentAnim,
                "an inactive elevator tick must not replace the crate with lift_still");

            interpreter.Eval($"set_pos {product.Handle} 15.5 18 15");
            Assert.AreEqual(new Vector3(15.5f, 20f, 15f), cabin.Position,
                "moving a boxed drive re-seats its hidden cabin at drive.y + 2");

            interpreter.Eval($"set_boxed {product.Handle} 0");
            interpreter.Eval($"set_buildupstate {product.Handle} 1");
            Assert.AreEqual("1", interpreter.Eval($"get_visibility {cabin.Handle}"));
        }

        [Test]
        public void RideStateMachineTable_CallsBoardsMovesExits_AndInterruptsLawfully()
        {
            (TclInterpreter interpreter, DigglesTclContext context) = Host();
            AttachTerrain(context, MakeWorld(hasShaft: true));
            DefineElevator(interpreter, "Lift");
            interpreter.Eval(@"
                def_class Rider none gnome 0 {} {}
                set lift [new Lift]
                set_pos $lift 15.5 18 15
                set_buildupstate $lift 1
                set cabin [new LiftCabin]
                set rider [new Rider]
                set rider2 [new Rider]
                create_elevatorlogic $lift $cabin 1
                set_elevatorproperties $lift 1000 0 20 0.4 4");

            DigglesTclEntity drive = context.ResolveEntity(interpreter.Eval("set lift"));
            DigglesTclEntity cabin = context.ResolveEntity(interpreter.Eval("set cabin"));
            DigglesTclEntity rider = context.ResolveEntity(interpreter.Eval("set rider"));
            DigglesTclEntity rider2 = context.ResolveEntity(interpreter.Eval("set rider2"));
            ElevatorLogicState elevator = drive.TransportLogic!.Elevator!;
            float topStop = elevator.TrackTopY;
            float bottomStop = elevator.TrackBottomY;

            Assert.AreEqual(ElevatorMotionState.Idle, elevator.MotionState);
            Assert.AreEqual(18f, topStop, 1e-5f);
            Assert.AreEqual(18f + elevator.ProjectedSpan + 0.5f, bottomStop, 1e-5f);
            Assert.Greater(bottomStop, elevator.CurrentCabinY);
            Assert.AreEqual(4, elevator.RiderCapacity);

            Vector3 exit = new(13.5f, topStop, 11f);
            var ride = new ElevatorRideAction(
                context,
                rider,
                drive.Handle,
                bottomStop,
                topStop,
                exit);
            Assert.IsTrue(ride.Tick(0.1));
            ElevatorRiderRequest request = elevator.Riders[rider.Handle];
            Assert.AreEqual(ElevatorRidePhase.Calling, request.Phase);
            float initialCabinY = elevator.CurrentCabinY;
            DigglesTclEngine.TickTransportLogic(context, 0.1);
            Assert.AreEqual(ElevatorMotionState.Moving, elevator.MotionState);
            Assert.Greater(elevator.CurrentCabinY, initialCabinY);

            TickRideUntil(
                context,
                ride,
                () => Mathf.Abs(elevator.CurrentCabinY - bottomStop) <= 1e-5f,
                32);
            Assert.AreEqual(ElevatorMotionState.StoppedForTransfer, elevator.MotionState);
            Assert.AreEqual(ElevatorRidePhase.Calling, request.Phase);

            Assert.IsTrue(ride.Tick(0.1));
            Assert.AreEqual(ElevatorRidePhase.Boarding, request.Phase);
            DigglesTclEngine.TickTransportLogic(context, 0.1);
            Assert.AreEqual(ElevatorMotionState.StoppedForTransfer, elevator.MotionState);

            Assert.IsTrue(ride.Tick(0.1));
            Assert.AreEqual(ElevatorRidePhase.Riding, request.Phase);
            DigglesTclEngine.TickTransportLogic(context, 0.1);
            Vector3 riderOffset = rider.Position - cabin.Position;
            Assert.AreEqual(request.CabinOffset, riderOffset);
            Assert.AreEqual(cabin.Position.z + request.CabinOffset.z, rider.Position.z, 1e-5f);

            bool sawRiderMove = false;
            TickRideUntil(
                context,
                ride,
                () => Mathf.Abs(elevator.CurrentCabinY - topStop) <= 1e-5f
                    && Mathf.Abs(elevator.Velocity) <= 1e-5f,
                64,
                () =>
                {
                    if (request.Phase != ElevatorRidePhase.Riding) return;
                    sawRiderMove |= elevator.MotionState == ElevatorMotionState.Moving;
                    Assert.AreEqual(request.CabinOffset, rider.Position - cabin.Position);
                });
            Assert.IsTrue(sawRiderMove);
            Assert.AreEqual(topStop, cabin.Position.y, 1e-5f);

            Assert.IsTrue(ride.Tick(0.1));
            Assert.AreEqual(ElevatorRidePhase.Exiting, request.Phase);
            Assert.IsFalse(elevator.Riders[rider.Handle].Phase == ElevatorRidePhase.Riding);
            Assert.IsFalse(ride.Tick(0.1));
            Assert.AreEqual(0, ride.ExitResult);
            Assert.AreEqual(exit, rider.Position);
            Assert.IsEmpty(elevator.Riders);
            Assert.AreEqual(ElevatorMotionState.Idle, elevator.MotionState);

            var interruptedRide = new ElevatorRideAction(
                context,
                rider2,
                drive.Handle,
                topStop,
                bottomStop,
                new Vector3(17.5f, bottomStop, 11f));
            Assert.IsTrue(interruptedRide.Tick(0.1));
            Assert.AreEqual(
                ElevatorRidePhase.Boarding,
                elevator.Riders[rider2.Handle].Phase,
                "the second request calls from the top stop");
            Assert.IsTrue(interruptedRide.Tick(0.1));
            DigglesTclEngine.TickTransportLogic(context, 0.1);
            ElevatorRiderRequest interruptedRequest = elevator.Riders[rider2.Handle];
            Assert.AreEqual(ElevatorRidePhase.Riding, interruptedRequest.Phase);
            bool sawReturnMotion = false;
            for (int step = 0; step < 16 && !sawReturnMotion; step++)
            {
                Assert.IsTrue(interruptedRide.Tick(0.1));
                DigglesTclEngine.TickTransportLogic(context, 0.1);
                Assert.AreEqual(
                    interruptedRequest.CabinOffset,
                    rider2.Position - cabin.Position);
                sawReturnMotion = elevator.MotionState == ElevatorMotionState.Moving;
            }
            Assert.IsTrue(sawReturnMotion, "the top call starts the return trip");
            Assert.Greater(elevator.CurrentCabinY, topStop);
            Vector3 lawfulCarPosition = rider2.Position;

            rider2.ActiveAction = interruptedRide;
            interpreter.Eval($"action {rider2.Handle} wait 1");

            Assert.IsFalse(elevator.Riders.ContainsKey(rider2.Handle));
            Assert.AreEqual(ElevatorRidePhase.Interrupted, interruptedRequest.Phase);
            Assert.AreEqual(ElevatorMotionState.Idle, elevator.MotionState);
            Assert.AreEqual(0f, elevator.Velocity, 1e-5f);
            DigglesTclEngine.TickTransportLogic(context, 0.1);
            Assert.AreEqual(lawfulCarPosition, rider2.Position,
                "an interrupted rider stays at its last linked cabin position");
        }

        [Test]
        public void BoardingTable_OppositeCallWaitsForCurrentPassengerSweep()
        {
            (TclInterpreter interpreter, DigglesTclContext context) = Host();
            AttachTerrain(context, MakeWorld(hasShaft: true));
            DefineElevator(interpreter, "Lift");
            interpreter.Eval(@"
                def_class Rider none gnome 0 {} {}
                set lift [new Lift]
                set_pos $lift 15.5 18 15
                set_buildupstate $lift 1
                set cabin [new LiftCabin]
                set downRider [new Rider]
                set upCaller [new Rider]
                create_elevatorlogic $lift $cabin 1
                set_elevatorproperties $lift 1000 0 20 0.4 4");

            DigglesTclEntity drive = context.ResolveEntity(interpreter.Eval("set lift"));
            DigglesTclEntity downRider = context.ResolveEntity(interpreter.Eval("set downRider"));
            DigglesTclEntity upCaller = context.ResolveEntity(interpreter.Eval("set upCaller"));
            ElevatorLogicState elevator = drive.TransportLogic!.Elevator!;
            float middle = (elevator.TrackTopY + elevator.TrackBottomY) * 0.5f;
            elevator.CurrentCabinY = middle;
            elevator.TargetY = middle;
            elevator.Velocity = 0f;
            elevator.Riders[downRider.Handle] = new ElevatorRiderRequest
            {
                RiderHandle = downRider.Handle,
                PickupY = middle,
                DestinationY = elevator.TrackBottomY,
                Phase = ElevatorRidePhase.Riding,
                CabinSlot = 0,
            };
            var oppositeCall = new ElevatorRideAction(
                context,
                upCaller,
                drive.Handle,
                middle,
                elevator.TrackTopY,
                new Vector3(15.5f, elevator.TrackTopY, 11f));

            Assert.IsTrue(oppositeCall.Tick(0.1));
            Assert.AreEqual(
                ElevatorRidePhase.Calling,
                elevator.Riders[upCaller.Handle].Phase,
                "FUN_00608140 keeps an opposite-direction pickup in the dynamic call set");
        }

        private static void TickRideUntil(
            DigglesTclContext context,
            ElevatorRideAction ride,
            System.Func<bool> condition,
            int limit,
            System.Action? afterTick = null)
        {
            for (int step = 0; step < limit && !condition(); step++)
            {
                Assert.IsTrue(ride.Tick(0.1), $"ride ended at step {step}");
                DigglesTclEngine.TickTransportLogic(context, 0.1);
                afterTick?.Invoke();
            }
            Assert.IsTrue(condition(), $"ride condition was not reached in {limit} ticks");
        }

        private DiggableTerrain AttachTerrain(DigglesTclContext context, NavWorld nav)
        {
            var terrainObject = new GameObject("ElevatorTransportRepairTerrain");
            _terrainObject = terrainObject;
            DiggableTerrain terrain = terrainObject.AddComponent<DiggableTerrain>();
            terrain.Nav = nav;
            context.Terrain = terrain;
            return terrain;
        }

        private static (TclInterpreter interpreter, DigglesTclContext context) Host()
        {
            var interpreter = new TclInterpreter
            {
                TolerateExprErrors = true,
                TolerateMissingVars = true,
            };
            interpreter.UnknownCommandHandler = (_, _) => "0";
            DigglesTclContext context = DigglesTclEngine.Register(interpreter);
            context.TolerateMissingClass = true;
            context.TolerateMissingEntities = true;
            context.TolerateMissingFiles = true;
            context.LocalPlayer = 0;
            return (interpreter, context);
        }

        private static void DefineElevator(TclInterpreter interpreter, string className)
        {
            interpreter.Eval($@"
                def_class {className} metal elevator 2 {{}} {{}}
                def_class {className}Cabin metal dummy 2 {{}} {{}}");
        }

        private static DigglesTclEntity Spawn(
            TclInterpreter interpreter,
            DigglesTclContext context,
            string className)
        {
            string handle = interpreter.Eval($"new {className}");
            return context.ResolveEntity(handle);
        }

        private static void SetVariantProperties(
            TclInterpreter interpreter,
            int driveHandle,
            string className)
        {
            string properties = className switch
            {
                "Dampfaufzug" => "1000 1 0.2 0.4",
                "Kristallaufzug" => "1000 0 100 100 1",
                _ => "1000 0 2",
            };
            interpreter.Eval($"set_elevatorproperties {driveHandle} {properties}");
        }

        private static NavWorld MakeWorld(bool hasShaft)
        {
            int tileCount = Width * Height;
            var blocks = new uint[tileCount];
            var aux = new byte[tileCount * BdbModel.AuxStride];
            for (int tileY = 0; tileY < Height; tileY++)
            for (int tileX = 0; tileX < Width; tileX++)
            {
                int tile = tileY * Width + tileX;
                blocks[tile] = (uint)tile;
                for (int localY = 0; localY < 8; localY++)
                for (int localX = 0; localX < 8; localX++)
                {
                    int subX = tileX * 8 + localX;
                    int subY = tileY * 8 + localY;
                    bool shaftCell = hasShaft
                        && Mathf.Abs(subX - ShaftCenterX) <= 4
                        && subY >= ShaftHeadY
                        && subY < ShaftEndY;
                    aux[tile * BdbModel.AuxStride + localY * 8 + localX] = shaftCell
                        ? (byte)11
                        : (byte)15;
                }
            }

            var bdb = new BdbModel
            {
                Header = new BdbHeader { NBlocks = (uint)tileCount },
                PerBlockAux = aux,
            };
            return new NavWorld(
                Width,
                Height,
                blocks,
                new byte[tileCount * 64],
                bdb);
        }
    }
}
