#nullable enable

namespace Diggles.Tests
{
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;

    // Pins the Tcl half of the horizon-zone chain that feeds the rear-skirt fade
    // (Docs/BACKDROP_PARALLAX_RE.md §3-4):
    //
    // template `new Info_Fog_*` → obj_init (info_obj.tcl else-branch)
    // → template `call_method $h Editor_Set_Info {...}` (every placement emits it —
    // obw_szene.tcl:1655, unq_menue.tcl:450)
    // → Editor_Set_Info body `call_method this init` (Scripts/misc/info_obj.tcl:27)
    // → the class's `method init` override: `horizon_set [get_pos this] r g b 32 size`
    // then self-delete (infoobjs.tcl:769-775 for Urw_d)
    // → ctx.HorizonRegions record → BackdropLaws.PickZone → _HorizonFog shader globals.
    //
    // The port never calls `method init` from CmdNew (only the obj_init chain), so this
    // Editor_Set_Info chain is the only route to horizon_set on a fresh world: a silent break
    // anywhere in it (method table, nested call_method `this`, stub shadowing of horizon_set)
    // leaves ctx.HorizonRegions empty and the earth rim raw. These tests drive the real data
    // files (classinit slice plus the real class body), headless.
    public sealed class BackdropHorizonChainTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

        static (TclInterpreter interp, DigglesTclContext ctx) Boot()
        {
            var interp = new TclInterpreter();
            interp.TolerateExprErrors = true;
            interp.TolerateMissingVars = true;
            var ctx = DigglesTclEngine.Register(interp);
            ctx.DataRoot = DataRoot;
            ctx.TolerateMissingEntities = true;
            ctx.TolerateMissingClass = true;
            interp.RegisterCommand("puts", (i, a) => "");
            interp.UnknownCommandHandler = (i, a) => "0";
            // The real class file (defines every Info_Fog_* incl. the class-def-time
            // `call scripts/misc/info_obj.tcl` that provides Editor_Set_Info).
            interp.Eval("call scripts/classes/items/infoobjs.tcl");
            return (interp, ctx);
        }

        [Test]
        public void InfoFog_EditorSetInfo_RegistersZone_AndSelfDeletes()
        {
            var (interp, ctx) = Boot();
            int before = ctx.HorizonRegions.Count;

            // The exact menu-world placement: unq_menue.tcl:445+450 at MapTemplateSet 25 28
            // → world pos (32.98, 39.87, 0.02).
            string handle = interp.Eval(
                "new Info_Fog_Urw_d \"\" \"32.98 39.87 0.02\" \"0 0 0\"").Trim();
            Assert.IsNotEmpty(handle, "new Info_Fog_Urw_d must return a handle");
            interp.Eval($"call_method {handle} Editor_Set_Info {{{{blah fahsel}}}}");

            Assert.AreEqual(before + 1, ctx.HorizonRegions.Count,
                "Editor_Set_Info → init → horizon_set must append exactly one zone record");
            var z = ctx.HorizonRegions[ctx.HorizonRegions.Count - 1];
            // infoobjs.tcl:773 — horizon_set [get_pos this] 0.305 0.375 0.429 32 15
            Assert.AreEqual(78, z.Red);     // NativeRound(0.305*255 = 77.775)
            Assert.AreEqual(96, z.Green);   // NativeRound(0.375*255 = 95.625)
            Assert.AreEqual(109, z.Blue);   // NativeRound(0.429*255 = 109.395)
            Assert.AreEqual(32f, z.Depth, 1e-4f);
            Assert.AreEqual(15f, z.Size, 1e-4f);
            Assert.AreEqual(32.98f, z.Position.x, 1e-3f);
            Assert.AreEqual(39.87f, z.Position.y, 1e-3f);

            // `if {![get_mapedit]} {del this}` — the marker object self-deletes; the zone
            // outlives it in the registry (the native CGame+0x2868 semantics).
            Assert.IsNull(ctx.GetEntity(handle),
                "Info_Fog marker must self-delete after registering its zone");
        }

        [Test]
        public void MenuWorldZone_IsPicked_AtTheMenuCameraFocus()
        {
            var (interp, ctx) = Boot();
            string handle = interp.Eval(
                "new Info_Fog_Urw_d \"\" \"32.98 39.87 0.02\" \"0 0 0\"").Trim();
            interp.Eval($"call_method {handle} Editor_Set_Info {{{{blah fahsel}}}}");

            // MenuBootTcl camera: set_view 32.368 40.858 … (DigglesWorldRuntime) — distance
            // ≈ 1.16 < size 15, so the menu diorama has an active zone. Natively FUN_004750b0
            // runs unconditionally (004781b0_CDisplay_Draw.c:210).
            int pick = BackdropLaws.PickZone(ctx.HorizonRegions, 32.368f, 40.858f);
            Assert.AreEqual(ctx.HorizonRegions.Count - 1, pick,
                "the unq_menue Info_Fog_Urw_d zone must be in pick range of the menu camera focus");
        }

        [Test]
        public void HorizonSet_IsNotStubShadowed()
        {
            var (interp, ctx) = Boot();
            int before = ctx.HorizonRegions.Count;
            // horizon_set/rem/clear sit in the bulk-stub name list; RegisterStubs must skip
            // them because the real commands registered first (stub-shadowing guard).
            interp.Eval("horizon_set {10 20 0} 0.5 0.25 1.0 32 40");
            Assert.AreEqual(before + 1, ctx.HorizonRegions.Count, "horizon_set must be the REAL command");
            interp.Eval("horizon_clear");
            Assert.AreEqual(0, ctx.HorizonRegions.Count, "horizon_clear must be the REAL command");
        }
    }
}
