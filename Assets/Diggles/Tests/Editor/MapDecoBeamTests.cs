#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using Diggles.Runtime;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    // The during-dig template-deco pass (CMapBeautifier — tunnel beams/supports/treasure/critters),
    // the sibling of the cave-skin completion swap. Ground truth (RE via decompile; full law in
    // Docs/DIGGING_MECHANIC.md §9.B):
    // • ScanAtPos (FUN_0058b130, from DigApply:112-118 before each carve): skip inside an
    // existing beam rect; else match the "actively dug" nibble field ((n&8)==0 && n!=0 —
    // the mode-1 dummy marks the standard tunnel order and the worldgen growth paint)
    // against the lg template registry (beautifier+0x1a4): templates not _hol_, not split,
    // with a deco set, zone == band(y), W*H ≤ 16, shape (PMP blocks whose aux hmap[0x1b]
    // nibble == 11) exactly equal to the local grid. Match → 4-aligned rect + queue the
    // authored deco (rect origin + template-relative offsets) as pending entries.
    // • Pending tick (FUN_0058d370, once per SrvDoThink): one entry per call — kept pending
    // while any dig nibble remains in its bbox ±2 area (native bbox = the class's 3DB
    // anim-frame box; port stand-in = the owning beam rect); then one fit evaluation (8 floor
    // probes: 3 ≤ #(hmap ≤ z) ≤ 7 and all hmap > 10 = the tunnel band) → spawn or drop.
    // • A random existing rect is retired per ScanAtPos once its ±4 area holds no nibble.
    // • Nothing here removes live objects (the native's only remover is the cave-skin +0xcc2
    // sweep). Port deviation: CaveSkinDigCheck also purges MapDecoSpawned deco inside a
    // completed cave rect.
    // • Spawned deco is not selectable/hoverable: native flags default off and dummy classes
    // never opt in (animinit.tcl SetFrontDummyClassesNoPhys — "keine obj_init"); the port's
    // opt-out model applies _selectable/_hoverable = 0 at MapDecoSpawn.
    // Bare all-solid terrain (bdb=null ⇒ tile hmap 15) + synthetic templates/shapes; two
    // real-data tests pin the.tcl deco parse and the PMP-shape law against original/data.
    public sealed class MapDecoBeamTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;
        readonly List<GameObject> _gos = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _gos) if (go != null) Object.DestroyImmediate(go);
            _gos.Clear();
        }

        [Test]
        public void SaveSeedLod_EnrolsInLiveDecoCullSet_AndObeysGate()
        {
            var liveDeco = new DigglesTclEntity { MapDecoSpawned = true };
            var saveSeed = new DigglesTclEntity
            {
                MapDecoSpawned = true,
                MapDecoSaveSeed = true,
                Position = new Vector3(10f, 12f, 0f),
            };
            var live = new List<DigglesTclEntity> { liveDeco };
            var seeds = new List<DigglesTclEntity> { saveSeed };
            var cullSet = new List<DigglesTclEntity>();

            MapDecoPresentationCull.FillCullSet(live, seeds, cullSet);

            Assert.That(cullSet, Has.Count.EqualTo(2));
            Assert.That(cullSet[0], Is.SameAs(liveDeco));
            Assert.That(cullSet[1], Is.SameAs(saveSeed));
            Assert.That(MapDecoPresentationCull.ShouldMaterialize(
                saveSeed.Position,
                2,
                2,
                new Vector2(10f, 12f),
                false), Is.True);
            Assert.That(MapDecoPresentationCull.ShouldMaterialize(
                saveSeed.Position,
                2,
                1,
                new Vector2(10f, 12f),
                false), Is.False);
            Assert.That(MapDecoPresentationCull.ShouldMaterialize(
                saveSeed.Position,
                0,
                2,
                new Vector2(10f + MapDecoPresentationCull.SeedRenderDistance + 1f, 12f),
                false), Is.False);
            Assert.That(MapDecoPresentationCull.ShouldMaterialize(
                saveSeed.Position,
                0,
                2,
                new Vector2(10f, 12f),
                true), Is.False);
        }

        DiggableTerrain MakeTerrain(int w, int h)
        {
            var go = new GameObject("MapDecoTerrain");
            _gos.Add(go);
            var t = go.AddComponent<DiggableTerrain>();
            t.Init(new BdbPmpWorldBuilder.TclWorldResult
            {
                Width = w, Height = h,
                BlockIdx = new uint[w * h],   // all block 0, bdb=null ⇒ tile height 15 (solid)
                Terrain = new byte[w * h],    // byte 0 ⇒ material default 2 (earth, diggable)
            }, null!, "", () => { });
            return t;
        }

        (TclInterpreter interp, DigglesTclContext ctx, DiggableTerrain terr, MapDecoBeautifier md) Host(int w = 64, int h = 64)
        {
            var interp = new TclInterpreter { TolerateExprErrors = true, TolerateMissingVars = true };
            interp.UnknownCommandHandler = (i, a) => "0";
            var ctx = DigglesTclEngine.Register(interp);
            ctx.TolerateMissingEntities = true;
            ctx.StoryRng = new System.Random(42);
            ctx.Terrain = MakeTerrain(w, h);
            ctx.LevelGen = new LevelGenerator { DataRoot = DataRoot };
            // Zone bands (CampaignInit shape): band 0 covers everything while no bound is set.
            foreach (var z in new[] { "Urwald", "Metall", "Kristall", "Lava" })
                interp.Eval($"sm_add_zone {z}");
            var md = DigglesTclEngine.MapDecoOf(ctx)!;
            Assert.IsNotNull(md, "beautifier must lazy-create once Terrain+LevelGen exist");
            return (interp, ctx, ctx.Terrain, md);
        }

        // A synthetic 4×4 tunnel template: the 2-wide horizontal corridor through rows 1-2
        // (the real gng_001 footprint) + a deco set. Shape injected by PMP base.
        static LevelGenerator.Piece DefTunnelTemplate(LevelGenerator lg, MapDecoBeautifier md,
            string name, int zone, params (string cls, float x, float y, float z, int detail)[] deco)
        {
            var p = lg.AddSyntheticDecoPiece(name, 4, 4, zone, deco);
            md.InjectShape(name, new[]
            {
                0, 0, 0, 0,
                1, 1, 1, 1,
                1, 1, 1, 1,
                0, 0, 0, 0,
            });
            return p;
        }

        // Paint the standard-tunnel dummy marks (mode-1 nibble 3 — the kind ScanAtPos matches)
        // over a 2-wide horizontal corridor [x0..x1] × [y0..y0+1], owner 0.
        static void PaintCorridor(DiggableTerrain t, int x0, int x1, int y0)
        {
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y0 + 1; y++)
                    t.SetDigMark(x, y, 0, 0, dummy: true);
        }

        static void ClearCorridor(DiggableTerrain t, int x0, int x1, int y0)
        {
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y0 + 1; y++)
                    t.ClearDigMark(x, y, 0);
        }

        static List<DigglesTclEntity> EntitiesOf(DigglesTclContext ctx, string cls)
            => ctx.Entities.Values.Where(e => e.Class?.Name == cls).ToList();

        // ---------------- ScanAtPos: match → rect + pending queue ----------------

        [Test]
        public void ScanAtPos_DummyTunnel_MatchesTemplate_RegistersRectAndQueuesDeco()
        {
            var (_, _, terr, md) = Host();
            DefTunnelTemplate(md.Templates!, md, "beam_t", 0,
                ("Dummy_Balken_T", 1.5f, 1.5f, 15.04f, 2),
                ("Schatz_T", 3.0f, 2.5f, 15f, 0));

            PaintCorridor(terr, 20, 27, 21);          // marks rows 21-22, cols 20..27
            md.ScanAtPos(21, 21, 0);                  // the dig node (nibble 3 = eligible)

            Assert.AreEqual(1, md.Rects.Count, "one beam rect per match");
            var r = md.Rects[0];
            // First matching placement = abs (20,20); rect edges (v+2)&~3 (FUN_0058b130:268-273).
            Assert.AreEqual((20, 20, 24, 24), (r.X0, r.Y0, r.X1, r.Y1), "4-aligned rect");
            Assert.AreEqual(0, r.Layer);

            Assert.AreEqual(2, md.Pending.Count, "the whole deco set queues");
            var beam = md.Pending.First(p => p.ClassName == "Dummy_Balken_T");
            // pos = rect origin + the authored template-relative offset (FUN_0058c920:140-144).
            Assert.AreEqual(21.5f, beam.Px, 1e-4f);
            Assert.AreEqual(21.5f, beam.Py, 1e-4f);
            Assert.AreEqual(15.04f, beam.Pz, 1e-4f);
            Assert.AreEqual(2, beam.Detail);
            var loot = md.Pending.First(p => p.ClassName == "Schatz_T");
            Assert.AreEqual(23.0f, loot.Px, 1e-4f);
            Assert.AreEqual(22.5f, loot.Py, 1e-4f);
        }

        [Test]
        public void ScanAtPos_InsideExistingRect_SkipsEntirely_ButNextWindowTilesOn()
        {
            var (_, _, terr, md) = Host();
            DefTunnelTemplate(md.Templates!, md, "beam_t", 0, ("Dummy_Balken_T", 1.5f, 1.5f, 15.04f, 2));

            PaintCorridor(terr, 20, 27, 21);
            md.ScanAtPos(21, 21, 0);
            Assert.AreEqual(1, md.Rects.Count);
            Assert.AreEqual(1, md.Pending.Count);

            // Node inside the rect (inclusive bounds,:15) → nothing new.
            md.ScanAtPos(22, 22, 0);
            Assert.AreEqual(1, md.Rects.Count, "inside-rect scan must not re-match");
            Assert.AreEqual(1, md.Pending.Count);

            // Node past the rect's X1 (25 > 24) → the corridor's next 4×4 window matches,
            // tiling a second rect along the tunnel.
            md.ScanAtPos(25, 21, 0);
            Assert.AreEqual(2, md.Rects.Count, "the dig front tiles new rects along the corridor");
        }

        [Test]
        public void ScanAtPos_ZoneMismatch_NoMatch()
        {
            var (_, _, terr, md) = Host();
            // kris-band template (zone 2) vs a dig in band 0.
            DefTunnelTemplate(md.Templates!, md, "kris_beam_t", 2, ("Dummy_Balken_T", 1.5f, 1.5f, 15.04f, 2));

            PaintCorridor(terr, 20, 27, 21);
            md.ScanAtPos(21, 21, 0);
            Assert.AreEqual(0, md.Rects.Count, "zone-mismatched templates never match (+0x4c gate)");
        }

        [Test]
        public void ScanAtPos_ModeZeroPlayerMarks_NotEligible()
        {
            var (_, _, terr, md) = Host();
            DefTunnelTemplate(md.Templates!, md, "beam_t", 0, ("Dummy_Balken_T", 1.5f, 1.5f, 15.04f, 2));

            // Depth-tool marks are mode-0 (cnt|8 → bit 3 set) — the matcher's field predicate
            // (n&8)==0 excludes them: only dummy (standard tunnel / worldgen) digs get deco.
            for (int x = 20; x <= 27; x++)
                for (int y = 21; y <= 22; y++)
                    terr.SetDigMark(x, y, 0, 0, dummy: false);
            md.ScanAtPos(21, 21, 0);
            Assert.AreEqual(0, md.Rects.Count, "mode-0 (cnt|8) marks are not the dummy band");
        }

        // ---------------- the pending tick ----------------

        [Test]
        public void Tick_KeepsPendingWhileMarksRemain_ThenSpawnsOverTunnelFloor()
        {
            var (interp, ctx, terr, md) = Host();
            interp.Eval("def_class Dummy_Balken_T none dummy 0 {} {}");
            interp.Eval("def_class Schatz_T none dummy 0 {} {}");
            DefTunnelTemplate(md.Templates!, md, "beam_t", 0,
                ("Dummy_Balken_T", 1.5f, 1.5f, 15.04f, 2),
                ("Schatz_T", 3.0f, 2.5f, 15f, 0));

            PaintCorridor(terr, 20, 27, 21);
            md.ScanAtPos(21, 21, 0);
            Assert.AreEqual(2, md.Pending.Count);

            // Marks still present in the ±2 area → every tick keeps the entries pending.
            for (int i = 0; i < 8; i++) DigglesTclEngine.MapDecoTick(ctx);
            Assert.AreEqual(2, md.Pending.Count, "dirty area must keep entries pending");
            Assert.AreEqual(0, EntitiesOf(ctx, "Dummy_Balken_T").Count);

            // Dig finished (nibbles cleared) → each tick evaluates one entry; bare terrain floor
            // = 15 (tunnel band, >10): top probes pass, bottom fail ⇒ count 4 ⇒ spawn.
            ClearCorridor(terr, 20, 27, 21);
            for (int i = 0; i < 4; i++) DigglesTclEngine.MapDecoTick(ctx);
            Assert.AreEqual(0, md.Pending.Count, "entries evaluate once and leave the queue");

            var beams = EntitiesOf(ctx, "Dummy_Balken_T");
            Assert.AreEqual(1, beams.Count, "the support must spawn behind the digger");
            Assert.AreEqual(21.5f, beams[0].Position.x, 1e-3f);
            Assert.AreEqual(21.5f, beams[0].Position.y, 1e-3f);
            Assert.AreEqual(15.04f, beams[0].Position.z, 1e-3f);
            Assert.AreEqual(-1, beams[0].Owner, "template deco is ownerless (set_owner -1)");
            Assert.AreEqual(1, EntitiesOf(ctx, "Schatz_T").Count, "the tunnel treasure spawns too");
            // Native deco classes never opt into the (default-off) selectable/hoverable flags
            // (animinit.tcl dummy generators have no obj_init); the port's opt-out equivalent
            // must be applied at spawn or the beam hover-auras and eats clicks.
            Assert.AreEqual("0", beams[0].VarsScope.Vars["_selectable"], "deco is not selectable");
            Assert.AreEqual("0", beams[0].VarsScope.Vars["_hoverable"], "deco is not hoverable");
            Assert.IsTrue(beams[0].MapDecoSpawned, "spawned deco carries the beautifier tag");
        }

        // The area-clear gate spans the whole beam rect (the port's stand-in for the native
        // 3DB-bbox min/max corners, FUN_0058d370:65-69) — a realistic west→east progressive
        // dig must not spawn the support while the rest of its template section is still
        // being dug (the old centre±0.5 box read a 5×5 window as "clear" and spawned supports
        // mid-dig beside the digger).
        [Test]
        public void Tick_AreaGate_KeepsPendingWhileLocalMarksRemain()
        {
            var (interp, ctx, terr, md) = Host();
            interp.Eval("def_class Dummy_Balken_T none dummy 0 {} {}");
            DefTunnelTemplate(md.Templates!, md, "beam_t", 0, ("Dummy_Balken_T", 1.5f, 1.5f, 15.04f, 2));

            PaintCorridor(terr, 20, 27, 21);
            md.ScanAtPos(21, 21, 0);
            Assert.AreEqual(1, md.Pending.Count);

            // The digger has cleared x 20..23; x 24..27 still marked. The v1 centre±(0.5+2)
            // window read clear here and spawned mid-dig — but the piece-local box
            // ±(DecoExt+2) = tiles [18..25] still holds the x=24/25 marks: must stay pending.
            ClearCorridor(terr, 20, 23, 21);
            for (int i = 0; i < 6; i++) DigglesTclEngine.MapDecoTick(ctx);
            Assert.AreEqual(1, md.Pending.Count, "mid-dig: entry must stay pending (rect area dirty)");
            Assert.AreEqual(0, EntitiesOf(ctx, "Dummy_Balken_T").Count, "no support beside the digger");

            // Two more tiles dug (24,25 clear) → the piece-local box [18..25] is clean
            // (26,27 lie outside it — the far end may still be in progress).
            ClearCorridor(terr, 24, 25, 21);
            for (int i = 0; i < 2; i++) DigglesTclEngine.MapDecoTick(ctx);
            Assert.AreEqual(0, md.Pending.Count);
            Assert.AreEqual(1, EntitiesOf(ctx, "Dummy_Balken_T").Count,
                "the support spawns once its whole template section is dug");
        }

        // ---------------- cave-over-tunnel purge ----------------

        // A cave dug over a finished (beamed) tunnel must sweep the tunnel deco out of the
        // completed cave rect. Documented port deviation: the native sweep (FUN_0058ba00:319)
        // tests only class+0xcc2 cave-skin classes and leaves tunnel deco forever; the port
        // extends the same rect sweep to MapDecoSpawned entities (beams were floating mid-air
        // inside finished caves).
        [Test]
        public void CaveCompletion_PurgesSpawnedTunnelDecoInsideTheRect()
        {
            var (interp, ctx, terr, md) = Host();
            interp.Eval("def_class Dummy_Balken_T none dummy 0 {} {}");
            DefTunnelTemplate(md.Templates!, md, "beam_t", 0, ("Dummy_Balken_T", 1.5f, 1.5f, 15.04f, 2));

            // Dig the tunnel to completion → the beam spawns.
            PaintCorridor(terr, 20, 27, 21);
            md.ScanAtPos(21, 21, 0);
            ClearCorridor(terr, 20, 27, 21);
            for (int i = 0; i < 2; i++) DigglesTclEngine.MapDecoTick(ctx);
            Assert.AreEqual(1, EntitiesOf(ctx, "Dummy_Balken_T").Count, "beam up after the tunnel dig");

            // A cave order over the tunnel: its record covers the beam; the completion pass
            // (CaveSkinDigCheck after a carve, all nibbles clear ⇒ rect complete) must purge it.
            terr.AddCaveRecord(18, 18, 26, 26, 0, 0, 0);
            interp.Eval("dig_apply {21 21 10}");
            Assert.AreEqual(0, EntitiesOf(ctx, "Dummy_Balken_T").Count,
                "cave completion sweeps the stale tunnel deco out of its rect");
        }

        [Test]
        public void Tick_CaveBandFloor_DropsEntryWithoutSpawning()
        {
            var (interp, ctx, terr, md) = Host();
            interp.Eval("def_class Dummy_Balken_T none dummy 0 {} {}");
            DefTunnelTemplate(md.Templates!, md, "beam_t", 0, ("Dummy_Balken_T", 1.5f, 1.5f, 15.04f, 2));

            PaintCorridor(terr, 20, 27, 21);
            md.ScanAtPos(21, 21, 0);
            Assert.AreEqual(1, md.Pending.Count);
            ClearCorridor(terr, 20, 27, 21);

            // The floor under the probes was carved into the cave band (hmap 7 ≤ 10) —
            // the all-probes tunnel-band gate (FUN_0058d370: hmap > 10) rejects the spawn.
            md.HmapSampler = (x, y) => 7;
            for (int i = 0; i < 2; i++) DigglesTclEngine.MapDecoTick(ctx);
            Assert.AreEqual(0, md.Pending.Count, "entry evaluates once and is dropped");
            Assert.AreEqual(0, EntitiesOf(ctx, "Dummy_Balken_T").Count, "no beam over a cave floor");
        }

        [Test]
        public void Tick_UnknownClass_DropsSilently()
        {
            var (_, ctx, terr, md) = Host();
            // not def_class'd — the class-exists spawn gate (native parse-time filter) skips it.
            DefTunnelTemplate(md.Templates!, md, "beam_t", 0, ("No_Such_Class_T", 1.5f, 1.5f, 15.04f, 2));

            PaintCorridor(terr, 20, 27, 21);
            md.ScanAtPos(21, 21, 0);
            ClearCorridor(terr, 20, 27, 21);
            for (int i = 0; i < 2; i++) DigglesTclEngine.MapDecoTick(ctx);
            Assert.AreEqual(0, md.Pending.Count);
            Assert.AreEqual(0, ctx.Entities.Values.Count(e => e.Class?.Name == "No_Such_Class_T"));
        }

        // ---------------- rect GC ----------------

        [Test]
        public void RectGC_RetiresRectOnceAreaHoldsNoMarks_AllowingRematch()
        {
            var (_, _, terr, md) = Host();
            DefTunnelTemplate(md.Templates!, md, "beam_t", 0, ("Dummy_Balken_T", 1.5f, 1.5f, 15.04f, 2));

            PaintCorridor(terr, 20, 27, 21);
            md.ScanAtPos(21, 21, 0);
            Assert.AreEqual(1, md.Rects.Count);

            // Marks gone (dig finished/erased) → a later scan's GC retires the rect
            // (FUN_0058b130:300-388: rect ±4 with no nibble at the rect's layer).
            ClearCorridor(terr, 20, 27, 21);
            md.ScanAtPos(40, 40, 0);   // far node: not eligible, not inside — GC only
            Assert.AreEqual(0, md.Rects.Count, "clean rects retire");

            // The area is re-matchable afterwards (fresh order over the same ground).
            PaintCorridor(terr, 20, 27, 21);
            md.ScanAtPos(21, 21, 0);
            Assert.AreEqual(1, md.Rects.Count, "retired area re-matches on a new dig order");
        }

        // ---------------- torch/light deco ----------------

        [Test]
        public void Tick_LightBearingDecoClass_SpawnsWithClassLight()
        {
            var (interp, ctx, terr, md) = Host();
            // A glowing deco class (the dummy_kristall.tcl shape) — class_light must ride the
            // spawned instance so the runtime light buffer (RuntimeWorldSpawner.AddLight +
            // per-frame UploadClassLights) picks it up.
            interp.Eval("def_class Kris_Glow_T none dummy 0 {} { class_light {0 0 0} 5 {1.0 0.8 0.5} }");
            Assert.IsNotNull(ctx.Classes["Kris_Glow_T"].Light, "class_light must parse in def_class");

            DefTunnelTemplate(md.Templates!, md, "beam_t", 0, ("Kris_Glow_T", 2.0f, 1.5f, 15f, 2));
            PaintCorridor(terr, 20, 27, 21);
            md.ScanAtPos(21, 21, 0);
            ClearCorridor(terr, 20, 27, 21);
            for (int i = 0; i < 2; i++) DigglesTclEngine.MapDecoTick(ctx);

            var glow = EntitiesOf(ctx, "Kris_Glow_T");
            Assert.AreEqual(1, glow.Count, "light deco spawns like any deco");
            Assert.IsNotNull(glow[0].Class!.Light, "the spawned instance carries the class light");
        }

        // ---------------- full pipeline through dig_apply ----------------

        [Test]
        public void DigApply_FullPipeline_BeamsAppearAfterTheTunnelIsDug()
        {
            var (interp, ctx, terr, md) = Host();
            interp.Eval("def_class Dummy_Balken_T none dummy 0 {} {}");
            DefTunnelTemplate(md.Templates!, md, "beam_t", 0, ("Dummy_Balken_T", 1.5f, 1.5f, 15.04f, 2));

            PaintCorridor(terr, 20, 27, 21);
            // The digger swings at every marked cell — dig_apply runs ScanAtPos before each
            // carve (FUN_006347a0:112-118), so the rect+queue must register mid-dig.
            for (int x = 20; x <= 27; x++)
                for (int y = 21; y <= 22; y++)
                    interp.Eval($"dig_apply {{{x} {y} 10}}");

            Assert.Greater(md.Rects.Count, 0, "the dig front must have matched the template");
            Assert.Greater(md.Pending.Count, 0, "the deco set must be queued mid-dig");
            Assert.AreEqual(0, EntitiesOf(ctx, "Dummy_Balken_T").Count,
                "nothing spawns while the tunnel marks remain");

            // On the bdb-less fixture CarveStep refuses (DiggableTerrain.CarveStep:573), so the
            // carve's completion sweep (which clears the dummy nibbles,:594-602) is surrogated
            // by clearing the marks directly — the same terminal state a finished dig leaves.
            ClearCorridor(terr, 20, 27, 21);

            // The sim think drains the pending queue (one entry per 10 Hz tick).
            for (int i = 0; i < 8; i++) DigglesTclEngine.MapDecoTick(ctx);
            Assert.Greater(EntitiesOf(ctx, "Dummy_Balken_T").Count, 0,
                "supports must appear once the tunnel around them is dug");
        }

        // ---------------- per-carve stranded-deco sweep ----------------

        // A cave (or Ctrl+LMB test carve, or a freehand cave with no cave_skin record) re-carves
        // a finished, beamed tunnel: the beam's tunnel-band floor is lowered to the cave band, so
        // it now floats in the opened space and must be removed. The engine wires
        // DiggableTerrain.OnCarveNode (fired after every successful carve) to MapDecoCarveSweep.
        [Test]
        public void CarveSweep_CaveReCarveOverBeamedTunnel_RemovesStrandedDeco()
        {
            var (interp, ctx, terr, md) = Host();
            interp.Eval("def_class Dummy_Balken_T none dummy 0 {} {}");
            DefTunnelTemplate(md.Templates!, md, "beam_t", 0, ("Dummy_Balken_T", 1.5f, 1.5f, 15.04f, 2));

            // Dig the tunnel to completion → the beam spawns over the tunnel-band floor (15).
            PaintCorridor(terr, 20, 27, 21);
            md.ScanAtPos(21, 21, 0);
            ClearCorridor(terr, 20, 27, 21);
            for (int i = 0; i < 2; i++) DigglesTclEngine.MapDecoTick(ctx);
            var beams = EntitiesOf(ctx, "Dummy_Balken_T");
            Assert.AreEqual(1, beams.Count, "beam spawned over the tunnel floor");
            // beam at (21.5,21.5) → RoundToInt = tile (22,22).

            // A cave is dug through the tunnel: the floor under the beam drops to the cave band.
            md.HmapSampler = (x, y) => 7;                 // hmap ≤ 10 = cave band / open
            // The engine wired the terrain hook in MapDecoOf: a carve at node (22,22) lowers the
            // four corner tiles {(21,21),(22,21),(21,22),(22,22)} — the beam's tile is among them.
            terr.OnCarveNode!.Invoke(22, 22);
            Assert.AreEqual(0, EntitiesOf(ctx, "Dummy_Balken_T").Count,
                "the cave carve strands the beam (floor now cave-band) → the sweep removes it");
        }

        // ---- the native kill law — bbox × carve rect ----
        // A wall-mounted deco piece (boards/moss) is removed when the block in front of it is
        // carved, not only when its own wall tile is carved. Ground truth: every authored
        // template deco line runs `set_physic $objhandle true`
        // (Templates/urw_gng_001_a.tcl:8/14/22/28 — boards at z=11.02 and front supports at
        // 15.04) → obj+0x3d0 |= 0x10 + a dummy record (FUN_0067fba0:141-146 → FUN_005eb470);
        // the MapValidator DUMMYDES phase (FUN_005a46d0 label 0x10 → FUN_005eb7b0:83) then
        // destroys any such objtype-dummy object whose anim bbox overlaps a carve dirty rect.
        // A wall board's bbox protrudes into the open space in front of its wall face, so
        // carving the block in front (not the wall tile itself) kills it natively — an
        // anchor-tile-only gate misses that case.
        [Test]
        public void CarveSweep_FrontBlockCarved_RemovesWallBoard_BboxLaw()
        {
            var (interp, ctx, terr, md) = Host();
            interp.Eval("def_class Dummy_Balken_T none dummy 0 {} {}");
            // The board rides the upper wall row: authored offset (1.5, 0.4) from the rect
            // origin (20,20) → pos (21.5, 20.4) → its own tile (22,20) is solid wall; its
            // model hangs over the corridor row 21 in front of the wall face.
            DefTunnelTemplate(md.Templates!, md, "beam_t", 0, ("Dummy_Balken_T", 1.5f, 0.4f, 15.04f, 2));

            var caveband = new HashSet<(int x, int y)>();
            int Sample(float x, float y)
            {
                int tx = Mathf.RoundToInt(x), ty = Mathf.RoundToInt(y);
                if (caveband.Contains((tx, ty))) return 7;
                if (tx >= 20 && tx <= 27 && ty >= 21 && ty <= 22) return 11;
                return 15;
            }
            md.HmapSampler = Sample;

            PaintCorridor(terr, 20, 27, 21);
            md.ScanAtPos(21, 21, 0);
            ClearCorridor(terr, 20, 27, 21);
            // Force-mature the pending entry regardless of probe geometry: spawn directly
            // through the engine seam (the law under test is the sweep, not the spawn fit).
            foreach (var p in md.Pending.ToList())
                if (p.ClassName == "Dummy_Balken_T") md.SpawnEntity!(p);
            var boards = EntitiesOf(ctx, "Dummy_Balken_T");
            Assert.AreEqual(1, boards.Count, "board spawned on the wall row");

            // Own tile (22,20) stays solid — the old floor law never fires; and no carve ever
            // touches the wall tile — the old anchor law never fires either.
            Assert.IsFalse(md.FloorStranded(boards[0].Position.x, boards[0].Position.y),
                "precondition: the board's own wall tile is intact");

            // A cave deepens the corridor in front of the board: node (22,22) lowers tiles
            // {21..22}×{21..22} — none of them is the board's tile or its wall row.
            foreach (var t in new[] { (21, 21), (22, 21), (21, 22), (22, 22) }) caveband.Add(t);
            terr.OnCarveNode!.Invoke(22, 22);
            Assert.AreEqual(0, EntitiesOf(ctx, "Dummy_Balken_T").Count,
                "the block in front of the wall face was carved into the cave band → " +
                "the board's protruding bbox overlaps the carve rect → removed (DUMMYDES law)");
        }

        // ---- template deco must be swept too — set_physic enrolment ----
        // The sweep above only covers MapDecoSpawned (beautifier spawns); authored template deco
        // (pieces still hanging after a dig) is enrolled instead through its own init lines:
        // `set_physic $objhandle true` (Templates/urw_gng_001_a.tcl:8/14/22/28) →
        // FUN_0067fba0:141-146 sets obj+0x3d0 |= 0x10 + creates the dummy record (FUN_005eb470) →
        // the DUMMYDES bbox kill applies. The port stores set_physic in the `_physic` entity prop —
        // the sweep must honor it for objtype-dummy classes, with the native per-object gates: not
        // link-attached (obj+0x278 == −1) and the objtype-dummy class verdict (class+0x54 == 0 → 3).
        [Test]
        public void CarveSweep_TemplateDeco_SetPhysicTrue_IsSwept_GuardsHold()
        {
            var (interp, ctx, terr, md) = Host();
            interp.Eval("def_class Dummy_Wand_T none dummy 0 {} {}");
            interp.Eval("def_class Dummy_Tool_T none tool 0 {} {}");

            var caveband = new HashSet<(int x, int y)>();
            int Sample(float x, float y)
            {
                int tx = Mathf.RoundToInt(x), ty = Mathf.RoundToInt(y);
                if (caveband.Contains((tx, ty))) return 7;
                if (tx >= 20 && tx <= 27 && ty >= 21 && ty <= 22) return 11;
                return 15;
            }
            md.HmapSampler = Sample;

            // Authored template-style placement (the exact urw_gng init shape: new +
            // set_owner −1 + set_physic true), pieces riding the upper wall row over the
            // corridor — all four bboxes overlap the carve window below.
            string a = interp.Eval("new Dummy_Wand_T \"\" \"21.5 20.4 11.02\" \"0 0 0\"").Trim();
            string b = interp.Eval("new Dummy_Wand_T \"\" \"21.7 20.4 11.02\" \"0 0 0\"").Trim();
            string c = interp.Eval("new Dummy_Wand_T \"\" \"21.3 20.4 11.02\" \"0 0 0\"").Trim();
            string d = interp.Eval("new Dummy_Tool_T \"\" \"21.6 20.4 11.02\" \"0 0 0\"").Trim();
            foreach (var hh in new[] { a, b, c, d }) interp.Eval($"set_owner {hh} -1");
            interp.Eval($"set_physic {a} true");            // enrolled — must crumble
 /* b: no set_physic → never enrolled → survives */
            interp.Eval($"set_physic {c} true");
            ctx.GetEntity(c)!.LinkedToHandle = 7;           // link-attached → exempt (+0x278)
            interp.Eval($"set_physic {d} true");            // non-dummy objtype → exempt

            // The cave carves the corridor block in front of the wall row (cave band).
            foreach (var t in new[] { (21, 21), (22, 21), (21, 22), (22, 22) }) caveband.Add(t);
            terr.OnCarveNode!.Invoke(22, 22);

            Assert.IsNull(ctx.GetEntity(a),
                "an authored set_physic'd dummy piece must be swept (the native enrolment)");
            Assert.IsNotNull(ctx.GetEntity(b), "a piece never enrolled by set_physic survives");
            Assert.IsNotNull(ctx.GetEntity(c), "a link-attached piece survives (obj+0x278 == −1 gate)");
            Assert.IsNotNull(ctx.GetEntity(d), "a non-dummy objtype survives (class verdict gate)");
        }

        // ---- emerge timing — spawn tracks the dig front, not the whole rect ----
        // Native area gate (FUN_0058d370:62-101) = the entry's own bbox min/max ±2 — piece-local,
        // so deco near the start of a long order matures while the far end is still being dug. An
        // earlier rect-gate stand-in waited for the whole beam rect ±2 to clear → every piece
        // appeared only at section end.
        [Test]
        public void Tick_PieceLocalAreaGate_SpawnsWhileFarEndStillMarked()
        {
            var (interp, ctx, terr, md) = Host();
            interp.Eval("def_class Dummy_Balken_T none dummy 0 {} {}");
            // Deco near the west edge of its rect: authored offset (0.5, 1.5) → pos (20.5, 21.5).
            DefTunnelTemplate(md.Templates!, md, "beam_t", 0, ("Dummy_Balken_T", 0.5f, 1.5f, 15.04f, 2));

            PaintCorridor(terr, 20, 27, 21);
            md.ScanAtPos(21, 21, 0);
            Assert.AreEqual(1, md.Pending.Count, "one pending beam");

            // The digger has cleared the piece's whole neighbourhood (x 20..24); the order's
            // far end (x 25..27) is still marked — inside the beam rect ±2 (25 < 26), but
            // outside the piece's own box ±2 (Round(20.5+1.5)+2 = 24 < 25).
            ClearCorridor(terr, 20, 24, 21);
            for (int i = 0; i < 4; i++) DigglesTclEngine.MapDecoTick(ctx);
            Assert.AreEqual(1, EntitiesOf(ctx, "Dummy_Balken_T").Count,
                "the piece must spawn once ITS OWN ±(ext+2) area is clear — tracking the dig " +
                "front — not wait for the whole rect (native FUN_0058d370:62-101 bbox±2 gate)");
        }

        // ---- wall-mounted deco must die with its wall ----
        // An earlier sweep only tested the deco's own floor band (FloorStranded at its x/y): a
        // cave dug above/beside the tunnel removes the wall rock the piece leans on, while the
        // piece's own tunnel floor stays hmap 11 (tunnel band). The bbox law above supersedes the
        // anchor-tile bookkeeping — this test still guards the scenario: cave above the tunnel,
        // own floor intact → the beam must go.
        [Test]
        public void CarveSweep_CaveAboveTunnel_RemovesWallAnchoredDeco_OwnFloorIntact()
        {
            var (interp, ctx, terr, md) = Host();
            interp.Eval("def_class Dummy_Balken_T none dummy 0 {} {}");
            DefTunnelTemplate(md.Templates!, md, "beam_t", 0, ("Dummy_Balken_T", 1.5f, 1.5f, 15.04f, 2));

            // Coordinate-aware floor model: corridor tiles (x 20..27, y 21..22) dug to the
            // tunnel band (11); everything else virgin solid rock (15). Mutable via `caveband`.
            var caveband = new HashSet<(int x, int y)>();
            int Sample(float x, float y)
            {
                int tx = Mathf.RoundToInt(x), ty = Mathf.RoundToInt(y);
                if (caveband.Contains((tx, ty))) return 7;                      // cave-carved
                if (tx >= 20 && tx <= 27 && ty >= 21 && ty <= 22) return 11;    // tunnel floor
                return 15;                                                      // solid wall
            }
            md.HmapSampler = Sample;

            PaintCorridor(terr, 20, 27, 21);
            md.ScanAtPos(21, 21, 0);
            ClearCorridor(terr, 20, 27, 21);
            for (int i = 0; i < 2; i++) DigglesTclEngine.MapDecoTick(ctx);
            var beams = EntitiesOf(ctx, "Dummy_Balken_T");
            Assert.AreEqual(1, beams.Count, "beam spawned over the tunnel floor");

            // A cave is dug above the tunnel: the wall row y=20 is carved into the cave band;
            // the tunnel floor under the beam stays 11 — an earlier FloorStranded-only law would
            // read "still footed" and keep the piece hanging in the opened cave. (The actual kill
            // is the piece's bbox — pos ± FowExt/DecoExt — overlapping the carve window that
            // opened cave-band ground, the DUMMYDES law.)
            for (int x = 20; x <= 27; x++) caveband.Add((x, 20));
            Assert.IsFalse(md.FloorStranded(beams[0].Position.x, beams[0].Position.y),
                "precondition: the beam's own floor is NOT stranded (this is the wall case)");
            // Carve node (22,21) lowers tiles {21..22}×{20..21} — the beam's anchor (22,20).
            terr.OnCarveNode!.Invoke(22, 21);
            Assert.AreEqual(0, EntitiesOf(ctx, "Dummy_Balken_T").Count,
                "the anchor wall face was carved away → the wall-mounted piece must be removed");
        }

        [Test]
        public void CarveSweep_UnrelatedWallCarve_KeepsWallAnchoredDeco()
        {
            var (interp, ctx, terr, md) = Host();
            interp.Eval("def_class Dummy_Balken_T none dummy 0 {} {}");
            DefTunnelTemplate(md.Templates!, md, "beam_t", 0, ("Dummy_Balken_T", 1.5f, 1.5f, 15.04f, 2));

            var caveband = new HashSet<(int x, int y)>();
            int Sample(float x, float y)
            {
                int tx = Mathf.RoundToInt(x), ty = Mathf.RoundToInt(y);
                if (caveband.Contains((tx, ty))) return 7;
                if (tx >= 20 && tx <= 27 && ty >= 21 && ty <= 22) return 11;
                return 15;
            }
            md.HmapSampler = Sample;

            PaintCorridor(terr, 20, 27, 21);
            md.ScanAtPos(21, 21, 0);
            ClearCorridor(terr, 20, 27, 21);
            for (int i = 0; i < 2; i++) DigglesTclEngine.MapDecoTick(ctx);
            Assert.AreEqual(1, EntitiesOf(ctx, "Dummy_Balken_T").Count);

            // 1. A carve whose window covers the anchor but leaves the wall solid → kept
            // (the anchor-death law is band-gated exactly like the floor law).
            terr.OnCarveNode!.Invoke(22, 21);
            Assert.AreEqual(1, EntitiesOf(ctx, "Dummy_Balken_T").Count,
                "an intact anchor wall keeps the piece");

            // 2. A cave-band carve of an unrelated wall tile (same wall row, far in x) → kept:
            // neither the deco's tile nor its anchors lie in that node's 2×2 window.
            caveband.Add((26, 20));
            terr.OnCarveNode!.Invoke(26, 20);
            Assert.AreEqual(1, EntitiesOf(ctx, "Dummy_Balken_T").Count,
                "carving an unrelated wall must not sweep the piece");
        }

        // The sweep is band-gated (mirrors the spawn/reject law): a beam still standing over
        // intact tunnel floor is kept, and a carve elsewhere never touches it.
        [Test]
        public void CarveSweep_BeamOverIntactTunnelFloor_IsKept()
        {
            var (interp, ctx, terr, md) = Host();
            interp.Eval("def_class Dummy_Balken_T none dummy 0 {} {}");
            DefTunnelTemplate(md.Templates!, md, "beam_t", 0, ("Dummy_Balken_T", 1.5f, 1.5f, 15.04f, 2));

            PaintCorridor(terr, 20, 27, 21);
            md.ScanAtPos(21, 21, 0);
            ClearCorridor(terr, 20, 27, 21);
            for (int i = 0; i < 2; i++) DigglesTclEngine.MapDecoTick(ctx);
            Assert.AreEqual(1, EntitiesOf(ctx, "Dummy_Balken_T").Count);

            // A carve ON the beam's node, but the floor is still tunnel-band (15) → kept.
            md.HmapSampler = (x, y) => 15;
            terr.OnCarveNode!.Invoke(22, 22);
            Assert.AreEqual(1, EntitiesOf(ctx, "Dummy_Balken_T").Count,
                "a beam over intact tunnel floor survives the carve");

            // A cave-band carve far from the beam (outside the node's 2×2 footprint) → kept.
            md.HmapSampler = (x, y) => 7;
            terr.OnCarveNode!.Invoke(40, 40);
            Assert.AreEqual(1, EntitiesOf(ctx, "Dummy_Balken_T").Count,
                "a distant carve never sweeps a beam outside the carved node's tiles");
        }

        // ---------------- cave-skin props are not selectable/hoverable ----------------

        // The auto-spawned cave-skin lining props (CaveSkinDigCheck step 2) must not hover-aura or
        // eat clicks. Native cave-skin classes are `def_class … dummy` with no obj_init and never
        // opt into set_hoverable/set_selectable (animinit.tcl:419-429), so the default-off native
        // flags leave them non-interactive; the port's opt-out model must opt out at spawn, the
        // same seam the tunnel deco uses.
        [Test]
        public void CaveCompletion_SpawnedSkinProps_AreNotHoverableOrSelectable()
        {
            var (interp, ctx, terr, md) = Host();
            interp.Eval("def_class Urw_caves_1_a_a none dummy 0 {} { add_cave_skin_class 0 2 0 }");
            Assert.IsTrue(ctx.Classes["Urw_caves_1_a_a"].CaveSkin, "add_cave_skin_class flags the class");

            // Complete a cave over the area → step 2 spawns the detail-2 skin slot.
            terr.AddCaveRecord(18, 18, 26, 26, 0, 0, 0);
            interp.Eval("dig_apply {21 21 10}");

            var skins = EntitiesOf(ctx, "Urw_caves_1_a_a");
            Assert.AreEqual(1, skins.Count, "the detail-2 skin slot spawns on cave completion");
            Assert.AreEqual("0", skins[0].VarsScope.Vars["_hoverable"], "cave-skin props are not hoverable");
            Assert.AreEqual("0", skins[0].VarsScope.Vars["_selectable"], "cave-skin props are not selectable");
        }

        // ---------------- real-data laws ----------------

        [Test]
        public void RealData_UrwGng001_TclDecoParse()
        {
            var lg = new LevelGenerator { DataRoot = DataRoot };
            lg.SetTemplateGroup("t", new[] { "urw_gng_001_a.tcl" });
            var p = lg.Registered.Single(q => q.TclName == "urw_gng_001_a.tcl");
            lg.EnsureMeta(p);

            Assert.AreEqual(0, p.Zone, "urw_ prefix = band 0");
            Assert.IsFalse(p.Hol);
            Assert.IsFalse(p.Split);
            Assert.AreEqual((4, 4), (p.W, p.H), "gng_001.pmp header dims");
            // The authored deco list (urw_gng_001_a.tcl): support + collision marker at detail
            // high(2), one wall at medium(1), one wall at low(0).
            Assert.AreEqual(4, p.Deco.Count, "all four `new` spawns parse");
            var st = p.Deco.Single(d => d.ClassName == "Dummy_Multi_Holzstuetze_a");
            Assert.AreEqual(3.09607f, st.X, 1e-4f);
            Assert.AreEqual(2.5f, st.Y, 1e-4f);
            Assert.AreEqual(15.04f, st.Z, 1e-4f);
            Assert.AreEqual(2, st.Detail);
            Assert.AreEqual(1, p.Deco.Single(d => d.ClassName == "Dummy_Holzwand_g").Detail);
            Assert.AreEqual(0, p.Deco.Single(d => d.ClassName == "Dummy_Holzwand_f").Detail);
        }

        [Test]
        public void RealData_Gng001Shape_IsTheTwoWideCorridor()
        {
            var bdb = BdbParser.Parse(DataRoot + "/BlockDB2.BDB");
            var lg = new LevelGenerator { DataRoot = DataRoot };
            lg.SetTemplateGroup("t", new[] { "urw_gng_001_a.tcl" });
            var p = lg.Registered[0];
            lg.EnsureMeta(p);

            var md = new MapDecoBeautifier { Templates = lg, Bdb = bdb };
            var shape = md.ShapeFor(p);
            Assert.IsNotNull(shape, "shape must rasterize from gng_001.pmp + BDB aux");
            // The FUN_00599fc0 law ((aux[0x1b] & 0xF) == 11) over gng_001's authored blocks
            // {76,77,78,79 / 80×4 / 81×4 / 0×4} = the middle 2-wide corridor (verified byte-level
            // against BlockDB2: aux1b = 12/13/12/12, 11, 11, 15).
            CollectionAssert.AreEqual(new[]
            {
                0, 0, 0, 0,
                1, 1, 1, 1,
                1, 1, 1, 1,
                0, 0, 0, 0,
            }, shape, "gng_001 = the standard 2-wide tunnel footprint");
        }

        // ---------------- render-side resolve ----------------

        // The spawn pipeline is proven clean by the tests above; the remaining suspect for
        // invisible tunnel supports was the render side — whether the model library actually
        // serves `Multi_Holzstuetze_a.standard` out of JoinedObjs.3db (BACKLOG §1 suspected a
        // capital-case clip-name mismatch). Pins the exact chain RuntimeWorldSpawner walks:
        // class → class_model_map_v2 obj → obj_db_map db →.3db object lookup → clip build.
        [Test]
        public void RealData_BeamClip_ResolvesFromJoinedObjs()
        {
            Assume.That(System.IO.File.Exists(DataRoot + "/3db/JoinedObjs.3db"),
                "original data dir not present — beam render data test skipped");
            Assume.That(System.IO.File.Exists(
                System.IO.Path.Combine(Application.streamingAssetsPath, "obj_db_map.tsv")),
                "StreamingAssets obj map not present — beam render data test skipped");

            var lib = new DigglesModelLibrary(DataRoot);
            var rc = lib.ResolveClass("Dummy_Multi_Holzstuetze_a");
            Assert.IsNotNull(rc, "class_model_map_v2.tsv must map the beam class");
            Assert.AreEqual("Multi_Holzstuetze_a", rc!.Value.Obj);

            var clips = lib.GetClipNames(rc.Value.Obj);
            Assert.Greater(clips.Count, 0,
                "object 'Multi_Holzstuetze_a' must be found inside JoinedObjs.3db");

            string anim = string.IsNullOrEmpty(rc.Value.DefaultAnim) ? "standard" : rc.Value.DefaultAnim;
            var clip = lib.GetClip(rc.Value.Obj, anim);
            Assert.IsNotNull(clip,
                $"'{rc.Value.Obj}.{anim}' must build; clips present: [{string.Join(", ", clips)}]");
            Assert.Greater(clip!.Frames.Length, 0);
            Assert.IsNotNull(clip.Frames[0]);
            Assert.Greater(clip.Frames[0].vertexCount, 0, "frame-0 mesh must be renderable");
        }
    }

    // ================== the death shatter on deco removal ==================
    // RE (MeshDestructor): FUN_005eae80's first act is the shatter — FUN_004845a0 (current frame
    // FUN_004059a0 + frustum gate + shatter sound FUN_00453f00) → FUN_004830a0 (per-vertex 16-bit
    // decode + world transform:289-303, adaptive triangle tessellation FUN_00482950, per-vertex
    // palette colours:314-327, symmetric random velocity:167-172, gravity + settle
    // FUN_005ea850). DUMMYDES deco kills, the FUN_0058ba00 skin purge and the creature death
    // modes classdef+0x68 ∈ {1,2,3} all route through it — dying objects shatter into pieces
    // rather than falling whole. Port: DigglesTclEngine.ShatterEntity → set-43 fragment burst on
    // the model's current-frame vertices (CreateTempShatter precomputes world verts so the entity
    // deletes same-tick, the native two-stage timing).
    public sealed class DecoShatterTests
    {
        readonly List<GameObject> _gos = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _gos) if (go != null) Object.DestroyImmediate(go);
            _gos.Clear();
            MeshShatterEffect.GroundProbe = null;    // static test seams — never leak
            MeshShatterEffect.SpeedSource = null;
            MeshShatterEffect.FlightShrinkDelay = 1.0f;    // restore tunables tests override
            MeshShatterEffect.FlightShrinkSeconds = 0.6f;
        }

        [Test]
        public void Table_Set43_IsThePortShatterSet()
        {
            var t = Diggles.Runtime.Particles.ParticleTypeTable.Get(43);
            Assert.AreEqual(43, t.SetId, "set 43 must exist (PORT-ONLY shatter)");
            Assert.AreEqual(Diggles.Runtime.Particles.SpawnShape.MeshVerts, t.Shape,
                "fragments spawn on model vertices");
            Assert.Greater(t.AgeCap, 0);
            Assert.Less(t.Gravity.y, 0f, "fragments fall (gravity DAT_00737058 analog)");
        }

        [Test]
        public void CreateTempShatter_CarriesThePrecomputedSource()
        {
            var ps = new ParticleSystemState();
            var verts = new[] { new Vector3(1, 2, 3), new Vector3(4, 5, 6) };
            var cols = new[] { new Color32(10, 20, 30, 255), new Color32(40, 50, 60, 255) };
            ps.CreateTempShatter(43, new Vector3(7, 8, 9), verts, cols, 24, 1.5f);
            Assert.AreEqual(1, ps.Temps.Count);
            var t = ps.Temps[0];
            Assert.AreEqual(43, t.Type);
            Assert.AreSame(verts, t.ShatterVerts);
            Assert.AreSame(cols, t.ShatterCols);
            Assert.AreEqual(24, t.Num);
            Assert.AreEqual(new Vector3(7, 8, 9), t.Pos, "native-space burst origin");
        }

        [Test]
        public void EmitterBurst_SpawnsFragmentsOnTheProvidedVertices()
        {
            var go = new GameObject("shatter-emitter");
            _gos.Add(go);
            var em = go.AddComponent<Diggles.Runtime.Particles.CpuParticleEmitter>();
            var verts = new[] { new Vector3(10, 0, 0), new Vector3(0, 10, 0), new Vector3(0, 0, 10) };
            var type = Diggles.Runtime.Particles.ParticleTypeTable.Get(43);
            em.Configure(type, new ParticleSystemState.Source { MaxParticles = 64 }, null, null);
            em.SetShatterSource(verts, null);
            em.Burst(12);
            Assert.AreEqual(12, em.AliveCount, "one fragment per burst slot");
            foreach (var p in em.AlivePositions())
                Assert.IsTrue(System.Array.Exists(verts, v => (v - p).sqrMagnitude < 1e-6f),
                    $"fragment at {p} must sit ON a provided model vertex (FUN_004830a0 law)");
            // Fragments must move once stepped (velocity + gravity integration, Step43).
            em.StepFrames(2);
            bool anyMoved = false;
            foreach (var p in em.AlivePositions())
                if (!System.Array.Exists(verts, v => (v - p).sqrMagnitude < 1e-6f)) { anyMoved = true; break; }
            Assert.IsTrue(anyMoved, "fragments fly apart after integration");
        }

        [Test]
        public void ShatterEntity_Headless_NoGameObject_IsFalse_NoBurst()
        {
            var interp = new TclInterpreter { TolerateExprErrors = true, TolerateMissingVars = true };
            interp.UnknownCommandHandler = (i, a) => "0";
            var ctx = DigglesTclEngine.Register(interp);
            ctx.TolerateMissingEntities = true;
            interp.Eval("def_class Dummy_Sh_T none dummy 0 {} {}");
            string h = interp.Eval("new Dummy_Sh_T \"\" \"1 2 3\" \"0 0 0\"").Trim();
            var e = ctx.GetEntity(h)!;
            Assert.IsFalse(DigglesTclEngine.ShatterEntity(ctx, e),
                "no GameObject → nothing to shatter (headless keeps the instant delete)");
            Assert.AreEqual(0, ctx.Particles!.Temps.Count, "no burst may be queued headless");
        }

        // ---------------- the real mesh shatter (geometry fragments) ----------------
        // The set-43 billboard rendition lost the geometry; the native emits real triangle shards
        // of the model (FUN_004830a0 walks the index buffer, FUN_00482950 tessellates oversized
        // triangles, shards keep material/UVs/colours). MeshShatterBuilder/MeshShatterEffect
        // implement that law.

        static Mesh QuadMesh(float size)
        {
            var m = new Mesh();
            m.SetVertices(new List<Vector3> {
                new(0, 0, 0), new(size, 0, 0), new(size, size, 0), new(0, size, 0) });
            m.SetUVs(0, new List<Vector2> { new(0, 0), new(1, 0), new(1, 1), new(0, 1) });
            m.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            return m;
        }

        static float TriArea(Vector3 a, Vector3 b, Vector3 c)
            => Vector3.Cross(b - a, c - a).magnitude * 0.5f;

        [Test]
        public void Builder_SmallTriangles_OneFragmentEach_UvsPreserved()
        {
            var verts = new[] { new Vector3(0, 0, 0), new Vector3(0.5f, 0, 0), new Vector3(0, 0.5f, 0),
                                new Vector3(2, 0, 0), new Vector3(2.5f, 0, 0), new Vector3(2, 0.5f, 0) };
            var uvs = new[] { new Vector2(0, 0), new Vector2(0.1f, 0), new Vector2(0, 0.1f),
                              new Vector2(0.5f, 0.5f), new Vector2(0.6f, 0.5f), new Vector2(0.5f, 0.6f) };
            var tris = new[] { 0, 1, 2, 3, 4, 5 };
            var frags = MeshShatterBuilder.Build(verts, tris, uvs, 1.0f, 3, 999);
            Assert.AreEqual(2, frags.Count, "small triangles stay whole — one fragment each");
            CollectionAssert.AreEquivalent(new[] { 0, 1 },
                new[] { frags[0].SourceTri, frags[1].SourceTri },
                "every index-buffer triangle covered exactly once");
            var f0 = frags.Find(f => f.SourceTri == 0);
            Assert.AreEqual(verts[0], f0.A); Assert.AreEqual(verts[1], f0.B); Assert.AreEqual(verts[2], f0.C);
            Assert.AreEqual(uvs[0], f0.Ua); Assert.AreEqual(uvs[1], f0.Ub); Assert.AreEqual(uvs[2], f0.Uc);
        }

        [Test]
        public void Builder_OversizedTriangle_TessellatesAreaPreserving()
        {
            // Longest edge = the hypotenuse 4√2 ≈ 5.66 > threshold 1: midpoint splits
            // 5.66→2.83→1.41→0.71 (three levels until all edges fit) ⇒ 4³ = 64 shards.
            var verts = new[] { new Vector3(0, 0, 0), new Vector3(4, 0, 0), new Vector3(0, 4, 0) };
            var uvs = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1) };
            var frags = MeshShatterBuilder.Build(verts, new[] { 0, 1, 2 }, uvs, 1.0f, 4, 999);
            Assert.AreEqual(64, frags.Count, "uniform midpoint split: 4 per level, hyp 5.66→0.71");
            float srcArea = TriArea(verts[0], verts[1], verts[2]);
            float sum = 0f;
            foreach (var f in frags)
            {
                Assert.AreEqual(0, f.SourceTri);
                sum += TriArea(f.A, f.B, f.C);
                Assert.LessOrEqual((f.A - f.B).magnitude, 1.0001f, "every shard edge fits the threshold");
                Assert.LessOrEqual((f.B - f.C).magnitude, 1.0001f);
                Assert.LessOrEqual((f.C - f.A).magnitude, 1.0001f);
            }
            Assert.AreEqual(srcArea, sum, srcArea * 1e-4f,
                "tessellation must cover the source triangle exactly (area preserved)");
        }

        [Test]
        public void Effect_Integration_AllFragmentsSettle()
        {
            var src = QuadMesh(2f);
            var host = new GameObject("shatter-src");
            _gos.Add(host);
            var fx = MeshShatterEffect.Spawn(src, host.transform, null, host.transform.position.y - 1.2f);
            Assert.IsNotNull(fx, "effect must build from a valid mesh");
            _gos.Add(fx!.gameObject);
            Assert.Greater(fx.FragmentCount, 0);
            int steps = 0;
            while (!fx.AllSettled && steps++ < 2000) fx.StepOnce();
            Assert.IsTrue(fx.AllSettled,
                $"all fragments must come to rest (FUN_005ea850 settle law); {steps} steps");
            Object.DestroyImmediate(src);
        }

        // ---------------- polish laws ----------------

        [Test]
        public void Advance_IsTheScaledClockAccumulator()
        {
            // The effect runs on game time: Update feeds dt × the speed factor into Advance
            // (SpeedSource defaults to DigglesAnimator.GlobalTimeScale — the animators' clock;
            // x10 → 10× steps, pause → 0 → frozen mid-air). Law: Advance(dt) = accumulated
            // fixed steps at StepHz; 0 scaled seconds = 0 steps.
            MeshShatterEffect.GroundProbe = _ => -100f;   // keep falling — steps observable
            var src = QuadMesh(1f);
            var host = new GameObject("ts-src");
            _gos.Add(host);
            var fx = MeshShatterEffect.Spawn(src, host.transform, null, -100f)!;
            _gos.Add(fx.gameObject);
            Assert.AreEqual(0, fx.StepsTaken);
            fx.Advance(0f);
            Assert.AreEqual(0, fx.StepsTaken, "no scaled time → no integration (pause law)");
            fx.Advance(1f);
            Assert.AreEqual((int)MeshShatterEffect.StepHz, fx.StepsTaken,
                "one scaled second = StepHz fixed steps");
            fx.Advance(0.5f);
            Assert.AreEqual((int)(MeshShatterEffect.StepHz * 1.5f), fx.StepsTaken,
                "fractional seconds accumulate deterministically (x10 = 10× the steps)");
            Object.DestroyImmediate(src);
        }

        [Test]
        public void GroundCache_SolidSettlesAtItsY_HoleFallsAndShrinksAway()
        {
            // Per-fragment spawn-time ground probe: solid ground beneath → bounce + settle at
            // its own cached Y; nothing beneath (pit) → keeps falling, then shrinks to nothing.
            MeshShatterEffect.GroundProbe = p => p.x < 5f ? 2f : (float?)null;
            // Longer flight before the shrink so the pit shard demonstrably falls past the
            // solid shard's ground line from its y=10 spawn (restored in TearDown).
            MeshShatterEffect.FlightShrinkDelay = 3f;
            MeshShatterEffect.FlightShrinkSeconds = 0.5f;
            var m = new Mesh();
            m.SetVertices(new List<Vector3> {
                new(1, 10, 0), new(1.5f, 10, 0), new(1, 10.5f, 0),       // over solid (x≈1)
                new(10, 10, 0), new(10.5f, 10, 0), new(10, 10.5f, 0) }); // over the pit (x≈10)
            m.SetUVs(0, new List<Vector2> { new(0, 0), new(1, 0), new(0, 1),
                                            new(0, 0), new(1, 0), new(0, 1) });
            m.SetTriangles(new[] { 0, 1, 2, 3, 4, 5 }, 0);
            var host = new GameObject("gc-src");
            _gos.Add(host);
            var fx = MeshShatterEffect.Spawn(m, host.transform, null, -50f)!;
            _gos.Add(fx.gameObject);
            Assert.AreEqual(2, fx.FragmentCount);
            int solid = fx.FragmentCenter(0).x < 5f ? 0 : 1, hole = 1 - solid;
            Assert.IsTrue(fx.FragmentHasGround(solid), "ground cached under the solid shard");
            Assert.IsFalse(fx.FragmentHasGround(hole), "no ground under the pit shard");
            int steps = 0;
            while (!fx.AllSettled && steps++ < 4000) fx.StepOnce();
            Assert.IsTrue(fx.AllSettled, $"both shards must finish; {steps} steps");
            Assert.AreEqual(2f, fx.FragmentCenter(solid).y, 0.2f,
                "the solid shard rests ON its cached ground line — not hovering, not sunk");
            Assert.Less(fx.FragmentCenter(hole).y, -1f,
                "the pit shard falls PAST the other's ground line and dies by flight-shrink");
            Object.DestroyImmediate(m);
        }

        [Test]
        public void AlphaCull_TransparentCentroidFragments_Dropped()
        {
            // Cutout foliage: shards whose texture alpha at the UV centroid is near zero are
            // dropped; moss breaks into its visible sprigs only.
            var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false)
            { wrapMode = TextureWrapMode.Clamp };   // Repeat would bleed the opaque left edge
                                                    // into u≈0.9 bilinear samples
            var px = new Color[16];
            for (int y = 0; y < 4; y++)
                for (int x = 0; x < 4; x++)
                    px[y * 4 + x] = new Color(0.4f, 0.6f, 0.2f, x < 2 ? 1f : 0f);   // left opaque
            tex.SetPixels(px);
            tex.Apply();
            var verts = new[] { new Vector3(0, 0, 0), new Vector3(0.5f, 0, 0), new Vector3(0, 0.5f, 0),
                                new Vector3(1, 0, 0), new Vector3(1.5f, 0, 0), new Vector3(1, 0.5f, 0) };
            var uvs = new[] { new Vector2(0.15f, 0.5f), new Vector2(0.25f, 0.5f), new Vector2(0.2f, 0.6f),
                              new Vector2(0.85f, 0.5f), new Vector2(0.95f, 0.5f), new Vector2(0.9f, 0.6f) };
            var frags = MeshShatterBuilder.Build(verts, new[] { 0, 1, 2, 3, 4, 5 }, uvs, 1f, 3, 999);
            Assert.AreEqual(2, frags.Count);
            var kept = MeshShatterBuilder.CullTransparent(frags, tex,
                MeshShatterEffect.AlphaCullThreshold);
            Assert.AreEqual(1, kept.Count, "the transparent-centroid shard must be dropped");
            Assert.AreEqual(0, kept[0].SourceTri, "the opaque-side shard survives");
            Object.DestroyImmediate(tex);
        }

        [Test]
        public void DenseMesh_CapPicksAUniformSpread_NotTheFirstN()
        {
            // Creature meshes (mann current frame ≈ thousands of tris) must shatter across the
            // whole body: the cap takes a uniform stride subset, not an index-order prefix.
            const int tri = 2000, cap = 100;
            var verts = new[] { Vector3.zero, Vector3.right * 0.1f, Vector3.up * 0.1f };
            var tris = new int[tri * 3];
            for (int t = 0; t < tri; t++) { tris[t * 3] = 0; tris[t * 3 + 1] = 1; tris[t * 3 + 2] = 2; }
            var frags = MeshShatterBuilder.Build(verts, tris, null, 1f, 3, cap);
            Assert.LessOrEqual(frags.Count, cap, "cap honored");
            Assert.Greater(frags.Count, cap - 3, "cap budget actually used");
            int maxSrc = 0;
            foreach (var f in frags) maxSrc = Mathf.Max(maxSrc, f.SourceTri);
            Assert.Greater(maxSrc, tri * 3 / 4,
                "the subset must reach the far end of the index buffer (uniform stride)");
        }

        [Test]
        public void GentleCollapse_DefaultsStayWithinTheNativeLook()
        {
            // The launch must stay gravity-dominant — an earlier version's spread read as an
            // explosion.
            Assert.LessOrEqual(MeshShatterEffect.LaunchSpread, 0.02f, "gentle scatter");
            Assert.LessOrEqual(MeshShatterEffect.LaunchUpBias, 0.012f, "barely-there arc");
            Assert.Greater(MeshShatterEffect.GravityPerStep, 0f, "gravity does the work");
            Assert.GreaterOrEqual(MeshShatterEffect.GravityPerStep, MeshShatterEffect.LaunchUpBias * 0.5f,
                "gravity-dominant collapse, not a blast");
        }

        [Test]
        public void ShatterEntity_MeshGo_SpawnsTheGeometryEffect_NoMeshGo_IsFalse()
        {
            var interp = new TclInterpreter { TolerateExprErrors = true, TolerateMissingVars = true };
            interp.UnknownCommandHandler = (i, a) => "0";
            var ctx = DigglesTclEngine.Register(interp);
            interp.Eval("def_class Dummy_Sh_T none dummy 0 {} {}");

            // A GO with a mesh → the geometry effect spawns (set 43 is not used anymore).
            var src = QuadMesh(1f);
            var go = new GameObject("piece");
            _gos.Add(go);
            go.AddComponent<MeshFilter>().sharedMesh = src;
            go.AddComponent<MeshRenderer>();
            var e = new DigglesTclEntity { Class = ctx.Classes["Dummy_Sh_T"], GameObject = go };
            Assert.IsTrue(DigglesTclEngine.ShatterEntity(ctx, e), "mesh present → geometry shatter");
            var fx = Object.FindObjectsByType<MeshShatterEffect>(FindObjectsSortMode.None);
            Assert.AreEqual(1, fx.Length, "exactly one MeshShatterEffect spawned");
            foreach (var f in fx) _gos.Add(f.gameObject);
            Assert.AreEqual(0, ctx.Particles!.Temps.Count,
                "no particle temp — the billboard set 43 is dormant fallback infra only");

            // A GO without a mesh → false (MapDecoRetire then falls back to dirt+crumble).
            var bare = new GameObject("bare");
            _gos.Add(bare);
            var e2 = new DigglesTclEntity { Class = ctx.Classes["Dummy_Sh_T"], GameObject = bare };
            Assert.IsFalse(DigglesTclEngine.ShatterEntity(ctx, e2), "no mesh → no shatter");
            Object.DestroyImmediate(src);
        }

        // ---------------- the anti-pink material law (death shards) ----------------
        // The shatter renderer used to wear the captured Material reference raw — null capture, a
        // dead per-instance clone (destroyed the same delete tick), or an error shader all fell to
        // Unity's magenta. ResolveRenderMaterial now snapshots the model's own material (immune to
        // the source dying that tick), re-resolves from the mesh's renderer when the capture was
        // null, and drops to a dirt fallback otherwise — never pink.

        // A material Unity can draw without painting magenta (mirrors MeshShatterEffect.IsRenderable;
        // isSupported is intentionally not queried — it is unreliable under a headless graphics device).
        static bool NotPink(Material? m)
            => m != null && m.shader != null && m.shader.name != "Hidden/InternalErrorShader";

        static Shader DrawableShader()
            => Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Lit")
               ?? Shader.Find("Unlit/Color");

        [Test]
        public void Material_ValidSource_IsSnapshotCopy_SurvivesSourceDestroy()
        {
            var srcMat = new Material(DrawableShader());
            var src = QuadMesh(1f);
            var host = new GameObject("mat-src");
            _gos.Add(host);
            var fx = MeshShatterEffect.Spawn(src, host.transform, srcMat, -100f)!;
            _gos.Add(fx.gameObject);
            var mr = fx.GetComponent<MeshRenderer>();
            Assert.IsTrue(NotPink(mr.sharedMaterial), "shatter carries a drawable material, never magenta");
            Assert.AreNotSame(srcMat, mr.sharedMaterial, "a private snapshot copy, not the live reference");
            Assert.AreSame(srcMat.shader, mr.sharedMaterial.shader, "the copy carries the model's real shader/look");
            // The source material dying the same tick (the del/destruct order) must not go pink.
            Object.DestroyImmediate(srcMat);
            Assert.IsTrue(NotPink(mr.sharedMaterial),
                "the snapshot outlives the destroyed source (dead-reference → still no magenta)");
            Object.DestroyImmediate(src);
        }

        [Test]
        public void Material_NullCapture_ReResolvesFromSourceRenderer()
        {
            var real = new Material(DrawableShader());
            var src = QuadMesh(1f);
            var host = new GameObject("mat-host");
            _gos.Add(host);
            host.AddComponent<MeshFilter>().sharedMesh = src;
            host.AddComponent<MeshRenderer>().sharedMaterial = real;
            // ShatterEntity can hand us null (GetComponentInChildren landing on a material-less child);
            // the effect re-resolves from the mesh's own renderer via sourceTf.
            var fx = MeshShatterEffect.Spawn(src, host.transform, null, -100f)!;
            _gos.Add(fx.gameObject);
            var mr = fx.GetComponent<MeshRenderer>();
            Assert.IsTrue(NotPink(mr.sharedMaterial), "resolved from the source renderer — not pink");
            Assert.AreSame(real.shader, mr.sharedMaterial.shader, "carries the source renderer's real shader");
            Object.DestroyImmediate(real);
            Object.DestroyImmediate(src);
        }

        [Test]
        public void Material_NoneAvailable_DirtFallback_NeverNullNeverPink()
        {
            var src = QuadMesh(1f);
            var host = new GameObject("mat-none");   // no renderer anywhere to resolve from
            _gos.Add(host);
            var fx = MeshShatterEffect.Spawn(src, host.transform, null, -100f)!;
            _gos.Add(fx.gameObject);
            var mr = fx.GetComponent<MeshRenderer>();
            Assert.IsNotNull(mr.sharedMaterial, "no model material → dirt fallback, still a real material");
            Assert.IsTrue(NotPink(mr.sharedMaterial), "the dirt fallback is drawable — never the magenta shader");
            Object.DestroyImmediate(src);
        }
    }

    // ================== cave-skin completion timing ==================
    // An earlier test battery only exercised CaveSkinDigCheck with hand-cleared nibbles
    // (CaveSkinSwapTests) — no test ever drove a real progressive carve (dig_apply → CarveStep on
    // real BDB blocks → the per-swing completion scan), so a nibble that clears without the
    // terrain being excavated (or a scan that reads the wrong cells) was invisible. This drives
    // an 8×6 mode-0 cave (targets like the stamp paints) swing by swing in the gnome's
    // mouth-first order and asserts the skins spawn only when the cave is actually dug out
    // (native law FUN_0058ba00:102-158 — the half-open scan tolerates at most the excluded max
    // row/col residue).
    public sealed class CaveSkinCompletionCarveTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;
        readonly List<GameObject> _gos = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _gos) if (go != null) Object.DestroyImmediate(go);
            _gos.Clear();
        }

        (TclInterpreter interp, DigglesTclContext ctx, DiggableTerrain terr) Host(int w, int h)
        {
            if (!System.IO.File.Exists(DataRoot + "/BlockDB2.BDB"))
                Assert.Ignore("real BDB not present");
            var go = new GameObject("CaveSkinCarveHost");
            _gos.Add(go);
            var t = go.AddComponent<DiggableTerrain>();
            t.Init(new BdbPmpWorldBuilder.TclWorldResult
            {
                Width = w, Height = h,
                BlockIdx = new uint[w * h],   // block 0 = solid earth (real aux transitions)
                Terrain = new byte[w * h],    // material default 2 (earth, diggable)
            }, BdbParser.Parse(DataRoot + "/BlockDB2.BDB"), "", () => { });

            var interp = new TclInterpreter { TolerateExprErrors = true, TolerateMissingVars = true };
            interp.UnknownCommandHandler = (i, a) => "0";
            var ctx = DigglesTclEngine.Register(interp);
            ctx.TolerateMissingEntities = true;
            ctx.StoryRng = new System.Random(42);
            ctx.Terrain = t;
            foreach (var z in new[] { "Urwald", "Metall", "Kristall", "Lava" })
                interp.Eval($"sm_add_zone {z}");
            // The {set 0, size 0} skin family, one variant, all three detail slots.
            interp.Eval("def_class Urw_caves_1_a_a none dummy 0 {} { add_cave_skin_class 0 2 0 }");
            interp.Eval("def_class Urw_caves_1_a_b none dummy 0 {} { add_cave_skin_class 0 1 0 }");
            interp.Eval("def_class Urw_caves_1_a_c none dummy 0 {} { add_cave_skin_class 0 0 0 }");
            return (interp, ctx, t);
        }

        static int SkinCount(DigglesTclContext ctx)
            => ctx.Entities.Values.Count(e => e.Class != null && e.Class.CaveSkin);

        [Test]
        public void RealCarve_CaveSkins_SpawnOnlyWhenTheCaveIsActuallyDugOut()
        {
            var (interp, ctx, terr) = Host(64, 64);

            // The cave order: 8×6 full-depth body (the stamp's mode-0 v=0 → nibble 8, target 0)
            // over cols 20..27 × rows 20..25; mouth = the bottom row 25 (ant-farm: caves extend
            // UP from the anchor). Record = the template footprint (CaveStamp.Commit's native
            // bbox law) — here every cell paints, so footprint == the painted region.
            const int X0 = 20, X1 = 27, Y0 = 20, Y1 = 25;
            for (int y = Y0; y <= Y1; y++)
                for (int x = X0; x <= X1; x++)
                    terr.SetDigMark(x, y, 0, 0, dummy: false);
            terr.AddCaveRecord(X0, Y0, X1, Y1, 0, terr.PlayerDigMarkId & 0xF, 0);
            terr.IncPlayerDigMarkId();

            // Swing-by-swing carve in the gnome's mouth-first order (dig_execute hammers one
            // voxel until its nibble clears — the same per-node loop CaveDigReproTests C1 runs).
            int swings = 0, firstSkinSwing = -1, rockyAtSpawn = -1, markedAtSpawn = -1;
            var spawnDiag = new StringBuilder();
            void Snapshot()
            {
                rockyAtSpawn = 0; markedAtSpawn = 0;
                for (int y = Y0; y <= Y1; y++)
                {
                    var row = new StringBuilder($"  y{y}: ");
                    for (int x = X0; x <= X1; x++)
                    {
                        // "Still rock" = the cell's own dig node (its NW corner) never reached
                        // the carve floor. The tile max-corner (GetHmap) over-counts the
                        // region's south/east boundary tiles, whose outer corners belong to
                        // nodes outside the order and legitimately stay 15 (the first run of
                        // this repro mis-flagged exactly those 14 boundary cells; the native
                        // half-open completion scan excludes that boundary by design).
                        int nw = terr.NwCornerHeight(x, y), nib = terr.RawDigNibble(x, y, 0);
                        if (nw >= 7) rockyAtSpawn++;
                        if (nib != 0) markedAtSpawn++;
                        row.Append($"c{nw:X1}n{nib:X1} ");
                    }
                    spawnDiag.AppendLine(row.ToString());
                }
            }
            for (int ty = Y1; ty >= Y0; ty--)
                for (int tx = X0; tx <= X1; tx++)
                {
                    int guard = 0;
                    while (terr.RawDigNibble(tx, ty, 0) != 0 && guard++ < 24)
                    {
                        interp.Eval($"dig_apply {{{tx} {ty} 5}}");
                        swings++;
                        if (firstSkinSwing < 0 && SkinCount(ctx) > 0) { firstSkinSwing = swings; Snapshot(); }
                    }
                }

            TestContext.WriteLine($"total swings={swings}, first skin at swing {firstSkinSwing}, " +
                $"rocky cells at spawn={rockyAtSpawn}, still-marked at spawn={markedAtSpawn}");
            if (spawnDiag.Length > 0) TestContext.WriteLine("cave state at first skin spawn (cXnY):\n" + spawnDiag);

            Assert.GreaterOrEqual(firstSkinSwing, 0, "the finished cave must grow its skin props");
            // The native completion law: skins spawn when the record's half-open scan region is
            // clear — at most the excluded max row/col may still be undug. Rows are dug mouth-
            // first (the max row goes first), so at spawn time no more than one cell (the
            // excluded max-column cell of the last row) may still hold rock. More rock than
            // that means props are spawning before the cave is actually dug out.
            Assert.LessOrEqual(rockyAtSpawn, 1,
                "cave-skin props spawned while the cave still holds undug rock (early completion)");
            Assert.AreEqual(0, terr.CaveRecords.Count, "the completed record must retire");
        }

        // ---- a cave re-stamped over mostly already-dug ground ----
        // (extending an existing cave / re-marking near a tunnel). The rim gate silently drops
        // every cell that is already open (MarkRimGate), so with the old painted-only bbox the
        // record collapsed — here to a single row, whose half-open completion scan [Y0,Y1) is
        // empty → the first swing "completed" the cave and the skins grew instantly. The native
        // rect is the full non-skip template footprint (FUN_00567cb0:164-198 — the bbox update
        // runs unconditionally after the painter call, whether or not the nibble landed).
        [Test]
        public void StampOverDugGround_RecordIsFullFootprint_AndPropsWaitForTheRealDig()
        {
            var (interp, ctx, terr) = Host(64, 64);

            // Synthetic template (the Play shape): rows 26..30 × cols 14..17 deep body (v0),
            // row 31 = door lips (v3). Rows land so the mouth pair (30,31) sits at ay−2/ay−1.
            var cells = new byte[CaveTemplate.Size * CaveTemplate.Size];
            for (int i = 0; i < cells.Length; i++) cells[i] = CaveTemplate.Skip;
            for (int r = 26; r <= 30; r++)
                for (int c = 14; c <= 17; c++) cells[r * CaveTemplate.Size + c] = 0;
            for (int c = 14; c <= 17; c++) cells[31 * CaveTemplate.Size + c] = 3;
            var tpl = new CaveTemplate(0, cells);

            const int ax = 32, ay = 40;
            var (x0, y0) = CaveStamp.Origin(ax, ay);
            // Pre-dig the world the way the player left it: the body rows 26..29 are an old
            // cave (carved to the floor, rim −1); the mouth rows 30..31 are a corridor
            // (one carve step, rim 7 — mouth rule < 0xc satisfied, lips will drop at 11 > 7).
            for (int r = 26; r <= 29; r++)
                for (int c = 14; c <= 17; c++)
                { int g = 0; while (terr.CarveTileNow(x0 + c, y0 + r) && ++g < 40) { } }
            for (int r = 30; r <= 31; r++)
                for (int c = 14; c <= 17; c++)
                    Assert.IsTrue(terr.CarveTileNow(x0 + c, y0 + r), $"mouth pre-carve ({c},{r})");

            Assert.IsTrue(CaveStamp.Validate(tpl, ax, ay, terr, 0),
                "the re-stamp must validate — rule 4 reads the STATIC corner height (0 ≥ target 0), "
                + "only the painter's rim gate drops the open cells (the divergence that shrank the rect)");
            int painted = CaveStamp.Commit(tpl, ax, ay, terr, 0, out _, out _);
            Assert.AreEqual(4, painted, "only the one strip of NEW rock (row 30) paints");
            for (int c = 14; c <= 17; c++)
            {
                Assert.AreNotEqual(0, terr.RawDigNibble(x0 + c, y0 + 30, 0), "strip cell must be marked");
                Assert.AreEqual(0, terr.RawDigNibble(x0 + c, y0 + 26, 0), "old-cave cell must drop");
                Assert.AreEqual(0, terr.RawDigNibble(x0 + c, y0 + 31, 0), "lip over corridor must drop");
            }

            // The rect law: full footprint, not the painted strip.
            Assert.AreEqual(1, terr.CaveRecords.Count, "one record appended");
            var rec = terr.CaveRecords[0];
            Assert.AreEqual((x0 + 14, y0 + 26, x0 + 17, y0 + 31), (rec.X0, rec.Y0, rec.X1, rec.Y1),
                "record rect = the FULL template footprint (FUN_00567cb0 unconditional bbox)");

            // The timing law: one swing at the strip — the cave is not dug out; no props yet.
            // (With the shrunken single-row rect this very swing hit an empty scan region,
            // spawned all three skins and retired the record.)
            interp.Eval($"dig_apply {{{x0 + 14} {y0 + 30} 5}}");
            Assert.AreEqual(0, SkinCount(ctx), "props must NOT grow while the strip is still marked");
            Assert.AreEqual(1, terr.CaveRecords.Count, "the record must survive the first swing");

            // Dig the strip for real → completion → the props grow, the record retires.
            for (int c = 14; c <= 17; c++)
            {
                int g = 0;
                while (terr.RawDigNibble(x0 + c, y0 + 30, 0) != 0 && g++ < 24)
                    interp.Eval($"dig_apply {{{x0 + c} {y0 + 30} 5}}");
            }
            Assert.Greater(SkinCount(ctx), 0, "the finished cave must grow its skin props");
            Assert.AreEqual(0, terr.CaveRecords.Count, "the completed record retires");
        }
    }

    // ================== real-carve beam fit ==================
    // The beam batteries above never run a real carve (bdb-less fixtures refuse CarveStep) and
    // therefore sample the fit through the tile fallback (t.GetHmap = max corner, biased toward
    // solid 15) — but the running game samples nav.GetHmapSub (per-sub-cell block aux), which
    // post-carve reads the stepped blocks' real depth profile. The clip-resolve side is proven
    // (RealData_BeamClip_ResolvesFromJoinedObjs), so this drives the remaining untested leg: a
    // genuine standard-tunnel carve (mode-1 dummy marks → dig_apply → CarveStep stepping on the
    // real BDB aux graph) with the live sampler wired, and requires the support to spawn.
    public sealed class MapDecoBeamRealCarveTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;
        readonly List<GameObject> _gos = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _gos) if (go != null) Object.DestroyImmediate(go);
            _gos.Clear();
        }

        static List<DigglesTclEntity> EntitiesOf(DigglesTclContext ctx, string cls)
            => ctx.Entities.Values.Where(e => e.Class?.Name == cls).ToList();

        [Test]
        public void RealCarve_StandardTunnel_PlaySampler_SupportSpawns()
        {
            if (!System.IO.File.Exists(DataRoot + "/BlockDB2.BDB"))
                Assert.Ignore("real BDB not present");
            var go = new GameObject("BeamRealCarveHost");
            _gos.Add(go);
            var terr = go.AddComponent<DiggableTerrain>();
            var bdb = BdbParser.Parse(DataRoot + "/BlockDB2.BDB");
            terr.Init(new BdbPmpWorldBuilder.TclWorldResult
            {
                Width = 64, Height = 64,
                BlockIdx = new uint[64 * 64],   // block 0 = solid earth (real aux transitions)
                Terrain = new byte[64 * 64],    // material default 2 (earth, diggable)
            }, bdb, "", () => { });

            var interp = new TclInterpreter { TolerateExprErrors = true, TolerateMissingVars = true };
            interp.UnknownCommandHandler = (i, a) => "0";
            var ctx = DigglesTclEngine.Register(interp);
            ctx.TolerateMissingEntities = true;
            ctx.StoryRng = new System.Random(42);
            ctx.Terrain = terr;
            ctx.LevelGen = new LevelGenerator { DataRoot = DataRoot };
            foreach (var z in new[] { "Urwald", "Metall", "Kristall", "Lava" })
                interp.Eval($"sm_add_zone {z}");
            interp.Eval("def_class Dummy_Balken_T none dummy 0 {} {}");
            var md = DigglesTclEngine.MapDecoOf(ctx);
            Assert.IsNotNull(md, "beautifier must exist once Terrain+LevelGen are wired");
            ctx.LevelGen.AddSyntheticDecoPiece("beam_rc", 4, 4, 0,
                ("Dummy_Balken_T", 1.5f, 1.5f, 15.04f, 2));
            md!.InjectShape("beam_rc", new[]
            {
                0, 0, 0, 0,
                1, 1, 1, 1,
                1, 1, 1, 1,
                0, 0, 0, 0,
            });

            // The live-game floor sampler: nav.GetHmapSub over the live block grid (same ×8
            // rounding as MapDecoBeautifier.SampleHmap's nav path). The NavWorld shares
            // terr.BlockIdx by reference, so every CarveStep block swap is visible to the probes
            // immediately.
            var nav = new Diggles.Runtime.Navigation.NavWorld(
                terr.Width, terr.Height, terr.BlockIdx, new byte[terr.Width * terr.Height * 64], bdb);
            md.HmapSampler = (wx, wy) => nav.GetHmapSub(Mathf.RoundToInt(wx * 8f), Mathf.RoundToInt(wy * 8f));

            // Standard tunnel order: 8×2 corridor, mode-1 dummy marks (the marquee's paint).
            const int Cx0 = 20, Cx1 = 27, Cy0 = 21;
            for (int x = Cx0; x <= Cx1; x++)
                for (int y = Cy0; y <= Cy0 + 1; y++)
                    terr.SetDigMark(x, y, 0, 0, dummy: true);
            terr.IncPlayerDigMarkId();

            // Swing rounds through the faithful Tcl entry (dig_apply = ScanAtPos before the
            // carve + CarveStep + completion sweep) until every nibble clears.
            for (int round = 0; round < 32; round++)
            {
                bool any = false;
                for (int x = Cx0; x <= Cx1; x++)
                    for (int y = Cy0; y <= Cy0 + 1; y++)
                        if (terr.RawDigNibble(x, y, 0) != 0)
                        {
                            any = true;
                            interp.Eval($"dig_apply {{{x} {y} 10}}");
                        }
                if (!any) break;
            }
            int leftover = 0;
            for (int x = Cx0; x <= Cx1; x++)
                for (int y = Cy0; y <= Cy0 + 1; y++)
                    if (terr.RawDigNibble(x, y, 0) != 0) leftover++;

            // Diagnostics: the real post-carve depth profile the fit reads.
            var sb = new StringBuilder(
                $"leftoverMarks={leftover} rects={md.Rects.Count} pending={md.Pending.Count}");
            for (int y = Cy0 - 1; y <= Cy0 + 2; y++)
            {
                sb.Append($"\n  subH y={y}.5: ");
                for (int x = Cx0; x <= Cx1; x++)
                    sb.Append(md.HmapSampler(x + 0.5f, y + 0.5f)).Append(' ');
            }
            foreach (var p in md.Pending)
                sb.Append($"\n  pending {p.ClassName} @({p.Px:0.##},{p.Py:0.##},{p.Pz:0.##}) probe h = "
                    + $"{md.HmapSampler(p.Bx0, p.By0)}/{md.HmapSampler(p.Bx1, p.By0)}/"
                    + $"{md.HmapSampler(p.Bx0, p.By1)}/{md.HmapSampler(p.Bx1, p.By1)}");
            TestContext.WriteLine(sb.ToString());

            Assert.AreEqual(0, leftover, "the standard-tunnel order must carve to completion");
            Assert.Greater(md.Rects.Count, 0, "ScanAtPos must match the corridor during the dig");

            // Drain the pending queue (one entry per 10 Hz tick).
            for (int i = 0; i < 64 && md.Pending.Count > 0; i++) DigglesTclEngine.MapDecoTick(ctx);
            Assert.AreEqual(1, EntitiesOf(ctx, "Dummy_Balken_T").Count,
                "the support must spawn over a REAL standard-tunnel carve ('beams not visible')");
        }
    }
}
