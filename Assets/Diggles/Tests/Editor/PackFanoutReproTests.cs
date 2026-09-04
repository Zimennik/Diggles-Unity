#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    // Repro for a fan-out bug: ordering a building packed must route the job to a single
    // gnome, not send every free gnome to pack it at once. Uses a real-class harness (the
    // WorkLoopTests / CookWorkChainTests pattern: full classinit, real Zwerg state machine,
    // real genericprod/Lager Tcl), driving the exact UI path: PlacementController.OrderPack →
    // PackRequested byte → ProdDispatcher kind-0 job.
    //
    // Native law: the kind-0 pack is a step plan — muetze("dienstleistung") then the pack emit
    // (plan builder FUN_00439760: step code 2 muetze + step 6 pack; emits FUN_0043a8a0:44-48 /
    //:11) — dispatched to one gnome; the planner's one-dispatch-per-pass cap + the
    // record-busy gate (FUN_00436440:31 rec+0x1c==0) exclude everyone else while the record is
    // in service.
    [Explicit]   // loads ~300 real classes from the data dir; run on demand (batch)
    public sealed class PackFanoutReproTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

        readonly List<GameObject> _spawnedGos = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _spawnedGos) if (go != null) Object.DestroyImmediate(go);
            _spawnedGos.Clear();
        }

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

        DiggableTerrain MakeOpenTerrain(int w, int h)
        {
            var go = new GameObject("DiggableTerrain");
            _spawnedGos.Add(go);
            var t = go.AddComponent<DiggableTerrain>();
            var block = new uint[w * h];
            for (int k = 0; k < block.Length; k++) block[k] = 59;
            var bdb = BdbParser.Parse(DataRoot + "/BlockDB2.BDB");
            t.Init(new BdbPmpWorldBuilder.TclWorldResult { Width = w, Height = h, BlockIdx = block, Terrain = new byte[w * h] }, bdb, "", () => { });
            return t;
        }

        static DigglesTclEntity Spawn(TclInterpreter interp, DigglesTclContext ctx, string cls, string pos)
            => ctx.GetEntity(interp.Eval($"new {cls} \"\" \"{pos}\" \"0 0 0\""))!;

        [Test, Explicit]
        public void PackOrder_RealScripts_ExactlyOneGnomePacks_WithServiceHat()
        {
            var (interp, ctx, sim) = RealHost();
            ctx.LocalPlayer = 0;
            ctx.Terrain = MakeOpenTerrain(64, 64);

            var lager = Spawn(interp, ctx, "Lager", "35 30 8");
            lager.Owner = 0;
            lager.BuildupComplete = true;

            var gnomes = new List<DigglesTclEntity>();
            for (int k = 0; k < 3; k++)
            {
                var g = Spawn(interp, ctx, "Zwerg", $"{30 + k} 30 8");
                g.Owner = 0;
                interp.Eval($"set_worktime {g.Handle} 0 12");
                gnomes.Add(g);
            }
            for (int i = 0; i < 30; i++) sim.Step(0.1);   // settle to idle

            // The UI path: the pack button → PlacementController.OrderPack
            ctx.Placement!.OrderPack(lager.Handle);
            Assert.IsTrue(lager.PackRequested, "precondition: the byte armed");

            var everPacked = new HashSet<int>();
            var hats = new Dictionary<int, string>();
            int prevClaim = lager.CurrentWorkerHandle;
            bool prevByte = lager.PackRequested;
            for (int i = 0; i < 900; i++)   // 90 s sim = ~22 player-0 sweeps
            {
                sim.Step(0.1);
                // claim/byte transition trace — who releases the claim while the byte stays?
                if (lager.CurrentWorkerHandle != prevClaim || lager.PackRequested != prevByte)
                {
                    lager.VarsScope.Vars.TryGetValue("buildup_step", out var bs);
                    string snap2 = "";
                    foreach (var g in gnomes)
                    {
                        g.VarsScope.Vars.TryGetValue("current_worktask", out var wt2);
                        snap2 += $" g{g.Handle}[{g.CurrentState}/{wt2}]";
                    }
                    TestContext.WriteLine($"TRANS t={i / 10.0:0.00}s claim {prevClaim}->{lager.CurrentWorkerHandle} byte {prevByte}->{lager.PackRequested} bstep={bs}{snap2}");
                    prevClaim = lager.CurrentWorkerHandle;
                    prevByte = lager.PackRequested;
                }
                foreach (var g in gnomes)
                {
                    if (g.VarsScope.Vars.TryGetValue("current_worktask", out var wt) && wt == "pack")
                    {
                        everPacked.Add(g.Handle);
                        if (g.VarsScope.Vars.TryGetValue("current_muetze_name", out var hat) && !string.IsNullOrEmpty(hat))
                            hats[g.Handle] = hat;
                    }
                }
                if (i % 100 == 0)
                {
                    string snap = "";
                    foreach (var g in gnomes)
                    {
                        g.VarsScope.Vars.TryGetValue("current_worktask", out var wt);
                        g.VarsScope.Vars.TryGetValue("current_muetze_name", out var hat);
                        snap += $" g{g.Handle}[st={g.CurrentState} wt={wt} hat={hat}]";
                    }
                    lager.VarsScope.Vars.TryGetValue("_boxed", out var bx);
                    TestContext.WriteLine($"t={i / 10.0:0.0}s byte={lager.PackRequested} claim={lager.CurrentWorkerHandle} boxed={bx}{snap}");
                }
            }

            lager.VarsScope.Vars.TryGetValue("_boxed", out var boxed);
            TestContext.WriteLine($"RESULT: everPacked={everPacked.Count} [{string.Join(",", everPacked)}] boxed={boxed} byte={lager.PackRequested}");
            foreach (var kv in hats) TestContext.WriteLine($"  hat g{kv.Key}: {kv.Value}");

            Assert.AreEqual(1, everPacked.Count,
                "EXACTLY ONE gnome may ever take the pack order (13.07 bug: all free gnomes ran at once)");
            // The kind-0 plan's muetze step: the packer wears the service hat while packing
            // (dienstleistung — the same hat family the unpack/buildup service jobs wear).
            foreach (var kv in hats)
                StringAssert.Contains("dienstleistung", kv.Value,
                    "the packer must wear the service hat (the plan's muetze step; 13.07: no hat change)");
        }
    }
}
