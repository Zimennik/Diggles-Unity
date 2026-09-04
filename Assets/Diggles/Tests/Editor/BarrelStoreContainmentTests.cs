#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    // Container/store containment laws: crates must not hover above or beside their treasure
    // barrels — they belong inside them.
    //
    // The RE'd original laws (all cited):
    // • BARREL SUCK-IN — takeitems.tcl:12-38 (called from Schatztonne/Schatzkiste obj_init,
    // misc_items.tcl:197/208): 0.2 s after the barrel spawns, `init_take` obj_queries
    // `-type transport` / `-type {material tool}` / `-flagpos boxed` / `-class Wuker` within
    // a ±0.32 x/y, ±0.64 z bounding box of the barrel and `inv_add this $item` — NO re-show,
    // NO set_pos: a swallowed item leaves the world grid and renders hidden (native InvAdd
    // FUN_0069bd30:132 clears the +0x3d0 bit-0 visible flag + pulls the object out of the
    // spatial grid). The growth templates author the content AT the barrel spot — e.g.
    // urw_gng_004_f.tcl:18-29: `new Schatztonne … 11.1737 2.5 12`, then `new Leiter …
    // 11.1544 2.5 12.01` + `set_boxed 1` + `set_physic true` — Δpos ≈ 0.02, well inside the
    // capture box. Anything that MOVES the crate ≥ 0.32 before the +0.2 s timer breaks the
    // swallow and leaves a visible crate floating at the barrel.
    // • set_boxed does NOT reposition — native FUN_0067dc80 flips flags (0x4000 | ~0x400000)
    // + the content icon (FUN_005dc6a0/FUN_005dc780) and merely KICKS THE POINT-PHYSICS JOB
    // (:128-129 → FUN_005ebc90 physics-mgr singleton + FUN_005eb470 enqueue). That physics
    // is gated on flag 0x10 (set_physic) AND not-contained (bit 2) AND no carrier
    // (+0x238==0, +0x278==-1) — and it is pure GRAVITY: the item falls straight down its own
    // column; it never climbs out of rock and never snaps laterally. The port's old
    // SettleDropPosition call here (climb-up-to-16-subrows + spiral-snap-to-walkable) lifted
    // template crates half a tile out of the authored ground line ⇒ capture missed ⇒ the
    // reported floating crates.
    // • SHELF POSE — lager.tcl store_item:724-744: `inv_add this $item; set_visibility $item 1;
    // set_pos $item <shelf slot>` (boxes included — store_boxes, is_slot_full's get_boxed
    // branch:529). The shelf set_pos must stick EXACTLY: the native physics job is gated on
    // the contained bit (FUN_005eb470), so a stored item is never re-settled.
    // • RESETTLE-ON-DIG — a contained/packed item is OUT of the world spatial grid (InvAdd);
    // terrain changes must not re-float it (same FUN_005eb470 bit-2 gate). Only free-world
    // loose items fall when the ground under them is carved away.
    public sealed class BarrelStoreContainmentTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

        readonly List<GameObject> _spawnedGos = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _spawnedGos) if (go != null) Object.DestroyImmediate(go);
            _spawnedGos.Clear();
        }

        static BdbModel? s_bdb;
        static BdbModel Bdb() => s_bdb ??= BdbParser.Parse(DataRoot + "/BlockDB2.BDB");

        // Flat ant-farm slab: tile rows 0..7 air (block 59), rows 8+ solid (block 0) — the
        // ground line is the tile-8 top (native y = 8.0). Content authored at y = 8.5 sits
        // 4 sub-rows INTO the relief, like the meadow's authored 30.5 half-row positions.
        DiggableTerrain MakeSlabTerrain(int w = 64, int h = 64, int groundRow = 8)
        {
            var go = new GameObject("DiggableTerrain");
            _spawnedGos.Add(go);
            var t = go.AddComponent<DiggableTerrain>();
            var block = new uint[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    block[y * w + x] = y >= groundRow ? 0u : 59u;
            t.Init(new BdbPmpWorldBuilder.TclWorldResult
            {
                Width = w, Height = h, BlockIdx = block, Terrain = new byte[w * h],
                Voxels = new byte[w * h * PmpMap.SubGridLength],   // sized buffer ⇒ Init builds NavWorld
                HasTpl = new System.Collections.BitArray(w * h, true),   // every tile exposed (EFFPASSWALK precondition)
            }, Bdb(), "", () => { });
            Assert.IsNotNull(t.Nav, "synthetic terrain must build a NavWorld");
            // Derive Play-like walk bits for the synthetic slab (EFFPASSWALK over the whole map —
            // the same FUN_005a6b40 pass every carve/stamp runs): the slab top becomes a real
            // walkable floor line, so the settle laws see the world the way live Play does.
            t.Nav!.RevalidateRect(0, 0, w * 8 - 1, h * 8 - 1);
            return t;
        }

        static (TclInterpreter interp, DigglesTclContext ctx, DigglesGameSim sim) Host()
        {
            var interp = new TclInterpreter { TolerateExprErrors = true, TolerateMissingVars = true };
            interp.UnknownCommandHandler = (i, a) => "0";
            var ctx = DigglesTclEngine.Register(interp);
            ctx.DataRoot = DataRoot;
            ctx.TolerateMissingEntities = true;
            ctx.TolerateMissingClass = true;
            ctx.TolerateMissingFiles = true;
            var sim = new DigglesGameSim(ctx);
            return (interp, ctx, sim);
        }

        static DigglesTclEntity Spawn(TclInterpreter interp, DigglesTclContext ctx, string cls, string pos)
        {
            string h = interp.Eval($"new {cls} \"\" \"{pos}\" \"0 0 0\"");
            var e = ctx.GetEntity(h);
            Assert.IsNotNull(e, $"spawn of {cls} failed");
            return e!;
        }

        // ---- 1. the template flow end-to-end: barrel swallows its authored boxed crate --------
        //
        // Reproduces urw_gng_004_f.tcl:18-29 verbatim (real Schatztonne class + real
        // takeitems.tcl timer/handler; Leiter/Grillpilz as minimal class defs — their def_class
        // kinds matter: elevator is only caught by the `-flagpos boxed` query, material by
        // `-type {material tool}`). Before the fix, the set_boxed-time SettleDropPosition lifted
        // the crate from the authored half-row (8.5) to the nav ground line (8.0) — dy 0.5 >
        // the 0.32 capture box — so the barrel never swallowed it and it floated forever.
        [Test]
        public void Template_BoxedCrateAtBarrel_IsSwallowedAndHidden()
        {
            var (interp, ctx, sim) = Host();
            ctx.Terrain = MakeSlabTerrain();

            interp.Eval("call scripts/classes/items/misc_items.tcl");   // real Schatztonne + takeitems
            interp.Eval("def_class Leiter metal elevator 2 {} {}");     // transport.tcl:277 header
            interp.Eval("def_class Grillpilz none material 0 {} {}");   // rohstoffe food drop

            // the template stanza: barrel first, content at the SAME spot (Δ ≈ 0.02..0.08)
            var tonne = Spawn(interp, ctx, "Schatztonne", "10.05 8.5 8");
            var crate = Spawn(interp, ctx, "Leiter", "10.0 8.5 8.01");
            interp.Eval($"set_boxed {crate.Handle} 1");
            interp.Eval($"set_owner {crate.Handle} -1");
            interp.Eval($"set_physic {crate.Handle} true");
            interp.Eval($"set_boxed {crate.Handle} true");
            var pilz = Spawn(interp, ctx, "Grillpilz", "10.02 8.5 8");
            interp.Eval($"set_physic {pilz.Handle} true");

            // the crate must still be inside the barrel's ±0.32/±0.64 capture box when the
            // +0.2 s evt_takeitems fires — the boxing settle may not teleport it
            Assert.Less(Mathf.Abs(crate.Position.x - 10.0f), 0.05f, "boxing must not move the crate in x");
            Assert.Less(Mathf.Abs(crate.Position.y - 8.5f), 0.05f,
                "boxing must not lift the crate off the authored half-row (native set_boxed never repositions; " +
                "its physics kick is gravity-only — FUN_0067dc80:128-129, FUN_005eb470)");

            for (int i = 0; i < 6; i++) sim.Step(0.1);   // past the takeitems timer (gettime+0.2)

            Assert.AreEqual(tonne.Handle, crate.ContainedIn,
                "the boxed crate authored at the barrel must be swallowed (takeitems.tcl:17 -flagpos boxed)");
            Assert.AreEqual(tonne.Handle, pilz.ContainedIn,
                "the material item at the barrel must be swallowed (takeitems.tcl:16 -type {material tool})");
            Assert.AreEqual("0", interp.Eval($"get_visibility {crate.Handle}").Trim(),
                "a swallowed item renders hidden (native InvAdd FUN_0069bd30:132 clears the visible bit)");
        }

        // ---- 2. the Lager shelf pose: set_pos on a CONTAINED boxed item is exact ---------------
        //
        // lager.tcl store_item:734-744: inv_add → set_visibility 1 → set_pos <shelf slot>.
        // Shelf slots sit ABOVE the ground line (sloty −0.4..−2.6); the native physics job is
        // gated on the contained bit (FUN_005eb470 checks +0x3d0 bit 2 clear), so the pose must
        // stick exactly. The old port re-settled every boxed set_pos and slammed stored crates
        // out of the shelf.
        [Test]
        public void ContainedBoxedItem_ShelfSetPos_IsExact()
        {
            var (interp, ctx, _) = Host();
            ctx.Terrain = MakeSlabTerrain();
            interp.Eval("def_class Lager wood production 0 {} {}");
            interp.Eval("def_class Leiter metal elevator 2 {} {}");

            var lager = Spawn(interp, ctx, "Lager", "20 8.0 8");
            var box = Spawn(interp, ctx, "Leiter", "21 8.0 8");
            interp.Eval($"set_physic {box.Handle} 1");
            interp.Eval($"set_boxed {box.Handle} 1");

            // the store_item law
            interp.Eval($"inv_add {lager.Handle} {box.Handle}");
            interp.Eval($"set_visibility {box.Handle} 1");
            interp.Eval($"set_pos {box.Handle} \"18.2 7.6 8\"");   // shelf row, 0.4 above ground
            interp.Eval($"set_physic {box.Handle} 0");
            interp.Eval($"set_instore {box.Handle} 1");

            Assert.AreEqual(18.2f, box.Position.x, 1e-3f);
            Assert.AreEqual(7.6f, box.Position.y, 1e-3f,
                "the shelf set_pos of a contained item must stick (FUN_005eb470 bit-2 gate — no physics while packed)");
            Assert.AreEqual(8f, box.Position.z, 1e-3f);
        }

        // ---- 3. resettle-on-dig skips contained items ------------------------------------------
        //
        // A packed item is OUT of the world spatial grid (native InvAdd → FUN_005ebc90 grid ops;
        // the physics enqueue FUN_005eb470 refuses contained objects). A carve under the barrel
        // must not re-float its contents — and must still drop genuinely loose items.
        [Test]
        public void ResettleLooseItems_SkipsContained_DropsLoose()
        {
            var (interp, ctx, _) = Host();
            var terrain = MakeSlabTerrain();
            ctx.Terrain = terrain;
            interp.Eval("def_class Grillpilz none material 0 {} {}");
            interp.Eval("def_class Schatztonne none material 0 {} {}");

            var tonne = Spawn(interp, ctx, "Schatztonne", "10 8.0 8");
            var packed = Spawn(interp, ctx, "Grillpilz", "10 7.0 8");   // hovers — would settle if loose
            var loose = Spawn(interp, ctx, "Grillpilz", "12 7.0 8");
            interp.Eval($"inv_add {tonne.Handle} {packed.Handle}");

            // ResettleLooseItems only touches entities with GameObjects
            foreach (var e in new[] { packed, loose })
            {
                var go = new GameObject($"t_{e.Handle}");
                _spawnedGos.Add(go);
                e.GameObject = go;
            }

            var before = packed.Position;
            int moved = Diggles.Runtime.RuntimeWorldSpawner.ResettleLooseItems(
                terrain.Nav!, ctx.Entities.Values, 8, 5, 14, 10);

            Assert.AreEqual(before, packed.Position,
                "a CONTAINED item must never be re-settled by terrain changes (packed items are out of the world grid)");
            Assert.AreEqual(8.0f, loose.Position.y, 1e-3f, "a loose hovering item still falls to the support row");
            Assert.AreEqual(1, moved, "exactly the loose item moved");
        }

        // ---- 4. regression guard: a FREE boxed crate positioned mid-air still falls ------------
        //
        // The intro chain (urw_3.tcl summon → packtobox) and Trigger_Tutorial.tcl:340 rely on the
        // physics drop: a physic-enabled, un-contained crate boxed/positioned in the air must
        // settle straight DOWN its own column to the support row — x/z untouched (gravity has no
        // lateral component; the old spiral-snap did).
        [Test]
        public void FreeBoxedCrate_MidAir_FallsStraightDown()
        {
            var (interp, ctx, _) = Host();
            ctx.Terrain = MakeSlabTerrain();
            interp.Eval("def_class Feuerstelle wood production 0 {} {}");

            var crate = Spawn(interp, ctx, "Feuerstelle", "12 5.0 8");
            interp.Eval($"set_physic {crate.Handle} 1");     // genericprod.tcl:446 object-init
            interp.Eval($"set_boxed {crate.Handle} 1");      // packtobox (genericprod.tcl:191)

            Assert.AreEqual(12f, crate.Position.x, 1e-3f, "gravity never moves x");
            Assert.AreEqual(8.0f, crate.Position.y, 1e-3f, "the crate falls to the ground line (tile-8 top)");
            Assert.AreEqual(8f, crate.Position.z, 1e-3f, "gravity never moves the slab depth");

            // …and set_pos into the air re-settles it the same way (Trigger_Tutorial.tcl:340 law)
            interp.Eval($"set_pos {crate.Handle} \"14 4.0 8\"");
            Assert.AreEqual(14f, crate.Position.x, 1e-3f);
            Assert.AreEqual(8.0f, crate.Position.y, 1e-3f, "a boxed physic-enabled crate set_pos'd mid-air falls to support");
        }
    }
}
