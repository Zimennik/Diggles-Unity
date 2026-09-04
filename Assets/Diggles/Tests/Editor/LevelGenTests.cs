#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;

    // Tests for the level generator (the lg_* world-growth engine the StoryMgr drives). Faithful
    // solver per FUN_00589790/005842c0/00586710/00584120 — citations in LevelGenerator.cs and
    // Docs/LEVEL_GENERATOR.md.
    public sealed class LevelGenTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

        static LevelGenerator NewSyntheticLg()
        {
            var lg = new LevelGenerator { Rng = new System.Random(1234) };   // deterministic test stream
            // seed 4x4 with 2 doors — the only seed class FUN_005862c0:45-53 accepts.
            lg.AddSyntheticPiece("seed", 4, 4, (0, 0, 0), (4, 0, 2));
            // corridor 8x4: doors left (0,0) dir0 and right (8,0) dir2 — chains horizontally.
            lg.AddSyntheticPiece("corr", 8, 4, (0, 0, 0), (8, 0, 2));
            // caps: one door each, both facings — the full-closure law needs both chain ends cappable.
            lg.AddSyntheticPiece("capL", 8, 8, (8, 0, 2));   // door on its right → closes a leftward chain
            lg.AddSyntheticPiece("capR", 8, 8, (0, 0, 0));   // door on its left → closes a rightward chain
            return lg;
        }

        // The story.tcl failure law: a generation may legitimately fail (e.g. a seed lands where
        // the area edge blocks closure) — the StoryMgr retries on a later scan (failcount,
        // story.tcl:212-222). Tests mirror that: retry with the same advancing RNG stream.
        static bool StartWithRetry(LevelGenerator lg, int tries = 20)
        {
            for (int i = 0; i < tries; i++) if (lg.Start()) return true;
            return false;
        }

        // Core (pure, no file IO): seed → door-matched docking → full closure (the native grow loop
        // only succeeds when the frontier drains, FUN_00589790:252/:262), no overlaps, all within
        // the area.
        [Test]
        public void Generate_SyntheticPieces_DoorMatchedNoOverlap()
        {
            var lg = NewSyntheticLg();
            lg.SetArea(0, 0, 64, 32);
            lg.SetTemplateRatio(0f, 0.6f, 1f);

            Assert.IsTrue(StartWithRetry(lg), "generator placed nothing");
            var lvl = lg.LastLevel;
            Assert.GreaterOrEqual(lvl.Count, 2, "expected door-matched chaining (>=2 pieces)");

            var dims = new Dictionary<string, (int w, int h)>
            { { "seed", (4, 4) }, { "corr", (8, 4) }, { "capL", (8, 8) }, { "capR", (8, 8) } };
            for (int i = 0; i < lvl.Count; i++)
            {
                var (n, x, y) = lvl[i]; var (w, h) = dims[n];
                Assert.IsTrue(x >= 0 && y >= 0 && x + w <= 64 && y + h <= 32, $"piece {n} out of area at ({x},{y})");
                for (int j = i + 1; j < lvl.Count; j++)
                {
                    var (n2, x2, y2) = lvl[j]; var (w2, h2) = dims[n2];
                    bool overlap = x < x2 + w2 && x2 < x + w && y < y2 + h2 && y2 < y + h;
                    Assert.IsFalse(overlap, $"pieces {i}@({x},{y}) and {j}@({x2},{y2}) overlap");
                }
            }
            TestContext.WriteLine($"placed {lvl.Count} pieces door-matched, no overlaps");
        }

        // Full-closure law (FUN_00589790:252/:262 + validator FUN_00584120): every door of every
        // placed piece is either mated to a neighbour or capped — after a successful run no open
        // door remains.
        [Test]
        public void Generate_Synthetic_ClosesEveryDoor()
        {
            var lg = NewSyntheticLg();
            lg.SetArea(0, 0, 64, 32);
            lg.SetTemplateRatio(0f, 0.10f, 1f);
            Assert.IsTrue(StartWithRetry(lg), "generation failed");

            // reconstruct door points from placements; each unmatched point = an open door.
            var doors = new Dictionary<(int, int), int>();
            var edges = new Dictionary<string, (int lx, int ly)[]>
            {
                { "seed", new[] { (0, 0), (4, 0) } }, { "corr", new[] { (0, 0), (8, 0) } },
                { "capL", new[] { (8, 0) } }, { "capR", new[] { (0, 0) } },
            };
            foreach (var (n, x, y) in lg.LastLevel)
                foreach (var (lx, ly) in edges[n])
                {
                    var k = (x + lx, y + ly);
                    doors[k] = doors.TryGetValue(k, out var c) ? c + 1 : 1;
                }
            foreach (var kv in doors)
                Assert.AreEqual(0, kv.Value % 2, $"open (unmated) door at {kv.Key}");
            TestContext.WriteLine($"{lg.LastLevel.Count} pieces, {doors.Count} door nodes, all mated");
        }

        // Variety law: the shared session RNG must produce different levels for different stream
        // states, and identical levels for identical states (determinism per seed).
        [Test]
        public void Generate_Synthetic_RngDrivesVariety()
        {
            string RunWith(int seed)
            {
                var lg = NewSyntheticLg();
                lg.Rng = new System.Random(seed);
                lg.SetArea(0, 0, 64, 32);
                lg.SetTemplateRatio(0f, 0.3f, 1f);
                Assert.IsTrue(StartWithRetry(lg), $"generation failed (seed {seed})");
                return lg.GetLevel();
            }
            string a1 = RunWith(42), a2 = RunWith(42);
            Assert.AreEqual(a1, a2, "same RNG state must reproduce the same level");
            bool anyDifferent = false;
            for (int s = 1; s <= 8 && !anyDifferent; s++) anyDifferent = RunWith(s) != a1;
            Assert.IsTrue(anyDifferent, "different RNG states must produce different levels (W5)");
        }

        // Level-cap + repeat-penalty law (FUN_00586710:120-122/:217): a piece at its levelcount
        // cap costs INT_MAX (still dockable as a last resort, but any alternative wins).
        [Test]
        public void Generate_Synthetic_LevelCapPrefersAlternatives()
        {
            var lg = NewSyntheticLg();
            lg.SetTemplateProps("corr", 1);   // -levelcount 1
            lg.SetArea(0, 0, 64, 32);
            lg.SetTemplateRatio(0f, 0.5f, 1f);
            Assert.IsTrue(StartWithRetry(lg), "generation failed");
            int corrs = 0;
            foreach (var (n, _, _) in lg.LastLevel) if (n == "corr") corrs++;
            // caps exist as alternatives on both ends, so with capL/capR available the corridor
            // must respect its levelcount cap.
            Assert.LessOrEqual(corrs, 1, "levelcount cap ignored (repeat duplicates — W3/W4)");
        }

        // Integration (real Urwald data): load the unchanged lgtools/lginit config, build the
        // Urwald.Std pool from the real templates, then run the generation chain (sm_get_level_info
        // free rect → lg_set_starttemplatepro dig anchor → lg_start); it must produce a non-empty
        // placement list of real urw_*.tcl pieces (Docs/LEVEL_GENERATOR.md step 7).
        [Test]
        public void Generate_RealUrwald_ProducesPlacements()
        {
            var interp = new TclInterpreter { TolerateExprErrors = true, TolerateMissingVars = true };
            interp.UnknownCommandHandler = (i, a) => "0";
            var ctx = DigglesTclEngine.Register(interp);
            ctx.DataRoot = DataRoot;
            ctx.TolerateMissingFiles = true; ctx.TolerateMissingClass = true; ctx.TolerateMissingEntities = true;

            foreach (var f in new[] { "scripts/init/lgtools.tcl", "scripts/init/lginit.tcl" })
                try { interp.Eval("call " + f); } catch (System.Exception e) { TestContext.WriteLine($"[load] {f}: {e.Message}"); }

            string Safe(string code) { try { return interp.Eval(code); } catch (System.Exception e) { return "ERR:" + e.Message; } }
            TestContext.WriteLine("glob(slash) count: " + TclList.Parse(Safe("glob data/templates/tcl/urw_gng_*.tcl")).Count);
            TestContext.WriteLine("LG_set_templategroup defined: '" + Safe("info procs LG_set_templategroup") + "'");
            TestContext.WriteLine("get_temp_size urw_gng_050_a.tcl: " + Safe("lg_get_temp_size urw_gng_050_a.tcl"));
            TestContext.WriteLine($"pieces={ctx.LevelGen!.PieceCount} groups=[{ctx.LevelGen.DebugGroups()}]");

            interp.Eval("lg_tp_clear");
            interp.Eval("lg_tp_addtemplatesets \"Urwald.Std\"");
            int pool = ctx.LevelGen!.PoolCount;
            TestContext.WriteLine($"Urwald.Std pool size = {pool}; has group = {ctx.LevelGen.HasGroup("Urwald.Std.Hol")}");
            Assert.Greater(pool, 0, "Urwald.Std pool empty — lgtools/lginit did not register templates (glob?)");

            // The faithful pre-generation chain: a story map, a (fabricated) dig-breakthrough latch
            // — what DigNotify writes — then the rect + anchor commands story.tcl runs.
            interp.Eval("sm_create_map 128 128");          // 32×32 story cells
            ctx.StoryLatchDir = 2; ctx.StoryLatchX = 41; ctx.StoryLatchY = 13; ctx.StoryLatchConsumed = false;
            string scan = interp.Eval("sm_scan").Trim();
            TestContext.WriteLine($"sm_scan → '{scan}'");
            Assert.AreEqual("10 3", scan, "sm_scan must return the sign-rounded latch cell");
            string linfo = interp.Eval("sm_get_level_info 10 3").Trim();
            TestContext.WriteLine($"sm_get_level_info → '{linfo}'");
            interp.Eval("lg_set_templateratio 0 0.1 1.0");
            string linfo2 = interp.Eval("lg_set_starttemplatepro 1 1").Trim();
            TestContext.WriteLine($"lg_set_starttemplatepro → '{linfo2}'");
            Assert.IsFalse(linfo2.Contains("nodigmark"),
                "anchor walk must succeed with a live latch (no terrain wired → probes pass)");

            // story failure law: a run may legitimately fail; the StoryMgr retries (failcount).
            string started = "0";
            for (int t = 0; t < 20 && started != "1"; t++) started = interp.Eval("lg_start").Trim();
            string level = interp.Eval("lg_get_level").Trim();
            TestContext.WriteLine($"started={started}; level head: {level.Substring(0, System.Math.Min(400, level.Length))}");
            Assert.AreEqual("1", started, "lg_start failed on Urwald.Std");
            Assert.IsNotEmpty(level, "lg_start produced no placements for Urwald.Std");
            Assert.IsTrue(level.Contains("urw_"), "placements are not real urw_* templates");

            // Anti-duplicate law (FUN_00652d10:276-279): lg_get_level marks every emitted
            // placement's story-cell rect grown, so the next free-rect expansion cannot overlap
            // this level. Verify each placement's origin cell reads 1.
            foreach (var (tcl, px, py, pw, ph) in ctx.LevelGen!.LastLevelRects())
            {
                string got = interp.Eval($"sm_map_get {px / 4} {py / 4}").Trim();
                Assert.AreEqual("1", got, $"placement {tcl}@({px},{py}) did not mark its story cell");
            }

            // A second generation from an adjacent latch must not overlap the first level's rects.
            var firstRects = new List<(int x, int y, int w, int h)>();
            foreach (var (_, px, py, pw, ph) in ctx.LevelGen.LastLevelRects()) firstRects.Add((px, py, pw, ph));
            ctx.StoryLatchDir = 2; ctx.StoryLatchX = 45; ctx.StoryLatchY = 53; ctx.StoryLatchConsumed = false;
            string scan2 = interp.Eval("sm_scan").Trim();
            interp.Eval($"sm_get_level_info {TclList.Parse(scan2)[0]} {TclList.Parse(scan2)[1]}");
            interp.Eval("lg_set_starttemplatepro 1 1");
            string started2 = "0";
            for (int t = 0; t < 10 && started2 != "1"; t++) started2 = interp.Eval("lg_start").Trim();
            if (started2 == "1")
            {
                foreach (var (tcl, px, py, pw, ph) in ctx.LevelGen.LastLevelRects())
                    foreach (var (fx, fy, fw, fh) in firstRects)
                    {
                        bool overlap = px < fx + fw && fx < px + pw && py < fy + fh && fy < py + ph;
                        Assert.IsFalse(overlap, $"2nd-gen {tcl}@({px},{py}) overlaps 1st-gen rect ({fx},{fy},{fw},{fh}) — W4");
                    }
                interp.Eval("lg_get_level");
                TestContext.WriteLine("second generation placed without overlapping the first");
            }
            else TestContext.WriteLine("second generation failed (acceptable — failcount law); no overlap to check");
        }

        // Session variety through the real chain: the native gets per-session variety from its
        // game-wide MT19937 (constant seed 0x1105 — FUN_0049c670:33 and FUN_00598f10:38 are the
        // only seeder callers, no time reseed) being advanced by every engine system between
        // generations. The port's equivalent is the shared session ctx.StoryRng. Two fresh
        // sessions digging the same spot (same latch, same rect) with different stream states
        // must produce different clusters — otherwise the draw sequence is frozen somewhere in
        // the chain.
        [Test]
        public void Generate_RealUrwald_VarietyAcrossSessions()
        {
            string RunSession(int seed)
            {
                var interp = new TclInterpreter { TolerateExprErrors = true, TolerateMissingVars = true };
                interp.UnknownCommandHandler = (i, a) => "0";
                var ctx = DigglesTclEngine.Register(interp);
                ctx.DataRoot = DataRoot;
                ctx.TolerateMissingFiles = true; ctx.TolerateMissingClass = true; ctx.TolerateMissingEntities = true;
                ctx.StoryRng = new System.Random(seed);   // the session stream state
                foreach (var f in new[] { "scripts/init/lgtools.tcl", "scripts/init/lginit.tcl" })
                    try { interp.Eval("call " + f); } catch { }
                interp.Eval("lg_tp_clear");
                interp.Eval("lg_tp_addtemplatesets \"Urwald.Std\"");
                interp.Eval("sm_create_map 128 128");
                // the same dig breakthrough every session (the player digs the same spot)
                ctx.StoryLatchDir = 2; ctx.StoryLatchX = 41; ctx.StoryLatchY = 13; ctx.StoryLatchConsumed = false;
                interp.Eval("sm_scan");
                interp.Eval("sm_get_level_info 10 3");
                interp.Eval("lg_set_templateratio 0 0.1 1.0");
                interp.Eval("lg_set_starttemplatepro 1 1");
                for (int t = 0; t < 20; t++) if (interp.Eval("lg_start").Trim() == "1") break;
                return interp.Eval("lg_get_level").Trim();
            }

            var seen = new HashSet<string>();
            for (int s = 1; s <= 5; s++)
            {
                string lvl = RunSession(s * 7919);
                int n = TclList.Parse(lvl).Count;
                seen.Add(lvl);
                TestContext.WriteLine($"session {s}: {n} pieces: {lvl.Substring(0, System.Math.Min(160, lvl.Length))}");
            }
            Assert.Greater(seen.Count, 1,
                "different session RNG states must produce DIFFERENT clusters for the same dig spot");
            // The cluster piece-count legitimately converges for a fixed area/ratio — the native's
            // first cluster is deterministic under the const 0x1105 seed, so count-variety is not a
            // faithfulness requirement. (The old assertion `counts.Count > 1` passed only because of
            // the min(ObjTotal,5) capTerm bug, which gave 58 pieces spurious discounts and
            // manufactured cluster-size noise. The meaningful replayability check is the composition
            // variety above.)
        }

        // 5a — the -gamecount → capTerm plumbing (FUN_00586710:28): capTerm =
        // (objectBearing ? min(+0x1c gamecount-remaining, 5): 5) * 200. +0x1c defaults INT_MAX
        // and is set only by lginit's four -gamecount-1 entries, so capTerm is a flat 1000 for
        // every piece except the two object-bearing set-piece caves (051_a Wuker, 054_a
        // Schatztonne → 200). The old port read the object-spawn count (min(ObjTotal,5)*200) — a
        // spurious −200..−800 discount on 58/185 pool pieces that dwarfed every legitimate cost
        // separation (band 100, size ~13) and deterministically front-loaded the same object caves.
        [Test]
        public void Generate_RealUrwald_GameCountFeedsCapTerm()
        {
            var interp = new TclInterpreter { TolerateExprErrors = true, TolerateMissingVars = true };
            interp.UnknownCommandHandler = (i, a) => "0";
            var ctx = DigglesTclEngine.Register(interp);
            ctx.DataRoot = DataRoot;
            ctx.TolerateMissingFiles = true; ctx.TolerateMissingClass = true; ctx.TolerateMissingEntities = true;
            foreach (var f in new[] { "scripts/init/lgtools.tcl", "scripts/init/lginit.tcl" })
                try { interp.Eval("call " + f); } catch { }
            var lg = ctx.LevelGen!;
            // lginit.tcl:45-50 sets -gamecount 1 -levelcount 1 for exactly these four.
            foreach (var t in new[] { "urw_gng_050_a", "urw_gng_051_a", "urw_gng_052_a", "urw_gng_054_a" })
                Assert.AreEqual(1, lg.GetGameCap(t + ".tcl"), $"{t}: -gamecount 1 not parsed into GameCap (5a)");
            // any piece not in lginit's list keeps the INT_MAX default → the flat capTerm 1000.
            Assert.AreEqual(int.MaxValue, lg.GetGameCap("urw_gng_002_a.tcl"),
                "a piece without -gamecount must default to INT_MAX (capTerm 1000, no spurious discount)");
        }

        // 5b — the object-cave session-thinning law (FUN_00586710:58-61 gate on +0x50, bumped by
        // lg_get_level FUN_00652d10:84-93, never reset): a given object-bearing variant ships at
        // most once per session, so successive breakthroughs are forced onto different object
        // caves. The old port gated on PlacedThisLevel (reset every generation), so the same
        // object caves reappeared every generation. (This law applies to cost-chosen pieces only;
        // the per-direction forced start piece is placed by ForcePlace every generation regardless
        // of +0x50 — faithful.) Deterministic synthetic scenario: the seed's right door can be
        // capped by either a discounted object cap or a plain cap; gen 1 picks the object cap
        // (capTerm 200 ≪ 1000), which thins so gen 2 falls back to the plain cap.
        [Test]
        public void ObjectCap_ThinsToPlainCap_AcrossGenerations()
        {
            var lg = new LevelGenerator { Rng = new System.Random(7) };
            lg.AddSyntheticPiece("seed", 4, 4, (0, 0, 0), (4, 0, 2));      // 4×4 2-door seed (L + R)
            lg.AddSyntheticPiece("capL", 8, 8, (8, 0, 2));                 // right door → caps the seed's LEFT door
            lg.AddSyntheticPiece("plainCapR", 8, 8, (0, 0, 0));            // left door → caps the RIGHT door (plain, capTerm 1000)
            lg.AddSyntheticObjectPiece("objCapR", 8, 8, "Kohle", 1, (0, 0, 0)); // same door, object + -gamecount 1 (capTerm 200)
            lg.SetArea(0, 0, 64, 32);
            lg.SetTemplateRatio(0f, 0.1f, 1f);

            // Gen 1: the discounted object cap wins the right-door dock.
            Assert.IsTrue(StartWithRetry(lg), "gen1 generation failed");
            Assert.IsTrue(Placed(lg, "objCapR"), "gen1 must pick the DISCOUNTED object cap (capTerm 200 ≪ 1000)");
            Assert.IsFalse(Placed(lg, "plainCapR"), "gen1 should not need the plain cap");
            lg.CommitSessionEmission();                                     // lg_get_level bumps +0x50
            Assert.AreEqual(1, lg.SessionEmitCount("objCapR"), "session emit counter not bumped (5b)");

            // Gen 2: same generator (session state persists). objCapR is now gated (+0x50>0 → INT_MAX),
            // so the plain cap wins — the cross-generation thinning the fix restores.
            Assert.IsTrue(StartWithRetry(lg), "gen2 generation failed");
            Assert.AreEqual(1, lg.SessionEmitCount("objCapR"),
                "the session counter must PERSIST across Start() (the old PlacedThisLevel reset it every generation — THAT was the bug)");
            Assert.IsFalse(Placed(lg, "objCapR"), "gen2 must AVOID the session-thinned object cap (5b)");
            Assert.IsTrue(Placed(lg, "plainCapR"), "gen2 must fall back to the plain cap");
        }
        static bool Placed(LevelGenerator lg, string name)
        { foreach (var (n, _, _) in lg.LastLevel) if (n == name) return true; return false; }

        // Story-cave depth variety: register the real Urwald zone and its sm_add_temp windows
        // (CampaignInit.tcl:675-692), mark the start area used (Campaign.tcl:20), and probe
        // sm_get_level_info across depth. The utemp pick must vary by depth (einsiedler → voodoo →
        // feen …), not have the first-registered temp shadow every depth (which made its huge
        // composite fail to place → plain 3-cave growth forever). The per-depth table is printed
        // for eyeballing; the asserts guard the un-shadowing.
        [Test]
        public void Generate_RealUrwald_StoryCavesVaryByDepth()
        {
            var interp = new TclInterpreter { TolerateExprErrors = true, TolerateMissingVars = true };
            interp.UnknownCommandHandler = (i, a) => "0";
            var ctx = DigglesTclEngine.Register(interp);
            ctx.DataRoot = DataRoot;
            ctx.TolerateMissingFiles = true; ctx.TolerateMissingClass = true; ctx.TolerateMissingEntities = true;
            ctx.StoryRng = new System.Random(12345);
            foreach (var f in new[] { "scripts/init/lgtools.tcl", "scripts/init/lginit.tcl" })
                try { interp.Eval("call " + f); } catch { }
            interp.Eval("lg_tp_clear");
            interp.Eval("lg_tp_addtemplatesets \"Urwald.Std\"");
            interp.Eval("sm_create_map 512 640");
            interp.Eval("sm_set_digcount 32");
            interp.Eval("sm_add_zone \"Urwald\"");
            foreach (var t in new[]
            {
                "sm_add_temp urw_unq_start",
                "sm_add_temp urw_der_einsiedler 56 60",
                "sm_add_temp urw_unq_voodoo_dorf 60 110 -digconnect 0 1",
                "sm_add_temp urw_unq_feen 60 160 -nonrelevant 0.4740217 -middist 10",
                "sm_add_temp urw_unq_troll_012_a 60 88 -nonrelevant 0.3 -digconnect 0 1",
                "sm_add_temp urw_trollburg_ring 88 128 -mindist urw_der_einsiedler 0.1",
                "sm_add_temp urw_uebergang_swf 132 164 -mindist urw_trollburg_ring 0.1 -exclusive 1 -zoneborder Urwald -stoneidx 0",
            }) interp.Eval(t);
            // Boot marks the start area used (Campaign.tcl:20 sm_set_temp {{urw_unq_start 208 0} …}),
            // so it drops out of the growth utemp pool. Without this the 0-window start temp shadows
            // every real story cave. Probe reflects the real boot.
            interp.Eval("sm_set_temp {{urw_unq_start 208 0}}");

            TestContext.WriteLine($"StoryTemps registered = {ctx.StoryTemps.Count}");
            foreach (var rt in ctx.StoryTemps)
                TestContext.WriteLine($"  '{rt.Name}' zone={rt.Zone} lo={rt.Lo} hi={rt.Hi} nonrel={rt.NonRelevant} mindistRef='{rt.MinDistRef}'");
            Assert.Greater(ctx.StoryTemps.Count, 0, "sm_add_temp did not populate ctx.StoryTemps");

            // Correct native selection (WORLDGEN_ORIGINAL_RE §B1/§B2): a unique stays eligible past
            // its band (weight ≥ 1 = certain-if-it-fits) until it is placed — variety comes from the
            // used-marking (sm_set_temp, story.tcl:229), not from a depth cutoff. So digging deeper
            // and marking each picked cave used must cycle through distinct story caves (einsiedler →
            // troll/voodoo → feen …), and once a band's uniques are consumed a depth yields no utemp
            // (a normal cave) or the next-band unique.
            var distinct = new HashSet<string>();
            var order = new List<string>();
            for (int cy = 14; cy <= 45; cy++)
            {
                ctx.StoryMap.Clear();
                var parts = TclList.Parse(interp.Eval($"sm_get_level_info 60 {cy}").Trim());
                string ut = parts.Count >= 5 ? parts[4].Trim('{', '}', ' ') : "";
                if (ut.Length == 0 || ut == "nodigmark") continue;
                distinct.Add(ut); order.Add($"cy{cy}:{ut}");
                interp.Eval("sm_set_temp {{" + ut + " 0 0}}");   // mark USED ⇒ next depth picks a different cave
            }
            TestContext.WriteLine("depth pick order: " + string.Join("  ", order));

            // Several distinct story caves must appear across depth as each is used up — not one temp
            // monopolizing (the pre-RE shadow bug), and never the boot start area.
            Assert.GreaterOrEqual(distinct.Count, 3,
                $"expected several distinct story caves across depth (via used-marking), got [{string.Join(",", distinct)}]");
            Assert.IsFalse(distinct.Contains("urw_unq_start"),
                "urw_unq_start (the boot area, marked used) must NOT be picked for growth");
        }

        // ===================== CORRIDOR ROUTER (FUN_00587480 / FUN_005fc3b0) =====================

        // The story-cave connection law: a start↔utemp connect record (FUN_005fc3b0, weight 0.4) is
        // routed by FUN_00587480 in lg_start (FUN_00589790:66-116) — it flood-fills empty grid cells
        // from the utemp door back to the walk piece's door and emits corridor pieces along the
        // path, so the story cave is reachable instead of an orphan island. This test drives the
        // ported RouteCorridor directly: a walk piece and a utemp placed 12 tiles apart, one connect
        // record — the generation must emit corridor pieces in the gap and leave the two forced
        // pieces in one connected footprint (no orphan). (Before the router landed, the utemp block
        // registered the composite but no connection, so it grew disconnected — the mesh-hole bug
        // this fixes; binding CmdLgSetStartTemplatePro / Docs step §DIG-ANCHOR:8.)
        [Test]
        public void RouteCorridor_ConnectsWalkToUtemp_NoOrphanIsland()
        {
            var lg = new LevelGenerator { Rng = new System.Random(1), RouterEnabled = true };
            lg.AddSyntheticPiece("walk.tcl", 4, 4, (4, 2, 2));                 // single RIGHT door (port dir 2)
            lg.AddSyntheticPiece("utemp.tcl", 4, 4, (0, 2, 0));                // single LEFT door (port dir 0)
            lg.AddSyntheticPiece("corrH.tcl", 4, 4, (0, 2, 0), (4, 2, 2));     // straight L+R corridor (mask 5)
            lg.SetArea(0, 0, 48, 16);
            lg.SetTemplateRatio(0f, 0.1f, 1f);
            lg.ClearForcedRecords();
            lg.AddForcedRecord("walk.tcl", 4, 4);     // record 0: tiles [4..8)×[4..8), right door (8,6)
            lg.AddForcedRecord("utemp.tcl", 20, 4);   // record 1: tiles [20..24)×[4..8), left door (20,6)
            lg.ClearConnections();
            lg.AddConnection(0, 1);                   // FUN_005fc3b0: walk(node0) ↔ utemp(node1)
            Assert.AreEqual(1, lg.ConnectionCount, "the connect record must register");

            Assert.IsTrue(lg.Start(), $"utemp generation failed: {lg.LastFailReason}");

            int corr = 0, walk = 0, ut = 0;
            foreach (var (n, _, _) in lg.LastLevel)
            { if (n == "corrH.tcl") corr++; else if (n == "walk.tcl") walk++; else if (n == "utemp.tcl") ut++; }
            Assert.AreEqual(1, walk, "the walk piece must be placed once");
            Assert.AreEqual(1, ut, "the utemp must be placed once");
            Assert.GreaterOrEqual(corr, 1, "the router must EMIT corridor pieces to bridge the gap");

            // No orphan island: every placed footprint must form one 4-connected region — the
            // utemp reachable from the walk piece through the carved corridor.
            Assert.IsTrue(FootprintsConnected(lg, "walk.tcl", "utemp.tcl"),
                "the utemp is an ORPHAN ISLAND — the corridor did not connect it to the walk piece");

            // the corridor really fills the between-tiles (gap x=8..20 at the door row y=6 occupied)
            var occ = OccupiedTiles(lg);
            for (int x = 8; x < 20; x++)
                Assert.IsTrue(occ.Contains((x, 6)), $"gap tile ({x},6) not carved — corridor is not contiguous");
            TestContext.WriteLine($"routed: walk + {corr} corridor pieces + utemp, one connected footprint");
        }

        // The cap-and-continue law: the native fails the whole gen when a corridor cannot carve
        // (FUN_00587480 returns 0 ⇒ FUN_00589790:11 rollback ⇒ the story failcount retry). The
        // port instead skips the unroutable connection so a placed story cave never silently
        // vanishes — TryRouteBetween is atomic, so nothing partial is emitted; the open src/dst
        // doors defer to the ordinary grow-loop closure law. A full-height doorless wall blocks
        // every 4×4-block path, so the route cannot carve and is skipped (CorridorsSkipped++), not
        // the old "corridor route failed" whole-gen abort.
        [Test]
        public void RouteCorridor_NoPath_CapAndContinues_NotFailed()
        {
            var lg = new LevelGenerator { Rng = new System.Random(1), RouterEnabled = true };
            lg.AddSyntheticPiece("walk.tcl", 4, 4, (4, 2, 2));
            lg.AddSyntheticPiece("utemp.tcl", 4, 4, (0, 2, 0));
            lg.AddSyntheticPiece("corrH.tcl", 4, 4, (0, 2, 0), (4, 2, 2));
            lg.AddSyntheticPiece("wall.tcl", 4, 16);   // doorless full-height blocker
            lg.SetArea(0, 0, 32, 16);
            lg.SetTemplateRatio(0f, 0.1f, 1f);
            lg.ClearForcedRecords();
            lg.AddForcedRecord("walk.tcl", 4, 4);      // record 0: right door (8,6), step-off (10,6)
            lg.AddForcedRecord("wall.tcl", 12, 0);     // record 1: tiles x[12..16) full height — blocks every path
            lg.AddForcedRecord("utemp.tcl", 20, 4);    // record 2: left door (20,6), step-off (18,6)
            lg.ClearConnections();
            lg.AddConnection(0, 2);                    // walk(node0) ↔ utemp(node2)

            bool ok = lg.Start();
            // The unroutable connection is SKIPPED, not failed — the cap-and-continue contract.
            Assert.AreEqual(1, lg.CorridorsSkipped,
                "the wall-blocked connection must be SKIPPED (cap-and-continue), not routed");
            StringAssert.DoesNotContain("corridor route failed", lg.LastFailReason,
                "cap-and-continue must NOT fail the whole generation on an unroutable corridor");
            TestContext.WriteLine($"unroutable connection capped-and-continued: ok={ok}, skipped={lg.CorridorsSkipped}, reason='{lg.LastFailReason}'");
        }

        // Occupied tile set from the emitted placements (real per-piece dims via LastLevelRects).
        static HashSet<(int, int)> OccupiedTiles(LevelGenerator lg)
        {
            var occ = new HashSet<(int, int)>();
            foreach (var (_, x, y, w, h) in lg.LastLevelRects())
                for (int ty = y; ty < y + h; ty++)
                    for (int tx = x; tx < x + w; tx++)
                        occ.Add((tx, ty));
            return occ;
        }

        // True iff the first placement named aName and the first named bName sit in the same
        // 4-connected component of the occupied-tile set (i.e. the corridor bridges them).
        static bool FootprintsConnected(LevelGenerator lg, string aName, string bName)
        {
            var occ = OccupiedTiles(lg);
            (int, int)? a = null, b = null;
            foreach (var (tcl, x, y, _, _) in lg.LastLevelRects())
            { if (tcl == aName && a == null) a = (x, y); if (tcl == bName && b == null) b = (x, y); }
            if (a == null || b == null) return false;
            var seen = new HashSet<(int, int)> { a.Value };
            var q = new Queue<(int, int)>(); q.Enqueue(a.Value);
            var steps = new[] { (1, 0), (-1, 0), (0, 1), (0, -1) };
            while (q.Count > 0)
            {
                var c = q.Dequeue();
                foreach (var (ox, oy) in steps)
                {
                    var n = (c.Item1 + ox, c.Item2 + oy);
                    if (occ.Contains(n) && seen.Add(n)) q.Enqueue(n);
                }
            }
            return seen.Contains(b.Value);
        }

        // Diagnostic: run the full generation (sm_get_level_info → lg_set_starttemplatepro →
        // lg_start → lg_get_level) at real utemp depths and print, per dig, whether a utemp fired,
        // whether lg_start succeeded (or the LastFailReason — a "corridor route failed" here means a
        // router regression), the piece count, and whether the emitted level actually contains a
        // story cave. This is what the synthetic tests were blind to.
        [Test, Explicit]
        public void Diag_UtempGeneration_RealChain()
        {
            var interp = new TclInterpreter { TolerateExprErrors = true, TolerateMissingVars = true };
            interp.UnknownCommandHandler = (i, a) => "0";
            var ctx = DigglesTclEngine.Register(interp);
            ctx.DataRoot = DataRoot;
            ctx.TolerateMissingFiles = true; ctx.TolerateMissingClass = true; ctx.TolerateMissingEntities = true;
            ctx.StoryRng = new Mt19937(4242);   // run the generation on the real MT19937 draw semantics
            foreach (var f in new[] { "scripts/init/lgtools.tcl", "scripts/init/lginit.tcl" })
                try { interp.Eval("call " + f); } catch { }
            interp.Eval("lg_tp_clear");
            interp.Eval("lg_tp_addtemplatesets \"Urwald.Std\"");
            interp.Eval("sm_create_map 512 640");
            interp.Eval("sm_set_digcount 32");
            interp.Eval("sm_add_zone \"Urwald\"");
            foreach (var t in new[]
            {
                "sm_add_temp urw_unq_start",
                "sm_add_temp urw_der_einsiedler 56 60",
                "sm_add_temp urw_unq_voodoo_dorf 60 110 -digconnect 0 1",
                "sm_add_temp urw_unq_feen 60 160 -nonrelevant 0.4740217 -middist 10",
                "sm_add_temp urw_unq_troll_012_a 60 88 -nonrelevant 0.3 -digconnect 0 1",
            }) interp.Eval(t);
            interp.Eval(@"sm_def_temp_group urw_der_einsiedler { {64 24 urw_gng_019_b} {8 16 urw_unq_einsiedler} {92 16 urw_unq_raetsel_001} {76 32 urw_gng_018_a} {68 24 urw_gng_044_a} {80 32 urw_gng_003_a} }");
            interp.Eval(@"sm_def_temp_group urw_unq_voodoo_dorf { {108 40 urw_unq_vodo_003_a} {72 32 urw_unq_vodo_002_a} {68 32 urw_unq_vodo_008_a} {36 32 urw_unq_vodo_004_a} {44 52 urw_unq_vodo_007_a} {72 52 urw_unq_vodo_005_a} {92 52 urw_unq_vodo_006_a} }");
            interp.Eval("sm_set_temp {{urw_unq_start 208 0}}");
            var lg = ctx.LevelGen!;
            lg.RouterEnabled = true;   // test the CONNECTION now that the rect fix makes utemps place

            string StoryCaveIn(string lvl)
            {
                foreach (var tr in TclList.Parse(lvl))
                {
                    var p = TclList.Parse(tr); if (p.Count == 0) continue;
                    var nm = p[0];
                    if (nm.Contains("unq_") || nm.Contains("einsiedler") || nm.Contains("vodo") || nm.Contains("feen") || nm.Contains("troll") || nm.Contains("raetsel"))
                        return nm;
                }
                return "";
            }

            // Dig in all 4 directions at utemp depths — verify the utemp lands away from the dig
            // cell in the dig direction (the FUN_005fc0a0 re-placement fix); output tags AWAY-OK /
            // WRONG-SIDE.
            foreach (var (dir, cx, cy, tag) in new[]
            { (2, 40, 15, "down-einsiedler"), (2, 40, 22, "down-feen"), (4, 40, 22, "left-feen"),
              (8, 40, 22, "right-feen"), (1, 40, 22, "up-feen") })
            {
                ctx.StoryMap.Clear();
                ctx.StoryLatchDir = dir; ctx.StoryLatchX = cx * 4; ctx.StoryLatchY = cy * 4; ctx.StoryLatchConsumed = false;
                interp.Eval($"sm_get_level_info {cx} {cy}");
                string pickUtemp = ctx.StoryUtemp;
                interp.Eval("lg_set_templateratio 0 0.1 1.0");
                interp.Eval("lg_set_starttemplatepro 1 1");
                int conns = lg.ConnectionCount;
                string started = "0", fail = "";
                for (int t = 0; t < 25 && started != "1"; t++) { started = interp.Eval("lg_start").Trim(); fail = lg.LastFailReason; }
                string level = interp.Eval("lg_get_level").Trim();
                int n = TclList.Parse(level).Count;
                string story = StoryCaveIn(level);
                int ux = -1, uy = -1;
                foreach (var tr in TclList.Parse(level))
                { var p = TclList.Parse(tr); if (p.Count >= 3 && p[0] == story) { int.TryParse(p[1], out ux); int.TryParse(p[2], out uy); break; } }
                string side = "n/a";
                if (ux >= 0)
                {
                    int dxc = ux - cx * 4, dyc = uy - cy * 4;
                    bool away = dir == 8 ? dxc > 0 : dir == 4 ? dxc < 0 : dir == 2 ? dyc > 0 : dyc < 0;
                    side = $"@({ux},{uy}) vs dig({cx * 4},{cy * 4}) d({dxc},{dyc}) {(away ? "AWAY-OK" : "WRONG-SIDE")}";
                }
                TestContext.WriteLine($"[{tag}] dir{dir} cy{cy}: pick='{pickUtemp}' conns={conns} corrSkip={lg.CorridorsSkipped} started={started} pieces={n} story='{story}' {side} fail='{fail}'");
                // mark the picked utemp used (the real story.tcl:229 `sm_set_temp $level` flow) so the
                // next scenario must pick a different temp — proves the selection varies, not monopolizes.
                if (started == "1" && pickUtemp.Length > 0)
                    interp.Eval("sm_set_temp {{" + pickUtemp + " 0 0}}");
            }
        }

        // Composite door-mating fix (unit). A split parent's doors live only in its merged pmp-base
        //.ent (the split children's.ents are empty); the native reads them because FUN_00583080
        // resolves every template through FUN_005826d0, whose.ent read runs outside the split guard
        // (FUN_005826d0:86/:146+). The port used to early-return for split templates, giving 0 doors
        // so the composite could never mate its seams. Assert the merged-ent doors now load, with
        // the exact seam that used to bury: vodo_002 right (36,14) ↔ vodo_003 left (0,6) → world
        // (108,46).
        [Test]
        public void SplitTemplate_LoadsMergedEntDoors()
        {
            var lg = new LevelGenerator { DataRoot = DataRoot };
            Assert.AreEqual(2, lg.DoorCount("urw_unq_vodo_002_a"), "vodo_002 (split 36x20) → unq_vodo_002.ent");
            Assert.AreEqual(4, lg.DoorCount("urw_unq_vodo_008_a"), "vodo_008 (split 4x40) → unq_vodo_gng.ent");
            Assert.AreEqual(1, lg.DoorCount("urw_unq_vodo_004_a"), "vodo_004 (split 32x20) → unq_vodo_004.ent");
            Assert.AreEqual(2, lg.DoorCount("urw_unq_vodo_005_a"), "vodo_005 (split 20x20) → hol_014.ent");
            CollectionAssert.Contains(lg.GetDoors("urw_unq_vodo_002_a"), (36, 14, 2), "vodo_002 RIGHT door local (36,14)");
            CollectionAssert.Contains(lg.GetDoors("urw_unq_vodo_003_a"), (0, 6, 0), "vodo_003 LEFT door local (0,6)");
        }

        // Composite door-mating fix (integration). The 7 vodo sub-pieces of urw_unq_voodoo_dorf
        // (CampaignInit.tcl:179) tile edge-to-edge with doors authored to coincide; registered as
        // forced records (as lg_set_starttemplatepro/FUN_005f7d10 do) and run through lg_start
        // (FUN_005896a0 → per-piece FUN_005848b0 + validator FUN_00584120), all 7 must place with no
        // buried-seam rejection, leaving one open door (vodo_003 right, world (116,46)) that a cap
        // closes. Before the fix this failed at vodo_002 burying vodo_003's (108,46).
        [Test]
        public void Composite_VoodooDorf_PlacesWithoutBuriedSeam()
        {
            var lg = new LevelGenerator { Rng = new System.Random(7), DataRoot = DataRoot, RouterEnabled = false };
            lg.SetArea(0, 0, 160, 96);
            lg.SetTemplateRatio(0f, 0.1f, 1f);
            var comp = new (string tcl, int dx, int dy)[]
            {
                ("urw_unq_vodo_003_a.tcl", 108, 40), ("urw_unq_vodo_002_a.tcl", 72, 32),
                ("urw_unq_vodo_008_a.tcl", 68, 32),  ("urw_unq_vodo_004_a.tcl", 36, 32),
                ("urw_unq_vodo_007_a.tcl", 44, 52),  ("urw_unq_vodo_005_a.tcl", 72, 52),
                ("urw_unq_vodo_006_a.tcl", 92, 52),
            };
            lg.ClearForcedRecords();
            foreach (var (t, dx, dy) in comp) lg.AddForcedRecord(t, dx, dy);
            lg.AddSyntheticPiece("entranceCap", 4, 4, (0, 2, 0));   // LEFT door caps vodo_003's open RIGHT door

            Assert.IsTrue(lg.Start(), "voodoo_dorf composite failed to place — " + lg.LastFailReason);
            var names = new HashSet<string>();
            foreach (var (tcl, _, _) in lg.LastLevel) names.Add(tcl);
            foreach (var (t, _, _) in comp)
                Assert.IsTrue(names.Contains(t), $"sub-piece {t} was not placed");
            TestContext.WriteLine($"voodoo_dorf placed {lg.LastLevel.Count} pieces (7 sub-pieces + cap), no buried seam");
        }

        // ===================== B10 — FINALIZE / DEAD-END PRUNE (FUN_00585390) =====================

        static bool AnyFlag(System.Collections.Generic.IReadOnlyList<(string tcl, int x, int y, int flag)> ps, string n, int flag)
        { foreach (var (t, _, _, f) in ps) if (t == n && f == flag) return true; return false; }
        static bool AnyName(System.Collections.Generic.IReadOnlyList<(string tcl, int x, int y, int flag)> ps, string n)
        { foreach (var (t, _, _, _) in ps) if (t == n) return true; return false; }

        // Note: piece origins are 4-aligned (TryPlace gate (gx|gy)&3==0 — the story-cell=4 lattice);
        // doors sit at a mid-edge coord (≡2 mod 4) and mating doors share a world coord.

        // Cap-junction path (FUN_00585390:22): a thin single-door empty dead-end hanging off a
        // junction (>2 doors) is deleted and the junction is re-placed with the pool variant carrying
        // its doors minus the one that faced the dead-end (FUN_00585130) — the branch is pruned and
        // the junction sealed. The re-placed junction is flag=1 (protected).
        [Test]
        public void Finalize_DeadEndOffJunction_CapsNeighbourWithVariant()
        {
            var lg = new LevelGenerator { Rng = new System.Random(1) };
            lg.AddSyntheticPiece("junction3", 8, 8, (0, 2, 0), (8, 2, 2), (2, 8, 3));  // 3 doors
            lg.AddSyntheticPiece("junction2", 8, 8, (0, 2, 0), (2, 8, 3));             // junction3 minus RIGHT
            lg.AddSyntheticPiece("deadend", 4, 4, (0, 2, 0));                          // single LEFT door
            // deadend@(16,8) LEFT door (16,10) == junction3@(8,8) RIGHT door (8+8,8+2).
            lg.TestRunFinalize(32, 32, new (string, int, int, byte)[]
            { ("junction3", 8, 8, (byte)0), ("deadend", 16, 8, (byte)0) });
            var placed = lg.TestPlacedWithFlags();
            Assert.AreEqual(1, placed.Count, "finalize should leave exactly the capped junction");
            Assert.AreEqual("junction2", placed[0].tcl, "junction must be swapped for the door-reduced variant");
            Assert.AreEqual(1, placed[0].flag, "the re-placed piece is protected (flag=1)");
            Assert.IsFalse(AnyName(placed, "deadend"), "the dead-end must be pruned");
        }

        // Stub-cap fallback (FUN_00585390:305-401): when the junction has no door-reduced pool
        // variant, the deleted thin dead-end is replaced by a clean 4×4 single-door pool cap (dir ==
        // the dead-end's) at the same door, flag=1; the junction itself survives (still mated).
        [Test]
        public void Finalize_DeadEndNoVariant_StubCapsWith4x4()
        {
            var lg = new LevelGenerator { Rng = new System.Random(1) };
            lg.AddSyntheticPiece("junction3", 8, 8, (0, 2, 0), (8, 2, 2), (2, 8, 3));  // NO 2-door variant pooled
            lg.AddSyntheticPiece("deadend4x8", 4, 8, (0, 2, 0));                        // thin (W==4) single-door
            lg.AddSyntheticPiece("cap4", 4, 4, (0, 2, 0));                              // a 4×4 LEFT-door cap
            lg.TestRunFinalize(32, 32, new (string, int, int, byte)[]
            { ("junction3", 8, 8, (byte)0), ("deadend4x8", 16, 8, (byte)0) });
            var placed = lg.TestPlacedWithFlags();
            Assert.IsTrue(AnyFlag(placed, "junction3", 0), "the junction survives unchanged (flag=0)");
            Assert.IsTrue(AnyFlag(placed, "cap4", 1), "the dead-end is stub-capped by a protected 4×4 (flag=1)");
            Assert.IsFalse(AnyName(placed, "deadend4x8"), "the thin dead-end is pruned");
        }

        // Chain-walk (FUN_00585390:301 spur continuation): a dead-end reached through a ≤2-door spur
        // corridor prunes the whole branch back to the nearest junction — the corridor is deleted
        // too and the junction capped with its door-reduced variant.
        [Test]
        public void Finalize_DeadEndCorridorChain_PrunedBackToJunction()
        {
            var lg = new LevelGenerator { Rng = new System.Random(1) };
            lg.AddSyntheticPiece("junction3", 8, 8, (0, 2, 0), (8, 2, 2), (2, 8, 3));
            lg.AddSyntheticPiece("junction2", 8, 8, (0, 2, 0), (2, 8, 3));   // variant
            lg.AddSyntheticPiece("corr", 4, 4, (0, 2, 0), (4, 2, 2));         // 2-door spur corridor
            lg.AddSyntheticPiece("deadend", 4, 4, (0, 2, 0));                 // dead-end
            // junction3@(8,8) RIGHT (16,10) — corr@(16,8) LEFT(16,10)/RIGHT(20,10) — deadend@(20,8) LEFT(20,10)
            lg.TestRunFinalize(32, 32, new (string, int, int, byte)[]
            { ("junction3", 8, 8, (byte)0), ("corr", 16, 8, (byte)0), ("deadend", 20, 8, (byte)0) });
            var placed = lg.TestPlacedWithFlags();
            Assert.AreEqual(1, placed.Count, "the dead-end branch (corridor + stub) must be pruned back to the junction");
            Assert.AreEqual("junction2", placed[0].tcl);
            Assert.AreEqual(1, placed[0].flag);
        }

        // Guard: finalize only prunes thin (W==4||H==4) single-door pieces — an 8×8 single-door cap
        // is not thin, so it survives (this is why the existing full-closure synthetic tests, which
        // cap with 8×8 capL/capR, are unaffected by the finalize now running inside Start).
        [Test]
        public void Finalize_NonThinCap_NotPruned()
        {
            var lg = new LevelGenerator { Rng = new System.Random(1) };
            lg.AddSyntheticPiece("seed", 8, 8, (0, 2, 0), (8, 2, 2));
            lg.AddSyntheticPiece("cap8", 8, 8, (0, 2, 0));   // 8×8 single-door — NOT thin
            // seed@(0,0) RIGHT (8,2) — cap8@(8,0) LEFT (8,2)
            lg.TestRunFinalize(32, 16, new (string, int, int, byte)[]
            { ("seed", 0, 0, (byte)0), ("cap8", 8, 0, (byte)0) });
            Assert.AreEqual(2, lg.TestPlacedWithFlags().Count, "an 8×8 single-door cap is not thin → finalize leaves it");
        }

        // A forced/composite piece (flag=1) that happens to be a thin single-door stub is never
        // pruned — finalize only touches grow-loop (flag=0) pieces (FUN_00585390:64).
        [Test]
        public void Finalize_ForcedDeadEnd_NotPruned()
        {
            var lg = new LevelGenerator { Rng = new System.Random(1) };
            lg.AddSyntheticPiece("junction3", 8, 8, (0, 2, 0), (8, 2, 2), (2, 8, 3));
            lg.AddSyntheticPiece("junction2", 8, 8, (0, 2, 0), (2, 8, 3));
            lg.AddSyntheticPiece("deadend", 4, 4, (0, 2, 0));
            lg.TestRunFinalize(32, 32, new (string, int, int, byte)[]
            { ("junction3", 8, 8, (byte)0), ("deadend", 16, 8, (byte)1) });   // dead-end FORCED (flag=1)
            var placed = lg.TestPlacedWithFlags();
            Assert.AreEqual(2, placed.Count, "a flag=1 dead-end is left untouched");
            Assert.IsTrue(AnyName(placed, "deadend"), "the forced dead-end survives");
        }

        // Atomic revert (FUN_00589790:280-327 snapshot/restore): when a dead-end can neither be
        // absorbed (no junction variant) nor stub-capped (no matching 4×4 in the pool), the whole
        // pass reverts to the pre-finalize placements — a valid closed cluster is never left broken.
        [Test]
        public void Finalize_UnresolvablePrune_RevertsAtomically()
        {
            var lg = new LevelGenerator { Rng = new System.Random(1) };
            lg.AddSyntheticPiece("junction3", 8, 8, (0, 2, 0), (8, 2, 2), (2, 8, 3));  // no 2-door variant
            lg.AddSyntheticPiece("deadend4x8", 4, 8, (0, 2, 0));                         // 4×8 → not a 4×4 stub candidate
            lg.TestRunFinalize(32, 32, new (string, int, int, byte)[]
            { ("junction3", 8, 8, (byte)0), ("deadend4x8", 16, 8, (byte)0) });
            var placed = lg.TestPlacedWithFlags();
            Assert.AreEqual(2, placed.Count, "an unresolvable prune must revert (both pieces restored)");
            Assert.IsTrue(AnyName(placed, "deadend4x8") && AnyName(placed, "junction3"), "pre-finalize placements restored");
        }

        // ===================== B8 — COMPOSITE `mat` COURTYARD RESERVATION =====================

        // The mat-row parser (FUN_0065b180): a `mat` row's 5-tuples {type x y w h} become corner rects
        // {x, y, x+w, y+h, type}; non-mat (building) rows are skipped.
        [Test]
        public void MatRects_ParsedAsCornersWithTypeLast()
        {
            var rects = DigglesTclEngine.ParseTempGroupMatRects(
                "{108 40 urw_unq_vodo_003_a} {mat {0 28 24 80 8} {0 28 32 8 28} {2 108 24 8 16}}");
            Assert.AreEqual(3, rects.Count, "3 mat 5-tuples parsed; the building row is skipped");
            Assert.AreEqual((28, 24, 108, 32, 0), rects[0]);   // {0 28 24 80 8} → 28+80=108, 24+8=32
            Assert.AreEqual((28, 32, 36, 60, 0), rects[1]);    // {0 28 32 8 28} → 28+8=36, 32+28=60
            Assert.AreEqual((108, 24, 116, 40, 2), rects[2]);  // {2 108 24 8 16} → type 2, 108+8, 24+16
        }

        // The reservation paint (FUN_00589790:117-171) stamps the courtyard rect's cells = 1 in the
        // placement grid (the same grid TryPlace gates on), keeping them clear of grown pieces.
        [Test]
        public void Reservation_BlocksCourtyardCells()
        {
            var lg = new LevelGenerator();
            lg.ClearReservedRects();
            lg.AddReservedRect(10, 8, 20, 16);   // grid-local CORNERS [10,20)×[8,16)
            Assert.IsTrue(lg.TestReservationBlocks(48, 32, 12, 10), "a cell inside the reserved rect is blocked");
            Assert.IsTrue(lg.TestReservationBlocks(48, 32, 19, 15), "the last interior cell (19,15) is blocked");
            Assert.IsFalse(lg.TestReservationBlocks(48, 32, 20, 16), "the exclusive corner (20,16) is free");
            Assert.IsFalse(lg.TestReservationBlocks(48, 32, 5, 5), "a cell outside is free");
        }

        // End-to-end: a registered reservation keeps that region clear through a real grow — no placed
        // piece can overlap it (TryPlace rejects grid!=0), and the cluster still closes around it.
        [Test]
        public void Reservation_KeepsCourtyardClearDuringGrowth()
        {
            var lg = NewSyntheticLg();
            lg.SetArea(0, 0, 64, 32);
            lg.SetTemplateRatio(0f, 0.6f, 1f);
            lg.ClearReservedRects();
            lg.AddReservedRect(28, 12, 36, 20);   // an 8×8 keep-clear in the middle
            Assert.IsTrue(StartWithRetry(lg, 50), "generation failed to close around the reservation");
            foreach (var (n, x, y, w, h) in lg.LastLevelRects())
            {
                bool overlap = x < 36 && 28 < x + w && y < 20 && 12 < y + h;
                Assert.IsFalse(overlap, $"piece {n}@({x},{y}) {w}x{h} overlaps the reserved courtyard [28,36)×[12,20)");
            }
            TestContext.WriteLine($"closed with a courtyard reservation; {lg.LastLevel.Count} pieces, none in the keep-clear");
        }
    }
}
