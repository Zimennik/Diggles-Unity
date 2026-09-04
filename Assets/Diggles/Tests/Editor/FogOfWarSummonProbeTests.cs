#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using Diggles.Runtime;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;
    using UnityEngine;

    // Focused probe for the meadow re-fog root cause: `sq_object summon` creates production
    // objects via `new $item` with no position (sequencer.tcl:881-887 — set_owner/set_pos come
    // after new returns), so obj_init runs at the default (0,0,0). The genericprod object-init
    // (genericprod.tcl:444-490) must still complete — set_buildupstate 1 (:451) and
    // set_fogofwar tttfow (:486) — exactly as it does for template spawns that pass a position
    // into `new`. This pins whichever command aborts the chain in the no-position case.
    public sealed class FogOfWarSummonProbeTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

        [Test]
        public void SummonShapedNew_NoPosition_StillRunsGenericprodInit()
        {
            var rootGo = new GameObject("WorldRoot");
            rootGo.transform.localRotation = Quaternion.Euler(270f, 0f, 0f);
            try
            {
                var engine = new DigglesEngine();
                engine.Build(DataRoot, rootGo.transform, "BlockDB2.BDB");
                var ctx = engine.Ctx;

                // Trace the obj_init command stream so an aborting command is visible.
                var trace = new List<string>();
                engine.Interp.CommandTrace = (name, args, result) =>
                {
                    if (trace.Count < 4000)
                        trace.Add(name + " " + string.Join(" ",
                            System.Linq.Enumerable.Select(
                                System.Linq.Enumerable.Skip(args, 1),
                                s => s.Length > 30 ? s.Substring(0, 30) + "…" : s)));
                };

                engine.Interp.Eval("call scripts/classes/work/feuerstelle.tcl");
                Assert.IsTrue(ctx.Classes.ContainsKey("Feuerstelle"), "class loaded");

                // Template-shaped: position passed into new (the known-good path).
                engine.Interp.Eval("sel /obj");
                trace.Clear();
                var hGood = engine.Interp.Eval("new Feuerstelle \"\" \"246 30 9\" \"0 0 0\"").Trim();
                var good = ctx.GetEntity(hGood)!;
                TestContext.WriteLine($"[template-shaped] built={good.BuildupComplete} fow={good.FowSizeX}x{good.FowSizeY}");

                // Summon-shaped: no position (sequencer.tcl:882 `set object [new $item]`).
                trace.Clear();
                var hSum = engine.Interp.Eval("new Feuerstelle").Trim();
                var sum = ctx.GetEntity(hSum)!;
                engine.Interp.Eval($"set_owner {hSum} 0");
                engine.Interp.Eval($"set_pos {hSum} {{246.8 30 9.8}}");
                TestContext.WriteLine($"[summon-shaped]   built={sum.BuildupComplete} fow={sum.FowSizeX}x{sum.FowSizeY}");
                TestContext.WriteLine("---- summon obj_init trace (last 60) ----");
                for (int i = Mathf.Max(0, trace.Count - 60); i < trace.Count; i++)
                    TestContext.WriteLine("  " + trace[i]);

                Assert.IsTrue(good.BuildupComplete, "template-shaped new must come out BUILT (genericprod.tcl:451)");
                Assert.That(good.FowSizeX, Is.EqualTo(15 * 8), "template-shaped new must get tttfow 15 (techtreetunes.tcl:38)");
                Assert.IsTrue(sum.BuildupComplete,
                    "summon-shaped new (no position) must come out BUILT too — the original runs the same " +
                    "obj_init at the default position and only then set_owner/set_pos (sequencer.tcl:881-887)");
                Assert.That(sum.FowSizeX, Is.EqualTo(15 * 8),
                    "summon-shaped new must get tttfow 15 — this is the urw_3 campfire, the camp's floor revealer");
            }
            finally
            {
                Object.DestroyImmediate(rootGo);
            }
        }

        // Ground-truth lock: the port's fog-ray collision probe (FogFillAt =
        // BDB aux byte 0 = sub-cell (0,0) fill) over the meadow floor rect must equal the
        // original's live values, read from the running game's memory
        // (dumps/diggles_meadow.dmp: world→tile[(x+y*512)%N]→detailCell[tile[0]*0x59]&0xF;
        // unassigned tiles carry tile[0]=0 whose cell is all-0x0F = solid). The expected map
        // below is that live extraction, byte-for-byte. If this ever diverges, fog rays start
        // dying/passing in the wrong places and the meadow reveal changes shape.
        static readonly string[] LiveMeadowFill =
        {   // x 215..265, y 26..36 — '#'=15, '.'=0, hex digit otherwise
            "#####ca886............................688ad########",
            "#######ed95..........................5ae###########",
            "#########b43........................35c############",
            "#########e93........................37bbbbbb#######",
            "###########4........................59bbbbbb#######",
            "##########################################bb#######",
            "##########################################bb#######",
            "##########################################bb#######",
            "###################################################",
            "###################################################",
            "###################################################",
        };

        [Test]
        public void FogFillAt_MeadowRect_MatchesLiveOriginalDump()
        {
            var rootGo = new GameObject("WorldRoot");
            rootGo.transform.localRotation = Quaternion.Euler(270f, 0f, 0f);
            try
            {
                var engine = new DigglesEngine();
                engine.Build(DataRoot, rootGo.transform, "BlockDB2.BDB");
                engine.Interp.Eval("map create 512 640");
                engine.Interp.Eval("set temppos {208 0}");
                try { engine.Interp.Eval("call templates/tcl/urw_unq_start.tcl"); }
                catch (System.Exception e) { TestContext.WriteLine($"[template] {e.Message}"); }
                engine.Map.EmitTerrain(rootGo);
                var terr = rootGo.GetComponent<Diggles.Runtime.World.DiggableTerrain>()!;
                var mismatches = new System.Text.StringBuilder();
                for (int ty = 26; ty <= 36; ty++)
                {
                    var row = new System.Text.StringBuilder();
                    for (int tx = 215; tx <= 265; tx++)
                    {
                        int f = terr.FogFillAt(tx, ty);
                        row.Append(f == 15 ? '#' : f == 0 ? '.' : "0123456789abcdef"[f & 0xF]);
                    }
                    string got = row.ToString(), want = LiveMeadowFill[ty - 26];
                    TestContext.WriteLine($"{ty,3} {got}");
                    if (got != want)
                        mismatches.AppendLine($"y={ty}:\n  port {got}\n  live {want}");
                }
                Assert.That(mismatches.Length, Is.EqualTo(0),
                    "FogFillAt diverged from the live-original collision probe:\n" + mismatches);
            }
            finally { Object.DestroyImmediate(rootGo); }
        }

        // The real path: `+sq_object summon Feuerstelle DropPos` executed by the live sequencer
        // mid-cutscene (sequence mode on, `new` runs inside the trigger's object scope) — the
        // urw_3 campfire summon in miniature. The summoned building must come out built with its
        // tttfow reveal sizes, exactly like a bare `new`.
        [Test]
        public void SequencerSummon_ProductionObject_ComesOutBuiltWithFow()
        {
            var rootGo = new GameObject("WorldRoot");
            rootGo.transform.localRotation = Quaternion.Euler(270f, 0f, 0f);
            string seqPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"dgl_fowsummon_{System.DateTime.Now.Ticks}.tcl").Replace('\\', '/');
            try
            {
                var engine = new DigglesEngine();
                engine.Build(DataRoot, rootGo.transform, "BlockDB2.BDB");
                var ctx = engine.Ctx;

                engine.Interp.Eval("call scripts/classes/work/feuerstelle.tcl");
                engine.Interp.Eval(
                    "def_class ProbeSeq none dummy 0 {} {\n" +
                    "  call scripts/classes/story/sequencer.tcl\n" +
                    "  obj_init { call scripts/classes/story/sequencer.tcl }\n" +
                    "  method run {} { sequencer_activate }\n" +
                    "}");
                System.IO.File.WriteAllText(seqPath,
                    "+sq_pen set DropPos TriggerPos\n" +
                    "do_wait time 1\n" +
                    "+sq_object summon Feuerstelle DropPos\n" +
                    "do_wait time 2\n");

                engine.Interp.Eval("sel /obj");
                int trig = int.Parse(engine.Interp.Eval("new ProbeSeq \"\" \"246 30 9\" \"0 0 0\"").Trim());
                engine.Interp.Eval($"call_method {trig} set_sequencescript {{{seqPath}}}");
                engine.Interp.Eval($"call_method {trig} run");
                Assert.IsTrue(ctx.SequenceActive, "probe cutscene must engage sequence mode");

                DigglesTclEntity? cf = null;
                for (int i = 0; i < 100 && cf == null; i++)
                {
                    engine.Sim.Step(0.1);
                    foreach (var e in ctx.Entities.Values)
                        if (e.Class?.Name == "Feuerstelle") cf = e;
                }
                Assert.IsNotNull(cf, "the sequencer must summon the Feuerstelle");
                TestContext.WriteLine($"[seq-summoned] built={cf!.BuildupComplete} fow={cf.FowSizeX}x{cf.FowSizeY} " +
                                      $"owner={cf.Owner} pos={cf.Position}");

                Assert.That(cf.Owner, Is.EqualTo(0), "summon owner (sequencer.tcl:883, opt3 default 0)");
                Assert.IsTrue(cf.BuildupComplete,
                    "a sequencer-summoned production object must be BUILT (genericprod.tcl:451 runs in its " +
                    "obj_init) — unbuilt is skipped by the sim's type-5 fog-reveal gate → the urw_3 campfire " +
                    "never reveals → the camp floor re-fogs when the gnomes leave (playtest 07-11)");
                Assert.That(cf.FowSizeX, Is.EqualTo(15 * 8),
                    "the summoned campfire must carry tttfow 15×15 (genericprod.tcl:486, techtreetunes.tcl:38)");
            }
            finally
            {
                try { System.IO.File.Delete(seqPath); } catch { }
                Object.DestroyImmediate(rootGo);
            }
        }

        // The skip variant: ESC raises the break flag before the summon line is reached, so the
        // sequencer's drain (`set is_breaked 1; while {[parse_next 1]} {}`, sequencer.tcl:228-239)
        // executes `+sq_object summon` in a burst. The urw_3 campfire must still come out built —
        // this is exactly how a player who skips the intro gets the camp campfire.
        [Test]
        public void SequencerSummon_DuringSkipDrain_ComesOutBuiltWithFow()
        {
            var rootGo = new GameObject("WorldRoot");
            rootGo.transform.localRotation = Quaternion.Euler(270f, 0f, 0f);
            string seqPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"dgl_fowskip_{System.DateTime.Now.Ticks}.tcl").Replace('\\', '/');
            try
            {
                var engine = new DigglesEngine();
                engine.Build(DataRoot, rootGo.transform, "BlockDB2.BDB");
                var ctx = engine.Ctx;

                engine.Interp.Eval("call scripts/classes/work/feuerstelle.tcl");
                engine.Interp.Eval(
                    "def_class ProbeSeq2 none dummy 0 {} {\n" +
                    "  call scripts/classes/story/sequencer.tcl\n" +
                    "  obj_init { call scripts/classes/story/sequencer.tcl }\n" +
                    "  method run {} { sequencer_activate }\n" +
                    "}");
                // A long wait first so the break lands before the summon executes.
                System.IO.File.WriteAllText(seqPath,
                    "+sq_pen set DropPos TriggerPos\n" +
                    "do_wait time 30\n" +
                    "+sq_object summon Feuerstelle DropPos\n" +
                    "do_wait time 2\n");

                engine.Interp.Eval("sel /obj");
                int trig = int.Parse(engine.Interp.Eval("new ProbeSeq2 \"\" \"246 30 9\" \"0 0 0\"").Trim());
                engine.Interp.Eval($"call_method {trig} set_sequencescript {{{seqPath}}}");
                engine.Interp.Eval($"call_method {trig} run");
                Assert.IsTrue(ctx.SequenceActive, "probe cutscene must engage sequence mode");
                for (int i = 0; i < 10; i++) engine.Sim.Step(0.1);   // inside the do_wait 30

                // ESC: the faithful skip (DigglesWorldRuntime.Update:900-903).
                ctx.SequenceBreaked = true;
                int guard = 0;
                while (ctx.SequenceActive && guard++ < 600) engine.Sim.Step(0.1);
                TestContext.WriteLine($"drain finished in {guard} steps; sequenceActive={ctx.SequenceActive}");

                DigglesTclEntity? cf = null;
                foreach (var e in ctx.Entities.Values)
                    if (e.Class?.Name == "Feuerstelle") cf = e;
                Assert.IsNotNull(cf, "the skip drain must still execute the '+' summon line");
                TestContext.WriteLine($"[drain-summoned] built={cf!.BuildupComplete} fow={cf.FowSizeX}x{cf.FowSizeY} " +
                                      $"owner={cf.Owner} pos={cf.Position}");
                Assert.That(cf.Owner, Is.EqualTo(0), "summon owner (sequencer.tcl:883, opt3 default 0)");
                Assert.IsTrue(cf.BuildupComplete,
                    "a drain-summoned production object must be BUILT (genericprod.tcl:451) — the skip path " +
                    "must not strip the campfire of its buildupstate/fog init");
                Assert.That(cf.FowSizeX, Is.EqualTo(15 * 8),
                    "the drain-summoned campfire must carry tttfow 15×15");
            }
            finally
            {
                try { System.IO.File.Delete(seqPath); } catch { }
                Object.DestroyImmediate(rootGo);
            }
        }
    }
}
