#nullable enable

namespace Diggles.Tests
{
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;

    // the caterpillar's whole AI hangs
    // off ONE chain — obj_init's `timer_event this evt_timer0 -interval 1` → evt_timer0 handler
    // `state_triggerfresh free|farmed` → the state body re-runs per think tick (crawl_free /
    // crawl_farmed / wait). This proves that chain headlessly: with no terrain, get_place returns
    // the "-1 0 0" sentinel and crawl_free degrades to `wait 3` — an ACTION — so a live Raupe
    // must (a) hold a state and (b) engage actions. If either assert fails, the break point is
    // in the timer/state plumbing, not in Play-only layers.
    [Explicit]   // loads ~300 real classes from the data dir; run on demand
    public sealed class RaupeAliveTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

        static (TclInterpreter interp, DigglesTclContext ctx, DigglesGameSim sim) RealHost()
        {
            var interp = new TclInterpreter { TolerateExprErrors = true, TolerateMissingVars = true };
            interp.UnknownCommandHandler = (i, a) => "0";
            var ctx = DigglesTclEngine.Register(interp);
            ctx.DataRoot = DataRoot;
            ctx.TolerateMissingEntities = true;
            ctx.TolerateMissingClass = true;
            ctx.TolerateMissingFiles = true;
            var sim = new DigglesGameSim(ctx);
            foreach (var s in new[] { "scripts/init/txtinit.tcl", "scripts/init/animinit.tcl", "scripts/init/classinit.tcl" })
                try { interp.Eval("call " + s); }
                catch (System.Exception e) { TestContext.WriteLine($"[init] {s}: {e.Message}"); }
            return (interp, ctx, sim);
        }

        [Test]
        public void Raupe_IdleAI_Engages()
        {
            var (interp, ctx, sim) = RealHost();
            var r = ctx.GetEntity(interp.Eval("new Raupe \"\" \"100 100 5\" \"0 0 0\"").Trim())!;
            Assert.IsNotNull(r, "Raupe must spawn");

            int actionTicks = 0;
            string? state = null;
            for (int i = 0; i < 80; i++)   // 8 s: timer fires at +1 s, then thinks at 10 Hz
            {
                sim.Step(0.1);
                if (r.ActiveAction != null) actionTicks++;
                state ??= r.CurrentState;
            }

            Assert.IsNotNull(r.CurrentState,
                "evt_timer0 never armed a state — the timer→state_triggerfresh chain is dead");
            StringAssert.IsMatch("free|farmed|contained", r.CurrentState!,
                $"unexpected state '{r.CurrentState}'");
            Assert.Greater(actionTicks, 0,
                "the state body never engaged an action (crawl_free should at least `wait 3`)");
        }
    }
}
