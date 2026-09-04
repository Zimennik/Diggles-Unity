#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;

    // Tests that load REAL Diggles.tcl files from the original game and
    // run them through our interpreter. Goal: prove parser tolerates the
    // syntax and engine commands either work or get cleanly stubbed.
    //
    // These tests use the "stub unknown" handler to swallow engine
    // commands we haven't implemented yet. As we port more commands,
    // we can tighten the asserts (e.g. expect a specific class to be
    // registered, expect no stubbed calls at all, etc.).
    public sealed class TclRealFilesTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;
        static string Script(string rel) => Path.Combine(DataRoot, "Scripts", rel);

        // Each test loads a file and reports the unknown commands hit.
        // Failure on parse / runtime exception (not on stubbed commands).

        static (TclInterpreter, Dictionary<string,int>) MakeStubbingInterp()
        {
            var interp = new TclInterpreter();
            interp.TolerateExprErrors = true;
            interp.TolerateMissingVars = true;
            var ctx = DigglesTclEngine.Register(interp);
            ctx.DataRoot = DataRoot;            // enable `call scripts/...` loading
            ctx.TolerateMissingEntities = true;
            ctx.TolerateMissingClass = true;
            ctx.TolerateMissingFiles = true;    // skip missing optional includes
            var unknownCalls = new Dictionary<string, int>();
            interp.UnknownCommandHandler = (i, args) =>
            {
                if (!unknownCalls.ContainsKey(args[0])) unknownCalls[args[0]] = 0;
                unknownCalls[args[0]]++;
                return "0";
            };
            // Quiet puts
            interp.RegisterCommand("puts", (i, a) => "");

            // Common Diggles globals that scripts assume are pre-set (these
            // would normally come from animinit.tcl etc., which are stubbed
            // via call → 0).
            interp.SetGlobal("ANIM_STILL", "0");
            interp.SetGlobal("ANIM_ONCE",  "1");
            interp.SetGlobal("ANIM_LOOP",  "2");
            interp.SetGlobal("standanim",      "");
            interp.SetGlobal("standardanim",   "");
            interp.SetGlobal("standardframe",  "0");
            interp.SetGlobal("influence_pf",   "0");
            interp.SetGlobal("status",         "off");
            interp.SetGlobal("switchmode",     "");
            interp.SetGlobal("switchpos",      "0");
            interp.SetGlobal("this",           "");   // placeholder

            return (interp, unknownCalls);
        }

        static string LoadLatin1(string path)
        {
            Assert.IsTrue(File.Exists(path), $"missing file: {path}");
            return File.ReadAllText(path, Encoding.GetEncoding("ISO-8859-1"));
        }

        // ----- standalone helper scripts -----

        [Test]
        public void Init_CustomCommands_Loads()
        {
            // Defines `repn` proc using uplevel. Pure Tcl, no engine deps.
            var (i, unknown) = MakeStubbingInterp();
            i.Eval(LoadLatin1(Script("init/customcommands.tcl")));
            Assert.IsTrue(i.ProcExists("repn"), "repn proc not registered");
            Assert.AreEqual(0, unknown.Count, "no engine stubs expected for customcommands.tcl");
        }

        [Test]
        public void Init_CustomCommands_Repn_Executes()
        {
            // Repn defined; call it from another script.
            var (i, _) = MakeStubbingInterp();
            i.Eval(LoadLatin1(Script("init/customcommands.tcl")));
            // repn calls uplevel 2 — so it expects to be wrapped one layer
            // deeper. Build a small wrapper:
            int n = int.Parse(i.Eval(@"
                set n 0
                proc wrap {} {
                    repn 3 { incr n }
                }
                wrap
                set n"));
            Assert.AreEqual(3, n);
        }

        // ----- class definition files -----

        [Test]
        public void Items_Pilz_Loads_Without_Throwing()
        {
            // pilz.tcl defines class Pilz with full obj_init / methods.
            // We just check the file parses and runs end-to-end under
            // stub mode (most commands will be stubbed).
            var (i, unknown) = MakeStubbingInterp();
            string body = LoadLatin1(Script("classes/items/pilz.tcl"));
            // pilz.tcl does `call scripts/...` which our stub turns into a
            // 0 — fine for now; we just want NO exception.
            i.Eval(body);
            TestContext.WriteLine($"pilz.tcl stubbed {unknown.Count} distinct commands");
            foreach (var kv in unknown)
                TestContext.WriteLine($"  {kv.Key}: {kv.Value}");
        }

        [Test]
        public void Items_Rohstoffe_Loads_Without_Throwing()
        {
            var (i, unknown) = MakeStubbingInterp();
            string body = LoadLatin1(Script("classes/items/rohstoffe.tcl"));
            i.Eval(body);
            TestContext.WriteLine($"rohstoffe.tcl stubbed {unknown.Count} distinct commands");
        }

        // ----- whole-folder smoke test -----

        // Surveys every.tcl file across the WHOLE Scripts tree and
        // aggregates which unknown commands get called + how many times.
        // Output is the "engine command shopping list" for Phase 3.1.
        // Marked Explicit because it's slow and produces a big report.
        [Test, Explicit("Slow — surveys every .tcl, reports all unknown commands")]
        public void Survey_All_Unknown_Commands()
        {
            string root = Path.Combine(DataRoot, "Scripts");
            var files = Directory.GetFiles(root, "*.tcl", SearchOption.AllDirectories);

            var agg = new Dictionary<string, int>();
            var sample = new Dictionary<string, string>();
            int ok = 0, failed = 0;
            foreach (var f in files)
            {
                var (i, unk) = MakeStubbingInterp();
                // Override the unknown handler to also capture arg samples.
                i.UnknownCommandHandler = (interp, args) =>
                {
                    string name = args[0];
                    if (!agg.ContainsKey(name)) agg[name] = 0;
                    agg[name]++;
                    if (!sample.ContainsKey(name))
                    {
                        var sb = new StringBuilder();
                        for (int k = 0; k < args.Length && k < 6; k++)
                        {
                            if (k > 0) sb.Append(' ');
                            string a = args[k];
                            sb.Append(a.Length > 30 ? a.Substring(0, 30) + "…" : a);
                        }
                        sample[name] = sb.ToString();
                    }
                    return "0";
                };
                try { i.Eval(LoadLatin1(f)); ok++; }
                catch { failed++; }
            }
            TestContext.WriteLine($"Surveyed {files.Length} files: {ok} ok, {failed} failed");
            TestContext.WriteLine($"Distinct unknown commands: {agg.Count}");
            TestContext.WriteLine($"Total stubbed calls: {System.Linq.Enumerable.Sum(agg.Values)}");
            TestContext.WriteLine("");
            TestContext.WriteLine("Top unknown commands (by call count):");
            var sorted = new List<KeyValuePair<string,int>>(agg);
            sorted.Sort((a, b) => b.Value.CompareTo(a.Value));
            foreach (var kv in sorted)
                TestContext.WriteLine($"  {kv.Value,6} × {kv.Key,-30} e.g. ({sample[kv.Key]})");
        }

        // Loads every.tcl file in classes/ — pure parse+stub smoke test.
        // If any file throws (parser bug, unhandled language feature),
        // this fails with that filename.
        [Test, Explicit("Slow — runs hundreds of files; use to surface parser bugs")]
        public void All_Class_Scripts_Parse()
        {
            string root = Path.Combine(DataRoot, "Scripts", "classes");
            var files = Directory.GetFiles(root, "*.tcl", SearchOption.AllDirectories);
            Assert.IsNotEmpty(files);
            var failures = new List<(string file, string err)>();
            int ok = 0;
            foreach (var f in files)
            {
                var (i, _) = MakeStubbingInterp();
                try { i.Eval(LoadLatin1(f)); ok++; }
                catch (System.Exception ex)
                {
                    failures.Add((f, ex.Message));
                    TestContext.WriteLine($"-- {Path.GetFileName(f)} stack:");
                    var frames = (ex.StackTrace ?? "").Split('\n');
                    for (int k = 0; k < System.Math.Min(8, frames.Length); k++)
                        TestContext.WriteLine($"   {frames[k].Trim()}");
                }
            }
            TestContext.WriteLine($"Parsed: {ok}/{files.Length}");
            foreach (var (f, e) in failures)
                TestContext.WriteLine($"FAIL {Path.GetFileName(f)}: {e}");
            Assert.IsEmpty(failures, $"{failures.Count} files failed");
        }
    }
}
