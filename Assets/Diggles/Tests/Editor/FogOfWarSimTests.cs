#nullable enable

namespace Diggles.Tests
{
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    // Fog-of-war end-to-end through the engine: Tcl `set_fogofwar` stores the ×8 fine sizes on
    // the entity, DigglesGameSim's every-4th-think pass reveals around it, and `get_max_fow`
    // reads the real byte back (replacing the old hardcoded "50"). Docs/FOG_OF_WAR_RE.md.
    public sealed class FogOfWarSimTests
    {
        [Test]
        public void SetFogofwar_SimPass_GetMaxFow_RoundTrip()
        {
            var interp = new TclInterpreter();
            var ctx = DigglesTclEngine.Register(interp);
            ctx.FogOfWar = new FogOfWarState(64, 64, (x, y) => 0);
            ctx.LocalPlayer = 0;

            var e = new DigglesTclEntity
            {
                Handle = 7,
                Class = new DigglesClass { Name = "TestGnome", Kind = "gnome" },
                Position = new Vector3(32f, 32f, 10f),
                Owner = 0,
            };
            ctx.Entities[7] = e;

            // Before any reveal: the real map answers 0 (unseen), not the legacy 50.
            Assert.That(interp.Eval("get_max_fow 7"), Is.EqualTo("0"));

            // The gnome law: set_fogofwar this 14 8 → stored ×8 in fine units (FUN_0068a980).
            interp.Eval("set_fogofwar 7 14 8");
            Assert.That(e.FowSizeX, Is.EqualTo(14 * 8));
            Assert.That(e.FowSizeY, Is.EqualTo(8 * 8));

            // Four 10 Hz thinks = one native FoW update ((game+0x6c)&3==3).
            var sim = new DigglesGameSim(ctx);
            for (int i = 0; i < 4; i++) sim.Step(0.1);

            Assert.That(ctx.FogOfWar.FogAt(32, 32, 0), Is.EqualTo(0xFF), "tile under the gnome revealed");
            Assert.That(interp.Eval("get_max_fow 7"), Is.EqualTo("255"));
            Assert.That(interp.Eval("get_max_fow 7 0"), Is.EqualTo("255"), "explicit owner lane");
            Assert.That(interp.Eval("get_max_fow 7 3"), Is.EqualTo("0"), "other owner still black");

            // set_fogofwar this -1 -1 disables; the tile then fades back to gray over updates.
            interp.Eval("set_fogofwar 7 -1 -1");
            Assert.That(e.FowSizeX, Is.EqualTo(-1), "-1 stays -1 (not ×8)");
            for (int i = 0; i < 4 * 80; i++) sim.Step(0.1);
            Assert.That(ctx.FogOfWar.FogAt(32, 32, 0), Is.EqualTo(1), "faded to the gray floor");
        }

        // Regression: linked hats / held tools went invisible. link_obj re-parents the rider
        // under the carrier's Model, so its logical Position goes stale (black tile) and the old
        // fog pass hid its renderers directly. The fix: link children are skipped (the carrier's
        // renderer sweep owns them) and never keep a stale FowHidden.
        [Test]
        public void LinkedRider_FollowsCarrierVisibility_NeverFogHiddenItself()
        {
            var interp = new TclInterpreter();
            var ctx = DigglesTclEngine.Register(interp);
            ctx.FogOfWar = new FogOfWarState(64, 64, (x, y) => 0);
            ctx.LocalPlayer = 0;

            var root = new GameObject("Root").transform;
            try
            {
                ctx.SpawnParent = root;
                var gnomeGo = new GameObject("gnome");
                gnomeGo.transform.SetParent(root, false);
                var model = new GameObject("Model");
                model.transform.SetParent(gnomeGo.transform, false);
                var gnomeMr = model.AddComponent<MeshRenderer>();
                var hatGo = new GameObject("hat");                    // rides the Model (link_obj)
                hatGo.transform.SetParent(model.transform, false);
                var hatMr = hatGo.AddComponent<MeshRenderer>();

                var gnome = new DigglesTclEntity
                {
                    Handle = 1, Owner = 0, GameObject = gnomeGo,
                    Class = new DigglesClass { Name = "TestGnome", Kind = "gnome" },
                    Position = new Vector3(32f, 32f, 10f),
                };
                var hat = new DigglesTclEntity
                {
                    Handle = 2, Owner = 0, GameObject = hatGo,
                    Class = new DigglesClass { Name = "TestHat", Kind = "tool" },
                    Position = Vector3.zero,                          // stale — the bug's trigger
                };
                ctx.Entities[1] = gnome;
                ctx.Entities[2] = hat;

                var sim = new DigglesGameSim(ctx);
                // No revealer yet → the gnome's tile is black → the carrier hides, and its
                // sweep takes the hat's renderer down with it; the hat itself stays un-flagged.
                for (int i = 0; i < 4; i++) sim.Step(0.1);
                Assert.That(gnomeMr.enabled, Is.False, "carrier hidden in black fog");
                Assert.That(hatMr.enabled, Is.False, "rider hidden WITH the carrier");
                Assert.That(hat.FowHidden, Is.False, "rider itself never fog-flagged");

                // Reveal around the gnome → the carrier shows again and the sweep re-enables the
                // hat. A stationary object starts revealing when its mod-6 round-robin slot comes
                // up (faithful ≤6-update latency, 0057f5c0:148-153) — give it 8 fog updates.
                interp.Eval("set_fogofwar 1 14 8");
                for (int i = 0; i < 4 * 8; i++) sim.Step(0.1);
                Assert.That(gnomeMr.enabled, Is.True, "carrier visible after reveal");
                Assert.That(hatMr.enabled, Is.True, "rider re-shown with the carrier");
            }
            finally
            {
                Object.DestroyImmediate(root.gameObject);
            }
        }

        [Test]
        public void RemoveBlackFog_And_FowBegin_Commands()
        {
            var interp = new TclInterpreter();
            var ctx = DigglesTclEngine.Register(interp);
            ctx.FogOfWar = new FogOfWarState(64, 64, (x, y) => 0);

            interp.Eval("remove_black_fog 0 10 10 20 20");
            Assert.That(ctx.FogOfWar.FogAt(15, 15, 0), Is.EqualTo(1), "story reveal = gray");

            interp.Eval("set_fow_begin 30 0");
            Assert.That(ctx.FogOfWar.FogAt(10, 6, 0), Is.EqualTo(0xFF), "sky band lit");
            Assert.That(ctx.FogOfWar.FowBegin(0), Is.EqualTo(30));

            // Native handler oddity kept faithful: player must be < 7 (0064b380:118).
            // The virgin FowBegin value is -1 (native ctor law) — a rejected write must leave it
            // untouched at -1, not an earlier revision default 0.
            interp.Eval("set_fow_begin 40 7");
            Assert.That(ctx.FogOfWar.FowBegin(7), Is.EqualTo(-1), "player 7 rejected");

            interp.Eval("show_fogofwar 0");
            Assert.That(ctx.FogOfWar.ShowFog, Is.False);
            interp.Eval("show_fogofwar 1");
            Assert.That(ctx.FogOfWar.ShowFog, Is.True);
        }
    }
}
