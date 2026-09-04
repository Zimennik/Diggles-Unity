#nullable enable

namespace Diggles.Tests.Editor
{
    using System.IO;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;

    // Covers the filesystem load-mechanics commands (glob / cd / pwd / file exists)
    // that classinit.tcl uses to enumerate the game's class files:
    // cd Data/scripts/classes/<dir>; lsort [glob -nocomplain *.tcl]; cd../../../..
    // These resolve against DigglesTclContext.DataRoot +.Cwd.
    public class TclFsCommandsTests
    {
        [Test]
        public void GlobCdFile_DriveTheClassinitEnumerationIdiom()
        {
            var interp = new TclInterpreter();
            var ctx = DigglesTclEngine.Register(interp);
            string root = Path.Combine(Path.GetTempPath(), "diggles_fs_" + System.Guid.NewGuid().ToString("N"));
            try
            {
                var cdir = Path.Combine(root, "scripts", "classes", "characters");
                Directory.CreateDirectory(cdir);
                File.WriteAllText(Path.Combine(cdir, "hamster.tcl"), "");
                File.WriteAllText(Path.Combine(cdir, "troll.tcl"), "");
                File.WriteAllText(Path.Combine(cdir, "t_methods.tcl"), "");  // include
                File.WriteAllText(Path.Combine(cdir, "notes.txt"), "");      // non-tcl, excluded
                ctx.DataRoot = root;

                // cd into the class dir (note the `Data/` prefix, case-insensitive).
                interp.Eval("cd Data/scripts/classes/characters");
                Assert.AreEqual(cdir.Replace('\\', '/'), interp.Eval("pwd"));

                // glob *.tcl returns base names only, excludes the.txt.
                var globbed = interp.Eval("lsort [glob -nocomplain \"*.tcl\"]");
                Assert.AreEqual("hamster.tcl t_methods.tcl troll.tcl", globbed);

                // cd back to the data root.
                interp.Eval("cd ../../../..");
                Assert.AreEqual(root.Replace('\\', '/'), interp.Eval("pwd"));

                // file exists resolves DataRoot-relative (with/without `data/` prefix).
                Assert.AreEqual("1", interp.Eval("file exists data/scripts/classes/characters/hamster.tcl"));
                Assert.AreEqual("1", interp.Eval("file exists scripts/classes/characters/troll.tcl"));
                Assert.AreEqual("0", interp.Eval("file exists data/scripts/classes/characters/nope.tcl"));

                // -nocomplain on a no-match pattern → empty list, no throw.
                Assert.AreEqual("", interp.Eval("glob -nocomplain \"*.xyz\""));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }
    }
}
