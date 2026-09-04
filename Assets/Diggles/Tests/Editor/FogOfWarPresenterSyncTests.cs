#nullable enable

namespace Diggles.Tests
{
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    // Presenter/overlay DESYNC certification (Play r6 "black fog sometimes renders
    // as WHOLE HARD BLOCKS in caves"). The overlay drops covers INSTANTLY from raw sim bytes;
    // the presenter's texture brightens by chasing decoded TARGETS — so any texel whose target
    // rebuild is MISSED by the dirty-rect union (c158834) stays black forever under a dropped
    // cover = a permanent hard black terrain block. These tests pin the invariant that after
    // every consumed Pump the TARGETS agree with the sim bytes:
    // byte == 0 → target (0, 0) (black — the cover's own tile);
    // byte == 0xFF → target (0, 255) (fully visible: no veil, not black);
    // byte == 1 → target (255, 255) (resting gray floor);
    // byte 2..0xFE → target.g == 255 (mid-fade: the R veil may lag by the native
    // quantization law — every 5th tick / 0x7C crossing —
    // but BLACK-ness must never be stale).
    // Pattern: FogOfWarPresenterTests / FogOfWarOverlayTests — GameObject + Pump-driven.
    public sealed class FogOfWarPresenterSyncTests
    {
        GameObject? _go;

        [TearDown]
        public void TearDown()
        {
            if (_go != null) Object.DestroyImmediate(_go);
            _go = null;
        }

        // The FUN_004ca830 fogFactor decode law (mirror of FogOfWarPresenter.BuildGrayLut).
        static byte ExpectedGray(byte b)
        {
            if (b == 0) return 0;
            if (b <= 0x80) return 255;
            int u = System.Math.Min(0x80, (int)((b - 0x80) * 1.3)) * 2;
            return (byte)System.Math.Max(0, 255 - u);
        }

        static void AssertTargetsMatchBytes(FogOfWarState fog, FogOfWarPresenter pr, string when)
        {
            for (int ty = 0; ty < fog.Height; ty++)
                for (int tx = 0; tx < fog.Width; tx++)
                {
                    byte b = fog.FogAt(tx, ty, fog.LocalPlayer);
                    var (r, g) = pr.TargetAt(tx, ty);
                    byte eg = b == 0 ? (byte)0 : (byte)255;
                    if (g != eg)
                        Assert.Fail($"{when}: STALE black target at ({tx},{ty}): byte={b:x2} target.g={g} " +
                                    "— the dirty-rect union missed this texel; the overlay cover state and the " +
                                    "fog texture disagree (the 'hard black block' bug class)");
                    if (r != ExpectedGray(b))
                        Assert.Fail($"{when}: STALE gray target at ({tx},{ty}): byte={b:x2} target.r={r} " +
                                    $"expected {ExpectedGray(b)} — after every consumed Pump the veil target " +
                                    "must equal the decode of the CURRENT byte (settled bytes via RegionDirty, " +
                                    "fading bytes via the per-tick fade-list refresh)");
                }
        }

        // THE c158834 REGRESSION REPRO (Play r6 "black fog renders as whole hard blocks, not
        // smooth"): FadeBlocks dirty-rects a fading tile only every 5th tick / at the 0x7C
        // crossing (the native re-mesh quantization law), and the dirty-rect union no longer
        // refreshes fading texels incidentally the way the old full-plane rebuild did — so the
        // whole fading region's veil TARGET froze for up to 5 updates (~2 s) and then jumped
        // as ONE synchronized block. The veil target must instead track the decoded byte on
        // every fog tick.
        [Test]
        public void FadingVeil_TargetTracksEveryUpdate_NoQuintileBlockJumps()
        {
            const int W = 32, H = 24;
            var fog = new FogOfWarState(W, H, (x, y) => 0);
            _go = new GameObject("FowSyncHost");
            var pr = _go.AddComponent<FogOfWarPresenter>();
            pr.Init(fog);

            float lx = 0f, ly = 0f;
            fog.BeginUpdate(0);
            fog.UpdateRevealer(16f, 12f, ref lx, ref ly, 6 * 8, 4 * 8, 0, 0);
            fog.EndUpdate();
            pr.Pump(0.033f);
            Assert.That(fog.FogAt(16, 12, 0), Is.EqualTo(0xFF), "revealed");

            // Abandon the pocket: the byte fades −4/update. Inside the visible ramp
            // (0x81..0xE2) the decoded veil changes EVERY update — the presenter target must
            // follow per update, not only when the quantized dirty happens to fire.
            for (int i = 0; i < 40; i++)
            {
                fog.BeginUpdate(0); fog.EndUpdate();
                pr.Pump(0.033f);
                byte b = fog.FogAt(16, 12, 0);
                if (b > 0x80 && b < 0xE3)
                    Assert.That(pr.TargetAt(16, 12).r, Is.EqualTo(ExpectedGray(b)),
                        $"update {i} (byte {b:x2}, tick {fog.Tick}): the veil target lags the " +
                        "sim — the fading region steps in whole quantized blocks instead of " +
                        "moving every update (the r6 'hard blocks' regression)");
            }
        }

        // The r6 Play shape: a revealer random-walking through rock (fresh reveals every update)
        // NEXT TO tiles it already left fading, several fog updates per pumped frame
        // (fast-forward), occasional remove_black_fog story reveals, occasional missed frames.
        // After EVERY consumed Pump the targets must agree with the bytes everywhere.
        [Test]
        public void UnionRect_NeverLeavesStaleTargets_UnderInterleavedRevealFadePumps()
        {
            const int W = 96, H = 64;
            // Scattered solid rock so reveal rays stop and the 3-tile penetration paths vary.
            var fog = new FogOfWarState(W, H, (x, y) => (x * 7 + y * 13) % 11 == 0 ? 15 : 0);
            _go = new GameObject("FowSyncHost");
            var pr = _go.AddComponent<FogOfWarPresenter>();
            pr.Init(fog);

            fog.SetFowBegin(8, 0);   // campaign-boot sky band, prunes fade entries above row 8
            pr.Pump(0.033f);
            AssertTargetsMatchBytes(fog, pr, "after set_fow_begin");

            var rng = new System.Random(1234);
            float wx = 12f, wy = 20f, lx = 0f, ly = 0f;
            for (int frame = 0; frame < 300; frame++)
            {
                int updates = 1 + rng.Next(3);           // fast-forward: several updates per frame
                for (int u = 0; u < updates; u++)
                {
                    fog.BeginUpdate(0);
                    wx = Mathf.Clamp(wx + (float)(rng.NextDouble() * 3.0 - 1.2), 2f, W - 3f);
                    wy = Mathf.Clamp(wy + (float)(rng.NextDouble() * 3.0 - 1.2), 9f, H - 3f);
                    // Stored fow sizes are fine units (tile·8): a gnome-scale 14×8 reveal.
                    fog.UpdateRevealer(wx, wy, ref lx, ref ly, 14 * 8, 8 * 8, 0, frame * 7 + u);
                    if (rng.Next(23) == 0)               // story remove_black_fog rect
                    {
                        int bx = rng.Next(W - 8), by = rng.Next(H - 6);
                        fog.RemoveBlackFog(0, bx, by, bx + 7, by + 5);
                    }
                    fog.EndUpdate();
                }
                if (rng.Next(4) != 0)                    // some frames the presenter never runs (hitch)
                {
                    pr.Pump(0.033f);
                    AssertTargetsMatchBytes(fog, pr, $"frame {frame}");
                }
            }
        }

        // Abandoned exploration decays 0xFF → 1 (−4/update); with a Pump per update and a final
        // settle the DISPLAY must land on the exact decode of every byte — the fade-list clamp
        // and the 0x7C crossing dirties must all be consumed through the union.
        [Test]
        public void AbandonedReveal_DisplaySettlesToExactDecode_AfterFadeListsDrain()
        {
            const int W = 48, H = 40;
            var fog = new FogOfWarState(W, H, (x, y) => 0);
            _go = new GameObject("FowSyncHost");
            var pr = _go.AddComponent<FogOfWarPresenter>();
            pr.Init(fog);

            float lx = 0f, ly = 0f;
            fog.BeginUpdate(0);
            fog.UpdateRevealer(20f, 20f, ref lx, ref ly, 14 * 8, 8 * 8, 0, 0);
            fog.EndUpdate();
            pr.Pump(0.033f);
            Assert.That(pr.TargetAt(20, 20).g, Is.EqualTo(255), "reveal target consumed");

            // 70 revealer-less updates: every explored byte reaches the gray floor 1.
            for (int i = 0; i < 70; i++) { fog.BeginUpdate(0); fog.EndUpdate(); pr.Pump(0.05f); }
            pr.Pump(10f); pr.Pump(10f);                  // settle the display chase

            for (int ty = 0; ty < H; ty++)
                for (int tx = 0; tx < W; tx++)
                {
                    byte b = fog.FogAt(tx, ty, 0);
                    Assert.That(b, Is.LessThanOrEqualTo(1), $"({tx},{ty}) drained");
                    var (r, g) = pr.DisplayAt(tx, ty);
                    if (r != ExpectedGray(b) || g != (b == 0 ? 0 : 255))
                        Assert.Fail($"({tx},{ty}): display ({r},{g}) != decode of byte {b:x2} " +
                                    $"(({ExpectedGray(b)},{(b == 0 ? 0 : 255)})) after full drain+settle");
                }
        }

        // The exact suspected r6 scenario, with the REAL overlay wired alongside in Play order
        // (overlay pumps before the presenter): a fresh reveal right next to a fading pocket.
        // Wherever a cover dropped (tile no longer all-black) every explored corner texel must
        // already have a bright target — i.e. the texture starts brightening the same frame the
        // cover drops, never later.
        [Test]
        public void FreshRevealNextToFadingTiles_CoversDropOnlyWhereTargetsBrighten()
        {
            const int W = 48, H = 40;
            var fog = new FogOfWarState(W, H, (x, y) => 0);
            _go = new GameObject("FowSyncHost");
            var pr = _go.AddComponent<FogOfWarPresenter>();
            pr.Init(fog);
            var ov = _go.AddComponent<FogOfWarOverlay>();
            ov.Init(fog, (x, y) => 0);

            // 1. Explore a pocket, then let it fade into the visible ramp (9 updates: 0xFF→0xDB).
            float lx = 0f, ly = 0f;
            fog.BeginUpdate(0);
            fog.UpdateRevealer(12f, 20f, ref lx, ref ly, 6 * 8, 4 * 8, 0, 1);
            fog.EndUpdate();
            ov.Pump(); pr.Pump(0.033f);
            for (int i = 0; i < 9; i++)
            {
                fog.BeginUpdate(0); fog.EndUpdate();
                ov.Pump(); pr.Pump(0.033f);
            }

            // 2. A fresh reveal beside the fading pocket — two updates in one frame (ff), then
            // one Play-ordered frame.
            float lx2 = 0f, ly2 = 0f;
            fog.BeginUpdate(0);
            fog.UpdateRevealer(20f, 20f, ref lx2, ref ly2, 6 * 8, 4 * 8, 0, 2);
            fog.EndUpdate();
            fog.BeginUpdate(0);
            fog.UpdateRevealer(22f, 20f, ref lx2, ref ly2, 6 * 8, 4 * 8, 0, 2);
            fog.EndUpdate();
            ov.Pump(); pr.Pump(0.016f);

            AssertTargetsMatchBytes(fog, pr, "after the fresh reveal frame");

            // The cover contract: for every tile whose cover is gone (some corner byte != 0),
            // each EXPLORED corner's target must be bright (g=255) — the texture began
            // brightening exactly where the overlay opened the hole.
            for (int ty = 0; ty < H - 1; ty++)
                for (int tx = 0; tx < W - 1; tx++)
                {
                    bool covered = fog.FogAt(tx, ty, 0) == 0 && fog.FogAt(tx + 1, ty, 0) == 0 &&
                                   fog.FogAt(tx, ty + 1, 0) == 0 && fog.FogAt(tx + 1, ty + 1, 0) == 0;
                    if (covered) continue;
                    for (int c = 0; c < 4; c++)
                    {
                        int cx = tx + (c & 1), cy = ty + (c >> 1);
                        if (fog.FogAt(cx, cy, 0) == 0) continue;
                        Assert.That(pr.TargetAt(cx, cy).g, Is.EqualTo(255),
                            $"tile ({tx},{ty}) lost its cover but corner ({cx},{cy}) target is still " +
                            "black — hard black terrain block under a dropped cover");
                    }
                }
        }
    }
}
