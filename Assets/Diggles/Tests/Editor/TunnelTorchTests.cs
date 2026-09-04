#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using Diggles.Runtime.World;
    using NUnit.Framework;

    // Tunnel wall-torch law tests — the native glow-drop → Misc_Light pipeline
    // (Docs/TUNNEL_TORCH_LIGHTING_RE.md §2-4; FUN_005cd520 / FUN_005912a0 / FUN_005d4250 /
    // FUN_00590e60). Pure POCO: no engine, no scene.
    public sealed class TunnelTorchTests
    {
        static TunnelTorchSystem Sys(System.Func<int, int, int>? fill = null, int w = 64, int h = 64)
            => new TunnelTorchSystem(w, h, fill ?? ((x, y) => 0));

        // FUN_005cd520:24-29 — x jitter (rand − 0.5), y raised 0.2 + rand up the wall.
        [Test]
        public void Drop_JitterFollowsNativeLaw()
        {
            var s = Sys((x, y) => 4);   // tunnel band: wall face at fill 4
            s.Rand = () => 0.0;
            s.DropAttempt(30f, 40f, 5f);
            Assert.AreEqual(1, s.GlowCount);
            Assert.AreEqual(30f - 0.5f, s.GlowAt(0).X, 1e-4f);          // rand=0 → x − 0.5
            Assert.AreEqual(40f - 0.2f, s.GlowAt(0).Y, 1e-4f);          // rand=0 → y − 0.2
            // Glow z follows the wall-face fill law (max over the ±1 sub-cell, 15s
            // excluded), not the carrier's z=5 — the native never reads obj+0x14
            // (0x5cd520 byte disasm).
            Assert.AreEqual(4f, s.GlowAt(0).Z, 1e-4f);

            var s2 = Sys((x, y) => 4);
            s2.Rand = () => 0.999;
            s2.DropAttempt(30f, 40f, 0f);
            Assert.AreEqual(1, s2.GlowCount);
            Assert.AreEqual(30f + 0.499f, s2.GlowAt(0).X, 1e-3f);       // rand→1 → x + 0.5
            Assert.AreEqual(40f - 1.199f, s2.GlowAt(0).Y, 1e-3f);       // 0.2 + 1.0 up the wall
        }

        // Drop-z fold: max over the ±1 sub-cell cross, fully-solid (15) samples excluded
        // (the cmp/cmovb chain), clamp ≤14 — the shallowest real wall face near the point.
        [Test]
        public void Drop_ZFoldsMaxExcludingSolid()
        {
            // Center tile (30,40) fill 3; right neighbour (31,40) fill 6; up (30,39) solid 15.
            int Fill(int x, int y) => (x == 31 && y == 40) ? 6 : (x == 30 && y == 39) ? 0xF : 3;
            var s = Sys(Fill);
            // Deterministic anchor near the right edge of (30,40), high enough that y stays
            // inside row 40: ox=30.99+0.5 → x'=30.99; oy − 0.2 − 0.5... choose Rand=0.5 →
            // x' = ox, y' = oy − 0.7. ox=30.9, oy=40.75 ⇒ anchor (30.9, 40.05): x-cross
            // reaches (31,40) → max(3,6)=6; y-cross up reaches (30,39)=15 → excluded.
            s.Rand = () => 0.5;
            s.DropAttempt(30.9f, 40.75f, 0f);
            Assert.AreEqual(1, s.GlowCount);
            Assert.AreEqual(6f, s.GlowAt(0).Z, 1e-4f, "MAX picked the neighbour face, solid 15 excluded");
        }

        // FUN_005cd520:34 — fill nibble ≥ 0xF at the jittered tile swallows the drop.
        [Test]
        public void Drop_RejectedInsideSolidRock()
        {
            var s = Sys((x, y) => 0xF);
            s.Rand = () => 0.5;
            s.DropAttempt(30f, 40f, 0f);
            Assert.AreEqual(0, s.GlowCount);
        }

        // FUN_005912a0:40-41 — manhattan |dx|+|dy| < 10 tiles dedupes against pending entries.
        [Test]
        public void Glow_DedupeManhattan10()
        {
            var s = Sys();
            s.AddGlow(10f, 10f, 0f);
            s.AddGlow(14f, 15f, 0f);        // |4|+|5| = 9 < 10 → swallowed
            Assert.AreEqual(1, s.GlowCount);
            s.AddGlow(15f, 15.5f, 0f);      // |5|+|5.5| = 10.5 ≥ 10 → new entry
            Assert.AreEqual(2, s.GlowCount);
        }

        // FUN_005d4250:139-303 — age+1 per pass; spawn exactly at age 3 (once), entry gone at 6.
        [Test]
        public void Age_SpawnAt3_RemoveAt6_ExactlyOnce()
        {
            var s = Sys();
            var spawns = new List<(float x, float y)>();
            s.SpawnTorch = (x, y, z) => spawns.Add((x, y));
            s.AddGlow(30f, 40f, 0f);

            s.Tick();                        // age 1
            s.Tick();                        // age 2
            Assert.AreEqual(0, spawns.Count, "no spawn before age 3");
            s.Tick();                        // age 3 → spawn
            Assert.AreEqual(1, spawns.Count, "spawn exactly at age 3");
            Assert.AreEqual(30f, spawns[0].x, 1e-4f);
            Assert.AreEqual(40f, spawns[0].y, 1e-4f);
            s.Tick();                        // age 4
            s.Tick();                        // age 5
            Assert.AreEqual(1, spawns.Count, "no double spawn");
            Assert.AreEqual(1, s.GlowCount, "entry still pending before age 6");
            s.Tick();                        // age 6 → removed
            Assert.AreEqual(0, s.GlowCount, "entry removed at age 6");
            Assert.AreEqual(1, spawns.Count);
        }

        // FUN_00590e60:45 — y < set_light_begin (above the sky line) rejects placement.
        [Test]
        public void Placement_AboveSkyLineRejected()
        {
            var s = Sys(null, 64, 128);
            s.LightBegin = 33;
            Assert.IsFalse(s.PlacementOk(30f, 20f), "above the sky line");
            Assert.IsTrue(s.PlacementOk(30f, 40f), "underground OK");
        }

        // FUN_00590e60:42-45 — the 2.0-tile border margin of the light rect.
        [Test]
        public void Placement_BorderMarginRejected()
        {
            var s = Sys();
            Assert.IsFalse(s.PlacementOk(1.5f, 30f));
            Assert.IsFalse(s.PlacementOk(63.0f, 30f));
            Assert.IsFalse(s.PlacementOk(30f, 1.0f));
            Assert.IsTrue(s.PlacementOk(30f, 30f));
        }

        // FUN_00590e60:122-165 — an existing light with distSq < radius + 4 blocks the spot.
        [Test]
        public void Placement_NearExistingLightRejected()
        {
            var s = Sys();
            float qx = 0, qy = 0;
            s.NearExistingLight = (x, y) => { qx = x; qy = y; return true; };
            Assert.IsFalse(s.PlacementOk(30f, 40f));
            Assert.AreEqual(30f, qx, 1e-4f);   // probe receives the glow position
            Assert.AreEqual(40f, qy, 1e-4f);
            s.NearExistingLight = (x, y) => false;
            Assert.IsTrue(s.PlacementOk(30f, 40f));
        }

        // FUN_004753f0 approximation — a sky-lit (daylight-shaft) tile takes no torch.
        [Test]
        public void Placement_SkyLitTileRejected()
        {
            var s = Sys();
            s.SkyLitAt = (tx, ty) => true;
            Assert.IsFalse(s.PlacementOk(30f, 40f));
            s.SkyLitAt = (tx, ty) => false;
            Assert.IsTrue(s.PlacementOk(30f, 40f));
        }

        // The native spacing comparison is literally distSq < radius + 4 (lopsided units,
        // FUN_00590e60:146-149) — for torch radius 6: dist 3.0 (sq 9 < 10) blocks,
        // dist 3.3 (sq 10.89 ≥ 10) allows.
        [Test]
        public void Spacing_DistSqVsRadiusPlus4Law()
        {
            const float radius = 6f, spacing = TunnelTorchSystem.SpacingAdd;
            Assert.IsTrue(3.0f * 3.0f < radius + spacing, "3.0 tiles: blocked");
            Assert.IsFalse(3.3f * 3.3f < radius + spacing, "3.3 tiles: allowed");
        }

        // ---- class_snaptowall z-settle (obj flag 0x20000; FUN_005cbf70:125-176) ----
        // z = min(fill over the ±¼-tile cross) + 0.01: the torch hangs on the exposed wall
        // face (rock occupies native z ∈ [0, fill]); a deeper-dug tile (cave) has a smaller
        // fill, so the face — and the torch — sits deeper.

        [Test]
        public void SettleZ_TunnelFace_IsFillPlusLift()
        {
            float z = TunnelTorchSystem.SettleZ((x, y) => 4, 30.5f, 40.5f);
            Assert.AreEqual(4f + TunnelTorchSystem.SettleLift, z, 1e-5f);
        }

        [Test]
        public void SettleZ_CaveSitsDeeperThanTunnel()
        {
            float tunnel = TunnelTorchSystem.SettleZ((x, y) => 4, 30.5f, 40.5f);
            float cave = TunnelTorchSystem.SettleZ((x, y) => 0, 30.5f, 40.5f);
            Assert.AreEqual(TunnelTorchSystem.SettleLift, cave, 1e-5f);
            Assert.Less(cave, tunnel, "cave face is deeper into the screen than the tunnel face");
        }

        [Test]
        public void SettleZ_CrossPicksNeighbourMinNearEdge()
        {
            // Tile (30,40) fill 4; the right neighbour (31,40) dug to 0.
            int Fill(int x, int y) => (x == 31 && y == 40) ? 0 : 4;
            // Centered anchor: the ±0.25 cross stays inside the tile → face 4.01.
            Assert.AreEqual(4f + TunnelTorchSystem.SettleLift,
                TunnelTorchSystem.SettleZ(Fill, 30.5f, 40.5f), 1e-5f);
            // Anchor within ¼ tile of the right edge: the cross reaches (31,40) → 0.01.
            Assert.AreEqual(TunnelTorchSystem.SettleLift,
                TunnelTorchSystem.SettleZ(Fill, 30.9f, 40.5f), 1e-5f);
        }

        [Test]
        public void SettleZAt_OutOfBounds_IsNaN()
        {
            var s = Sys();
            Assert.IsTrue(float.IsNaN(s.SettleZAt(0.3f, 30f)), "inside the ½-tile margin");
            Assert.IsFalse(float.IsNaN(s.SettleZAt(30f, 30f)));
        }
    }

    // End-to-end through the engine: `set_autolight this 1` (the zwerg.tcl:156 opt-in) →
    // DigglesTclEngine.TunnelTorchTick drop + age → a real Misc_Light entity spawns through
    // the `new` pipeline with the native post-spawn scrub (owner -1, viewinfog off), and its
    // class_light then blocks a second torch at the same spot (the FUN_00590e60 spacing law
    // closing the loop through AnyLightNear).
    public sealed class TunnelTorchEngineTests
    {
        [Test]
        public void Autolight_TickPipeline_SpawnsMiscLight_ThenSpacingBlocksSecond()
        {
            var interp = new Diggles.Runtime.Tcl.TclInterpreter();
            var ctx = Diggles.Runtime.Tcl.Engine.DigglesTclEngine.Register(interp);
            ctx.LocalPlayer = 0;
            // The torch class with its real class_light radius + wall snap (emitter.tcl:3-11).
            interp.Eval("def_class Misc_Light none dummy 0 {} { class_light {0 -0.5 1.0} 6 {0.9 0.9 0.8} \n class_snaptowall 1 }");
            Assert.IsTrue(ctx.Classes["Misc_Light"].SnapToWall, "class_snaptowall parsed (classdef+0x2c bit 8)");
            // A gnome stand-in: Autolight opt-in via the entity prop var.
            interp.Eval("def_class G none gnome 0 {} {}");
            var h = int.Parse(interp.Eval("new G \"\" {30 40 5} {0 0 0}"),
                              System.Globalization.CultureInfo.InvariantCulture);
            var gnome = ctx.Entities[h];
            gnome.Owner = 0;
            interp.Eval($"set_autolight {h} 1");

            // Mutable terrain: the drop tunnel band starts at fill 4 (rock face z=4); the
            // "cave dig" below flips it to 0.
            int tunnelFill = 4;
            ctx.TunnelTorches = new TunnelTorchSystem(64, 64, (x, y) => tunnelFill)
            {
                Rand = () => 0.5,   // deterministic drop at (30, 39.3)
                NearExistingLight = (x, y) => Diggles.Runtime.Tcl.Engine.DigglesTclEngine.AnyLightNear(ctx, x, y),
                SpawnTorch = (x, y, z) => Diggles.Runtime.Tcl.Engine.DigglesTclEngine.TunnelTorchSpawn(ctx, x, y, z),
            };

            int Torches() { int n = 0; foreach (var e in ctx.Entities.Values) if (e.Class?.Name == "Misc_Light") n++; return n; }
            Diggles.Runtime.Tcl.Engine.DigglesTclEntity? Torch()
            { foreach (var e in ctx.Entities.Values) if (e.Class?.Name == "Misc_Light") return e; return null; }

            // Drop stagger (handle+tick)%5==0 hits within 5 ticks; +3 aging ticks to mature.
            for (int t = 0; t < 8; t++)
                Diggles.Runtime.Tcl.Engine.DigglesTclEngine.TunnelTorchTick(ctx);
            Assert.AreEqual(1, Torches(), "one torch spawned through the new pipeline");
            var torch = Torch()!;
            Assert.AreEqual(-1, torch.Owner, "native +0x36c = -1 (no fow lane)");
            Assert.AreEqual("0", torch.VarsScope.Vars["_viewinfog"], "native clears bit 0x80");
            // Placement: spawn z = the tunnel wall face (fill 4 → 4.01), not the gnome's
            // walk depth (5).
            Assert.AreEqual(4f + TunnelTorchSystem.SettleLift, torch.Position.z, 1e-4f,
                "torch hangs on the tunnel back-wall face");

            // Keep walking the same spot: the spawned torch's class_light (r=6) now blocks any
            // new torch within distSq < 6+4 — no duplicates ever.
            for (int t = 0; t < 20; t++)
                Diggles.Runtime.Tcl.Engine.DigglesTclEngine.TunnelTorchTick(ctx);
            Assert.AreEqual(1, Torches(), "spacing law blocks a second torch at the same spot");

            // Cave over the tunnel: the wall recedes (fill 4 → 0); the 16-tick snaptowall
            // settle re-seats the torch on the new deeper face (FUN_005cbf70:125-176).
            tunnelFill = 0;
            for (int t = 0; t < 40; t++)
                Diggles.Runtime.Tcl.Engine.DigglesTclEngine.TunnelTorchTick(ctx);
            Assert.AreEqual(TunnelTorchSystem.SettleLift, Torch()!.Position.z, 1e-4f,
                "torch tracked the receded cave wall deeper");
        }
    }

    // Underground darkness baseline — the native per-tile BASELIGHT byte (tile+0x07,
    // FUN_005906a0; Docs/TUNNEL_TORCH_LIGHTING_RE.md §6) at the port's tile scale.
    public sealed class BaseLightMapTests
    {
        // Open tile above the set_light_begin row → daylight 20.
        [Test]
        public void Sky_OpenTileAboveLightBegin_IsDaylight()
        {
            var m = new BaseLightMap(32, 64, (x, y) => 0) { LightBegin = 33 };
            m.RecomputeAll();
            Assert.AreEqual(BaseLightMap.Daylight, m.At(10, 10));
            Assert.IsTrue(m.SkyLit(10, 10));
        }

        // Open flat tunnel below the sky line → the flat base 4 (acc/2 + 4 with acc = 0).
        [Test]
        public void Underground_FlatOpenTunnel_IsFlatBase()
        {
            var m = new BaseLightMap(32, 64, (x, y) => 0) { LightBegin = 33 };
            m.RecomputeAll();
            Assert.AreEqual(BaseLightMap.FlatBase, m.At(10, 50));
            Assert.IsFalse(m.SkyLit(10, 50));
        }

        // Open tile whose up-left neighbour is solid rock → clamped to 0 (the native corner
        // occlusion: 12 diagonal samples all hit rock, acc = −96 → /2 + 4 → clamp 0).
        [Test]
        public void Underground_UpLeftRock_ClampsToZero()
        {
            var m = new BaseLightMap(32, 64,
                (x, y) => (x == 9 && y == 49) ? 0xF : 0) { LightBegin = 33 };
            m.RecomputeAll();
            Assert.AreEqual(0, m.At(10, 50));
        }

        // Solid tile above the sky line under 7+ solid rows (overhang nook) falls through to
        // the underground term instead of daylight (FUN_005906a0:335-378 scan exhausted).
        [Test]
        public void Sky_BlockedOverhang_FallsThroughToUndergroundTerm()
        {
            // Column 10: everything solid → the upward scan never finds an open cell.
            var m = new BaseLightMap(32, 64, (x, y) => 0xF) { LightBegin = 33 };
            m.RecomputeAll();
            // own=15, upleft=15 → acc = Σ 3·clamp(15+j−15, −8, 0) = 0 → base 4 (dim rock face).
            Assert.AreEqual(BaseLightMap.FlatBase, m.At(10, 30));
        }

        // A solid tile above the sky line with an opening within the 6-tile scan → daylight
        // (sunlit rock face near the surface).
        [Test]
        public void Sky_OpeningWithinScanRange_IsDaylight()
        {
            var m = new BaseLightMap(32, 64,
                (x, y) => y == 24 ? 0 : 0xF) { LightBegin = 33 };
            m.RecomputeAll();
            Assert.AreEqual(BaseLightMap.Daylight, m.At(10, 30), "opening 6 rows up");
            Assert.AreNotEqual(BaseLightMap.Daylight, m.At(10, 31), "opening 7 rows up — out of scan");
        }

        // Default LightBegin (-10000, FUN_00594160:258) = no sky anywhere: the whole map is
        // underground (maps without set_light_begin are underground-only).
        [Test]
        public void Default_NoLightBegin_WholeMapUnderground()
        {
            var m = new BaseLightMap(32, 64, (x, y) => 0);
            m.RecomputeAll();
            Assert.AreEqual(BaseLightMap.FlatBase, m.At(10, 5));
        }

        // A carve (fill change) recomputed through RecomputeRect flips the value and raises
        // RegionChanged — the presenter's re-upload hook.
        [Test]
        public void Recompute_AfterCarve_UpdatesAndRaisesRegion()
        {
            int fillAt10_50 = 0xF;
            var m = new BaseLightMap(32, 64, (x, y) => (x == 10 && y == 50) ? fillAt10_50 : 0)
            { LightBegin = 33 };
            m.RecomputeAll();
            // (11,51)'s up-left neighbour (10,50) is rock → 0.
            Assert.AreEqual(0, m.At(11, 51));
            (int l, int t, int r, int b)? region = null;
            m.RegionChanged += (l, t, r, b) => region = (l, t, r, b);
            fillAt10_50 = 0;                       // the carve opens the neighbour
            m.RecomputeRect(9, 49, 12, 52);
            Assert.AreEqual(BaseLightMap.FlatBase, m.At(11, 51));
            Assert.IsNotNull(region, "RegionChanged raised");
        }
    }

    // Smoothness law: the gray veil glides between fog ticks instead of snapping per tick,
    // and torch light fades from the same smoothed display field (one mechanism —
    // FogOfWarPresenter.GlideSeconds + RuntimeWorldSpawner.FogLightFactor).
    public sealed class FogVeilGlideTests
    {
        UnityEngine.GameObject? _go;

        [TearDown]
        public void Teardown()
        {
            if (_go != null) UnityEngine.Object.DestroyImmediate(_go);
            _go = null;
        }

        // The stutter fix: a fade tick moves the decoded veil target by ~10 units; the old
        // constant-speed chase (255/SmoothSeconds ≈ 364 u/s) finished that step within one
        // frame (~30 ms) and idled until the next fog tick — a visible 2.5 Hz veil stutter.
        // The glide law (speed = min(maxSpeed, gap/GlideSeconds)) stretches a small step
        // across ~GlideSeconds: still moving mid-tick, settled exactly by ~0.45 s.
        // (Written against the fix; on the pre-fix presenter the mid-tick assert fails —
        // display would already sit on the target after 0.1 s.)
        [Test]
        public void VeilFadeStep_GlidesAcrossTheFogTick_NoSnap()
        {
            var fog = new FogOfWarState(16, 16, (x, y) => 0);
            _go = new UnityEngine.GameObject("FowGlideHost");
            var pr = _go.AddComponent<FogOfWarPresenter>();
            pr.Init(fog);

            float lx = 0f, ly = 0f;
            fog.BeginUpdate(0);
            fog.UpdateRevealer(8f, 8f, ref lx, ref ly, 4 * 8, 4 * 8, 0, 0);
            fog.EndUpdate();
            pr.Pump(10f);                                       // settle: visible, r display = 0
            Assert.AreEqual(0, pr.DisplayAt(8, 8).r, "visible tile — no veil");

            // Fade into the visible ramp: 8 updates → byte 0xDF (r target 9), settle there.
            for (int i = 0; i < 8; i++) { fog.BeginUpdate(0); fog.EndUpdate(); }
            pr.Pump(10f);
            byte r0 = pr.DisplayAt(8, 8).r;
            Assert.AreEqual(pr.TargetAt(8, 8).r, r0, "settled on the ramp");

            // One more fog tick: target steps ~+10 units. The display must still be gliding
            // 0.1 s in (not already snapped onto the target — the old 30 ms law), and must
            // land exactly by ~GlideSeconds.
            fog.BeginUpdate(0); fog.EndUpdate();
            byte tgt = 0; // read after the pump consumes the tick
            pr.Pump(0.1f);
            tgt = pr.TargetAt(8, 8).r;
            Assert.Greater(tgt, r0, "fade tick raised the veil target");
            byte mid = pr.DisplayAt(8, 8).r;
            Assert.Greater((int)mid, (int)r0 - 1, "display moves toward the veil");
            Assert.Less((int)mid, (int)tgt, "0.1 s in: still gliding — NOT snapped (the r7 stutter)");
            pr.Pump(FogOfWarPresenter.GlideSeconds);            // finish the glide
            Assert.AreEqual(tgt, pr.DisplayAt(8, 8).r, "settled exactly after the glide");
        }

        // Full-swing reveals must keep the historical constant pace (255/SmoothSeconds) —
        // the glide law only slows small steps. Mirrors Reveal_FadesInOverSmoothSeconds.
        [Test]
        public void FullSwingReveal_KeepsSmoothSecondsPace()
        {
            var fog = new FogOfWarState(16, 16, (x, y) => 0);
            _go = new UnityEngine.GameObject("FowGlideHost");
            var pr = _go.AddComponent<FogOfWarPresenter>();
            pr.Init(fog);
            float lx = 0f, ly = 0f;
            fog.BeginUpdate(0);
            fog.UpdateRevealer(8f, 8f, ref lx, ref ly, 4 * 8, 4 * 8, 0, 0);
            fog.EndUpdate();
            pr.Pump(FogOfWarPresenter.SmoothSeconds * 0.5f);
            int mid = pr.DisplayAt(8, 8).g;
            Assert.Greater(mid, 100, "half the swing covered at half SmoothSeconds");
            Assert.Less(mid, 155, "…and no more than about half");
            pr.Pump(FogOfWarPresenter.SmoothSeconds);
            Assert.AreEqual(255, pr.DisplayAt(8, 8).g, "full swing completes on the old clock");
        }

        // The torch light factor — pure display→intensity law (RuntimeWorldSpawner
        //.FogLightFactor): torches die with the veil and in the black; viewinfog ambience
        // (LightSrc markers) follows only the explored indicator (lit under gray).
        [Test]
        public void FogLightFactor_Law()
        {
            const float eps = 1e-4f;
            // torch (viewInFog=false): clear → 1; full veil → 0; half veil → ~0.5; black → 0.
            Assert.AreEqual(1f, Diggles.Runtime.RuntimeWorldSpawner.FogLightFactor(0, 255, false), eps);
            Assert.AreEqual(0f, Diggles.Runtime.RuntimeWorldSpawner.FogLightFactor(255, 255, false), eps);
            Assert.AreEqual(0.5f, Diggles.Runtime.RuntimeWorldSpawner.FogLightFactor(127, 255, false), 0.01f);
            Assert.AreEqual(0f, Diggles.Runtime.RuntimeWorldSpawner.FogLightFactor(0, 0, false), eps);
            // viewinfog ambience: full under any veil while explored; fades only from black.
            Assert.AreEqual(1f, Diggles.Runtime.RuntimeWorldSpawner.FogLightFactor(255, 255, true), eps);
            Assert.AreEqual(0.5f, Diggles.Runtime.RuntimeWorldSpawner.FogLightFactor(255, 127, true), 0.01f);
            Assert.AreEqual(0f, Diggles.Runtime.RuntimeWorldSpawner.FogLightFactor(0, 0, true), eps);
        }
    }
}
