#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using System.Linq;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    // The cave-skin prop swap (CMapBeautifier) — fixes a dug-out cave keeping its tunnel props
    // and never getting its cave interior. Ground truth (decompile):
    // • add_cave_skin_class <set> <detail> <size> (FUN_00617570, CTclClassDefMethods::
    // AddCaveSkinClass): flags the defining class (class+0xcc2) + appends a rule row
    // {set, size, detail, class} — driven by SetCaveSkinClasses (animinit.tcl:382-432) over
    // dummy_auskleidung.tcl's Urw/Met/Kris/Golden_caves_1..4_{a..c}_{a..c} lining classes.
    // • cave_skin add/rem (FUN_0066a9a0): the beautifier cave-rect list (+0x30) — the same list
    // the cave-stamp commit appends to (FUN_00567cb0:204-214 = DiggableTerrain.CaveRecords),
    // with the FUN_0058eb30 overlap-drop on append.
    // • the completion pass (FUN_0058ba00, run from DigApply FUN_006347a0:127-133 after the
    // carve): a containing rect whose owner nibbles are all cleared → remove every live object
    // of a +0xcc2 class inside the rect, spawn the three detail slots (a=2,b=1,c=0) of the
    // matching {zone set, rect size} rules at the rect centre ((x1+x2)/2+1, (y1+y2)/2, z=15)
    // with one shared variant pick (FUN_0058e9c0's &local_34), retire the rect.
    // No real data needed: bare all-solid terrain (bdb=null), synthetic skin classes.
    public sealed class CaveSkinSwapTests
    {
        readonly List<GameObject> _gos = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _gos) if (go != null) Object.DestroyImmediate(go);
            _gos.Clear();
        }

        DiggableTerrain MakeTerrain(int w, int h)
        {
            var go = new GameObject("CaveSkinTerrain");
            _gos.Add(go);
            var t = go.AddComponent<DiggableTerrain>();
            t.Init(new BdbPmpWorldBuilder.TclWorldResult
            {
                Width = w, Height = h,
                BlockIdx = new uint[w * h],   // all block 0 = solid (bdb=null ⇒ height 15)
                Terrain = new byte[w * h],
            }, null!, "", () => { });
            return t;
        }

        (TclInterpreter interp, DigglesTclContext ctx, DiggableTerrain terr) Host()
        {
            var interp = new TclInterpreter { TolerateExprErrors = true, TolerateMissingVars = true };
            interp.UnknownCommandHandler = (i, a) => "0";
            var ctx = DigglesTclEngine.Register(interp);
            ctx.TolerateMissingEntities = true;
            ctx.StoryRng = new System.Random(42);           // deterministic variant pick
            var t = MakeTerrain(32, 32);
            ctx.Terrain = t;
            // The campaign zone bands (CampaignInit.tcl:675-713) — set 0 = Urwald covers all rows
            // while no bound is set (StoryZoneIndex/FUN_005fcdf0).
            foreach (var z in new[] { "Urwald", "Metall", "Kristall", "Lava" })
                interp.Eval($"sm_add_zone {z}");
            return (interp, ctx, t);
        }

        // def_class one synthetic skin class carrying its add_cave_skin_class registration
        // (the SetCaveSkinClasses shape: def_class body runs at class-definition time with CurrentClass set).
        static void DefSkin(TclInterpreter interp, string name, int set, int detail, int size)
            => interp.Eval($"def_class {name} none dummy 0 {{}} {{ add_cave_skin_class {set} {detail} {size} }}");

        // Registers the {set 0, size 1} skin family: 2 variants (a/b) × 3 details (a=2, b=1, c=0).
        static void DefSkinFamily(TclInterpreter interp)
        {
            DefSkin(interp, "Urw_caves_2_a_a", 0, 2, 1);
            DefSkin(interp, "Urw_caves_2_a_b", 0, 1, 1);
            DefSkin(interp, "Urw_caves_2_a_c", 0, 0, 1);
            DefSkin(interp, "Urw_caves_2_b_a", 0, 2, 1);
            DefSkin(interp, "Urw_caves_2_b_b", 0, 1, 1);
            DefSkin(interp, "Urw_caves_2_b_c", 0, 0, 1);
        }

        // ---------------- registration (FUN_00617570) ----------------

        [Test]
        public void AddCaveSkinClass_FlagsClassAndRecordsRule()
        {
            var (interp, ctx, _) = Host();
            DefSkin(interp, "Urw_caves_2_a_a", 0, 2, 1);

            Assert.IsTrue(ctx.Classes.ContainsKey("Urw_caves_2_a_a"), "class not defined");
            Assert.IsTrue(ctx.Classes["Urw_caves_2_a_a"].CaveSkin, "class+0xcc2 not set");
            Assert.AreEqual(1, ctx.CaveSkinRules.Count, "rule row not appended");
            var r = ctx.CaveSkinRules[0];
            Assert.AreEqual(0, r.Set);
            Assert.AreEqual(2, r.Detail);
            Assert.AreEqual(1, r.Size);
            Assert.AreEqual("Urw_caves_2_a_a", r.ClassName);
        }

        // ---------------- rect list (FUN_0066a9a0 / FUN_0058eb30 / FUN_0058ec00) ----------------

        [Test]
        public void CaveSkin_AddRem_And_OverlapDrop()
        {
            var (interp, _, terr) = Host();

            interp.Eval("cave_skin add 1 10 10 14 14");
            Assert.AreEqual(1, terr.CaveRecords.Count);
            Assert.AreEqual(1, terr.CaveRecords[0].TemplateIdx, "size must land in TemplateIdx");

            // FUN_0058eb30: a strictly-overlapping add replaces the old rect (one drop + append).
            interp.Eval("cave_skin add 2 12 12 20 20");
            Assert.AreEqual(1, terr.CaveRecords.Count, "overlapping add must drop the old rect");
            Assert.AreEqual(2, terr.CaveRecords[0].TemplateIdx);

            // Non-overlapping (edge-touching is not overlap — strict <) appends.
            interp.Eval("cave_skin add 0 20 12 24 20");
            Assert.AreEqual(2, terr.CaveRecords.Count, "edge-touching rect must not be dropped");

            // FUN_0058ec00: point removal drops the first containing rect only.
            interp.Eval("cave_skin rem 13 13");
            Assert.AreEqual(1, terr.CaveRecords.Count);
            Assert.AreEqual(0, terr.CaveRecords[0].TemplateIdx, "the wrong rect was removed");
        }

        // ---------------- the completion pass (FUN_0058ba00) ----------------

        [Test]
        public void DigApply_CompletedCaveRect_RemovesOldSkinsAndSpawnsNewOnes()
        {
            var (interp, ctx, terr) = Host();
            DefSkinFamily(interp);
            // A non-skin deco class inside the rect must survive the sweep (only +0xcc2 is removed).
            interp.Eval("def_class Dummy_NotASkin none dummy 0 {} {}");

            // The cave order: rect (10,10)-(14,14), size 1 (the cave_skin-add path; the stamp
            // commit path lands in the same list via DiggableTerrain.AddCaveRecord).
            interp.Eval("cave_skin add 1 10 10 14 14");

            // Pre-existing props inside the rect: one skin-class lining piece (the "beam"/old
            // Auskleidung) + one unflagged deco.
            string beam = interp.Eval("new Urw_caves_2_a_a \"\" \"12 12 15\" \"0 0 0\"").Trim();
            string bystander = interp.Eval("new Dummy_NotASkin \"\" \"11 11 15\" \"0 0 0\"").Trim();

            // Owner-0 dig marks across the rect — the cave is not dug out yet.
            for (int y = 10; y <= 14; y++)
                for (int x = 10; x <= 14; x++)
                    terr.SetDigMark(x, y, 0, 0);

            int entsBefore = ctx.Entities.Count;
            interp.Eval("dig_apply {12 12 0}");
            Assert.IsNotNull(ctx.GetEntity(beam), "swap must NOT run while owner nibbles survive");
            Assert.AreEqual(1, terr.CaveRecords.Count, "rect must survive an unfinished dig");
            Assert.AreEqual(entsBefore, ctx.Entities.Count, "nothing may spawn early");

            // The gnomes finish the cave: every owner-0 nibble in the rect clears (CarveStep's
            // clear-on-done, simulated directly — the law only reads nibbles).
            for (int y = 10; y <= 14; y++)
                for (int x = 10; x <= 14; x++)
                    terr.ClearDigMark(x, y, 0);

            var before = new HashSet<int>(ctx.Entities.Keys);
            interp.Eval("dig_apply {12 12 0}");

            // 1. The old skin-class prop is swapped out; the unflagged deco survives.
            Assert.IsNull(ctx.GetEntity(beam), "the old cave-skin prop must be removed");
            Assert.IsNotNull(ctx.GetEntity(bystander), "non-skin props must survive the sweep");

            // 2. Three fresh skins (details a/b/c of one variant) at the rect centre (13, 12, 15).
            var spawned = ctx.Entities.Where(kv => !before.Contains(kv.Key)).Select(kv => kv.Value).ToList();
            Assert.AreEqual(3, spawned.Count, "one skin per detail slot (a=2, b=1, c=0)");
            var names = spawned.Select(e => e.Class!.Name).OrderBy(n => n).ToList();
            string variant = names[0].Substring(0, "Urw_caves_2_a".Length);   // "Urw_caves_2_a" or..._b
            CollectionAssert.AreEquivalent(
                new[] { variant + "_a", variant + "_b", variant + "_c" }, names,
                "the three slots must share ONE variant family (the shared FUN_0058e9c0 pick)");
            foreach (var e in spawned)
                Assert.AreEqual(new Vector3(13f, 12f, 15f), e.Position,
                    "skins spawn at ((x1+x2)/2+1, (y1+y2)/2, 15)");

            // 3. The rect is retired.
            Assert.AreEqual(0, terr.CaveRecords.Count, "the completed rect must be removed");

            // 4. A further dig is inert (no rect, no double spawn).
            var again = new HashSet<int>(ctx.Entities.Keys);
            interp.Eval("dig_apply {12 12 0}");
            Assert.AreEqual(again.Count, ctx.Entities.Count, "no respawn after the rect retired");
        }

        [Test]
        public void DigApply_OutsideRectOrMissingRules_DoesNothing()
        {
            var (interp, ctx, terr) = Host();
            DefSkinFamily(interp);

            // Rect with no marks at all: complete-on-first-dig only when the dig lands inside it.
            interp.Eval("cave_skin add 1 10 10 14 14");
            int ents = ctx.Entities.Count;
            interp.Eval("dig_apply {20 20 0}");   // outside → untouched
            Assert.AreEqual(1, terr.CaveRecords.Count);
            Assert.AreEqual(ents, ctx.Entities.Count);

            // A size with no registered rules (size 3): the swap still retires the rect and removes
            // flagged props, but spawns nothing (all three FUN_0058e9c0 lookups return −1).
            interp.Eval("cave_skin rem 12 12");
            interp.Eval("cave_skin add 3 10 10 14 14");
            interp.Eval("dig_apply {12 12 0}");
            Assert.AreEqual(0, terr.CaveRecords.Count, "rect must retire even with no matching rules");
            Assert.AreEqual(ents, ctx.Entities.Count, "no skins may spawn without a matching rule");
        }
    }
}
