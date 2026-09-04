#nullable enable

namespace Diggles.Tests
{
    using System.Text;
    using Diggles.Runtime;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    // The campaign start meadow must be fully and permanently lit. Ground truth:
    // Campaign.tcl:206 drops an undeletable FogRemover at {239 14 15} with
    // `fog_remove 0 46 22` (set_owner 0 + set_fogofwar 46 22 → the continuous radial reveal),
    // not set_fow_begin (line 7 is commented out). Regression: black patches on the meadow,
    // and the meadow graying out once everyone leaves. This test boots the real urw start
    // template (NavWorldRealTerrainTests harness) + the real FogRemover class and verifies
    // the reveal end-to-end, with an ASCII dump for eyeballing.
    public sealed class FogOfWarMeadowTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

        [Test]
        public void CampaignMeadow_FogRemover_KeepsTheMeadowLit()
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
                var terr = rootGo.GetComponent<DiggableTerrain>();
                Assert.IsNotNull(terr, "no DiggableTerrain after EmitTerrain");

                var ctx = engine.Ctx;
                // Ray collision = the native sub-cell(0,0) probe (FogFillAt) — the tile-nibble
                // variant reads open-topped surface tiles as solid and shadows the meadow.
                ctx.FogOfWar = new FogOfWarState(terr!.Width, terr.Height, terr.FogFillAt);
                ctx.LocalPlayer = 0;

                // The real FogRemover class + the exact Campaign.tcl:204-208 drop.
                try { engine.Interp.Eval("call scripts/classes/items/infoobjs.tcl"); }
                catch (System.Exception e) { TestContext.WriteLine($"[infoobjs] {e.Message}"); }
                engine.Interp.Eval("sel /obj");
                engine.Interp.Eval("set FR [new FogRemover]");
                engine.Interp.Eval("set_undeletable $FR 1");
                engine.Interp.Eval("set_pos $FR {239 14 15}");
                engine.Interp.Eval("call_method $FR fog_remove 0 46 22");

                DigglesTclEntity? fr = null;
                foreach (var e in ctx.Entities.Values)
                    if (e.Class?.Name == "FogRemover") fr = e;
                Assert.IsNotNull(fr, "FogRemover entity exists");
                TestContext.WriteLine($"FR: pos={fr!.Position} owner={fr.Owner} " +
                                      $"fowSize={fr.FowSizeX}x{fr.FowSizeY} contained={fr.ContainedIn}");

                // 12 fog updates (48 thinks) — plenty for the mod-6 slot + refreshes.
                for (int i = 0; i < 48; i++) engine.Sim.Step(0.1);

                DumpFog(ctx.FogOfWar, terr, 193, 0, 285, 44);

                Assert.That(fr.FowSizeX, Is.EqualTo(46 * 8), "fog_remove stored ×8");
                Assert.That(fr.Owner, Is.EqualTo(0), "fog_remove set_owner 0");
                // ≥0xE3 = fully lit for every consumer (renderer fogFactor 0, GetMaxFow
                // early-out): the byte legally oscillates 0xFF→0xEB between the revealer's
                // mod-6 refreshes.
                Assert.That(ctx.FogOfWar.FogAt(239, 14, 0), Is.GreaterThanOrEqualTo(0xE3), "center lit");
                // The meadow surface across the FogRemover's geometric reach must be lit. The
                // 46×22 ellipse from (239,12.5) dips to the surface row (~31) only while
                // 22·sqrt(1−(dx/46)²) ≥ 18.5 → |dx| ≤ ~25 → x ∈ 214..264 (the start camp);
                // meadow edges beyond that stay dark in the original too (same ellipse math)
                // until explored by walkers.
                int black = 0, total = 0;
                for (int tx = 215; tx <= 263; tx++)
                {
                    int surfY = FindSurface(terr, tx);
                    if (surfY < 0) continue;
                    total++;
                    if (ctx.FogOfWar.FogAt(tx, surfY, 0) < 2) black++;
                }
                TestContext.WriteLine($"meadow surface: {black}/{total} dark columns");
                Assert.That(black, Is.EqualTo(0), "no black/gray columns on the start meadow surface");

                // Permanence: everyone \"leaves\" (only the FogRemover exists) — 80 more fog
                // updates of fading must not gray the meadow (the FogRemover re-pumps it).
                for (int i = 0; i < 320; i++) engine.Sim.Step(0.1);
                Assert.That(ctx.FogOfWar.FogAt(239, FindSurface(terr, 239), 0), Is.GreaterThanOrEqualTo(0xE3),
                    "meadow stays effectively fully lit with no gnomes around");
            }
            finally
            {
                Object.DestroyImmediate(rootGo);
            }
        }

        // First open-air→ground transition scanning down the column (the walkable surface).
        static int FindSurface(DiggableTerrain terr, int tx)
        {
            for (int ty = 1; ty < 60; ty++)
                if (terr.GetHmap(tx, ty) >= 14) return ty - 1;   // last open tile above ground
            return -1;
        }

        static void DumpFog(FogOfWarState fog, DiggableTerrain terr, int x0, int y0, int x1, int y1)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"fog lane 0, x {x0}..{x1}, y {y0}..{y1} — '#'=solid-black '.'=open-black '±'=gray 'o'=fading 'O'=lit");
            for (int ty = y0; ty <= y1; ty++)
            {
                sb.Append($"{ty,3} ");
                for (int tx = x0; tx <= x1; tx++)
                {
                    byte b = fog.FogAt(tx, ty, 0);
                    bool solid = terr.GetHmap(tx, ty) >= 14;
                    sb.Append(b == 0 ? (solid ? '#' : '.')
                            : b == 1 ? '±'
                            : b >= 0xE3 ? 'O' : 'o');
                }
                sb.AppendLine();
            }
            TestContext.WriteLine(sb.ToString());
        }
    }
}
