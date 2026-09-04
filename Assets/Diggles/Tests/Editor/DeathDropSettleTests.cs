#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    // Death-scatter drop laws.
    //
    // The RE'd original chain (all cited):
    // • DEATH SCATTER — inv_rem_all (z_procs.tcl:1839-1864), called from destroy (:1883):
    // container contents go through `set_posbottom … [vector_fix …]` (:1847 — covered by the
    // b609c94 putdown seat law), but the MAIN branch drops every inventory item with
    // `set_physic $item 1; set_pos $item [vector_add [get_pos this] {0 -1 0}]` (:1859-1860)
    // — one tile ABOVE the corpse (native y−1 = up), RAW, no vector_fix. `from_wall` (:424-438)
    // only adjusts depth when the gnome died ON A WALL (get_gnomeposition gate:426).
    // • The native then relies on the POINT-PHYSICS job: set_pos on a physic-enabled free item
    // kicks FUN_0067dc80:128-129 → FUN_005ebc90/FUN_005eb470 (gate: flag 0x10 set_physic,
    // NOT contained +0x3d0 bit 2, no carrier) — pure gravity down the item's own column.
    // The port has no continuous physics; RuntimeWorldSpawner.SettleFreeItemDrop is the
    // one-shot stand-in: GravitySettle + vector_fix's depth seat law
    // z = min(max(z, hmap(x, y−1) + 1), 14.5) (FUN_00595770:255-267, cap DAT_00737564).
    // • The same raw shape is the production release: release_item (genericprod.tcl:520-537)
    // `set_physic $item 1; … set_pos $item $opos`.
    // • RESETTLE-ON-DIG — a death-dropped TOOL/pannier is the same physics client as a
    // material chunk (the FUN_005eb470 gate is flag-based, not kind-based), so a carve under
    // it must drop it too — but ONLY with the live physic flag set (a posed prod output runs
    // set_physic 0, z_work_prod.tcl:616, and must stick).
    public sealed class DeathDropSettleTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

        // Static: the Spawn helper is static (mirrors the sibling fixtures); NUnit creates one
        // instance per test either way and TearDown drains the list.
        static readonly List<GameObject> _spawnedGos = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _spawnedGos) if (go != null) Object.DestroyImmediate(go);
            _spawnedGos.Clear();
        }

        static BdbModel? s_bdb;
        static BdbModel Bdb() => s_bdb ??= BdbParser.Parse(DataRoot + "/BlockDB2.BDB");

        // Flat ant-farm slab: tile rows 0..7 air (block 59), rows 8+ solid (block 0) — the
        // ground line is the tile-8 top (native y = 8.0). Same fixture as
        // BarrelStoreContainmentTests.
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
                Voxels = new byte[w * h * PmpMap.SubGridLength],
                HasTpl = new System.Collections.BitArray(w * h, true),
            }, Bdb(), "", () => { });
            Assert.IsNotNull(t.Nav, "synthetic terrain must build a NavWorld");
            t.Nav!.RevalidateRect(0, 0, w * 8 - 1, h * 8 - 1);
            return t;
        }

        static (TclInterpreter interp, DigglesTclContext ctx) Host()
        {
            var interp = new TclInterpreter { TolerateExprErrors = true, TolerateMissingVars = true };
            interp.UnknownCommandHandler = (i, a) => "0";
            var ctx = DigglesTclEngine.Register(interp);
            ctx.DataRoot = DataRoot;
            ctx.TolerateMissingEntities = true;
            ctx.TolerateMissingClass = true;
            ctx.TolerateMissingFiles = true;
            return (interp, ctx);
        }

        static DigglesTclEntity Spawn(TclInterpreter interp, DigglesTclContext ctx, string cls, string pos)
        {
            string h = interp.Eval($"new {cls} \"\" \"{pos}\" \"0 0 0\"");
            var e = ctx.GetEntity(h);
            Assert.IsNotNull(e, $"spawn of {cls} failed");
            e!.GameObject = new GameObject($"t_{e.Handle}");
            _spawnedGos.Add(e.GameObject);
            return e;
        }

        // ---- 1. the death drop falls straight down its own column ------------------------------
        //
        // inv_rem_all authors the item ONE TILE ABOVE the corpse ({0 -1 0}, z_procs.tcl:1860).
        // The physics stand-in must bring it down to the support row without any lateral
        // component (point gravity, FUN_005eb470), and the depth must obey the vector_fix seat
        // law at the LANDED row.
        [Test]
        public void SettleFreeItemDrop_MidAir_FallsStraightDown()
        {
            var terrain = MakeSlabTerrain();
            var nav = terrain.Nav!;

            // corpse stood at (12, 8.0, 8); the scatter authors the item at y = 7.0
            var settled = Diggles.Runtime.RuntimeWorldSpawner.SettleFreeItemDrop(
                nav, new Vector3(12f, 7f, 8f));

            Assert.AreEqual(12f, settled.x, 1e-3f, "gravity never moves x");
            Assert.AreEqual(8.0f, settled.y, 1e-3f,
                "the scattered item must fall from the authored corpse+{0 -1 0} spot to the ground line " +
                "(native point-physics job FUN_005eb470 — the port previously left it hanging one tile up)");
            float expectedZ = Mathf.Min(
                Mathf.Max(8f, nav.GetHmapSub(Mathf.RoundToInt(settled.x * 8f),
                                             Mathf.RoundToInt(settled.y * 8f) - 1) + 1f),
                14.5f);
            Assert.AreEqual(expectedZ, settled.z, 1e-3f,
                "depth follows vector_fix's seat law at the landed row (FUN_00595770:255 — hmap(y−1)+1, cap 14.5)");
        }

        // ---- 2. the depth cap (DAT_00737564) ---------------------------------------------------
        [Test]
        public void SettleFreeItemDrop_DepthCapsAt14_5()
        {
            var terrain = MakeSlabTerrain();
            var settled = Diggles.Runtime.RuntimeWorldSpawner.SettleFreeItemDrop(
                terrain.Nav!, new Vector3(12f, 7f, 14.9f));
            Assert.AreEqual(14.5f, settled.z, 1e-3f,
                "the vector_fix depth cap (DAT_00737564 = 14.5) bounds the seat law");
        }

        // ---- 3. a supported drop keeps its exact spot ------------------------------------------
        //
        // GravitySettle's anchor+below double-support check must survive the wrapper: an item
        // already resting on the ground line stays put (the Schatztonne ±0.32 capture-box
        // guarantee, urw_gng_004_f.tcl:18-29 + takeitems.tcl:17).
        [Test]
        public void SettleFreeItemDrop_AlreadySupported_KeepsPosition()
        {
            var terrain = MakeSlabTerrain();
            var nav = terrain.Nav!;
            var authored = new Vector3(10.05f, 8.5f, 8f);   // the template half-row seat
            var settled = Diggles.Runtime.RuntimeWorldSpawner.SettleFreeItemDrop(nav, authored);
            Assert.AreEqual(authored.x, settled.x, 1e-3f);
            Assert.AreEqual(authored.y, settled.y, 1e-3f,
                "a supported item never moves (GravitySettle's i==0 anchor-support early-out)");
            Assert.AreEqual(authored.z, settled.z, 1e-3f,
                "a kept authored seat is script truth BYTE-EXACT — the depth law must not fire " +
                "when nothing fell (the half-row seat's row-above is solid rock, hmap 15 → a " +
                "blind clamp would slam z to 14.5 and break the Schatztonne capture box)");
        }

        // ---- 4. resettle-on-dig now covers death-scattered tools -------------------------------
        //
        // The FUN_005eb470 enqueue gate is FLAG-based (0x10 physic + not contained), not
        // kind-based: a death-dropped Hammer (set_physic 1, z_procs.tcl:1859) falls when the
        // floor under it is carved, exactly like a material chunk. A tool WITHOUT the physic
        // flag (posed prod output — set_physic 0, z_work_prod.tcl:616) must stick.
        [Test]
        public void ResettleLooseItems_DropsPhysicTool_KeepsPosedTool()
        {
            var (interp, ctx) = Host();
            var terrain = MakeSlabTerrain();
            ctx.Terrain = terrain;
            interp.Eval("def_class Hammer none tool 0 {} {}");

            var dropped = Spawn(interp, ctx, "Hammer", "12 7.0 8");   // hovers — death scatter
            var posed = Spawn(interp, ctx, "Hammer", "14 7.0 8");     // hovers — but physic OFF
            interp.Eval($"set_physic {dropped.Handle} 1");

            int moved = Diggles.Runtime.RuntimeWorldSpawner.ResettleLooseItems(
                terrain.Nav!, ctx.Entities.Values, 8, 5, 16, 10);

            Assert.AreEqual(8.0f, dropped.Position.y, 1e-3f,
                "a physic-enabled death-dropped tool falls to the support row (FUN_005eb470 flag gate)");
            Assert.AreEqual(7.0f, posed.Position.y, 1e-3f,
                "a tool with set_physic 0 (posed prod output) is never re-settled");
            Assert.AreEqual(1, moved, "exactly the physic-enabled tool moved");
        }

        // ---- 5. logic-less transports (panniers) are the same physics clients ------------------
        [Test]
        public void ResettleLooseItems_DropsPhysicLogiclessTransport()
        {
            var (interp, ctx) = Host();
            var terrain = MakeSlabTerrain();
            ctx.Terrain = terrain;
            interp.Eval("def_class Tragekorb none transport 2 {} {}");

            var korb = Spawn(interp, ctx, "Tragekorb", "12 7.0 8");
            Assert.IsNull(korb.TransportLogic,
                "a bare transport def (pannier/ring family) must have no transport logic — " +
                "otherwise this test covers the wrong branch");
            interp.Eval($"set_physic {korb.Handle} 1");

            int moved = Diggles.Runtime.RuntimeWorldSpawner.ResettleLooseItems(
                terrain.Nav!, ctx.Entities.Values, 8, 5, 16, 10);

            Assert.AreEqual(8.0f, korb.Position.y, 1e-3f,
                "a physic-enabled logic-less transport falls like any loose item (same b609c94 item scope)");
            Assert.AreEqual(1, moved);
        }
    }
}
