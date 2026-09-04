#nullable enable

namespace Diggles.Tests
{
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;

    // The automatic event already carries a concrete mushroom handle
    // (z_events.tcl:1402-1439); harvest consumes that handle without searching
    // (z_procs.tcl:739-746). The native planner registers only unlocked own/neutral
    // resources in explored fog (FUN_00434e60:14), then resolves the concrete
    // target through FUN_0043bae0:33 -> FUN_004317a0. The native resolver applies TWO
    // visibility conditions: the requester's bit in the per-object mask byte
    // (FUN_004317a0:46/:55) AND the live GetMaxFow(candidate, player) > 0x8c test
    // (FUN_004317a0:71-74). The mask byte is NOT a forever-sticky latch: CAiCentral's
    // tick re-stamps 150 object slots per pass (FUN_00424680:57-73, cursor wraps at
    // 0x2000 => each object revisited every ~5.5 s), each visit clearing the byte
    // (FUN_004315a0:55) and re-stamping it — neutral objects: every lane with
    // fow > 200 plus the single best lane > 0x80 (:61-78,:93-102); owned objects: the
    // owner's lane at fow > 0x80 (:83-91). DOCUMENTED PORT DEVIATION (,
    // decision: not modelled — see the ProdDispatcher fog-eligibility block): the port
    // checks only the live > 0x8c; the residual deltas are the <=5.5 s stamp staleness
    // on freshly revealed objects and the stricter > 200 bar for a non-best lane on
    // neutral objects. The black/gray/visible/foreign matrix below is identical under
    // both laws — it pins the shared contract. The resolver ranks survivors by
    // weighted distance only (FUN_004317a0:75-83); there is no navigation-reachability
    // predicate in this scan. Manual clicks remain separate: eventgen.tcl:236-243
    // emits evt_task_harvest directly, and z_events.tcl:900-935 forwards its supplied
    // handle to harvest.
    public sealed class MushroomFogDispatchTests
    {
        private const string AutoHarvestEvent = "evt_autoprod_harvest";

        private static (TclInterpreter interp, DigglesTclContext ctx, DigglesGameSim sim) Host()
        {
            var interp = new TclInterpreter
            {
                TolerateExprErrors = true,
                TolerateMissingVars = true,
            };
            interp.UnknownCommandHandler = (i, a) => "0";
            var ctx = DigglesTclEngine.Register(interp);
            ctx.TolerateMissingEntities = true;
            ctx.TolerateMissingClass = true;
            ctx.TolerateMissingFiles = true;
            var sim = new DigglesGameSim(ctx);
            interp.Eval(@"
                def_class HarvestGnome none gnome 0 {} {
                    member manual_target
                    obj_init { set manual_target 0 }
                    handle_event evt_task_harvest {
                        set manual_target [event_get this -subject1]
                    }
                }
                def_class HarvestWorkshop none production 0 {} {
                    method prod_item_materials {item} { return {Pilzhut} }
                }
                def_class Pilzhut none item 0 {} {}
                def_class Pilz none resource 0 {} {}
                def_attrib BpGrillpilz 0 1 1
            ");
            return (interp, ctx, sim);
        }

        private static DigglesTclEntity Spawn(
            TclInterpreter interp,
            DigglesTclContext ctx,
            string className,
            string position)
            => ctx.GetEntity(interp.Eval(
                $"new {className} \"\" \"{position}\" \"0 0 0\"").Trim())!;

        private static (DigglesTclEntity gnome, DigglesTclEntity mushroom) HarvestWorld(
            TclInterpreter interp,
            DigglesTclContext ctx,
            int mushroomOwner)
        {
            ctx.LocalPlayer = 0;
            ctx.FogOfWar = new FogOfWarState(64, 64, (x, y) => 0);

            var workshop = Spawn(interp, ctx, "HarvestWorkshop", "10 10 8");
            workshop.Owner = 0;
            workshop.BuildupComplete = true;
            workshop.ProdSlots["Grillpilz"] = 1;

            var mushroom = Spawn(interp, ctx, "Pilz", "14 10 8");
            mushroom.Owner = mushroomOwner;

            var gnome = Spawn(interp, ctx, "HarvestGnome", "11 10 8");
            gnome.Owner = 0;
            interp.Eval($"set_worktime {gnome.Handle} 0 12");
            return (gnome, mushroom);
        }

        private static EntityEvent? FindEvent(DigglesTclEntity entity, string eventName)
        {
            foreach (var candidate in entity.PendingEvents)
                if (candidate.Name == eventName)
                    return candidate;
            return null;
        }

        private static void RevealMushroom(DigglesTclContext ctx, DigglesTclEntity mushroom)
        {
            var fog = ctx.FogOfWar!;
            fog.RemoveFogBoxNoCollision(
                (int)mushroom.Position.x * 8,
                (int)mushroom.Position.y * 8,
                24,
                24,
                0);
            fog.EndUpdate();
        }

        [Test]
        public void AutoHarvest_GrayHiddenNeutralMushroom_IsNotDispatched()
        {
            var (interp, ctx, _) = Host();
            var (gnome, mushroom) = HarvestWorld(interp, ctx, -1);
            ctx.FogOfWar!.RemoveBlackFog(0, 12, 8, 16, 12);
            ctx.FogOfWar.EndUpdate();

            ctx.ProdDispatcher!.OnGnomeIdle(gnome);
            ctx.ProdDispatcher.SweepNow(0);

            Assert.That(FindEvent(gnome, AutoHarvestEvent), Is.Null,
                "gray fog is below the native >0x8c final-source threshold");
            Assert.That(ctx.Entities.ContainsKey(mushroom.Handle), Is.True);
        }

        [Test]
        public void AutoHarvest_VisibleForeignMushroom_IsNotDispatched()
        {
            var (interp, ctx, _) = Host();
            var (gnome, mushroom) = HarvestWorld(interp, ctx, 1);
            RevealMushroom(ctx, mushroom);

            ctx.ProdDispatcher!.OnGnomeIdle(gnome);
            ctx.ProdDispatcher.SweepNow(0);

            Assert.That(FindEvent(gnome, AutoHarvestEvent), Is.Null,
                "another player's mushroom must fail the native own-or-neutral gate");
        }

        [TestCase(0)]
        [TestCase(-1)]
        public void AutoHarvest_VisibleOwnOrNeutralMushroom_IsDispatched(int mushroomOwner)
        {
            var (interp, ctx, _) = Host();
            var (gnome, mushroom) = HarvestWorld(interp, ctx, mushroomOwner);
            RevealMushroom(ctx, mushroom);

            ctx.ProdDispatcher!.OnGnomeIdle(gnome);
            ctx.ProdDispatcher.SweepNow(0);

            var harvestEvent = FindEvent(gnome, AutoHarvestEvent);
            Assert.That(harvestEvent, Is.Not.Null);
            Assert.That(harvestEvent!.Value.Payload, Is.Not.Null);
            Assert.That(
                harvestEvent.Value.Payload!["-subject1"],
                Is.EqualTo(mushroom.Handle.ToString()));
        }

        [Test]
        public void DirectManualOrder_VisibleLockedForeignMushroom_ReachesHarvestHandler()
        {
            var (interp, ctx, sim) = Host();
            var (gnome, mushroom) = HarvestWorld(interp, ctx, 1);
            interp.Eval($"set_lock {mushroom.Handle} 1");
            RevealMushroom(ctx, mushroom);

            var previous = ctx.This;
            ctx.This = gnome;
            try
            {
                interp.Eval(
                    $"set_event this evt_task_harvest -subject1 {mushroom.Handle}");
            }
            finally
            {
                ctx.This = previous;
            }
            sim.Step(0.025);

            Assert.That(
                gnome.VarsScope.Vars["manual_target"],
                Is.EqualTo(mushroom.Handle.ToString()),
                "search-side fog, owner, and ripeness gates must not intercept a direct order");
        }
    }
}
