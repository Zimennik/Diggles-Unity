#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using System.IO;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;

    // Phase 5 — Tcl-driven map construction. Verifies map_create,
    // map_template, map_setlayer2, split_template route through to the
    // host-provided IDigglesMapBuilder with correct args.
    //
    // Uses a recording mock so tests don't need real BDB/PMP/L2M files
    // or the Editor assembly's MarkerModelResolver.
    public sealed class TclMapBuilderTests
    {
        sealed class RecordingMapBuilder : IDigglesMapBuilder
        {
            public (int w, int h)? Created;
            public List<(string path, int x, int y)> Pmps = new();
            public List<(string path, int x, int y)> L2ms = new();

            public void CreateMap(int width, int height) => Created = (width, height);
            public void LoadPmp(string p, int x, int y) => Pmps.Add((p, x, y));
            public void LoadL2m(string p, int x, int y) => L2ms.Add((p, x, y));
        }

        (TclInterpreter interp, DigglesTclContext ctx, RecordingMapBuilder mock) BuildHost()
        {
            var interp = new TclInterpreter();
            var ctx = DigglesTclEngine.Register(interp);
            var mock = new RecordingMapBuilder();
            ctx.MapBuilder = mock;
            ctx.TolerateMissingFiles = true;  // for split_template chunk sources
            return (interp, ctx, mock);
        }

        [Test]
        public void MapCreate_ForwardsWidthHeight()
        {
            var (interp, _, mock) = BuildHost();
            interp.Eval("map create 512 640");
            Assert.IsTrue(mock.Created.HasValue);
            Assert.AreEqual(512, mock.Created!.Value.w);
            Assert.AreEqual(640, mock.Created.Value.h);
        }

        [Test]
        public void SmCreateMap_AliasesMapCreate()
        {
            var (interp, _, mock) = BuildHost();
            interp.Eval("sm_create_map 256 256");
            Assert.IsTrue(mock.Created.HasValue);
            Assert.AreEqual(256, mock.Created!.Value.w);
            Assert.AreEqual(256, mock.Created.Value.h);
        }

        [Test]
        public void MapTemplate_PmpFirst_ForwardsPathAndCoords()
        {
            var (interp, _, mock) = BuildHost();
            interp.Eval("map_template hol_024.pmp 208 0");
            Assert.AreEqual(1, mock.Pmps.Count);
            Assert.AreEqual("hol_024.pmp", mock.Pmps[0].path);
            Assert.AreEqual(208, mock.Pmps[0].x);
            Assert.AreEqual(0, mock.Pmps[0].y);
        }

        [Test]
        public void MapTemplate_CoordsFirst_PmpLast()
        {
            // Rarer ordering — engine accepts both per WORLD_SPAWN_PIPELINE doc.
            var (interp, _, mock) = BuildHost();
            interp.Eval("map_template 30 23 path/to/some.pmp");
            Assert.AreEqual(1, mock.Pmps.Count);
            Assert.AreEqual("path/to/some.pmp", mock.Pmps[0].path);
            Assert.AreEqual(30, mock.Pmps[0].x);
            Assert.AreEqual(23, mock.Pmps[0].y);
        }

        [Test]
        public void MapSetLayer2_ForwardsCoordsThenPath()
        {
            var (interp, _, mock) = BuildHost();
            interp.Eval("map_setlayer2 30 23 data/templates/foo.l2m");
            Assert.AreEqual(1, mock.L2ms.Count);
            Assert.AreEqual("data/templates/foo.l2m", mock.L2ms[0].path);
            Assert.AreEqual(30, mock.L2ms[0].x);
            Assert.AreEqual(23, mock.L2ms[0].y);
        }

        [Test]
        public void SplitTemplate_LoadsPmpForEachChunk_AddingTempposOffset()
        {
            var (interp, _, mock) = BuildHost();
            interp.Eval(@"
                set temppos {208 0}
                split_template parent.tcl parent.pmp 60 36 {
                    { chunk_AA.tcl chunk_AA.pmp 0 0 }
                    { chunk_BA.tcl chunk_BA.pmp 16 0 }
                    { chunk_AB.tcl chunk_AB.pmp 0 16 }
                }
            ");
            Assert.AreEqual(3, mock.Pmps.Count);
            // Absolute coords = temppos + chunk offset.
            Assert.AreEqual(("chunk_AA.pmp", 208, 0), mock.Pmps[0]);
            Assert.AreEqual(("chunk_BA.pmp", 224, 0), mock.Pmps[1]);
            Assert.AreEqual(("chunk_AB.pmp", 208, 16), mock.Pmps[2]);
        }

        [Test]
        public void SplitTemplate_InvokesMapTemplateSetWithAbsCoords_WhenSubScriptLoadable()
        {
            // Write a fake sub-tcl that records the offset it was called
            // with into a global, then have the engine load it via the
            // chunk's source mechanism.
            var (interp, ctx, _) = BuildHost();
            string tmpRoot = Path.Combine(Path.GetTempPath(), "diggles_tcl_test_" + System.Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(tmpRoot, "templates", "tcl", "split"));
                File.WriteAllText(
                    Path.Combine(tmpRoot, "templates", "tcl", "split", "chunk_AA.tcl"),
                    "proc MapTemplateSet {xoff yoff} { global called_x called_y; set called_x $xoff; set called_y $yoff }");
                ctx.DataRoot = tmpRoot;

                interp.Eval(@"
                    set called_x -1
                    set called_y -1
                    set temppos {100 200}
                    split_template parent.tcl parent.pmp 60 36 {
                        { chunk_AA.tcl chunk_AA.pmp 5 7 }
                    }
                ");
                Assert.AreEqual("105", interp.Eval("set called_x"));
                Assert.AreEqual("207", interp.Eval("set called_y"));
            }
            finally
            {
                if (Directory.Exists(tmpRoot)) Directory.Delete(tmpRoot, recursive: true);
            }
        }

        [Test]
        public void NoBuilder_CommandsAreNoOpButDontThrow()
        {
            var interp = new TclInterpreter();
            DigglesTclEngine.Register(interp);
            // MapBuilder left null — engine should still parse these without error.
            Assert.DoesNotThrow(() => interp.Eval(@"
                map create 64 64
                map_template hol.pmp 0 0
                map_setlayer2 5 5 path.l2m
            "));
        }
    }
}
