#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using Diggles.Runtime;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    // WORLD GROWTH DURING DIGGING — the original
    // StoryMgr pipeline: standard-tunnel digs (mode-1 dummy marks) feed CStoryMgr::DigNotify
    // (FUN_005fd060) → weight over sm_set_digcount latches a breakthrough → sm_scan (FUN_006616d0)
    // hands the cell to story.tcl's generate_next → sm_get_level_info free rect (FUN_005fac90) +
    // lg_set_starttemplatepro dig anchor (FUN_00650f90) → lg_start → load_next stamps templates
    // into the LIVE world (RuntimeMapBuilder live mode → BlitPmpIntoWorld + RefreshRect + nav
    // revalidation).
    //
    // Staging mirrors the plan §6: stage-0 constants/falsy (light, no data); DigNotify semantics
    // (fabricated terrain, no data); boot chain / think loop / the growth money test (real data,
    // [Explicit] like the other full-stack suites).
    public sealed class WorldGrowthTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

        readonly List<GameObject> _spawnedGos = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _spawnedGos) if (go != null) Object.DestroyImmediate(go);
            _spawnedGos.Clear();
        }

        static (TclInterpreter interp, DigglesTclContext ctx) LightHost()
        {
            var interp = new TclInterpreter { TolerateExprErrors = true, TolerateMissingVars = true };
            var ctx = DigglesTclEngine.Register(interp);
            ctx.TolerateMissingEntities = true;
            ctx.TolerateMissingClass = true;
            ctx.TolerateMissingFiles = true;
            return (interp, ctx);
        }

        // A fabricated solid terrain (no BDB — DigNotify only reads the dig nibbles + dims; the
        // carve no-ops without aux data, which is exactly what these semantic tests want).
        DiggableTerrain SolidTerrain(int w, int h)
        {
            var go = new GameObject("DiggableTerrain");
            _spawnedGos.Add(go);
            var t = go.AddComponent<DiggableTerrain>();
            t.Init(new BdbPmpWorldBuilder.TclWorldResult
            {
                Width = w, Height = h,
                BlockIdx = new uint[w * h],   // block 0 = non-air ⇒ solid
                Terrain = new byte[w * h],
            }, null!, "", () => { });
            return t;
        }

        // ===================== stage 0 — constants / falsy contracts =====================

        // sm_get_resolution = the CONSTANT 4 (FUN_0065ffb0:301). The old port returned the
        // conflated digcount (32) → all story.tcl cell↔world math was 8× off.
        [Test]
        public void SmGetResolution_IsConstant4()
        {
            var (interp, _) = LightHost();
            Assert.AreEqual("4", interp.Eval("sm_get_resolution").Trim());
        }

        // sm_set_digcount feeds the GROWTH THRESHOLD (+0x9c, FUN_0065c750:233), default 5
        // (ctor reset FUN_005fa7c0:32) — and never the resolution.
        [Test]
        public void SmSetDigcount_FeedsThreshold_NotResolution()
        {
            var (interp, ctx) = LightHost();
            Assert.AreEqual(5, ctx.StoryDigThreshold, "ctor default threshold is 5");
            interp.Eval("sm_set_digcount 32");
            Assert.AreEqual(32, ctx.StoryDigThreshold);
            Assert.AreEqual("4", interp.Eval("sm_get_resolution").Trim(), "resolution must stay 4");
        }

        // sm_scan with no latch returns Tcl int 0 — NEVER "" (FUN_006616d0:88-100). story.tcl:1123
        // gates on `$area_list != 0`; the "" stub was the wrong falsy (string-compares != 0 → TRUE).
        [Test]
        public void SmScan_NoLatch_ReturnsIntZero_AndStoryGateStaysClosed()
        {
            var (interp, _) = LightHost();
            Assert.AreEqual("0", interp.Eval("sm_scan").Trim());
            // the exact story.tcl gate shape must NOT fire on the empty result
            interp.Eval("set hit 0\nset area_list [sm_scan]\nif { $area_list != 0 } { set hit 1 }");
            Assert.AreEqual("0", interp.Eval("set hit").Trim(), "the story gate must stay closed on no-latch");
        }

        // sm_scan consume-once (+0x94): a fresh latch reads exactly once as the sign-rounded /4
        // cell pair, then 0 — until a new latch lands.
        [Test]
        public void SmScan_ConsumeOnce_ThenRelatchable()
        {
            var (interp, ctx) = LightHost();
            ctx.StoryLatchDir = 2; ctx.StoryLatchX = 209; ctx.StoryLatchY = 47; ctx.StoryLatchConsumed = false;
            Assert.AreEqual("52 11", interp.Eval("sm_scan").Trim(), "fresh latch → {x/4 y/4}");
            Assert.AreEqual("0", interp.Eval("sm_scan").Trim(), "consumed latch → int 0");
            ctx.StoryLatchDir = 8; ctx.StoryLatchX = 100; ctx.StoryLatchY = 100; ctx.StoryLatchConsumed = false;
            Assert.AreEqual("25 25", interp.Eval("sm_scan").Trim(), "a new latch reads again");
        }

        // map getoffset = a valid "0 0" pair (the port never relocates) — generate_next and
        // check_uebergang do `lindex [map getoffset] 1` + expr on it (story.tcl:235/:641).
        [Test]
        public void MapGetoffset_IsZeroPair()
        {
            var (interp, _) = LightHost();
            Assert.AreEqual("0 0", interp.Eval("map getoffset").Trim());
            Assert.AreEqual("0", interp.Eval("lindex [map getoffset] 1").Trim());
        }

        // Zone bands (FUN_005fcdf0): zone i spans (bound[i−1], bound[i]], bound −1 = +inf.
        [Test]
        public void SmZones_BandLookup()
        {
            var (interp, _) = LightHost();
            interp.Eval("sm_add_zone Urwald\nsm_add_zone Metall\nsm_add_zone Kristall");
            Assert.AreEqual("Urwald", interp.Eval("sm_get_zone 100").Trim(), "unbounded first zone covers all");
            interp.Eval("sm_set_zone 0 200\nsm_set_zone 1 400");
            Assert.AreEqual("Urwald", interp.Eval("sm_get_zone 100").Trim());
            Assert.AreEqual("Metall", interp.Eval("sm_get_zone 300").Trim());
            Assert.AreEqual("Kristall", interp.Eval("sm_get_zone 500").Trim());
        }

        // sm_create_map W H allocates a fresh zeroed (W/4)×(H/4) story grid (FUN_0065f430:176-186);
        // OOB cells read 1 = GROWN (FUN_005fa5d0:52-57) so expansion stops at the borders.
        [Test]
        public void SmCreateMap_SizesStoryGrid_AndOobReadsGrown()
        {
            var (interp, ctx) = LightHost();
            interp.Eval("sm_map_set 5 5 1");                  // pre-existing mark…
            interp.Eval("sm_create_map 64 64");
            Assert.AreEqual(16, ctx.StoryMapW);
            Assert.AreEqual(16, ctx.StoryMapH);
            Assert.AreEqual("0", interp.Eval("sm_map_get 5 5").Trim(), "…must be wiped by the fresh grid");
            Assert.AreEqual("1", interp.Eval("sm_map_get 20 0").Trim(), "OOB reads grown");
            Assert.AreEqual("1", interp.Eval("sm_map_get 0 -1").Trim(), "OOB reads grown");
        }

        // sm_add_temp window + flag parsing; sm_set_temp marks the registry record placed and
        // feeds sm_get_temppos (FUN_00660a80 → FUN_005fa320).
        [Test]
        public void SmAddTemp_AndSetTemp_RegistryRoundTrip()
        {
            var (interp, ctx) = LightHost();
            interp.Eval("sm_add_temp urw_der_einsiedler 56 60");
            interp.Eval("sm_add_temp urw_unq_feen 60 160 -nonrelevant 0.474 -middist 10");
            interp.Eval("sm_add_temp urw_uebergang_swf 132 164 -mindist urw_trollburg_ring 0.1 -exclusive 1 -zoneborder \"Urwald\" -stoneidx 0");
            Assert.AreEqual(3, ctx.StoryTemps.Count);
            var ue = ctx.StoryTemps[2];
            Assert.AreEqual(132, ue.Lo); Assert.AreEqual(164, ue.Hi);
            Assert.IsTrue(ue.Exclusive);
            Assert.AreEqual("urw_trollburg_ring", ue.MinDistRef);
            Assert.AreEqual(0.1f, ue.MinDistVal, 1e-4f);
            Assert.AreEqual("Urwald", ue.ZoneBorder);
            Assert.AreEqual(0, ue.StoneIdx);

            Assert.AreEqual("-1 -1", interp.Eval("sm_get_temppos urw_der_einsiedler").Trim());
            interp.Eval("sm_set_temp {{urw_der_einsiedler 208 100} {urw_unq_start 208 0}}");
            Assert.AreEqual("208 100", interp.Eval("sm_get_temppos urw_der_einsiedler").Trim());
            Assert.IsTrue(ctx.StoryTemps[0].Placed, "the matching registry record gets the used flag + pos");
        }

        // sm_get_level_info: the RAW free-rect expansion stops at grown cells and the map border —
        // but the RETURNED rect is the FUN_005fa9a0 placement fit, NOT the raw expansion: w/h are
        // clamped to min(rawW, max(16, seedW+8)) × min(rawH, max(8, seedH)) cells (16×8 with no
        // utemp) and the box is positioned to CONTAIN the scan cell, centered between the feasible
        // bounds when no utemp was picked (FUN_005fa9a0:25-45 clamp,:83-102 bounds,:136-165
        // case-1 centering; result stored at +0x40/+0x44/+0x38/+0x3c, FUN_005fac90:833-836). This
        // is what keeps growth LOCAL to the dig on a young (mostly empty) story map.
        [Test]
        public void SmGetLevelInfo_FreeRect_StopsAtMarksAndBorders()
        {
            var (interp, ctx) = LightHost();
            interp.Eval("sm_create_map 64 64");               // 16×16 cells
            // wall of grown cells at cx = 8 → the raw rect from (4,4) can't cross it (raw = 0,0 8×16)
            for (int cy = 0; cy < 16; cy++) interp.Eval($"sm_map_set 8 {cy} 1");
            string linfo = interp.Eval("sm_get_level_info 4 4").Trim();
            var t = TclList.Parse(linfo);
            int x = int.Parse(t[0]), y = int.Parse(t[1]), w = int.Parse(t[2]), h = int.Parse(t[3]);
            TestContext.WriteLine($"linfo = {linfo}");
            Assert.AreEqual(8, w, "width = the raw rect (8 wide, the grown-cell wall) — under the 16-cell target");
            Assert.AreEqual(8, h, "height = the FUN_005fa9a0 clamp min(rawH 16, max(8, seed 1)) — NOT the full raw height");
            Assert.AreEqual(0, x, "x: the 8-wide box filling the 8-wide raw rect can only sit at 0");
            Assert.AreEqual(2, y, "y centered: yMin 0, yMax = min(scanY 4, rawY1 16 − h 8) = 4 → (0+4)/2");
            Assert.AreEqual(ctx.StoryRectW, w, "rect must persist in ctx for lg_set_starttemplatepro");
            // a grown SCAN cell → FUN_005fa9a0:21-23 rejects up front, leaving the 1×1 seed at the
            // scan cell; story.tcl:155's "too small" gate (w*4 < 16) then aborts the generation.
            string deg = interp.Eval("sm_get_level_info 8 4").Trim();
            Assert.AreEqual("1", TclList.Parse(deg)[2], "grown scan cell → the 1×1 seed rect");
        }

        // lg_set_starttemplatepro without a latch returns "nodigmark" (FUN_00650f90:245-249 — the
        // story.tcl:191 abort); with a latch it anchors and returns the stored rect + utemp slot.
        // The faithful law also PICKS the dig-direction start piece from the ACTIVE POOL
        // (FUN_005f7980 → FUN_00585d80: a down dig wants a 4×4 with left+right doors) and
        // registers the forced record at the PRE-STEPPED pose — the walk offset starts at ONE
        // ±4 step along the dig direction (FUN_005f7a90 pre-call at:182, byte-verified), so
        // the piece's top-left = latchCell + 1 cell along the dig dir — it never overlaps the
        // player's tunnel (the carve↔stamp junction law,).
        [Test]
        public void LgSetStartTemplatePro_NoLatchIsNodigmark_LatchAnchors()
        {
            var (interp, ctx) = LightHost();
            interp.Eval("sm_create_map 64 64");
            interp.Eval("sm_get_level_info 4 4");
            // pool: one east-west through piece (down-dig signature) + one bottom cap (mode 0)
            ctx.LevelGen!.AddSyntheticPiece("syn_through.tcl", 4, 4, (0, 2, 0), (4, 2, 2));
            ctx.LevelGen.AddSyntheticPiece("syn_capdown.tcl", 4, 4, (2, 4, 3));
            Assert.IsTrue(interp.Eval("lg_set_starttemplatepro 1 1").Contains("nodigmark"),
                "no latch → nodigmark");
            ctx.StoryLatchDir = 2; ctx.StoryLatchX = 18; ctx.StoryLatchY = 18; ctx.StoryLatchConsumed = true;
            string r = interp.Eval("lg_set_starttemplatepro 1 1").Trim();
            TestContext.WriteLine($"starttemplatepro = {r}");
            Assert.IsFalse(r.Contains("nodigmark"), "live latch (no terrain → probes pass) → anchored");
            var t = TclList.Parse(r);
            Assert.AreEqual(ctx.StoryRectX.ToString(), t[0]);
            Assert.AreEqual(ctx.StoryRectW.ToString(), t[2]);
            // THE PRE-STEP LAW: latch (18,18) → cell (16,16); down dir pre-steps +4 on Y →
            // the forced record's ABSOLUTE pose is (16, 20) = latch cell + one cell down.
            Assert.AreEqual(1, ctx.LevelGen.ForcedRecordCount, "one forced start record");
            var rec = ctx.LevelGen.ForcedRecords[0];
            int absX = ctx.StoryRectX * 4 + rec.gx, absY = ctx.StoryRectY * 4 + rec.gy;
            TestContext.WriteLine($"forced record '{rec.tcl}' abs=({absX},{absY})");
            Assert.AreEqual(16, absX, "anchor X = latchX & ~3 (FUN_00650f90:179)");
            Assert.AreEqual(20, absY, "anchor Y = (latchY & ~3) + 4 — the PRE-STEP (:182)");
            // interior anchor → the mode-1 through piece survives (no near-border swap:261-273)
            Assert.AreEqual("syn_through.tcl", rec.tcl,
                "a down dig anchors the {left,right} through-corridor piece (FUN_00585d80:58-110)");
        }

        // ===================== DigNotify semantics (fabricated terrain) =====================

        // The growth gate accepts ONLY mode-1 dummy nibbles ((n&8)==0 && n!=0, FUN_005fd060:75-77):
        // a dummy-marked tunnel front ramps the weight (+4 per downward dig), a mode-0 depth order
        // (cnt|8) contributes NOTHING — the new-blocker correction #2 of the RE doc.
        [Test]
        public void DigNotify_DummyMarksRamp_ModeZeroDoesNot()
        {
            var (interp, ctx) = LightHost();
            var t = SolidTerrain(64, 64);
            ctx.Terrain = t;
            interp.Eval("sm_create_map 64 64");
            interp.Eval("sm_set_digcount 32");

            // mode-0 column at x=30: digging it never bumps the weight
            for (int ty = 10; ty <= 40; ty++) t.SetDigMark(30, ty, 0, 0);          // cnt|8
            for (int ty = 12; ty <= 20; ty++) interp.Eval($"dig_apply {{30 {ty} 8}}");
            Assert.AreEqual(0, ctx.StoryDigWeight, "mode-0 depth orders must not feed growth");
            Assert.AreEqual("0", interp.Eval("sm_scan").Trim());
            Assert.AreEqual("1", interp.Eval("sm_map_get 7 3").Trim(),
                "the story cell is marked by EVERY in-bounds dig (gate-independent, :71-73)");

            // mode-1 dummy column at x=20: +4 per downward dig (front marked, cell un-grown)
            for (int ty = 10; ty <= 40; ty++) t.SetDigMark(20, ty, 0, 0, dummy: true);
            interp.Eval("dig_apply {20 12 8}");
            Assert.AreEqual(4, ctx.StoryDigWeight, "downward front bump = +4 (dir 2, FUN_005fd060:113)");
            interp.Eval("dig_apply {20 13 8}");
            Assert.AreEqual(8, ctx.StoryDigWeight);
        }

        // Weight > threshold latches the breakthrough {dir,x,y}; sm_scan consumes once; sm_scan
        // does NOT reset the counter (only a placement-emitting lg_get_level does, FUN_00652d10:280)
        // so BEFORE a successful generation every later eligible dig re-latches immediately; an
        // unconsumed latch is overwritten by the next dig (correction #4 — latch cleared at:66-70).
        [Test]
        public void DigNotify_LatchScanRelatchOverwrite()
        {
            var (interp, ctx) = LightHost();
            var t = SolidTerrain(64, 64);
            ctx.Terrain = t;
            interp.Eval("sm_create_map 64 64");
            interp.Eval("sm_set_digcount 8");

            for (int ty = 10; ty <= 40; ty++) t.SetDigMark(20, ty, 0, 0, dummy: true);
            interp.Eval("dig_apply {20 12 8}");   // weight 4
            interp.Eval("dig_apply {20 13 8}");   // weight 8 (== threshold → no latch yet)
            Assert.AreEqual(0, ctx.StoryLatchDir, "latch fires on weight > threshold, not >=");
            interp.Eval("dig_apply {20 14 8}");   // weight 12 > 8 → latch (20,18) dir 2
            Assert.AreEqual(2, ctx.StoryLatchDir);
            Assert.AreEqual(20, ctx.StoryLatchX);
            Assert.AreEqual(18, ctx.StoryLatchY);

            // unconsumed latch overwritten by the NEXT dig (only the latest breakthrough survives)
            interp.Eval("dig_apply {20 15 8}");
            Assert.AreEqual(19, ctx.StoryLatchY, "newer dig re-latches before consumption");

            Assert.AreEqual("5 4", interp.Eval("sm_scan").Trim());
            Assert.AreEqual("0", interp.Eval("sm_scan").Trim(), "consume-once");

            // sm_scan alone does not reset the counter → the next eligible dig re-latches
            // immediately (the reset happens only when a generation EMITS placements —
            // see LgGetLevel_WithPlacements_RearmsDigcountThreshold below)
            interp.Eval("dig_apply {20 16 8}");
            Assert.AreEqual(2, ctx.StoryLatchDir);
            Assert.IsFalse(ctx.StoryLatchConsumed);
            Assert.AreEqual("5 5", interp.Eval("sm_scan").Trim());
        }

        // THE GROWTH PACING LAW (FUN_00652d10:73 — local_30 = FUN_005f8080 is the STORYMGR
        // singleton — and:280 `*(local_30+0x98) = 0` inside the per-placement emit loop): a
        // generation that EMITS placements zeroes the dig-pressure counter, so N further carve
        // strokes produce NO new breakthrough until a fresh sm_set_digcount worth of pressure
        // accumulates. This is what paces growth to "one cluster per ~digcount digs". Without it
        // (the pre-fix port) the weight stayed above the threshold forever after the first
        // breakthrough → EVERY dig stroke latched a new breakthrough → a new cave per swing +
        // multi-generation stamp bursts. A FAILED (empty)
        // generation must NOT reset — that is the story.tcl failcount retry channel.
        [Test]
        public void LgGetLevel_WithPlacements_RearmsDigcountThreshold()
        {
            var (interp, ctx) = LightHost();
            var t = SolidTerrain(64, 64);
            ctx.Terrain = t;
            interp.Eval("sm_create_map 256 256");   // 64×64 story cells — probes stay un-grown
            interp.Eval("sm_set_digcount 8");

            // synthetic pool (LevelGenTests.NewSyntheticLg shape) so lg_start can succeed
            var lg = ctx.LevelGen!;
            lg.AddSyntheticPiece("seed", 4, 4, (0, 0, 0), (4, 0, 2));
            lg.AddSyntheticPiece("corr", 8, 4, (0, 0, 0), (8, 0, 2));
            lg.AddSyntheticPiece("capL", 8, 8, (8, 0, 2));
            lg.AddSyntheticPiece("capR", 8, 8, (0, 0, 0));

            // dig a dummy-marked column until the breakthrough latches (+4 per down-dig)
            for (int ty = 10; ty <= 60; ty++) t.SetDigMark(20, ty, 0, 0, dummy: true);
            int tyCur = 12;
            for (; tyCur <= 40 && ctx.StoryLatchDir == 0; tyCur++)
                interp.Eval($"dig_apply {{20 {tyCur} 8}}");
            Assert.AreEqual(2, ctx.StoryLatchDir, "the down-dig column must latch a breakthrough");
            Assert.AreNotEqual("0", interp.Eval("sm_scan").Trim(), "sm_scan must consume the latch");

            // an EMPTY lg_get_level (nothing generated) must NOT reset — failcount retry channel
            int weightBefore = ctx.StoryDigWeight;
            Assert.Greater(weightBefore, 8, "precondition: pressure above threshold");
            Assert.IsEmpty(interp.Eval("lg_get_level").Trim(), "no generation ran yet");
            Assert.AreEqual(weightBefore, ctx.StoryDigWeight,
                "an empty lg_get_level must NOT re-arm (failed generations retry on the next dig)");

            // a SUCCESSFUL generation (area away from the dug story cells) + placement emit
            lg.SetArea(128, 128, 192, 160);
            lg.SetTemplateRatio(0f, 0.3f, 1f);
            bool started = false;
            for (int k = 0; k < 20 && !started; k++) started = interp.Eval("lg_start").Trim() == "1";
            Assert.IsTrue(started, "synthetic lg_start must succeed");
            Assert.IsNotEmpty(interp.Eval("lg_get_level").Trim(), "lg_get_level must emit placements");

            // THE LAW: the emit re-arms the digcount threshold …
            Assert.AreEqual(0, ctx.StoryDigWeight,
                "a placement-emitting lg_get_level must zero the dig pressure (FUN_00652d10:280)");

            // … so further swings do NOT latch until the threshold is crossed again
            interp.Eval($"dig_apply {{20 {tyCur} 8}}"); tyCur++;   // +4 → 4 (≤ 8)
            interp.Eval($"dig_apply {{20 {tyCur} 8}}"); tyCur++;   // +4 → 8 (≤ 8, strict >)
            Assert.AreEqual(0, ctx.StoryLatchDir,
                "no per-swing relatch after a successful generation (the 2026-07-05 cave-per-swing bug)");
            interp.Eval($"dig_apply {{20 {tyCur} 8}}"); tyCur++;   // +4 → 12 > 8 → fresh breakthrough
            Assert.AreEqual(2, ctx.StoryLatchDir, "the threshold must re-arm (growth continues later)");
        }

        // Bounds gate (FUN_005fd060:29-32): digs within 8 tiles of a border do NOTHING (no story
        // mark, no weight). Story cells already grown don't bump either (the un-grown probe).
        [Test]
        public void DigNotify_BoundsAndGrownCellGates()
        {
            var (interp, ctx) = LightHost();
            var t = SolidTerrain(64, 64);
            ctx.Terrain = t;
            interp.Eval("sm_create_map 64 64");
            interp.Eval("sm_set_digcount 8");

            for (int ty = 0; ty <= 30; ty++) t.SetDigMark(4, ty, 0, 0, dummy: true);
            interp.Eval("dig_apply {4 12 8}");    // x=4 < 8 → bounds-gated
            Assert.AreEqual(0, ctx.StoryDigWeight);
            Assert.AreEqual("0", interp.Eval("sm_map_get 1 3").Trim(), "border dig must not mark the story map");

            // pre-grow the front cell → the probe skips it (FUN_005fa5d0 == 1 check)
            for (int ty = 10; ty <= 40; ty++) t.SetDigMark(20, ty, 0, 0, dummy: true);
            interp.Eval("sm_map_set 5 4 1");      // cell of (20,16..19)
            interp.Eval("dig_apply {20 12 8}");   // +y probe (20,16) → cell (5,4) grown → next dirs fail too
            Assert.AreEqual(0, ctx.StoryDigWeight, "a grown front cell contributes no pressure");
        }

        // DigNotify fires the sm_register'd callback "<cb> dig shut -1" in the OBJECT's scope
        // (FUN_005fd060:33-64) — BUT ONLY when the dig-message flag +0xa0 is set (BA/BC —
        // FUN_005fd060:33 gates on `+0xa0 != 0 && +0x78 != -1`). +0xa0 = sm_dig_message
        // (FUN_0065c500), ctor default 0 (FUN_005fa7c0:33). The shipped campaign never enables it
        // (the `sm_dig_message 1` calls are commented, story.tcl:524/539/555), so newsticker_dig
        // never runs → the Umzug is never fired prematurely. This test proves BOTH the default-off
        // gate and the sm_dig_message setter that opens it.
        [Test]
        public void DigNotify_StoryCallback_GatedOnDigMessageFlag()
        {
            var (interp, ctx) = LightHost();
            var t = SolidTerrain(64, 64);
            ctx.Terrain = t;
            interp.Eval("sm_create_map 64 64");
            interp.Eval(@"def_class TestSM none info 0 {} {
                obj_init { proc callback {trg msg tid} { global cb_log; lappend cb_log ""$trg $msg $tid"" } }
            }");
            interp.Eval("set sm [new TestSM]");
            interp.Eval("sm_register $sm callback");
            for (int ty = 12; ty <= 14; ty++) t.SetDigMark(20, ty, 0, 0, dummy: true);
            var smEnt = ctx.GetEntity(interp.Eval("set sm").Trim());
            Assert.IsNotNull(smEnt);

            // gate DEFAULT OFF (+0xa0 = 0, FUN_005fa7c0:33) → the callback must NOT fire
            Assert.IsFalse(ctx.StoryDigMessage, "ctor default: dig-message off");
            interp.Eval("dig_apply {20 12 8}");
            Assert.IsFalse(smEnt!.VarsScope.Vars.ContainsKey("cb_log"),
                "with dig-message OFF the story callback must NOT fire (the BA premature-Umzug gate)");

            // sm_dig_message 1 (BC) flips +0xa0 and returns the boolean → the next dig fires the cb
            Assert.AreEqual("1", interp.Eval("sm_dig_message 1").Trim(), "setter returns the parsed boolean");
            Assert.IsTrue(ctx.StoryDigMessage);
            interp.Eval("dig_apply {20 13 8}");
            Assert.IsTrue(smEnt.VarsScope.Vars.TryGetValue("cb_log", out var log) && log!.Contains("dig shut -1"),
                $"with dig-message ON the callback runs in the StoryMgr's object scope (cb_log='{(smEnt.VarsScope.Vars.TryGetValue("cb_log", out var l) ? l : "<unset>")}')");

            // sm_dig_message 0 turns it back off (the only live shipped call, story.tcl:792)
            Assert.AreEqual("0", interp.Eval("sm_dig_message 0").Trim());
            Assert.IsFalse(ctx.StoryDigMessage);
        }

        // sm_add_event (BB) — FUN_0065a820: registers a story-event NAME (so sm_get_event/quest-log
        // see it) + stores the optional Trigger_* class and keyword metadata (:122-162), returning the
        // insertion index (:203). sm_get_event/sm_set_event round-trip the FLAG (create-on-write, so
        // the growth/zone path never depended on registration — sm_add_event is not
        // generator-relevant). Re-registration is idempotent (native errors; the port returns the
        // existing index and must not clear a set flag).
        [Test]
        public void SmAddEvent_RegistersEvent_MetadataAndGetSetRoundTrip()
        {
            var (interp, ctx) = LightHost();
            interp.Eval("sm_create_map 64 64");   // fresh event registry
            Assert.AreEqual("0", interp.Eval("sm_add_event Voodoo_Hoehle_entdeckt Trigger_FirstContact").Trim(),
                "first event → index 0 (FUN_005f8670 append)");
            Assert.AreEqual("1", interp.Eval("sm_add_event Einsiedler_Karte_erhalten Trigger_Einsiedler_2 shut").Trim(),
                "second event → index 1");
            Assert.AreEqual("2", interp.Eval("sm_add_event Uebergang_Urw_Met").Trim(),
                "a trigger-less event still registers");

            // metadata captured (the Trigger_* class + keyword,:122-162)
            Assert.IsTrue(ctx.StoryEventTriggers.TryGetValue("Einsiedler_Karte_erhalten", out var m));
            Assert.AreEqual("Trigger_Einsiedler_2", m.Trigger);
            Assert.AreEqual("shut", m.Extra);
            Assert.AreEqual("", ctx.StoryEventTriggers["Uebergang_Urw_Met"].Trigger, "no trigger → empty metadata");

            // a registered event reads 0 (unset) then round-trips through set/get — the exact
            // check_uebergang gate (story.tcl:622 `set ue [sm_get_event Uebergang_Urw_Met]`)
            Assert.AreEqual("0", interp.Eval("sm_get_event Uebergang_Urw_Met").Trim(), "registered but unset → 0");
            interp.Eval("sm_set_event Uebergang_Urw_Met");
            Assert.AreEqual("1", interp.Eval("sm_get_event Uebergang_Urw_Met").Trim(), "set → 1");

            // idempotent re-registration: SAME index, no throw, metadata PRESERVED, flag preserved.
            // The native errors out ("Story Event redefinition", FUN_0065a820:118-125 via
            // FUN_005f8670 == -1) BEFORE the metadata store — an existing event's trigger/keyword
            // is NEVER touched. Scripts bank on that: Trigger_Urwald.tcl:678 re-adds
            // Torwaechterin_befreit bare inside catch{}; the port's earlier metadata overwrite
            // severed CampaignInit.tcl:593's binding and killed the Metalltor gate scene
            Assert.AreEqual("0", interp.Eval("sm_add_event Voodoo_Hoehle_entdeckt Trigger_Other").Trim(),
                "re-add returns the SAME index (native errors; the port is idempotent)");
            Assert.AreEqual("Trigger_FirstContact", ctx.StoryEventTriggers["Voodoo_Hoehle_entdeckt"].Trigger,
                "redefinition must NOT clobber the original trigger binding (FUN_0065a820 errors before the store)");
            Assert.AreEqual("1", interp.Eval("sm_get_event Uebergang_Urw_Met").Trim(),
                "re-adding another event must NOT clear an already-set flag");
        }

        // ===================== the world-border stone band (FUN_0065e090 -border) =====================

        // `sm_draw_stone -border` (FUN_0065e090:930-941, byte-verified) paints three
        // probabilistic STONE-CLASS-0 bands via FUN_005fc9d0 (material arg = literal 0): top 32
        // rows (strength 2.5, falloff flag 1), left/right 32 columns (strength 1.0, flags 4/8).
        // Class 0 = granite/undiggable at every marker/carve gate — this is what stops a player
        // digging to the world edge in the original. Taper law (FUN_005fc9d0:54-121):
        // p = 1 − (d/span)²/(strength/(span·0.2)) on the INNER half only; accept iff
        // (rand&0x7fff) <= p·32768; tiles already class 0/3 skipped. Port representation:
        // terrain byte 4 (TileMaterialType → 0).
        [Test]
        public void DrawStone_BorderBand_TaperAndGates()
        {
            int W = 512, H = 640;
            var terrain = new byte[W * H];
            var rng = new System.Random(1234);
            // the -border triple (FUN_0065e090:930-941; H+4 = SM+0x80*4+4, clamped by the painter)
            BdbPmpWorldBuilder.DrawStoneIntoWorld(terrain, null, W, H, 0, 0, W, 32, 1, 0, 2.5f, rng);
            BdbPmpWorldBuilder.DrawStoneIntoWorld(terrain, null, W, H, 0, 0, 32, H + 4, 4, 0, 1.0f, rng);
            BdbPmpWorldBuilder.DrawStoneIntoWorld(terrain, null, W, H, W - 32, 0, W, H + 4, 8, 0, 1.0f, rng);

            bool Stone(int x, int y) => (terrain[y * W + x] & 0x7F) == 4;
            // OUTER HALF of the side bands = p 1.0 → solid stone (left x 0..16, right x 496..510;
            // the native loop excludes the last row/col — x 511 stays untouched, covered by the
            // ±1-tile marker rim FUN_00566870:19-22).
            int solidL = 0, solidR = 0, taperFar = 0, n = 0;
            for (int y = 40; y < 600; y++)
            {
                n++;
                if (Stone(2, y)) solidL++;
                if (Stone(505, y)) solidR++;
                if (Stone(30, y)) taperFar++;      // p(x=30) = 1 − 14²/160 < 0 → never painted
            }
            Assert.AreEqual(n, solidL, "left band outer half must be SOLID stone (p=1)");
            Assert.AreEqual(n, solidR, "right band outer half must be SOLID stone (p=1)");
            Assert.AreEqual(0, taperFar, "x=30 is past the quadratic taper zero (~x 28.6) — never stone");
            Assert.IsFalse(Stone(256, 320), "map interior untouched");
            Assert.IsFalse(Stone(511, 320), "native loop excludes the last column (the ±1 rim covers it)");
            // taper midpoint x=20: p = 1 − 16/160 = 0.9 → mostly stone but not all
            int mid = 0;
            for (int y = 40; y < 600; y++) if (Stone(20, y)) mid++;
            TestContext.WriteLine($"taper x=20: {mid}/{n} stone (expect ≈0.9)");
            Assert.Greater(mid, (int)(n * 0.8), "taper x=20 ≈ 90% stone");
            Assert.Less(mid, n, "taper x=20 must not be fully solid");
            // top band (strength 2.5): rows 0..15 = p 1.0 solid across the interior
            for (int x = 64; x < 448; x += 37)
                Assert.IsTrue(Stone(x, 8), $"top band row 8 must be solid stone (x={x})");
            // boot-order emulation skips only tile records actually overwritten by a template
            var t2 = new byte[W * H];
            var recordWrites = new System.Collections.BitArray(W * H);
            recordWrites[100 * W + 2] = true;
            BdbPmpWorldBuilder.DrawStoneIntoWorld(t2, recordWrites, W, H, 0, 0, 32, H + 4, 4, 0, 1.0f, new System.Random(7));
            Assert.AreEqual(0, t2[100 * W + 2] & 0x7F,
                "a template-written tile record must survive the delayed boot paint");
            Assert.AreEqual(4, t2[100 * W + 3] & 0x7F,
                "an adjacent tile not written by the template must keep the earlier native paint");
        }

        // Campaign.tcl appends a `mat {type x y w h}...` record to the generated level;
        // mp_tools.tcl:26-28 applies it AFTER MapTemplateSet through this exact live command.
        // FUN_0065e090:190-206 translates every rect by argv[2:3], grows its far edge +2,
        // and calls FUN_005fc9d0 with flags 0 / strength 2.5 / class=type. The painter then
        // excludes its final row and column (FUN_005fc9d0:56,62) and publishes RENDERJOBS over
        // the surrounding band (:127-138; dispatcher FUN_005a46d0:333-401).
        [Test]
        public void DrawStone_MatList_LiveCommandPaintsOffsetRectsAndPublishesEffectBand()
        {
            const int W = 32, H = 32;
            var (interp, ctx) = LightHost();
            var result = new BdbPmpWorldBuilder.TclWorldResult
            {
                Width = W,
                Height = H,
                BlockIdx = new uint[W * H],
                Terrain = new byte[W * H],
            };
            var terrainObject = new GameObject(nameof(DrawStone_MatList_LiveCommandPaintsOffsetRectsAndPublishesEffectBand));
            _spawnedGos.Add(terrainObject);
            var terrain = terrainObject.AddComponent<DiggableTerrain>();
            terrain.Init(result, null!, "", () => { });

            var map = new RuntimeMapBuilder("", "");
            map.AttachLiveWorld(result, terrain);
            ctx.MapBuilder = map;
            ctx.StoryMapW = W / 4;
            ctx.StoryMapH = H / 4;
            ctx.StoryRng = new System.Random(7);

            (int x0, int y0, int x1, int y1)? changed = null;
            int materialDuringEvent = -1;
            terrain.TilesChanged += (x0, y0, x1, y1) =>
            {
                changed = (x0, y0, x1, y1);
                materialDuringEvent = result.NativeTerrainFlags[7 * W + 7] & 3;
            };

            interp.Eval("sm_draw_stone -matlist 3 2 {mat {0 4 5 4 3}}");

            bool Stone(int x, int y) => (result.Terrain[y * W + x] & 0x7F) == 4;
            Assert.That(Stone(7, 7), Is.True, "offset origin must be painted");
            Assert.That(Stone(11, 10), Is.True, "+2 far-edge growth leaves this as the final painted cell");
            Assert.That(Stone(12, 10), Is.False, "FUN_005fc9d0 excludes the last column");
            Assert.That(Stone(11, 11), Is.False, "FUN_005fc9d0 excludes the last row");
            Assert.That(changed, Is.EqualTo((6, 6, 13, 12)),
                "the live material mutation must synchronously publish its rect±1 RENDERJOBS band");
            Assert.That(materialDuringEvent, Is.Zero,
                "the painted native +0x18 material class must be visible before the effect event");
        }

        [Test]
        public void LiveStamp_MatListPreservesAuthoredTerrainAndPaintsTransparentPadding()
        {
            const int width = 64;
            const int height = 64;
            const int originX = 8;
            const int originY = 8;
            int tileCount = width * height;
            var blocks = new uint[tileCount];
            for (int index = 0; index < blocks.Length; index++) blocks[index] = 7;
            var result = new BdbPmpWorldBuilder.TclWorldResult
            {
                Width = width,
                Height = height,
                BlockIdx = blocks,
                Zone = new byte[tileCount],
                Terrain = new byte[tileCount],
                HasTpl = new System.Collections.BitArray(tileCount),
            };
            var terrainObject = new GameObject(nameof(LiveStamp_MatListPreservesAuthoredTerrainAndPaintsTransparentPadding));
            _spawnedGos.Add(terrainObject);
            var terrain = terrainObject.AddComponent<DiggableTerrain>();
            terrain.Init(result, null!, DataRoot, () => { });
            var map = new RuntimeMapBuilder(DataRoot, "");
            map.AttachLiveWorld(result, terrain);

            const string pmpName = "gng_029.pmp";
            PmpMap pmp = PmpParser.Parse(System.IO.Path.Combine(DataRoot, "Templates/pmp", pmpName));
            map.LoadPmp(pmpName, originX, originY);

            int authoredIndex = -1;
            int transparentIndex = -1;
            for (int py = 0; py < pmp.Height; py++)
            for (int px = 0; px < pmp.Width; px++)
            {
                int worldIndex = (originY + py) * width + originX + px;
                if (BdbPmpWorldBuilder.PmpTileTransparent(pmp, px, py))
                {
                    int material = BdbPmpWorldBuilder.MaterialTypeAtTile(
                        result.Terrain,
                        width,
                        height,
                        originX + px,
                        originY + py);
                    if (transparentIndex < 0 && (material == 1 || material == 2))
                        transparentIndex = worldIndex;
                }
                else if (authoredIndex < 0 &&
                         (BdbPmpWorldBuilder.MaterialTypeFromTerrainByte(pmp.TerrainAt(px, py)) == 1 ||
                          BdbPmpWorldBuilder.MaterialTypeFromTerrainByte(pmp.TerrainAt(px, py)) == 2))
                {
                    authoredIndex = worldIndex;
                }
            }
            Assert.That(authoredIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(transparentIndex, Is.GreaterThanOrEqualTo(0),
                "fixture needs native-transparent padding in material class 1 or 2 so DrawStone visits it");
            byte authoredTerrain = result.Terrain[authoredIndex];
            uint authoredBlock = result.BlockIdx[authoredIndex];

            Assert.That(result.TemplateRecordWrites[authoredIndex], Is.True);
            Assert.That(result.TemplateRecordWrites[transparentIndex], Is.False,
                "FUN_005992b0's transparent branch carries no tile-record write");
            map.DrawStone(0, 0, width, height, 0, 0, 1f, new System.Random(7));

            Assert.That(result.Terrain[authoredIndex], Is.EqualTo(authoredTerrain),
                "the delayed live matlist must not replace authored debris terrain");
            Assert.That(result.BlockIdx[authoredIndex], Is.EqualTo(authoredBlock),
                "material painting must never replace the authored block id");
            Assert.That(result.Terrain[transparentIndex] & 0x7F, Is.EqualTo(4),
                "native-transparent padding must retain the earlier stone paint");
            Assert.That(result.BlockIdx[transparentIndex], Is.EqualTo(7),
                "transparent padding must retain the pre-existing world block id");
        }

        // ===================== native blit laws (FUN_005992b0) =====================

        // TRANSPARENCY (FUN_005992b0:292-454): a source tile whose 2×2 terrain-corner window is all
        // zero (&0x7f) keeps the pre-existing block/material/height record fields. HasTpl and the
        // authored navigation seed is an independent whole-rect pass (:461-568). 34 shipped PMPs carry such
        // filler tiles (all block 0), incl. the Urwald shaft pieces gng_029/030/032 — the old
        // verbatim blit walled the world over with their block-0 filler.
        [Test]
        public void BlitPmp_TransparentTiles_KeepPreexistingWorld()
        {
            var pmpPath = System.IO.Path.Combine(DataRoot, "Templates/pmp/gng_029.pmp");
            var pmp = PmpParser.Parse(pmpPath);
            int W = 64, H = 64, ox = 8, oy = 8;
            var blk = new uint[W * H];
            for (int i = 0; i < blk.Length; i++) blk[i] = 7;   // sentinel pre-existing world
            var zone = new byte[W * H]; var ter = new byte[W * H];
            var hasTpl = new System.Collections.BitArray(W * H);
            var recordWrites = new System.Collections.BitArray(W * H);

            BdbPmpWorldBuilder.BlitPmpIntoWorld(pmp, ox, oy, W, H, blk, zone, ter, hasTpl, null,
                (x, y) => recordWrites[y * W + x] = true);

            int transparent = 0, kept = 0, covered = 0, recordSkipped = 0, stamped = 0, recorded = 0;
            for (int py = 0; py < pmp.Height; py++)
                for (int px = 0; px < pmp.Width; px++)
                {
                    uint got = blk[(oy + py) * W + (ox + px)];
                    if (BdbPmpWorldBuilder.PmpTileTransparent(pmp, px, py))
                    {
                        transparent++;
                        if (got == 7) kept++;
                        if (hasTpl[(oy + py) * W + (ox + px)]) covered++;
                        if (!recordWrites[(oy + py) * W + (ox + px)]) recordSkipped++;
                    }
                    else
                    {
                        if (got == pmp.BlockIdxAt(px, py)) stamped++;
                        if (recordWrites[(oy + py) * W + (ox + px)]) recorded++;
                    }
                }
            TestContext.WriteLine($"gng_029: {transparent} transparent tiles, {kept} kept, {stamped} stamped");
            Assert.Greater(transparent, 0, "gng_029 must contain native-transparent filler tiles (data has 15)");
            Assert.AreEqual(transparent, kept,
                "every native-transparent source tile must leave the pre-existing tile-record block untouched (FUN_005992b0:292-454)");
            Assert.AreEqual(transparent, covered,
                "the independent tile+0x13 pass covers transparent padding too (FUN_005992b0:461-529)");
            Assert.AreEqual(transparent, recordSkipped,
                "transparent padding must not enter the tile-record-write mask (FUN_005992b0:292-454)");
            Assert.Greater(stamped, 0, "non-transparent tiles must still stamp");
            Assert.AreEqual(stamped, recorded, "every stamped tile record must enter the delayed-paint mask");
        }

        // PRE-CUT CELLS: block 59 in template DATA = an authored open (pre-dug) cell — hol_023_cut
        // is hol_023 with 36 interior tiles pre-carved to block 59 (valid floor heights). The
        // native blit copies 59 verbatim (FUN_005992b0:379-415 — no 59 special case) and renders
        // it as a dug-out floor. Our renderer skips block 59 unless the DugFloor mask is set —
        // RefreshRect must therefore mark stamped 59 tiles dug, else every pre-cut tile is a
        // see-through HOLE (and the seam metric was BLIND to it: un-rendered tiles are simply
        // skipped by the edge comparison).
        [Test]
        public void RefreshRect_StampedBlock59_RendersAsDugFloor()
        {
            // the data linkage: the _cut variant really is the pre-carved twin
            var cut = PmpParser.Parse(System.IO.Path.Combine(DataRoot, "Templates/pmp/hol_023_cut.pmp"));
            int cut59 = 0;
            for (int py = 0; py < cut.Height; py++)
                for (int px = 0; px < cut.Width; px++)
                    if (cut.BlockIdxAt(px, py) == 59) cut59++;
            Assert.AreEqual(36, cut59, "hol_023_cut must carry 36 pre-cut (block-59) tiles");

            // the render law, on a fabricated live world (bdb-null Init, same shared-array wiring
            // as Play: r.DugFloor IS DiggableTerrain._dug) driven through the REAL blit chain
            // (BlitPmpIntoWorld + the LiveBlitPmp onTileStamped=SyncStampedTile wiring).
            var go = new GameObject("DiggableTerrain59");
            _spawnedGos.Add(go);
            var t = go.AddComponent<DiggableTerrain>();
            int W = 48, H = 48, ox = 12, oy = 12;
            var r = new BdbPmpWorldBuilder.TclWorldResult
            {
                Width = W, Height = H,
                BlockIdx = new uint[W * H],   // block 0 = solid
                Terrain = new byte[W * H],
                Zone = new byte[W * H],
                HasTpl = new System.Collections.BitArray(W * H),
            };
            t.Init(r, null!, "", () => { });
            Assert.IsNotNull(r.DugFloor, "Init must share the dug mask with the renderer");

            BdbPmpWorldBuilder.BlitPmpIntoWorld(cut, ox, oy, W, H,
                r.BlockIdx, r.Zone, r.Terrain, r.HasTpl, null,
                t.SyncStampedTile);   // exactly LiveBlitPmp's wiring (template-wins, no skip)

            int dug59 = 0, invisible59 = 0;
            for (int py = 0; py < cut.Height; py++)
                for (int px = 0; px < cut.Width; px++)
                {
                    if (cut.BlockIdxAt(px, py) != 59) continue;
                    int ii = (oy + py) * W + (ox + px);
                    Assert.AreEqual(59u, r.BlockIdx[ii], "the pre-cut tile must have stamped");
                    if (r.DugFloor![ii]) dug59++; else invisible59++;
                }
            TestContext.WriteLine($"stamped 59-tiles: {dug59} render as dug floor, {invisible59} invisible");
            Assert.AreEqual(0, invisible59,
                "every stamped block-59 (pre-cut) tile must be marked dug — an unset mask renders NOTHING (see-through hole)");
            Assert.AreEqual(36, dug59);
            Assert.IsFalse(r.DugFloor![(oy - 1) * W + ox], "tiles outside the stamp stay un-dug");
        }

        // ===================== stage 5 — incremental live emit (real PMP, no class load) =====================

        // Post-EmitTerrain, map_template must blit into the LIVE arrays (BlockIdx/Voxels/HasTpl/
        // CellId shared with DiggableTerrain + NavWorld), extend the render bbox and revalidate nav
        // over the stamped rect — NEVER re-EmitTerrain (worldgen-wiring-re.md §3).
        [Test, Explicit("Loads the real BDB + start template — run on demand")]
        public void LiveMode_MapTemplate_BlitsIntoLiveWorld()
        {
            var rootGo = new GameObject("WorldRoot");
            _spawnedGos.Add(rootGo);
            rootGo.transform.localRotation = Quaternion.Euler(270f, 0f, 0f);
            var engine = new DigglesEngine();
            engine.Build(DataRoot, rootGo.transform, "BlockDB2.BDB");
            engine.Interp.Eval("map create 512 640");
            engine.Interp.Eval("set temppos {208 0}");
            try { engine.Interp.Eval("call templates/tcl/urw_unq_start.tcl"); }
            catch (System.Exception e) { TestContext.WriteLine($"[call] {e.Message}"); }
            engine.Map.EmitTerrain(rootGo);
            var terr = rootGo.GetComponent<DiggableTerrain>();
            Assert.IsNotNull(terr, "no DiggableTerrain after EmitTerrain");
            engine.Ctx.Terrain = terr;
            var r = engine.Map.WorldResult!;
            Assert.IsTrue(engine.Map.LiveMode, "post-emit = live mode");

            // stamp a real 16×16 cave chunk far from the start area, where nothing is templated yet
            const int sx = 100, sy = 200;
            Assert.IsFalse(r.HasTpl[sy * r.Width + sx], "stamp target must be un-templated");
            uint blockBefore = r.BlockIdx[sy * r.Width + sx];
            engine.Interp.Eval($"map_template splt_hol_014_tAA.pmp {sx} {sy}");
            Assert.IsTrue(r.HasTpl[sy * r.Width + sx], "live blit must set HasTpl in the rect");
            // the template's block layout replaces the virgin block somewhere in the rect
            bool blockChanged = false;
            for (int ty = sy; ty < sy + 16 && !blockChanged; ty++)
                for (int tx = sx; tx < sx + 16 && !blockChanged; tx++)
                    blockChanged = r.BlockIdx[ty * r.Width + tx] != blockBefore;
            Assert.IsTrue(blockChanged, "live blit must write BlockIdx");
            Assert.IsTrue(r.RenderXMin <= sx && r.RenderXMax >= sx + 15
                          && r.RenderYMin <= sy && r.RenderYMax >= sy + 15,
                "render bbox must extend to cover the stamped rect");

            // nav: the stamped tiles become derivable (EFFPASSWALK over the dirty rect)
            terr!.RevalidateDirtyNav();
            var nav = terr.Nav!;
            int walkable = 0;
            for (int gy = sy * 8; gy < (sy + 16) * 8; gy++)
                for (int gx = sx * 8; gx < (sx + 16) * 8; gx++)
                    if (nav.SubWalkable(gx, gy)) walkable++;
            TestContext.WriteLine($"walkable sub-cells in the stamped rect: {walkable}");
            Assert.Greater(walkable, 0, "the stamped chunk must yield walkable floor after RevalidateRect");
        }

        // ===================== full chain on the REAL campaign data =====================

        static (GameObject root, DigglesEngine engine) BootCampaign(List<GameObject> own)
        {
            var rootGo = new GameObject("WorldRoot");
            own.Add(rootGo);
            rootGo.transform.localRotation = Quaternion.Euler(270f, 0f, 0f);
            var engine = new DigglesEngine();
            engine.Build(DataRoot, rootGo.transform, "BlockDB2.BDB");
            foreach (var s in new[] { "scripts/init/txtinit.tcl", "scripts/init/animinit.tcl",
                                      "scripts/init/classinit.tcl", "scripts/init/lginit.tcl" })
                try { engine.Interp.Eval("call " + s); }
                catch (System.Exception e) { TestContext.WriteLine($"[init] {s}: {e.Message}"); }
            Assert.IsTrue(engine.Ctx.Classes.ContainsKey("StoryMgr"), "classinit must register StoryMgr");
            // The faithful new-game wrapper (GameScripts/single_CampaignX.tcl:1-4) — the same lines
            // DigglesWorldRuntime.RunCampaign now boots through.
            engine.Interp.Eval("obj_clear\nsel /obj\nset sm [new StoryMgr]\ncall_method $sm load_level Campaign");
            engine.Map.EmitTerrain(rootGo);
            engine.Ctx.Terrain = rootGo.GetComponent<DiggableTerrain>();
            return (rootGo, engine);
        }

        static DigglesTclEntity? FindByClass(DigglesTclContext ctx, string cls)
        {
            foreach (var e in ctx.Entities.Values) if (e.Class?.Name == cls) return e;
            return null;
        }

        // Stage 1 — the boot chain headless: single_CampaignX-equivalent boot loads CampaignInit
        // (sm_create_map 512 640, sm_set_digcount 32, zones, unique temps) + Campaign (boot stamps,
        // first_level occupancy marks) inside the StoryMgr object context.
        [Test, Explicit("Boots the full real campaign (classes + templates) — run on demand")]
        public void Boot_StoryMgrCampaignChain_Headless()
        {
            var (root, engine) = BootCampaign(_spawnedGos);
            var ctx = engine.Ctx;

            var sm = FindByClass(ctx, "StoryMgr");
            Assert.IsNotNull(sm, "new StoryMgr must spawn an entity");
            Assert.AreEqual("idle", sm!.CurrentState, "obj_init state_triggerfresh idle");
            Assert.AreEqual("4", engine.Interp.Eval("sm_get_resolution").Trim());
            Assert.AreEqual(32, ctx.StoryDigThreshold, "CampaignInit.tcl:36 sm_set_digcount 32");
            Assert.AreEqual(128, ctx.StoryMapW, "sm_create_map 512 → 128 cells");
            Assert.AreEqual(160, ctx.StoryMapH, "sm_create_map 640 → 160 cells");
            Assert.AreEqual(4, ctx.StoryZones.Count, "Urwald/Metall/Kristall/Lava");
            Assert.AreEqual("Urwald", ctx.StoryZones[0].Name);
            Assert.IsTrue(ctx.StoryTemps.Exists(t => t.Name == "urw_der_einsiedler" && t.Lo == 56 && t.Hi == 60),
                "CampaignInit unique-temp windows registered");
            Assert.IsTrue(ctx.LevelGen!.HasGroup("Urwald.Std.Gng"), "lginit pools registered");
            Assert.Greater(ctx.StoryMap.Count, 0, "first_level sm_mark_temparea must mark the boot stamps");
            Assert.IsTrue(ctx.StoryPlacedTemps.Exists(p => p.name == "urw_unq_start"),
                "Campaign.tcl:20 sm_set_temp boot registry");
            Assert.IsNotNull(ctx.Terrain, "EmitTerrain must build the diggable terrain");
            Assert.IsNotNull(ctx.Terrain!.Nav, "the terrain must carry a NavWorld");
            Assert.IsNotNull(FindByClass(ctx, "Zwerg"), "the start template must spawn dwarves");
            TestContext.WriteLine($"entities={ctx.Entities.Count} storyMarks={ctx.StoryMap.Count} " +
                                  $"pmps={engine.Map.PmpsLoaded} terrain={ctx.Terrain.Width}x{ctx.Terrain.Height}");
        }

        // Stage 2 — the think loop: the StoryMgr idle state ticks on the normal state pass,
        // StartCnt drains via wait_time, GameOverCheck stays quiet with dwarves alive, sm_scan
        // returns 0 with no digs.
        [Test, Explicit("Boots the full real campaign + ~15 game-seconds of sim — run on demand")]
        public void ThinkLoop_StoryIdle_NoGameOver()
        {
            var (root, engine) = BootCampaign(_spawnedGos);
            var ctx = engine.Ctx;
            var sm = FindByClass(ctx, "StoryMgr")!;

            for (int i = 0; i < 150; i++) engine.Sim.Step(0.1);   // 15 game-seconds

            Assert.IsTrue(sm.VarsScope.Vars.TryGetValue("StartCnt", out var sc) && sc == "0",
                $"StartCnt must drain (now '{(sm.VarsScope.Vars.TryGetValue("StartCnt", out var v) ? v : "<unset>")}')");
            Assert.AreNotEqual("disabled", sm.CurrentState,
                "GameOver must NOT fire with dwarves alive (obj_query -owner player)");
            Assert.IsNotNull(FindByClass(ctx, "Zwerg"), "dwarves must survive the think loop");
            Assert.AreEqual("0", engine.Interp.Eval("sm_scan").Trim(), "no digs → no latch");
        }

        // Stages 3-5 — THE MONEY TEST: a standard-tunnel (mode-1 dummy) dig line below the meadow
        // ramps DigNotify past sm_set_digcount, sm_scan hands the cell to story.tcl, generate_next
        // anchors the free rect at the dig and lg_start fills it, load_next stamps the templates
        // into the LIVE world arrays (RuntimeMapBuilder live mode), the story map is marked and
        // walkable nav cells appear in the grown region.
        [Test, Explicit("Boots the full real campaign + drives the growth loop — run on demand")]
        public void Growth_DigToGeneration_StampsAdjacentToDig()
        {
            var (root, engine) = BootCampaign(_spawnedGos);
            var ctx = engine.Ctx;
            var interp = engine.Interp;
            var terr = ctx.Terrain!;
            Assert.IsTrue(engine.Map.LiveMode, "post-EmitTerrain the map builder must be in live mode");

            // settle the StartCnt grace (5 × wait_time 1.0) so the idle loop reaches sm_scan
            for (int i = 0; i < 70; i++) engine.Sim.Step(0.1);

            // ---- find a dig site: a 4-aligned solid column ≥8 tiles from borders whose own and
            // downward story cells are un-grown (so the breakthrough has room to grow) ----
            int dx = -1, dy0 = -1;
            for (int y0 = 24; y0 < 200 && dx < 0; y0 += 4)
                for (int x = 16; x < terr.Width - 24; x += 4)
                {
                    bool ok = true;
                    for (int k = 0; k <= 20 && ok; k++)
                        for (int ox = 0; ox < 4 && ok; ox++) ok = terr.HasGeometry(x + ox, y0 + k);
                    for (int k = 0; k <= 6 && ok; k++)
                        ok = interp.Eval($"sm_map_get {x / 4} {y0 / 4 + k}").Trim() == "0";
                    if (ok) { dx = x; dy0 = y0; break; }
                }
            Assert.GreaterOrEqual(dx, 0, "no virgin solid dig site found");
            TestContext.WriteLine($"dig site: column x={dx}..{dx + 3}, y from {dy0}");

            // ---- the standard tunnel order: a 4-wide mode-1 dummy swath down the column (the
            // ClickToMoveService default tool paints exactly this data) ----
            for (int ty = dy0; ty <= dy0 + 20; ty++)
                for (int ox = 0; ox < 4; ox++)
                    terr.SetDigMark(dx + ox, ty, 0, 0, dummy: true);

            // ---- drive dig_apply down the swath until DigNotify latches (digcount 32 → +4/dig
            // downward → the 9th eligible dig trips it) ----
            int digs = 0;
            for (int ty = dy0; ty <= dy0 + 14 && ctx.StoryLatchDir == 0; ty++, digs++)
                interp.Eval($"dig_apply {{{dx + 1} {ty} 8}}");
            TestContext.WriteLine($"latched after {digs} digs: dir={ctx.StoryLatchDir} at ({ctx.StoryLatchX},{ctx.StoryLatchY}) weight={ctx.StoryDigWeight}");
            Assert.AreEqual(2, ctx.StoryLatchDir, "downward tunnel → dir-2 latch");
            int cellX = (ctx.StoryLatchX + ((ctx.StoryLatchX >> 31) & 3)) >> 2;
            int cellY = (ctx.StoryLatchY + ((ctx.StoryLatchY >> 31) & 3)) >> 2;

            // ---- tick the story think loop until templates stamp into the live world ----
            int CountLive()
            {
                int n = 0;
                foreach (var d in engine.Map.Diagnostics) if (d.Contains("LIVE pmp")) n++;
                return n;
            }
            int before = CountLive();
            int steps = 0;
            for (; steps < 1500 && CountLive() == before; steps++) engine.Sim.Step(0.1);
            for (int i = 0; i < 100; i++) engine.Sim.Step(0.1);   // let a few more templates stream
            int live = CountLive() - before;
            TestContext.WriteLine($"{live} live template stamps after {steps} ticks; rect=({ctx.StoryRectX},{ctx.StoryRectY} {ctx.StoryRectW}x{ctx.StoryRectH}) utemp='{ctx.StoryUtemp}'");
            foreach (var d in engine.Map.Diagnostics) if (d.Contains("LIVE")) TestContext.WriteLine(d);
            // Chain forensics on failure: generate_next's own state shows WHERE it died —
            // gencount 0 + empty temp_buffer = lg_start produced nothing (or generate_next threw
            // before it); gencount > 0 + entries = the load_next→map_template leg is broken.
            var smEnt = FindByClass(ctx, "StoryMgr")!;
            string SmVar(string n) => smEnt.VarsScope.Vars.TryGetValue(n, out var v) ? v : "<unset>";
            TestContext.WriteLine($"sm state: gencount={SmVar("gencount")} failcount={SmVar("failcount")} " +
                                  $"bSplit={SmVar("bSplit")} temp_buffer='{SmVar("temp_buffer")}'");
            Assert.Greater(live, 0, "the growth chain must stamp templates into the live world");

            // (No assert on StoryLatchConsumed: autonomous gnomes may pick up the remaining dummy
            // marks and re-latch during the ticks — faithful behaviour, not a failure.)
            Assert.AreEqual("1", interp.Eval($"sm_map_get {cellX} {cellY}").Trim(),
                "generate_next must mark the scanned cell done (sm_map_set orgx orgy 1 1)");
            Assert.IsTrue(cellX >= ctx.StoryRectX && cellX < ctx.StoryRectX + ctx.StoryRectW
                       && cellY >= ctx.StoryRectY && cellY < ctx.StoryRectY + ctx.StoryRectH,
                "the free rect must contain the breakthrough cell (the growth is ADJACENT to the dig)");

            // ---- stamped templates land inside the anchored rect (not at the origin) ----
            var rx = new System.Text.RegularExpressions.Regex(@"LIVE pmp '[^']+' @\((-?\d+),(-?\d+)\)");
            int x0 = ctx.StoryRectX * 4 - 8, y0r = ctx.StoryRectY * 4 - 8;
            int x1 = (ctx.StoryRectX + ctx.StoryRectW) * 4 + 8, y1 = (ctx.StoryRectY + ctx.StoryRectH) * 4 + 8;
            int inRect = 0, total = 0;
            int sx0 = int.MaxValue, sy0 = int.MaxValue, sx1 = -1, sy1 = -1;
            foreach (var d in engine.Map.Diagnostics)
            {
                var m = rx.Match(d);
                if (!m.Success) continue;
                total++;
                int px = int.Parse(m.Groups[1].Value), py = int.Parse(m.Groups[2].Value);
                if (px >= x0 && px < x1 && py >= y0r && py < y1) inRect++;
                if (px < sx0) sx0 = px; if (py < sy0) sy0 = py;
                if (px > sx1) sx1 = px; if (py > sy1) sy1 = py;
            }
            Assert.Greater(inRect, 0, $"stamps must land in the anchored rect ({inRect}/{total} in [{x0},{y0r})..({x1},{y1}))");

            // ---- nav: after the dirty-rect revalidation the grown region has walkable floor ----
            terr.RevalidateDirtyNav();
            var nav = terr.Nav!;
            int walkable = 0;
            for (int sy = System.Math.Max(0, sy0) * 8; sy < System.Math.Min(terr.Height, sy1 + 16) * 8; sy++)
                for (int sx = System.Math.Max(0, sx0) * 8; sx < System.Math.Min(terr.Width, sx1 + 16) * 8; sx++)
                    if (nav.SubWalkable(sx, sy)) walkable++;
            TestContext.WriteLine($"walkable sub-cells in the grown region: {walkable}");
            Assert.Greater(walkable, 0, "the grown caves must be walkable after RevalidateRect");
        }
    }
}
