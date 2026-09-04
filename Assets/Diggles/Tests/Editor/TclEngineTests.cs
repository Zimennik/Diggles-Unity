#nullable enable

namespace Diggles.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using Diggles.Runtime;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;

    // Engine-integration tests: class def, instantiate, mutate, read back.
    public sealed class TclEngineTests
    {
        sealed class MovingProbeAction : IEntityAction
        {
            public bool Tick(double dt) => true;
        }

        static (TclInterpreter, DigglesTclContext) MakeEngine()
        {
            var i = new TclInterpreter();
            var ctx = DigglesTclEngine.Register(i);
            return (i, ctx);
        }

        [Test]
        public void LightAndGroundBegin_UseNativeCouplingLaw()
        {
            var (interpreter, context) = MakeEngine();

            Assert.That(context.LightBeginRow, Is.EqualTo(-10000));
            Assert.That(context.GroundBeginRow, Is.EqualTo(-10000));

            interpreter.Eval("set_light_begin 33");

            Assert.That(context.LightBeginRow, Is.EqualTo(33));
            Assert.That(context.GroundBeginRow, Is.EqualTo(33));

            interpreter.Eval("set_ground_begin 7");

            Assert.That(context.LightBeginRow, Is.EqualTo(33));
            Assert.That(context.GroundBeginRow, Is.EqualTo(7));
        }

        [Test]
        public void DefClass_Registers_With_Defaults()
        {
            var (i, ctx) = MakeEngine();
            i.Eval(@"
                def_class Pilz food material 0 {} {
                    class_defaultanim pilz.standard
                    class_physic none
                }");
            Assert.IsTrue(ctx.Classes.ContainsKey("Pilz"));
            var c = ctx.Classes["Pilz"];
            Assert.AreEqual("food", c.Material);
            Assert.AreEqual("material", c.Kind);
            Assert.AreEqual("pilz.standard", c.DefaultAnim);
            Assert.AreEqual("none", c.Physic);
        }

        [Test]
        public void New_Returns_Handle_And_Stores_Position()
        {
            var (i, ctx) = MakeEngine();
            i.Eval(@"def_class Pilz food material 0 {} {}");
            string h = i.Eval(@"new Pilz """" ""10 20 5"" ""0 0 0""");
            Assert.AreEqual("1", h);
            Assert.AreEqual(1, ctx.Entities.Count);
            var ent = ctx.Entities[1];
            Assert.AreEqual(10f, ent.Position.x);
            Assert.AreEqual(20f, ent.Position.y);
            Assert.AreEqual(5f,  ent.Position.z);
        }

        [Test]
        public void SetPos_GetPos_RoundTrip()
        {
            var (i, _) = MakeEngine();
            i.Eval(@"
                def_class Pilz food material 0 {} {}
                set h [new Pilz]
                set_pos $h 100 200 50");
            Assert.AreEqual("100 200 50", i.Eval("get_pos $h"));
        }

        [Test]
        public void SetVel_GetVel_RoundTrip()
        {
            var (i, _) = MakeEngine();
            i.Eval(@"
                def_class Pilz food material 0 {} {}
                set h [new Pilz]
                set_vel $h ""1 2 3""");
            Assert.AreEqual("1 2 3", i.Eval("get_vel $h"));
        }

        [Test]
        public void Del_Removes_Entity()
        {
            var (i, ctx) = MakeEngine();
            i.Eval(@"
                def_class Pilz food material 0 {} {}
                set h [new Pilz]");
            Assert.AreEqual(1, ctx.Entities.Count);
            i.Eval("del $h");
            Assert.AreEqual(0, ctx.Entities.Count);
        }

        [Test]
        public void Del_Runs_ObjExit_DerivedBeforeBase_AndStillRemovesEntity()
        {
            var (i, ctx) = MakeEngine();
            i.Eval(@"
                def_class Base none dummy 0 {} {
                    obj_exit { global exits; lappend exits base }
                }
                def_class Derived none dummy 0 {} {
                    inherit Base
                    obj_exit { global exits; lappend exits derived }
                }
                set h [new Derived]");

            var entity = ctx.GetEntity(i.Eval("set h"));
            Assert.NotNull(entity);

            i.Eval("del $h");

            Assert.AreEqual("derived base", entity!.VarsScope.Vars["exits"]);
            Assert.AreEqual(0, ctx.Entities.Count);
        }

        [Test]
        public void Destruct_HidesButDoesNotDeleteEntity()
        {
            var (i, ctx) = MakeEngine();
            i.Eval(@"
                def_class Prop none dummy 0 {} {}
                set h [new Prop]
                set_visibility $h 1");

            Assert.AreEqual("0", i.Eval("destruct $h"));
            Assert.AreEqual(1, ctx.Entities.Count,
                "native destruct shatters the mesh; del owns logical deletion");
            Assert.AreEqual("0", i.Eval("get_visibility $h"));
        }

        [Test]
        public void ClassObjSnapClass_StoresTargetAndRange()
        {
            var (i, ctx) = MakeEngine();
            i.Eval(@"
                def_class ReflectorSlot none dummy 0 {} {
                    class_objsnapclass Reflektor 1.5
                }");

            var cls = ctx.Classes["ReflectorSlot"];
            Assert.AreEqual("Reflektor", cls.ObjectSnapClass);
            Assert.AreEqual(1.5f, cls.ObjectSnapRange, 1e-6f);
        }

        [Test]
        public void TransportCommands_StoreDoorLadderElevatorAndObjectFlags()
        {
            var (i, ctx) = MakeEngine();
            i.Eval(@"
                def_class Device none dummy 0 {} {}
                set door [new Device]
                set_buildupstate $door 1
                create_doorlogic $door
                set_doorproperties $door openforclasses {Troll}
                set ladder [new Device]
                create_ladderlogic $ladder
                set_ladderrange $ladder 32
                set_snaptowall $ladder 1
                set cabin [new Device]
                set_forceipol $cabin 1
                set lift [new Device]
                create_elevatorlogic $lift $cabin 1
                set_elevatorproperties $lift 1000 1 0.2 0.4 7");

            var door = ctx.ResolveEntity(i.Eval("set door"));
            Assert.AreEqual(DoorAccessMode.OpenForClasses, door.TransportLogic!.Door!.Mode);
            CollectionAssert.AreEquivalent(new[] { "Troll" }, door.TransportLogic.Door.AllowedClasses);
            Assert.AreEqual(int.MaxValue, door.PathInfluence!.Cost);

            var ladder = ctx.ResolveEntity(i.Eval("set ladder"));
            Assert.AreEqual(32f, ladder.TransportLogic!.Ladder!.Range, 1e-6f);
            Assert.IsTrue(ladder.SnapToWall);
            Assert.AreEqual("1", i.Eval("get_snaptowall $ladder"));

            var cabin = ctx.ResolveEntity(i.Eval("set cabin"));
            Assert.IsTrue(cabin.ForcePositionInterpolation);
            Assert.AreEqual("1", i.Eval("get_forceipol $cabin"));

            var lift = ctx.ResolveEntity(i.Eval("set lift"));
            var elevator = lift.TransportLogic!.Elevator!;
            Assert.AreEqual(cabin.Handle, elevator.CabinHandle);
            Assert.IsTrue(elevator.UsesRegisteredTrack);
            Assert.AreEqual(1000f, elevator.MaxRange, 1e-6f);
            Assert.AreEqual(1, elevator.AccelerationMode);
            Assert.AreEqual(0.2f, elevator.Speed, 1e-6f);
            Assert.AreEqual(0.4f, elevator.BrakeAcceleration, 1e-6f);
            Assert.AreEqual(7, elevator.Flags);

            i.Eval("delete_transportlogic $lift");
            Assert.IsNull(lift.TransportLogic);
            Assert.IsFalse(ctx.Entities.ContainsKey(cabin.Handle));
        }

        [Test]
        public void DoorAccessModes_FollowOwnerDiplomacyAndClassRules()
        {
            var (i, ctx) = MakeEngine();
            i.Eval(@"
                def_class Gate none dummy 0 {} {}
                def_class Troll none monster 0 {} {}
                def_class TrollChild none monster 0 {} { inherit Troll }
                def_class Gnome none gnome 0 {} {}
                set gate [new Gate]
                set_buildupstate $gate 1
                set troll [new TrollChild]
                set gnome [new Gnome]
                create_doorlogic $gate");

            var gate = ctx.ResolveEntity(i.Eval("set gate"));
            var troll = ctx.ResolveEntity(i.Eval("set troll"));
            var gnome = ctx.ResolveEntity(i.Eval("set gnome"));
            gate.Owner = 2;
            troll.Owner = 3;
            gnome.Owner = 2;

            i.Eval("set_doorproperties $gate open");
            Assert.AreEqual(DoorAccessMode.Open, gate.TransportLogic!.Door!.Mode);
            Assert.IsTrue(DigglesTclEngine.DoorAllows(ctx, gate, gnome));
            Assert.IsTrue(DigglesTclEngine.DoorAllows(ctx, gate, troll));
            Assert.AreEqual(0, gate.PathInfluence!.Cost);

            i.Eval("set_doorproperties $gate closed");
            Assert.AreEqual(DoorAccessMode.Closed, gate.TransportLogic.Door.Mode);
            Assert.IsFalse(DigglesTclEngine.DoorAllows(ctx, gate, gnome));
            Assert.IsFalse(DigglesTclEngine.DoorAllows(ctx, gate, troll));
            Assert.AreEqual(int.MaxValue, gate.PathInfluence.Cost);

            i.Eval("set_doorproperties $gate openforplayer");
            Assert.AreEqual(DoorAccessMode.OpenForPlayer, gate.TransportLogic.Door.Mode);
            Assert.IsTrue(DigglesTclEngine.DoorAllows(ctx, gate, gnome));
            Assert.IsFalse(DigglesTclEngine.DoorAllows(ctx, gate, troll));
            Assert.AreEqual(int.MaxValue, gate.PathInfluence.Cost);

            i.Eval("set_doorproperties $gate openforfriends");
            Assert.AreEqual(DoorAccessMode.OpenForFriends, gate.TransportLogic.Door.Mode);
            Assert.IsTrue(DigglesTclEngine.DoorAllows(ctx, gate, gnome));
            // FUN_0060d220 case 3 (:41-56): BOTH lookups read the DOOR OWNER'S row
            // (FUN_004c4130 row = door owner, FUN_0045f830 col = mover) — first expects
            // 2 (friend,:47-49), the fallback expects 1 (ally,:50-53). The mover's own
            // row is never consulted.
            ctx.Diplomacy[2, 3] = 1;   // door owner calls the troll ALLY → pass
            ctx.Diplomacy[3, 2] = 4;   // …even while the troll's own row says enemy
            Assert.IsTrue(DigglesTclEngine.DoorAllows(ctx, gate, troll));
            ctx.Diplomacy[2, 3] = 2;   // FRIEND → pass (the first lookup)
            Assert.IsTrue(DigglesTclEngine.DoorAllows(ctx, gate, troll));
            ctx.Diplomacy[2, 3] = 3;   // neutral → refuse…
            ctx.Diplomacy[3, 2] = 1;   // …even though the troll considers the owner an ally
            Assert.IsFalse(DigglesTclEngine.DoorAllows(ctx, gate, troll));
            ctx.Diplomacy[2, 3] = 4;   // enemy → refuse
            Assert.IsFalse(DigglesTclEngine.DoorAllows(ctx, gate, troll));
            Assert.AreEqual(int.MaxValue, gate.PathInfluence.Cost);

            i.Eval("set_doorproperties $gate openforclasses {Troll}");
            Assert.AreEqual(DoorAccessMode.OpenForClasses, gate.TransportLogic.Door.Mode);
            Assert.IsTrue(DigglesTclEngine.DoorAllows(ctx, gate, troll));
            Assert.IsFalse(DigglesTclEngine.DoorAllows(ctx, gate, gnome));
            Assert.AreEqual(int.MaxValue, gate.PathInfluence.Cost);

            i.Eval("set_doorproperties $gate openforall");
            Assert.AreEqual(DoorAccessMode.OpenForAll, gate.TransportLogic.Door.Mode);
            Assert.IsTrue(DigglesTclEngine.DoorAllows(ctx, gate, gnome));
            Assert.IsTrue(DigglesTclEngine.DoorAllows(ctx, gate, troll));
            Assert.AreEqual(0, gate.PathInfluence.Cost);
            Assert.AreEqual("opening_r", gate.CurrentAnim);
        }

        [Test]
        public void ConditionalDoor_OpensForNearbyAuthorizedMover_ThenCloses()
        {
            const int width = 24;
            const int height = 24;
            var sub = new byte[width * height * 64];
            for (int tile = 0; tile < width * height; tile++)
                sub[tile * 64 + 36] = Diggles.Runtime.Navigation.NavWorld.SubWalk
                    | Diggles.Runtime.Navigation.NavWorld.SubFloor;
            var nav = new Diggles.Runtime.Navigation.NavWorld(
                width,
                height,
                new uint[width * height],
                sub,
                new Diggles.Runtime.World.BdbModel());
            var terrainObject = new UnityEngine.GameObject("door-terrain");
            try
            {
                var terrain = terrainObject.AddComponent<Diggles.Runtime.World.DiggableTerrain>();
                terrain.Nav = nav;
                var (i, ctx) = MakeEngine();
                ctx.Terrain = terrain;
                i.Eval(@"
                    def_class Gate none dummy 0 {} {
                        set_class_anim opening_r gate.opening_right
                        set_class_anim closing_r gate.closing_right
                    }
                    def_class Gnome none gnome 0 {} {}
                    set gate [new Gate]
                    set_buildupstate $gate 1
                    set_pos $gate 14 10 0
                    create_doorlogic $gate
                    set_doorproperties $gate openforplayer
                    set mover [new Gnome]
                    set_pos $mover 2 10 0
                    set intruder [new Gnome]
                    set_pos $intruder 15 10 0");

                var gate = ctx.ResolveEntity(i.Eval("set gate"));
                var mover = ctx.ResolveEntity(i.Eval("set mover"));
                var intruder = ctx.ResolveEntity(i.Eval("set intruder"));
                gate.Owner = mover.Owner = 2;
                intruder.Owner = 3;
                intruder.ActiveAction = new MovingProbeAction();

                Assert.AreEqual(-1, nav.EffectiveTileBaseCost(14, 10));
                int closedVersion = nav.Version;
                DigglesTclEngine.TickTransportLogic(ctx);
                Assert.IsFalse(gate.TransportLogic!.Door!.IsTemporarilyOpen,
                    "an unauthorized walker INSIDE the aperture band must not swing the door");
                Assert.AreEqual(-1, nav.EffectiveTileBaseCost(14, 10),
                    "a nearby unauthorized walker must not lift the hard block");

                // The auto-open envelope is the door's own influence rect [-8,-20,+8,+4]
                // sub-cells (FUN_0060ceb0:78-81) — the stand-in for the native CAct_Door
                // membership registration (FUN_0060d220:80-103 / FUN_0060d470:43-44). There
                // is NO 13.5 sphere: that constant is the metawalk destination DEPTH
                // (CAct_MetaWalk+0x24 → FUN_0041a2f0:41-42), not a radius. An authorized
                // mover 12 tiles out must therefore keep the door shut.
                mover.ActiveAction = new MovingProbeAction();
                DigglesTclEngine.TickTransportLogic(ctx);
                Assert.IsFalse(gate.TransportLogic.Door.IsTemporarilyOpen,
                    "a distant authorized walker (12 tiles) must not swing the door");
                Assert.AreEqual(-1, nav.EffectiveTileBaseCost(14, 10));

                mover.Position = new UnityEngine.Vector3(13.5f, 10f, 0f);   // dx = -4 sub, in-rect
                DigglesTclEngine.TickTransportLogic(ctx);
                int openVersion = nav.Version;
                Assert.Greater(openVersion, closedVersion);
                Assert.IsTrue(gate.TransportLogic.Door.IsTemporarilyOpen);
                Assert.AreEqual(0, gate.PathInfluence!.Cost);
                Assert.AreEqual(20, nav.EffectiveTileBaseCost(14, 10));
                Assert.AreEqual("gate.opening_right", gate.CurrentAnim);

                mover.Position = new UnityEngine.Vector3(30, 30, 0);
                DigglesTclEngine.TickTransportLogic(ctx);
                Assert.Greater(nav.Version, openVersion);
                Assert.IsFalse(gate.TransportLogic.Door.IsTemporarilyOpen);
                Assert.AreEqual(int.MaxValue, gate.PathInfluence.Cost);
                Assert.AreEqual(-1, nav.EffectiveTileBaseCost(14, 10));
                Assert.AreEqual("gate.closing_right", gate.CurrentAnim);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(terrainObject);
            }
        }

        [Test]
        public void DoorStateFlip_ChangesFogReachThroughWalkerPassage_NotDoorLineOfSight()
        {
            const int width = 16;
            const int height = 16;
            var sub = new byte[width * height * 64];
            for (int tileX = 0; tileX < width; tileX++)
                sub[(6 * width + tileX) * 64 + 36] = Diggles.Runtime.Navigation.NavWorld.SubWalk
                    | Diggles.Runtime.Navigation.NavWorld.SubFloor;
            var nav = new Diggles.Runtime.Navigation.NavWorld(
                width,
                height,
                new uint[width * height],
                sub,
                new Diggles.Runtime.World.BdbModel());
            var terrainObject = new UnityEngine.GameObject("door-fog-terrain");
            try
            {
                var terrain = terrainObject.AddComponent<Diggles.Runtime.World.DiggableTerrain>();
                terrain.Nav = nav;
                var (i, ctx) = MakeEngine();
                ctx.Terrain = terrain;
                var fog = new Diggles.Runtime.World.FogOfWarState(
                    width,
                    height,
                    (_, _) => 0);
                ctx.FogOfWar = fog;
                i.Eval(@"
                    def_class Gate none dummy 0 {} {}
                    set gate [new Gate]
                    set_pos $gate 8.5 6.5 0
                    set_buildupstate $gate 1
                    create_doorlogic $gate
                    set_doorproperties $gate closed");

                var pathfinder = new Diggles.Runtime.Navigation.Calc1Pathfinder(nav);
                var closedPath = pathfinder.FindPath(52, 52, 84, 52, canClimb: true);
                Assert.IsNull(closedPath);

                float lastX = 0f;
                float lastY = 0f;
                fog.BeginUpdate(0);
                fog.UpdateRevealer(
                    6.5f,
                    6.5f,
                    ref lastX,
                    ref lastY,
                    16,
                    16,
                    owner: 0,
                    slotId: 0);
                fog.EndUpdate();
                Assert.AreEqual(0, fog.FogAt(10, 5, 0),
                    "the closed hard block keeps the walker revealer on the near side");

                i.Eval("set_doorproperties $gate open");
                var openPath = pathfinder.FindPath(52, 52, 84, 52, canClimb: true);
                Assert.NotNull(openPath);

                fog.BeginUpdate(0);
                fog.UpdateRevealer(
                    10.5f,
                    6.5f,
                    ref lastX,
                    ref lastY,
                    16,
                    16,
                    owner: 0,
                    slotId: 0);
                fog.EndUpdate();
                Assert.AreEqual(0xFF, fog.FogAt(10, 5, 0),
                    "opening lets the radial walker revealer reach and uncover the far side");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(terrainObject);
            }
        }

        [Test]
        public void LadderLogic_ProjectsNativeClimbBand_AndSettlesDepth()
        {
            const int width = 20;
            const int height = 20;
            var aux = new byte[Diggles.Runtime.World.BdbModel.AuxStride];
            for (int index = 0; index < 64; index++) aux[index] = 11;
            var bdb = new Diggles.Runtime.World.BdbModel
            {
                Header = new Diggles.Runtime.World.BdbHeader { NBlocks = 1 },
                PerBlockAux = aux,
            };
            var sub = new byte[width * height * 64];
            SetSubCell(sub, width, 80, 88,
                Diggles.Runtime.Navigation.NavWorld.SubWalk
                | Diggles.Runtime.Navigation.NavWorld.SubFloor);
            SetSubCell(sub, width, 80, 112,
                Diggles.Runtime.Navigation.NavWorld.SubWalk
                | Diggles.Runtime.Navigation.NavWorld.SubFloor);
            SetSubCell(sub, width, 72, 100,
                Diggles.Runtime.Navigation.NavWorld.SubWalk
                | Diggles.Runtime.Navigation.NavWorld.SubFloor);
            var nav = new Diggles.Runtime.Navigation.NavWorld(
                width,
                height,
                new uint[width * height],
                sub,
                bdb);
            var terrainObject = new UnityEngine.GameObject("ladder-terrain");
            try
            {
                var terrain = terrainObject.AddComponent<Diggles.Runtime.World.DiggableTerrain>();
                terrain.Nav = nav;
                var (i, ctx) = MakeEngine();
                ctx.Terrain = terrain;
                i.Eval(@"
                    def_class Ladder none elevator 0 {} {}
                    set ladder [new Ladder]
                    set_pos $ladder 10 15 3
                    set_buildupstate $ladder 1
                    set_snaptowall $ladder 1
                    create_ladderlogic $ladder
                    set_ladderrange $ladder 4");

                var ladder = ctx.ResolveEntity(i.Eval("set ladder"));
                Assert.AreEqual(4f, ladder.TransportLogic!.Ladder!.ProjectedSpan, 1e-6f);
                Assert.AreEqual(-12, ladder.PathInfluence!.X0);
                Assert.AreEqual(-36, ladder.PathInfluence.Y0);
                Assert.AreEqual(12, ladder.PathInfluence.X1);
                Assert.AreEqual(6, ladder.PathInfluence.Y1);
                Assert.AreEqual(-20, ladder.PathInfluence.Cost);
                Assert.IsTrue(ladder.PathInfluence.AddsClimbSurface);
                Assert.IsTrue(ladder.PathInfluence.HasClimbBounds);
                Assert.AreEqual(0, ladder.PathInfluence.ClimbX0);
                Assert.AreEqual(0, ladder.PathInfluence.ClimbX1);
                Assert.AreEqual(11.01f, ladder.Position.z, 1e-5f);
                Assert.IsTrue(nav.HasInfluence(ladder.Handle));
                Assert.IsTrue((nav.SubAt(80, 100) & Diggles.Runtime.Navigation.NavWorld.SubClimb) != 0);
                Assert.IsFalse((nav.SubAt(72, 100) & Diggles.Runtime.Navigation.NavWorld.SubClimb) != 0,
                    "the native ±12 attractor band must not widen the ladder's traversal column");
                Assert.AreEqual(1, nav.EffectiveTileBaseCost(9, 12),
                    "the side tile remains inside the native 20 + (-20) attractor band");
                Assert.IsTrue(nav.TryGetTransportInfluence(
                    80,
                    100,
                    out var transportKind,
                    out int transportSource));
                Assert.AreEqual(Diggles.Runtime.Navigation.NavTransportKind.Ladder, transportKind);
                Assert.AreEqual(ladder.Handle, transportSource);
                Assert.IsFalse(nav.TryGetTransportInfluence(72, 100, out _, out _));

                i.Eval("del $ladder");

                Assert.IsFalse(nav.HasInfluence(ladder.Handle));
                Assert.IsFalse((nav.SubAt(80, 100) & Diggles.Runtime.Navigation.NavWorld.SubClimb) != 0);
                Assert.AreEqual(20, nav.EffectiveTileBaseCost(9, 12));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(terrainObject);
            }
        }

        [Test]
        public void LadderLogic_BoxedProductKeepsItsPutdownDepth()
        {
            const int width = 20;
            const int height = 20;
            var aux = new byte[Diggles.Runtime.World.BdbModel.AuxStride];
            for (int index = 0; index < 64; index++) aux[index] = 11;
            var bdb = new Diggles.Runtime.World.BdbModel
            {
                Header = new Diggles.Runtime.World.BdbHeader { NBlocks = 1 },
                PerBlockAux = aux,
            };
            var nav = new Diggles.Runtime.Navigation.NavWorld(
                width,
                height,
                new uint[width * height],
                new byte[width * height * 64],
                bdb);
            var terrainObject = new UnityEngine.GameObject("boxed-ladder-terrain");
            try
            {
                var terrain = terrainObject.AddComponent<Diggles.Runtime.World.DiggableTerrain>();
                terrain.Nav = nav;
                var (i, ctx) = MakeEngine();
                ctx.Terrain = terrain;
                i.Eval(@"
                    def_class BoxedLadder none elevator 0 {} {}
                    set ladder [new BoxedLadder]
                    set_pos $ladder 10 15 3
                    set_boxed $ladder 1
                    create_ladderlogic $ladder
                    set_ladderrange $ladder 4
                    set_snaptowall $ladder 1");

                var ladder = ctx.ResolveEntity(i.Eval("set ladder"));
                DigglesTclEngine.TickTransportLogic(ctx);

                Assert.AreEqual(3f, ladder.Position.z, 1e-6f,
                    "a boxed ladder must stay where the carrier put it down");
                Assert.AreEqual(0f, ladder.TransportLogic!.Ladder!.ProjectedSpan, 1e-6f);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(terrainObject);
            }
        }

        [Test]
        public void ActionPlaybackModifier_IsRestoredOnReplacementAndCompletion()
        {
            var gameObject = new UnityEngine.GameObject("ladder-speed-lifecycle");
            try
            {
                var animator = gameObject.AddComponent<DigglesAnimator>();
                var controller = gameObject.AddComponent<DigglesAnimController>();
                controller.Animator = animator;
                var (interpreter, context) = MakeEngine();
                interpreter.Eval("def_class Gnome none gnome 0 {} {}; set gnome [new Gnome]");
                var gnome = context.ResolveEntity(interpreter.Eval("set gnome"));
                gnome.GameObject = gameObject;
                gnome.ActiveAction = new WaitAction(10d);
                animator.PlaybackSpeed = 0.2f;

                interpreter.Eval("action $gnome wait 1");

                Assert.AreEqual(1f, animator.PlaybackSpeed, 1e-6f,
                    "re-ordering must restore the interrupted action's cadence modifier");
                animator.PlaybackSpeed = 0.35f;
                var sim = new Diggles.Runtime.Sim.DigglesGameSim(context);
                sim.Step(1.1d);

                Assert.IsNull(gnome.ActiveAction);
                Assert.AreEqual(1f, animator.PlaybackSpeed, 1e-6f,
                    "natural completion must restore the action-scoped modifier too");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void ElevatorLogic_ProjectsShaft_CallsCarriesAndDismountsRider()
        {
            const int width = 20;
            const int height = 30;
            var aux = new byte[Diggles.Runtime.World.BdbModel.AuxStride];
            for (int index = 0; index < 64; index++) aux[index] = 11;
            var bdb = new Diggles.Runtime.World.BdbModel
            {
                Header = new Diggles.Runtime.World.BdbHeader { NBlocks = 1 },
                PerBlockAux = aux,
            };
            var nav = new Diggles.Runtime.Navigation.NavWorld(
                width,
                height,
                new uint[width * height],
                new byte[width * height * 64],
                bdb);
            var terrainObject = new UnityEngine.GameObject("elevator-terrain");
            try
            {
                var terrain = terrainObject.AddComponent<Diggles.Runtime.World.DiggableTerrain>();
                terrain.Nav = nav;
                var (i, ctx) = MakeEngine();
                ctx.Terrain = terrain;
                i.Eval(@"
                    def_class Lift none elevator 0 {} {
                        set_class_anim lift_still lift.still
                        set_class_anim lift_up lift.up
                        set_class_anim lift_down lift.down
                    }
                    def_class Cabin none dummy 0 {} {}
                    def_class Gnome none gnome 0 {} {}
                    set lift [new Lift]
                    set_pos $lift 10 10 3
                    set_buildupstate $lift 1
                    set cabin [new Cabin]
                    set rider [new Gnome]
                    create_elevatorlogic $lift $cabin 1
                    set_elevatorproperties $lift 4 0 2.0");

                var lift = ctx.ResolveEntity(i.Eval("set lift"));
                var cabin = ctx.ResolveEntity(i.Eval("set cabin"));
                var rider = ctx.ResolveEntity(i.Eval("set rider"));
                var elevator = lift.TransportLogic!.Elevator!;
                Assert.AreEqual(4f, elevator.ProjectedSpan, 1e-6f);
                Assert.AreEqual(-16, lift.PathInfluence!.X0);
                Assert.AreEqual(8, lift.PathInfluence.Y0);
                Assert.AreEqual(16, lift.PathInfluence.X1);
                Assert.AreEqual(36, lift.PathInfluence.Y1);
                Assert.AreEqual(12, lift.PathInfluence.Type);
                Assert.IsTrue(nav.TryGetTransportInfluence(
                    80,
                    92,
                    out var kind,
                    out int source));
                Assert.AreEqual(Diggles.Runtime.Navigation.NavTransportKind.Elevator, kind);
                Assert.AreEqual(lift.Handle, source);

                var navService = terrainObject.AddComponent<Diggles.Runtime.Navigation.NavWorldService>();
                var startWorld = Diggles.Runtime.RuntimeWorldSpawner.NativeToLocal(
                    new UnityEngine.Vector3(10f, 11f, 11f));
                var goalWorld = Diggles.Runtime.RuntimeWorldSpawner.NativeToLocal(
                    new UnityEngine.Vector3(10f, 14f, 11f));
                var route = navService.FindPath(startWorld, goalWorld, canClimb: true);
                Assert.NotNull(route, Diggles.Runtime.Navigation.NavWorldService.LastNullReason);
                Assert.IsTrue(route!.Exists(waypoint => waypoint.IsElevator
                    && waypoint.TransportSourceHandle == lift.Handle));

                DigglesTclEngine.TickTransportLogic(ctx, 0.1);
                Assert.AreEqual(12f, cabin.Position.y, 1e-6f);
                var ride = new ElevatorRideAction(
                    ctx,
                    rider,
                    lift.Handle,
                    pickupY: 12f,
                    destinationY: 14f,
                    exitNative: new UnityEngine.Vector3(12f, 14f, 3f));
                Assert.IsTrue(ride.Tick(0.1));

                bool sawDown = false;
                bool running = true;
                for (int step = 0; step < 30 && running; step++)
                {
                    DigglesTclEngine.TickTransportLogic(ctx, 0.1);
                    sawDown |= lift.CurrentAnim == "lift.down";
                    running = ride.Tick(0.1);
                }

                Assert.IsFalse(running);
                Assert.AreEqual(0, ride.ExitResult);
                Assert.IsTrue(sawDown);
                Assert.AreEqual(14f, cabin.Position.y, 1e-5f);
                Assert.AreEqual(new UnityEngine.Vector3(12f, 14f, 3f), rider.Position);
                DigglesTclEngine.TickTransportLogic(ctx, 0.1);
                Assert.AreEqual("lift.still", lift.CurrentAnim);
                Assert.IsEmpty(elevator.Riders);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(terrainObject);
            }
        }

        static void SetSubCell(byte[] sub, int width, int sx, int sy, byte value)
        {
            int tile = (sy >> 3) * width + (sx >> 3);
            sub[tile * 64 + ((sy & 7) << 3) + (sx & 7)] = value;
        }

        [Test]
        public void ElevatorAcceleratedMotion_BrakesWithoutOvershootingTarget()
        {
            var elevator = new ElevatorLogicState
            {
                AccelerationMode = 1,
                Speed = 0.2f,
                BrakeAcceleration = 0.4f,
                CurrentCabinY = 2f,
                TargetY = 8f,
            };

            float previous = elevator.CurrentCabinY;
            float peakVelocity = 0f;
            for (int step = 0; step < 200; step++)
            {
                DigglesTclEngine.AdvanceElevatorMotion(elevator, 0.1f);
                Assert.GreaterOrEqual(elevator.CurrentCabinY + 1e-6f, previous);
                Assert.LessOrEqual(elevator.CurrentCabinY, 8f + 1e-6f);
                previous = elevator.CurrentCabinY;
                peakVelocity = System.Math.Max(peakVelocity, System.Math.Abs(elevator.Velocity));
                if (System.Math.Abs(elevator.CurrentCabinY - 8f) < 1e-5f) break;
            }

            Assert.Greater(peakVelocity, 0.2f);
            Assert.AreEqual(8f, elevator.CurrentCabinY, 1e-5f);
            Assert.AreEqual(0f, elevator.Velocity, 1e-5f);
        }

        [Test]
        public void SetPfInfluence_ParsesNativeSentinels_AndDeleteTransportClearsState()
        {
            var (i, ctx) = MakeEngine();
            i.Eval(@"
                def_class Gate none dummy 0 {} {}
                set gate [new Gate]
                set_pf_influence $gate -1 -20 +1 +4 INT_MAX 0");

            var gate = ctx.ResolveEntity(i.Eval("set gate"));
            Assert.AreEqual(-1, gate.PathInfluence!.X0);
            Assert.AreEqual(-20, gate.PathInfluence.Y0);
            Assert.AreEqual(1, gate.PathInfluence.X1);
            Assert.AreEqual(4, gate.PathInfluence.Y1);
            Assert.AreEqual(int.MaxValue, gate.PathInfluence.Cost);

            i.Eval("create_doorlogic $gate; delete_transportlogic $gate");
            Assert.IsNull(gate.TransportLogic);
            Assert.IsNull(gate.PathInfluence);
        }

        [Test]
        public void ExecDeferred_RunsOnNextStep_AndNestedWorkWaitsAnotherStep()
        {
            var (i, ctx) = MakeEngine();
            var sim = new Diggles.Runtime.Sim.DigglesGameSim(ctx);
            i.Eval(@"
                set marker 0
                exec_deferred {
                    set marker 1
                    exec_deferred { set marker 2 }
                }");

            Assert.AreEqual("0", i.Eval("set marker"));
            sim.Step(0.1);
            Assert.AreEqual("1", i.Eval("set marker"));
            sim.Step(0.1);
            Assert.AreEqual("2", i.Eval("set marker"));
        }

        [Test]
        public void VectorNormalize_And_Roty_FollowNativeFacingBasis()
        {
            var (i, _) = MakeEngine();
            Assert.AreEqual("0.6 0 0.8", i.Eval("vector_normalize {3 0 4}"));
            Assert.AreEqual("0 0 0", i.Eval("vector_normalize {0 0 0}"));

            var rotated = TclList.Parse(i.Eval(
                "vector_roty {0 0 1} 1.5707963267948966"));
            Assert.AreEqual(-1f, float.Parse(rotated[0], CultureInfo.InvariantCulture), 1e-5f);
            Assert.AreEqual(0f, float.Parse(rotated[1], CultureInfo.InvariantCulture), 1e-5f);
            Assert.AreEqual(0f, float.Parse(rotated[2], CultureInfo.InvariantCulture), 1e-5f);
        }

        [Test]
        public void Default_Anim_Applied_On_New()
        {
            var (i, ctx) = MakeEngine();
            i.Eval(@"
                def_class Pilz food material 0 {} { class_defaultanim pilz.standard }
                set h [new Pilz]");
            Assert.AreEqual("pilz.standard", ctx.Entities[1].CurrentAnim);
        }

        [Test]
        public void SetAnim_Overrides()
        {
            var (i, ctx) = MakeEngine();
            i.Eval(@"
                def_class Pilz food material 0 {} { class_defaultanim pilz.standard }
                set h [new Pilz]
                set_anim $h pilz.wachsen01");
            Assert.AreEqual("pilz.wachsen01", ctx.Entities[1].CurrentAnim);
        }

        [Test]
        public void Method_Registers_On_Class_And_Inherits_Resolve()
        {
            var (i, ctx) = MakeEngine();
            i.Eval(@"
                def_class Animal none monster 0 {} {
                    method speak {} { return ""generic"" }
                }
                def_class Dog none monster 0 {} {
                    inherit Animal
                    method bark {} { return ""woof"" }
                }");
            var dog = ctx.Classes["Dog"];
            Assert.IsNotNull(ctx.FindMethod(dog, "bark"));
            Assert.IsNotNull(ctx.FindMethod(dog, "speak"), "inherited method not found");
        }

        [Test]
        public void HandleEvent_State_StateEnter_StateLeave_Register()
        {
            var (i, ctx) = MakeEngine();
            i.Eval(@"
                def_class Sentinel none monster 0 {} {
                    def_event evt_intruder
                    handle_event evt_intruder { log alert }
                    state idle { return }
                    state_enter idle { log entering }
                    state_leave idle { log leaving }
                    member alert_count
                }");
            var c = ctx.Classes["Sentinel"];
            Assert.IsTrue(c.EventHandlers.ContainsKey("evt_intruder"));
            Assert.IsTrue(c.States.ContainsKey("idle"));
            Assert.IsTrue(c.StateEnter.ContainsKey("idle"));
            Assert.IsTrue(c.StateLeave.ContainsKey("idle"));
            Assert.IsTrue(c.MemberVars.Contains("alert_count"));
        }

        [Test]
        public void ObjInit_Runs_With_This_And_Member_Vars()
        {
            var (i, ctx) = MakeEngine();
            i.Eval(@"
                def_class Pilz food material 0 {} {
                    member content
                    obj_init {
                        set content 30
                        set_pos this 1 2 3
                    }
                }
                set h [new Pilz]");
            var ent = ctx.Entities[1];
            Assert.AreEqual("30", ent.VarsScope.Vars["content"]);
            Assert.AreEqual(1f, ent.Position.x);
            Assert.AreEqual(3f, ent.Position.z);
        }

        [Test]
        public void StateTrigger_HandlersRunAtThinkTime_NotAtTrigger()
        {
            // FINAL LAW 13.07 (two corrections in one day — the chop-age rounds): native
            // state_trigger (FUN_0069ec80:109) only writes the TARGET state index; BOTH
            // handlers run at THINK time in CObj::HandleStates (FUN_005cac90 — leave:30-55
            // gated `live != target && live != -1`, enter:56-80, live = target:81, body
            //:82-104). Coalesced triggers inside one handler ⇒ a TRANSIENT state never goes
            // live ⇒ NEITHER handler runs (protects the harvest/mine/dig task→work_dispatch
            // chains from `state_leave task {unlock_item}` AND the dispatcher pool from
            // `state_enter idle {gnome_idle this 1}` leaks). Leaving a LIVE state runs its
            // leave at the next trigger (documented port divergence: native defers that too).
            var (i, ctx) = MakeEngine();
            var sim = new Diggles.Runtime.Sim.DigglesGameSim(ctx);
            int enters = 0, leaves = 0;
            i.RegisterCommand("mark_enter", (_, _) => { enters++; return ""; });
            i.RegisterCommand("mark_leave", (_, _) => { leaves++; return ""; });
            i.Eval(@"
                def_class M none monster 0 {} {
                    state_enter idle { mark_enter }
                    state_leave idle { mark_leave }
                }
                set h [new M]
                state_trigger $h idle
                state_trigger $h");
            Assert.AreEqual(0, enters,
                "a transient state never went live — its enter must NOT run (FUN_005cac90:56-80)");
            Assert.AreEqual(0, leaves,
                "a transient state never went live — its leave must NOT run (FUN_005cac90:30-55)");

            // A think tick reaches the freshly-triggered state: enter runs, state goes live.
            i.Eval("state_trigger $h idle");
            for (int t = 0; t < 10 && enters == 0; t++) sim.Step(0.1);
            Assert.AreEqual(1, enters, "enter runs on the first think tick of the new state");
            Assert.AreEqual(0, leaves, "nothing left yet");

            // Leaving the LIVE state runs its leave.
            i.Eval("state_trigger $h");
            Assert.AreEqual(1, leaves, "leave runs when the state had gone live");
        }

        [Test]
        public void CallMethod_Invokes_With_This_Bound()
        {
            var (i, ctx) = MakeEngine();
            i.Eval(@"
                def_class Counter none monster 0 {} {
                    member n
                    obj_init { set n 0 }
                    method bump {} { set n [expr {$n + 1}] }
                    method get {} { return $n }
                }
                set h [new Counter]
                call_method $h bump
                call_method $h bump
                call_method $h bump");
            Assert.AreEqual("3", i.Eval("call_method $h get"));
        }

        [Test]
        public void LiveWalkCapture_FillsNativeAnimationBanksAndCtorState()
        {
            const string options =
                "-target {1 2 3} -waving 1 -forcetarget 0 -speedtype 0 " +
                "-animsets {0 1 3 0}";
            var (state, library) = CaptureSyntheticWalk(options, alternateAnimationDatabase: false);
            int[] bankStates = state.CopyNativeAnimationBankStates();
            int[] slotBits = state.CopyNativeAnimationSlotBits();

            CollectionAssert.AreEqual(new[] { 0, 1, 3, 0 }, bankStates);
            Assert.That(state.MoveNeededOrApproach, Is.EqualTo(1));
            Assert.That(state.Waving, Is.EqualTo(1));
            Assert.That(state.DontStop, Is.Zero);
            Assert.That(state.StopAt, Is.EqualTo(2f));
            Assert.That(state.UseObjects, Is.EqualTo(1));
            CollectionAssert.AreEqual(new byte[] { 1, 0, 0, 0 }, state.CopyMoveFlags());
            Assert.That(state.ObjectBreakDistance, Is.EqualTo(-1f));
            Assert.That(BitConverter.SingleToInt32Bits(state.ClimbRotation.y), Is.EqualTo(0x40490FDB));
            Assert.That(state.WalkCollisionCallback, Is.EqualTo("walk_collision_callback"));
            Assert.That(state.ObjectUseCallback, Is.EqualTo("obj_use_callback"));
            Assert.That(state.NativeArgument, Is.EqualTo(options));
            Assert.That(state.StatusOrOpaque, Is.EqualTo(unchecked((int)0xDFDFDFDF)));

            AssertWalkField(slotBits, 0, 0, 1, 4);
            AssertWalkField(slotBits, 0, 0, 4, 2);
            AssertWalkField(slotBits, 0, 1, 1, 35);
            AssertWalkField(slotBits, 0, 1, 4, 2);
            AssertWalkField(slotBits, 0, 7, 1, 7);
            AssertWalkField(slotBits, 0, 7, 4, 10);
            AssertWalkField(slotBits, 2, 1, 1, 851);
            AssertWalkField(slotBits, 2, 1, 4, 2);
            AssertWalkField(slotBits, 2, 7, 1, 846);
            AssertWalkField(slotBits, 2, 7, 4, 10);

            var mappings = new[]
            {
                StandardWalkMappings(),
                RunWalkMappings(),
                SwimWalkMappings(),
                StandardWalkMappings(),
            };
            for (int bank = 0; bank < mappings.Length; bank++)
            {
                for (int slot = 0; slot < WalkSlotNames.Length; slot++)
                {
                    if (!mappings[bank].ContainsKey(WalkSlotNames[slot]))
                        AssertMissingWalkSlot(slotBits, bank, slot);
                }
            }

            ThreeDbModel model = library.GetDatabaseModel("mann") ??
                throw new InvalidOperationException("mann.3db did not load.");
            AssertNativeRootArc(slotBits, model, 0, 7);
            AssertNativeRootArc(slotBits, model, 2, 7);

            int left90Animation = WalkField(slotBits, 0, 2, 1);
            Assert.That(WalkField(slotBits, 0, 2, 3), Is.EqualTo(
                BitConverter.SingleToInt32Bits(model.Anims[left90Animation].VecB.z)));
            foreach (int slot in new[] { 2, 3 })
            {
                int swimAnimation = WalkField(slotBits, 2, slot, 1);
                Assert.That(model.Anims[swimAnimation].VecB.z, Is.Zero);
                AssertNativeTurnDefault(slotBits, model, 2, slot, 0.10f);
            }

            foreach (int slot in new[] { 4, 5 })
            {
                AssertNativeTurnDefault(slotBits, model, 0, slot, 0.15f);
                int swimAnimation = WalkField(slotBits, 2, slot, 1);
                Assert.That(model.Anims[swimAnimation].VecB.z, Is.Zero);
                AssertNativeTurnDefault(slotBits, model, 2, slot, 0.15f);
            }
        }

        [Test]
        public void LiveWalkCapture_AlternateDatabaseAddsOneToEveryResolvedA()
        {
            const string options = "-target {1 2 3} -animsets {0 1 3 0}";
            var (maleState, _) = CaptureSyntheticWalk(options, alternateAnimationDatabase: false);
            var (femaleState, _) = CaptureSyntheticWalk(options, alternateAnimationDatabase: true);
            int[] male = maleState.CopyNativeAnimationSlotBits();
            int[] female = femaleState.CopyNativeAnimationSlotBits();
            int resolved = 0;
            for (int offset = 0; offset < male.Length; offset += 5)
            {
                if (male[offset] == -1)
                {
                    for (int field = 0; field < 5; field++)
                        Assert.That(female[offset + field], Is.EqualTo(male[offset + field]));
                    continue;
                }

                resolved++;
                Assert.That(female[offset], Is.EqualTo(male[offset] + 1));
                for (int field = 1; field < 5; field++)
                    Assert.That(female[offset + field], Is.EqualTo(male[offset + field]));
            }
            Assert.That(resolved, Is.GreaterThan(0));
        }

        [Test]
        public void LiveWalkCapture_MissingAnimsetElementsKeepNativeDefaultsAndSentinels()
        {
            const string options = "-target {1 2 3} -animsets 0 -useobjects 0";
            var (state, _) = CaptureSyntheticWalk(options, alternateAnimationDatabase: false);
            CollectionAssert.AreEqual(new[] { 0, -1, -1, 0 },
                state.CopyNativeAnimationBankStates());
            Assert.That(state.MoveNeededOrApproach, Is.Zero);
            Assert.That(state.UseObjects, Is.Zero);
            Assert.That(state.NativeArgument, Is.EqualTo(
                "-target {1 2 3} -waving 1 -forcetarget 0 -speedtype 0 " +
                "-animsets {0 -1 -1 0} -useobjects 0"));
            int[] slotBits = state.CopyNativeAnimationSlotBits();
            for (int bank = 1; bank <= 2; bank++)
            {
                for (int slot = 0; slot < WalkSlotNames.Length; slot++)
                    AssertMissingWalkSlot(slotBits, bank, slot);
            }
        }

        private static readonly string[] WalkSlotNames =
        {
            "standard", "climb_standard", "turn_left_90", "turn_right_90",
            "turn_left_180", "turn_right_180", "walk_start", "walk_loop", "walk_stop",
            "climb_up", "climb_down", "climb_right", "climb_left", "walk_loop_wave",
            "ladder_climb_up", "ladder_climb_down", "ground_to_wall", "wall_to_ground",
            "ground_to_ladder", "ladder_to_ground", "walk_begin", "walk_end",
            "ladder_climb_right", "ladder_climb_left",
        };

        private static (WalkActionSaveState State, DigglesModelLibrary Library) CaptureSyntheticWalk(
            string options,
            bool alternateAnimationDatabase)
        {
            string dataRoot = RequireWalkDataRoot();
            var (interpreter, context) = MakeEngine();
            var library = new DigglesModelLibrary(dataRoot);
            context.ModelLibrary = library;
            var walkClass = new DigglesClass
            {
                Name = "Zwerg",
                Kind = "gnome",
            };
            walkClass.Animsets[0] = StandardWalkMappings();
            walkClass.Animsets[1] = RunWalkMappings();
            walkClass.Animsets[3] = SwimWalkMappings();
            var owner = new DigglesTclEntity
            {
                Class = walkClass,
                InstanceName = "Zwerg",
                NativeObjectType = 1,
                AlternateAnimDb = alternateAnimationDatabase,
            };
            context.RegisterEntityAtHandle(owner, 1);
            interpreter.Eval("action 1 walk {" + options + "}");
            var saveable = owner.ActiveAction as ISaveableEntityAction ??
                throw new InvalidOperationException("Live walk action was not saveable.");
            return ((WalkActionSaveState)saveable.CaptureSaveState(), library);
        }

        private static string RequireWalkDataRoot()
        {
            string dataRoot;
            try
            {
                dataRoot = OriginalDataTestFixture.DataRoot;
            }
            catch (Exception exception)
            {
                Assert.Ignore("original data dir not present — walk-bank tests skipped: " +
                    exception.Message);
                return string.Empty;
            }
            Assume.That(File.Exists(Path.Combine(dataRoot, "3db", "mann.3db")),
                "original data dir not present — walk-bank tests skipped");
            return dataRoot;
        }

        private static Dictionary<string, string> StandardWalkMappings() => new()
        {
            ["standard"] = "mann.standard",
            ["walk_start"] = "mann.gehen_start",
            ["walk_loop"] = "mann.gehen_loop",
            ["walk_loop_wave"] = "mann.gehen_gruessen",
            ["walk_stop"] = "mann.gehen_end",
            ["turn_left_90"] = "mann.drehen_links",
            ["turn_right_90"] = "mann.drehen_rechts",
            ["turn_left_180"] = "mann.drehen_ganz_links",
            ["turn_right_180"] = "mann.drehen_ganz",
            ["climb_standard"] = "mann.kletterstand",
            ["climb_up"] = "mann.kletter_hoch_loop",
            ["climb_down"] = "mann.kletter_runter_loop",
            ["climb_right"] = "mann.kletter_rechts_loop",
            ["climb_left"] = "mann.kletter_links_loop",
            ["ladder_climb_up"] = "mann.leiter_hoch_loop",
            ["ladder_climb_down"] = "mann.leiter_runter_loop",
            ["ground_to_wall"] = "mann.stand_zu_kletterstand",
            ["wall_to_ground"] = "mann.kletterstand_zu_stand",
            ["ladder_to_ground"] = "mann.leiter_zu_boden",
        };

        private static Dictionary<string, string> RunWalkMappings() => new()
        {
            ["standard"] = "mann.standard",
            ["walk_start"] = "mann.laufen_schnell_start",
            ["walk_loop"] = "mann.laufen_schnell_loop",
            ["walk_loop_wave"] = "mann.laufen_schnell_loop",
            ["walk_stop"] = "mann.laufen_schnell_end",
            ["turn_left_90"] = "mann.drehen_links",
            ["turn_right_90"] = "mann.drehen_rechts",
            ["turn_left_180"] = "mann.drehen_ganz_links",
            ["turn_right_180"] = "mann.drehen_ganz",
            ["climb_standard"] = "mann.kletterstand",
            ["climb_up"] = "mann.kletter_hoch_schnell",
            ["climb_down"] = "mann.kletter_runter_schnell",
            ["climb_right"] = "mann.kletter_rechts_schnell",
            ["climb_left"] = "mann.kletter_links_schnell",
            ["ladder_climb_up"] = "mann.leiter_hoch_loop",
            ["ladder_climb_down"] = "mann.leiter_runter_loop",
            ["ground_to_wall"] = "mann.stand_zu_kletterstand",
            ["wall_to_ground"] = "mann.kletterstand_zu_stand",
            ["ladder_to_ground"] = "mann.leiter_zu_boden",
        };

        private static Dictionary<string, string> SwimWalkMappings() => new()
        {
            ["standard"] = "mann.standard",
            ["walk_start"] = "mann.schwimmen_start",
            ["walk_loop"] = "mann.schwimmen_loop",
            ["walk_loop_wave"] = "mann.schwimmen_loop",
            ["walk_stop"] = "mann.schwimmen_end",
            ["turn_left_90"] = "mann.schwimmen_loop",
            ["turn_right_90"] = "mann.schwimmen_loop",
            ["turn_left_180"] = "mann.schwimmen_loop",
            ["turn_right_180"] = "mann.schwimmen_loop",
            ["climb_standard"] = "mann.schwimmen_wand_stand",
            ["climb_up"] = "mann.schwimmen_hoch",
            ["climb_down"] = "mann.schwimmen_runter",
            ["climb_right"] = "mann.schwimmen_hoch",
            ["climb_left"] = "mann.schwimmen_hoch",
            ["ladder_climb_up"] = "mann.leiter_hoch_loop",
            ["ladder_climb_down"] = "mann.leiter_runter_loop",
            ["ground_to_wall"] = "mann.stand_zu_kletterstand",
            ["wall_to_ground"] = "mann.kletterstand_zu_stand",
            ["ladder_to_ground"] = "mann.leiter_zu_boden",
        };

        private static int WalkField(int[] slotBits, int bank, int slot, int field) =>
            slotBits[(bank * WalkSlotNames.Length + slot) * 5 + field];

        private static void AssertWalkField(
            int[] slotBits,
            int bank,
            int slot,
            int field,
            int expected) =>
            Assert.That(WalkField(slotBits, bank, slot, field), Is.EqualTo(expected));

        private static void AssertMissingWalkSlot(int[] slotBits, int bank, int slot)
        {
            int[] expected =
            {
                -1,
                -1,
                BitConverter.SingleToInt32Bits(1f),
                BitConverter.SingleToInt32Bits(10f),
                0,
            };
            for (int field = 0; field < expected.Length; field++)
                AssertWalkField(slotBits, bank, slot, field, expected[field]);
        }

        private static void AssertNativeRootArc(
            int[] slotBits,
            ThreeDbModel model,
            int bank,
            int slot)
        {
            int animationIndex = WalkField(slotBits, bank, slot, 1);
            var vector = model.Anims[animationIndex].VecA;
            float xSquared = (float)(vector.x * vector.x);
            float zSquared = (float)(vector.z * vector.z);
            float xzSquared = (float)(xSquared + zSquared);
            float ySquared = (float)(vector.y * vector.y);
            float magnitudeSquared = (float)(xzSquared + ySquared);
            float magnitude = (float)Math.Sqrt(magnitudeSquared);
            float expected = (float)(magnitude * 0.05f);
            Assert.That(WalkField(slotBits, bank, slot, 2), Is.EqualTo(
                BitConverter.SingleToInt32Bits(expected)));
        }

        private static void AssertNativeTurnDefault(
            int[] slotBits,
            ThreeDbModel model,
            int bank,
            int slot,
            float defaultValue)
        {
            int animationIndex = WalkField(slotBits, bank, slot, 1);
            float authoredValue = model.Anims[animationIndex].VecB.z;
            float expected = authoredValue == 0f ? defaultValue : authoredValue;
            Assert.That(WalkField(slotBits, bank, slot, 3), Is.EqualTo(
                BitConverter.SingleToInt32Bits(expected)));
        }

        [Test]
        public void IRandom_In_Range()
        {
            var (i, _) = MakeEngine();
            for (int k = 0; k < 50; k++)
            {
                int v = int.Parse(i.Eval("irandom 10"));
                Assert.IsTrue(v >= 0 && v < 10, $"got {v}");
            }
        }
    }
}
