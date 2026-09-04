#nullable enable

namespace Diggles.Tests
{
    using System.Text;
    using Diggles.Runtime;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    // round 3+ — the meadow "re-fog" report, driven through the REAL campaign
    // boot path (not the minimal FogOfWarMeadowTests harness):
    // DigglesEngine.Build → animinit/classinit/lginit (DigglesWorldRuntime.LoadRealClasses)
    // → "obj_clear; sel /obj; set sm [new StoryMgr]; call_method $sm load_level Campaign"
    // (RunCampaign:997-1001) → EmitTerrain → FogOfWarState wiring (RunWorldScript:1030-1040).
    //
    // VERDICT of the investigation (this file pins it as regression contracts):
    // • The Campaign.tcl:29 FogRemover ({239 14 15}, fog_remove 0 46 22) SURVIVES the
    // full real boot + the urw_3 intro + the ESC-skip drain, keeps owner 0 / 368×176, and
    // holds the camp funnel ≥0xE3 forever — all six suspected boot-order breakages ruled out.
    // • The port's fog SIM is byte-faithful to the ORIGINAL, proven against the live memory
    // the meadow memory dump: the ray-collision fill probe
    // (FogFillAt vs live `detailCell[tile[0]*0x59]&0xF`, unassigned tile[0]=0 → cell0 =
    // all-0x0F) matches BYTE-FOR-BYTE over the meadow rect, and the live fog lane 0 shows
    // the SAME lit funnel (x≈221..256, rows 0..32) with black meadow edges and black
    // below the floor — the original genuinely leaves those dark.
    // • The urw_3-summoned campfire is NOT a camp revealer: the script itself boxes it
    // (urw_3.tcl:29 summon → packtobox), which clears its fog sizes.
    public sealed class FogOfWarCampaignBootTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

        [Test]
        public void RealCampaignBoot_FogRemover_KeepsMeadowLit_AfterGnomesLeave()
        {
            UnityEngine.Random.InitState(20260711);   // pins irandom (first_level layout picks)
            var rootGo = new GameObject("WorldRoot");
            rootGo.transform.localRotation = Quaternion.Euler(270f, 0f, 0f);
            try
            {
                var engine = new DigglesEngine();
                engine.Build(DataRoot, rootGo.transform, "BlockDB2.BDB");
                var ctx = engine.Ctx;
                ctx.IsStoryMgr = true;   // campaign-shaped (DigglesWorldRuntime.NewGame:277)

                // LoadRealClasses (DigglesWorldRuntime:1191) minus the audio inits.
                RunInit(engine, "scripts/init/animinit.tcl");
                RunInit(engine, "scripts/init/classinit.tcl");
                RunInit(engine, "scripts/init/lginit.tcl");
                Assert.IsTrue(ctx.Classes.ContainsKey("StoryMgr"), "classinit must define StoryMgr");
                Assert.IsTrue(ctx.Classes.ContainsKey("FogRemover"), "classinit must define FogRemover");

                // THE REAL BOOT CHAIN (RunCampaign:1000) — CampaignInit.tcl + Campaign.tcl run
                // inside the StoryMgr object context; Campaign.tcl itself does map create,
                // urw_unq_start, first_level worldgen, the Wuker cleanup and the FR drop.
                engine.Interp.Eval("obj_clear\nsel /obj\nset sm [new StoryMgr]\ncall_method $sm load_level Campaign");

                engine.Map.EmitTerrain(rootGo);
                var terr = rootGo.GetComponent<DiggableTerrain>();
                Assert.IsNotNull(terr, "no DiggableTerrain after EmitTerrain");

                // RunWorldScript's fog wiring (DigglesWorldRuntime:1024-1040). LocalPlayer is
                // deliberately NOT set — Play leaves the ctx default (0).
                ctx.Terrain = terr;
                ctx.FogOfWar = new FogOfWarState(terr!.Width, terr.Height, terr.FogFillAt)
                {
                    DigNibbleAt = terr.RawDigNibble,
                    ClearDigNibble = terr.ClearDigMark,
                };

                // ---- the FogRemover after the REAL boot ----
                DigglesTclEntity? fr = null;
                int zwerge = 0;
                foreach (var e in ctx.Entities.Values)
                {
                    if (e.Class?.Name == "FogRemover") fr = e;
                    if (e.Class?.Name == "Zwerg") zwerge++;
                }
                Assert.IsNotNull(fr, "the Campaign.tcl:206 FogRemover must exist after the real boot");
                var frGo = fr!.GameObject;
                TestContext.WriteLine(
                    $"FR#{fr.Handle}: pos={fr.Position} owner={fr.Owner} fow={fr.FowSizeX}x{fr.FowSizeY} " +
                    $"contained={fr.ContainedIn} go={(frGo == null ? "<none>" : frGo.name)} " +
                    $"parentIsRoot={(frGo != null && frGo.transform.parent == rootGo.transform)} " +
                    $"| zwerge={zwerge} entities={ctx.Entities.Count} localPlayer={ctx.LocalPlayer}");

                // Investigation #2: the fog_remove method must have left Owner=0 / sizes ×8.
                Assert.That(fr.Owner, Is.EqualTo(0), "fog_remove does set_owner this 0 (infoobjs.tcl:969)");
                Assert.That(fr.FowSizeX, Is.EqualTo(46 * 8), "fog_remove stored xrange*8 (FUN_0068a980)");
                Assert.That(fr.FowSizeY, Is.EqualTo(22 * 8), "fog_remove stored yrange*8");
                Assert.That(fr.ContainedIn, Is.LessThan(0), "the FR is not packed");
                if (frGo != null)
                    Assert.That(frGo.transform.parent, Is.EqualTo(rootGo.transform),
                        "the FR GameObject must be root-parented or the reveal loop skips it (DigglesGameSim:235)");

                // ---- pre-roll: ~5 s live like Play (12 fog updates incl. the FR's mod-6 slot) ----
                for (int i = 0; i < 48; i++) engine.Sim.Step(0.1);

                Assert.That(fr.Owner, Is.EqualTo(0), "FR owner must survive the live pre-roll");
                Assert.That(fr.FowSizeX, Is.EqualTo(46 * 8), "FR fow size must survive the live pre-roll");
                Assert.That(ctx.FogOfWar.FogAt(239, 14, 0), Is.GreaterThanOrEqualTo(0xE3),
                    "the FR center tile must be lit after the pre-roll");

                int blackNow = CountDarkMeadowColumns(ctx.FogOfWar, terr, out var totalNow);
                TestContext.WriteLine($"after pre-roll: {blackNow}/{totalNow} dark meadow columns");

                // ---- "everyone leaves": teleport every gnome far off the meadow ----
                foreach (var e in ctx.Entities.Values)
                    if (e.Class?.Name == "Zwerg")
                        engine.Interp.Eval($"set_pos {e.Handle} {{60 60 8}}");

                // ---- 100 fog updates with no gnome near the camp ----
                for (int i = 0; i < 400; i++) engine.Sim.Step(0.1);

                Assert.That(fr.Owner, Is.EqualTo(0), "FR owner must survive the long run");
                Assert.That(fr.FowSizeX, Is.EqualTo(46 * 8),
                    "FR fow size must survive the long run (evt_timer0 / story code must not clobber it)");
                Assert.IsTrue(ctx.Entities.ContainsKey(fr.Handle), "the undeletable FR must not be deleted");

                DumpFog(ctx.FogOfWar, terr, 193, 0, 285, 44);
                Assert.That(ctx.FogOfWar.FogAt(239, 14, 0), Is.GreaterThanOrEqualTo(0xE3),
                    "the FR center tile must STAY lit with no gnomes around");
                int black = CountDarkMeadowColumns(ctx.FogOfWar, terr, out var total);
                TestContext.WriteLine($"after long run: {black}/{total} dark meadow columns");
                Assert.That(black, Is.EqualTo(0),
                    "no meadow surface column inside the FR's reach may fade to gray/black " +
                    "(the Play re-fog symptom)");
            }
            finally
            {
                Object.DestroyImmediate(rootGo);
            }
        }

        // FULL PLAY TIMELINE headlessly: real boot → live pre-roll → the REAL Trigger_Urw_006
        // fires urw_3 through the sequencer → ESC-skip (SequenceBreaked drain) → gnomes leave →
        // 100 fog updates. Presenter + overlay are pumped every step so the RENDER lane
        // (display texels / black covers) is asserted too, not just the sim bytes.
        [Test]
        public void RealCampaignBoot_WithIntroCutsceneAndSkip_MeadowStaysLit()
        {
            UnityEngine.Random.InitState(20260712);
            var rootGo = new GameObject("WorldRoot");
            rootGo.transform.localRotation = Quaternion.Euler(270f, 0f, 0f);
            try
            {
                var engine = new DigglesEngine();
                engine.Build(DataRoot, rootGo.transform, "BlockDB2.BDB");
                var ctx = engine.Ctx;
                ctx.IsStoryMgr = true;
                RunInit(engine, "scripts/init/animinit.tcl");
                RunInit(engine, "scripts/init/classinit.tcl");
                RunInit(engine, "scripts/init/lginit.tcl");
                engine.Interp.Eval("obj_clear\nsel /obj\nset sm [new StoryMgr]\ncall_method $sm load_level Campaign");
                engine.Map.EmitTerrain(rootGo);
                var terr = rootGo.GetComponent<DiggableTerrain>();
                Assert.IsNotNull(terr, "no DiggableTerrain after EmitTerrain");
                ctx.Terrain = terr;
                ctx.FogOfWar = new FogOfWarState(terr!.Width, terr.Height, terr.FogFillAt)
                {
                    DigNibbleAt = terr.RawDigNibble,
                    ClearDigNibble = terr.ClearDigMark,
                };
                // The RENDER half, exactly as RunWorldScript wires it (DigglesWorldRuntime:1047-1055).
                var presenter = rootGo.AddComponent<FogOfWarPresenter>();
                presenter.Init(ctx.FogOfWar);
                var overlay = rootGo.AddComponent<FogOfWarOverlay>();
                overlay.Init(ctx.FogOfWar, terr.GetHmap);
                terr.TilesChanged += (a, b, c, d) => overlay.MarkRectDirty(a, b, c, d);

                DigglesTclEntity? fr = null;
                var gnomes = new System.Collections.Generic.List<DigglesTclEntity>();
                foreach (var e in ctx.Entities.Values)
                {
                    if (e.Class?.Name == "FogRemover") fr = e;
                    if (e.Class?.Name == "Zwerg") gnomes.Add(e);
                }
                Assert.IsNotNull(fr, "FogRemover exists after the real boot");
                TestContext.WriteLine($"boot: FR#{fr!.Handle} owner={fr.Owner} fow={fr.FowSizeX}x{fr.FowSizeY}, " +
                                      $"{gnomes.Count} gnomes, triggers={ctx.TriggerService?.LiveCount}");

                void StepAll(int n)
                {
                    for (int i = 0; i < n; i++)
                    {
                        engine.Sim.Step(0.1);
                        presenter.Pump(0.1f);
                        overlay.Pump();
                    }
                }

                // Pre-roll t=0..6: gnomes idle at the camp → Trigger_Urw_006's +5 s timer arms
                // the proximity trigger → urw_3 engages through the REAL chain.
                StepAll(60);
                TestContext.WriteLine($"t=6: sequenceActive={ctx.SequenceActive}");

                // Let the intro play a while, then ESC-skip it (the faithful break-flag drain).
                StepAll(100);
                if (ctx.SequenceActive)
                {
                    ctx.SequenceBreaked = true;
                    int guard = 0;
                    while (ctx.SequenceActive && guard++ < 600) StepAll(1);
                    TestContext.WriteLine($"skip drain took {guard} steps; sequenceActive={ctx.SequenceActive}");
                }

                // Post-intro live camp for a bit.
                StepAll(50);
                Assert.That(fr.Owner, Is.EqualTo(0), "FR owner must survive the intro cutscene + skip");
                Assert.That(fr.FowSizeX, Is.EqualTo(46 * 8), "FR fow size must survive the intro cutscene + skip");
                Assert.IsTrue(ctx.Entities.ContainsKey(fr.Handle), "undeletable FR must survive the cutscene skip");

                // The urw_3-summoned CAMPFIRE (`+sq_object summon Feuerstelle DropPos`, owner 0)
                // is deliberately PACKED INTO A CRATE by the script itself (urw_3.tcl:29:
                // `+sq_object summon Feuerstelle DropPos` then `+call_method [Object 0]
                // packtobox`) — the story hands the player a BOX to build, NOT a live campfire.
                // packtobox clears its fog sizes (`set_fogofwar this -1 -1`, genericprod.tcl:187)
                // and boxes it (set_boxed 1 → the built flag drops, FUN_0067dc80:157-161). So
                // after the intro the camp's ONLY permanent revealer is the FogRemover — pinned
                // here so nobody "fixes" the campfire into a revealer again (
                // investigation: that hypothesis was falsified by the script itself).
                DigglesTclEntity? campfire = null;
                foreach (var e in ctx.Entities.Values)
                {
                    // The CAMP campfire (meadow rows) — templates spawn other Feuerstellen deep
                    // in the story caves (y ≥ 40), owned by AI players.
                    if (e.Class?.Name == "Feuerstelle" && e.Position.y < 40f) campfire = e;
                    if (e.FowSizeX != -1 || e.Class?.Name == "Feuerstelle")
                        TestContext.WriteLine($"[revealer] #{e.Handle} {e.Class?.Name} kind={e.Class?.Kind} " +
                            $"owner={e.Owner} fow={e.FowSizeX}x{e.FowSizeY} built={e.BuildupComplete} " +
                            $"pos={e.Position} contained={e.ContainedIn}");
                }
                Assert.IsNotNull(campfire, "urw_3 must summon the camp Feuerstelle (sq_object summon)");
                Assert.That(campfire!.Owner, Is.EqualTo(0), "summoned campfire owner (sequencer.tcl:883 opt3=0)");
                Assert.That(campfire.FowSizeX, Is.EqualTo(-1),
                    "the campfire crate must NOT reveal fog (urw_3.tcl:209 packtobox → set_fogofwar -1 -1)");
                Assert.IsFalse(campfire.BuildupComplete,
                    "the campfire arrives BOXED (urw_3.tcl:209) — boxing clears the built flag (FUN_0067dc80:157-161)");

                // Everyone leaves the meadow.
                foreach (var g in gnomes)
                    if (ctx.Entities.ContainsKey(g.Handle))
                        engine.Interp.Eval($"set_pos {g.Handle} {{60 60 8}}");
                StepAll(400);   // 100 fog updates

                DumpFog(ctx.FogOfWar, terr, 193, 0, 285, 44);
                Assert.That(ctx.FogOfWar.FogAt(239, 14, 0), Is.GreaterThanOrEqualTo(0xE3), "FR center lit");
                int black = CountDarkMeadowColumns(ctx.FogOfWar, terr, out var total);
                TestContext.WriteLine($"meadow: {black}/{total} dark columns");
                Assert.That(black, Is.EqualTo(0), "no dark meadow columns after the full Play timeline");

                // RENDER lane: the camp surface must DISPLAY clear (r=0) and non-black (g=255).
                for (int tx = 230; tx <= 250; tx++)
                {
                    int s = FindSurface(terr, tx);
                    if (s < 0) continue;
                    var (r, g) = presenter.DisplayAt(tx, s);
                    Assert.That(g, Is.EqualTo(255), $"presenter shows BLACK at camp surface ({tx},{s})");
                    Assert.That(r, Is.LessThanOrEqualTo(8), $"presenter shows a gray veil (r={r}) at camp surface ({tx},{s})");
                }
            }
            finally
            {
                Object.DestroyImmediate(rootGo);
            }
        }

        static void RunInit(DigglesEngine engine, string rel)
        {
            try { engine.Interp.Eval("call " + rel); }
            catch (System.Exception e) { TestContext.WriteLine($"[init {rel}] {e.Message}"); }
        }

        // The meadow surface across the FR's GEOMETRIC reach (see FogOfWarMeadowTests:79-88:
        // the 46×22 ellipse from (239,12.5) covers surface row ~31 only for x ∈ ~214..264).
        static int CountDarkMeadowColumns(FogOfWarState fog, DiggableTerrain terr, out int total)
        {
            int black = 0; total = 0;
            for (int tx = 215; tx <= 263; tx++)
            {
                int surfY = FindSurface(terr, tx);
                if (surfY < 0) continue;
                total++;
                if (fog.FogAt(tx, surfY, 0) < 2) black++;
            }
            return black;
        }

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
