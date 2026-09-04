#nullable enable

namespace Diggles.Tests
{
    using Diggles.Runtime;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class InputInteractionTests
    {
        static (TclInterpreter interp, DigglesTclContext ctx) Host()
        {
            var interp = new TclInterpreter { TolerateExprErrors = true, TolerateMissingVars = true };
            interp.UnknownCommandHandler = (i, a) => "0";
            var ctx = DigglesTclEngine.Register(interp);
            ctx.TolerateMissingEntities = true;
            ctx.TolerateMissingClass = true;
            ctx.TolerateMissingFiles = true;
            ctx.LocalPlayer = 0;
            interp.Eval("def_class G none gnome 0 {} {}");
            interp.Eval("def_class B none production 0 {} {}");
            return (interp, ctx);
        }

        static DigglesTclEntity Spawn(TclInterpreter interp, DigglesTclContext ctx, string className)
        {
            var entity = ctx.GetEntity(
                interp.Eval($"new {className} \"\" \"0 0 0\" \"0 0 0\"").Trim())!;
            entity.Owner = ctx.LocalPlayer;
            return entity;
        }

        [Test]
        public void SelectionVersionBump_ReplacesAuraTargetSet()
        {
            var (interp, ctx) = Host();
            var gnome = Spawn(interp, ctx, "G");
            var crate = Spawn(interp, ctx, "B");
            ctx.Selection.ClickSelect(gnome.Handle, additive: false);
            int observedVersion = ctx.Selection.Version;
            int previousPrimary = ctx.Selection.Primary;

            Assert.IsFalse(SelectionAuraService.SelectionNeedsRefresh(
                observedVersion, ctx.Selection.Version));

            ctx.Selection.ClickSelect(crate.Handle, additive: false);

            Assert.IsTrue(SelectionAuraService.SelectionNeedsRefresh(
                observedVersion, ctx.Selection.Version));
            Assert.AreNotEqual(previousPrimary, ctx.Selection.Primary);
            Assert.AreEqual(crate.Handle, ctx.Selection.Primary);
            Assert.AreEqual(1, ctx.Selection.Count);
        }

        // The gray-fog render law, pinned through the actual verdict producer: DigglesGameSim's
        // fog pass samples the entity's fog byte (GetMaxFow) and computes
        // hidden = viewInFog ? fow < 1: fow < 0x80
        // (native render-job gate FUN_004845a0 — no render job unless GetMaxFow(local) ≥ 0x80;
        // viewinfog objects stand in for the CDummyObjMgr last-seen ghosts, drawn in explored
        // gray ≥ 1), then ApplyFogVisible writes entity.FowHidden — the exact field the cursor
        // gate consumes. The test crafts the player-0 fog lane, gives the entity a bare
        // GameObject (the pass skips GO-less entities) and steps the actual sim to the fog pass
        // (every 4th 10 Hz logic tick), so the whole chain
        // fog byte → GetMaxFow → law → FowHidden → RenderVisibilityBlocksCursor is exercised.
        [TestCase(0x00, false, true, TestName = "FogVerdict_Black_Normal_IsHidden")]
        [TestCase(0x01, false, true, TestName = "FogVerdict_Gray_Normal_IsHidden")]
        [TestCase(0x7F, false, true, TestName = "FogVerdict_JustBelowLit_Normal_IsHidden")]
        [TestCase(0x80, false, false, TestName = "FogVerdict_Lit_Normal_IsVisible")]
        [TestCase(0x00, true, true, TestName = "FogVerdict_Black_ViewInFog_IsHidden")]
        [TestCase(0x01, true, false, TestName = "FogVerdict_Gray_ViewInFog_IsVisible")]
        [TestCase(0x80, true, false, TestName = "FogVerdict_Lit_ViewInFog_IsVisible")]
        public void FogVerdict_RealSimPass_PinsGrayFogRenderLaw(
            int fogByte, bool viewInFog, bool hidden)
        {
            var (interp, ctx) = Host();
            var fog = new Diggles.Runtime.World.FogOfWarState(64, 64, (x, y) => 0);
            ctx.FogOfWar = fog;
            var sim = new Diggles.Runtime.Sim.DigglesGameSim(ctx);
            var entity = Spawn(interp, ctx, "B");
            if (viewInFog) entity.VarsScope.Vars["_viewinfog"] = "1";

            var go = new GameObject("fog-verdict-probe");
            try
            {
                entity.GameObject = go;
                // Craft the local player's whole fog lane to the case byte (the sampler takes
                // the max over the entity's tile rect, so the lane must be uniform). Fade lists
                // stay empty, so BeginUpdate's FadeBlocks leaves the crafted bytes untouched.
                var state = fog.CaptureSaveState();
                for (int i = 0; i < state.Fog.Length; i += Diggles.Runtime.World.FogOfWarState.Players)
                    state.Fog[i] = (byte)fogByte;
                fog.RestoreSaveState(state);

                // The fog pass runs on every 4th 10 Hz logic tick — step through one full window.
                for (int i = 0; i < 4; i++) sim.Step(0.1);

                Assert.AreEqual(hidden, entity.FowHidden,
                    $"sim fog verdict for fog=0x{fogByte:X2}, viewInFog={viewInFog}");
                Assert.AreEqual(hidden, ClickToMoveService.RenderVisibilityBlocksCursor(entity),
                    "the cursor gate must consume the sim's verdict field");
            }
            finally
            {
                entity.GameObject = null;
                Object.DestroyImmediate(go);
            }
        }

        // The tests assembly does not reference VContainer — invoke the actual explicit
        // ITickable.Tick by reflection (the BottomMenuLiveMutationTests pattern).
        static void TickService(SelectionAuraService service)
            => service.GetType().GetMethod(
                    "VContainer.Unity.ITickable.Tick",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(service, null);

        // Pending-resync law: presentation spawns a frame after the sim — selecting an entity
        // right after an F5 restore could hit the version gate while GameObject was still null,
        // leaving the aura absent until the next selection mutation. The service must keep
        // retrying (allocation-free) while a live selected entity lacks its GameObject, and
        // settle once presentation catches up — with no new selection mutation. Runs the actual
        // ITickable.Tick; the AuraBandRenderer slots are inert outside Play mode.
        [Test]
        public void SelectionAura_PrimaryWithoutGameObject_RetriesUntilPresentationSpawns()
        {
            var (interp, ctx) = Host();
            var service = new SelectionAuraService(ctx);
            var gnome = Spawn(interp, ctx, "G");
            ctx.Selection.ClickSelect(gnome.Handle, additive: false);

            TickService(service);
            Assert.IsTrue(service.SelectionResyncPending,
                "a live selected entity without presentation must arm the retry");
            TickService(service);
            Assert.IsTrue(service.SelectionResyncPending,
                "the retry must persist across ticks while the GameObject is still missing");

            var go = new GameObject("aura-resync-probe");
            try
            {
                gnome.GameObject = go;
                TickService(service);
                Assert.IsFalse(service.SelectionResyncPending,
                    "the late-spawned GameObject must be picked up without a selection mutation");
            }
            finally
            {
                gnome.GameObject = null;
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void SelectionAura_DeadHandle_DoesNotArmRetry()
        {
            var (interp, ctx) = Host();
            var service = new SelectionAuraService(ctx);
            var gnome = Spawn(interp, ctx, "G");
            ctx.Selection.ClickSelect(gnome.Handle, additive: false);
            ctx.Entities.Remove(gnome.Handle);

            TickService(service);

            Assert.IsFalse(service.SelectionResyncPending,
                "a handle with no live entity can never gain a GameObject — no retry spin");
        }

        [TestCase("none", 0, false, 0, 0, 0)]
        [TestCase("select", 1, true, 255, 255, 255)]
        [TestCase("standard", 1, true, 255, 255, 255)]
        [TestCase("click", 2, true, 255, 255, 255)]
        [TestCase("use", 3, true, 0, 255, 0)]
        [TestCase("walk", 4, false, 0, 0, 0)]
        [TestCase("nowalk", 5, false, 0, 0, 0)]
        [TestCase("dig", 6, true, 0, 255, 0)]
        [TestCase("pickup", 7, true, 0, 0, 255)]
        [TestCase("putdown", 8, true, 0, 0, 255)]
        [TestCase("attack", 9, true, 255, 0, 0)]
        [TestCase("pack", 10, true, 0, 0, 255)]
        [TestCase("unpack", 11, true, 0, 0, 255)]
        [TestCase("assign", 12, true, 0, 255, 0)]
        [TestCase("wait", 13, false, 0, 0, 0)]
        [TestCase("zoom", 14, false, 0, 0, 0)]
        [TestCase("pan", 15, false, 0, 0, 0)]
        [TestCase("", 1, true, 255, 255, 255)]
        [TestCase(null, 1, true, 255, 255, 255)]
        [TestCase("unknown", 1, true, 255, 255, 255)]
        public void CursorVerbPalette_MatchesNativeTable(string? verb, int expectedCursorId,
            bool hasColor, int red, int green, int blue)
        {
            var cursorId = SelectionAuraService.CursorIdForVerb(verb);
            Assert.AreEqual(expectedCursorId, (int)cursorId);
            AssertColor(SelectionAuraService.HoverColorForCursorId(cursorId), hasColor, red, green, blue);
        }

        [Test]
        public void CursorPalette_ReservedId16_HasNoAura()
        {
            AssertColor(SelectionAuraService.HoverColorForCursorId(
                SelectionAuraService.HoverCursorId.Reserved16), false, 0, 0, 0);
        }

        [TestCase(false, false, "")]
        [TestCase(true, false, "-alternate")]
        [TestCase(false, true, "-multiselect")]
        [TestCase(true, true, "-multiselect -alternate")]
        public void HoverTxtPar_CarriesNativeModifierOrder(
            bool alternate, bool multiselect, string expected)
        {
            Assert.AreEqual(expected, SelectionAuraService.HoverTxtPar(alternate, multiselect));
        }

        [Test]
        public void HoverRefreshKey_TracksShiftAndSelectionVersion()
        {
            Assert.IsTrue(SelectionAuraService.HoverNeedsRefresh(
                42, false, false, 7, 42, false, true, 7));
            Assert.IsTrue(SelectionAuraService.HoverNeedsRefresh(
                42, false, false, 7, 42, false, false, 8));
            Assert.IsFalse(SelectionAuraService.HoverNeedsRefresh(
                42, false, true, 8, 42, false, true, 8));
        }

        [TestCase("select", false, true, false, false, 255, 255, 255,
            TestName = "ModifierColor_PlainOwnObject_IsWhite")]
        [TestCase("use", false, true, false, true, 0, 255, 0,
            TestName = "ModifierColor_AltUse_IsGreen")]
        [TestCase("assign", false, true, false, true, 0, 255, 0,
            TestName = "ModifierColor_AltAssign_IsGreen")]
        [TestCase("select", false, true, true, false, 255, 255, 255,
            TestName = "ModifierColor_NativeShiftSelect_IsWhite")]
        [TestCase("select", true, true, true, false, 0, 0, 255,
            TestName = "ModifierColor_ShiftCratePickupOverride_IsBlue")]
        [TestCase("pickup", true, true, false, true, 0, 0, 255,
            TestName = "ModifierColor_AltGroundCratePickup_IsBlue")]
        public void ModifierActionDecision_IsPure(string eventVerb, bool ownBoxedCrate,
            bool gnomeSelected, bool multiselect, bool alternate, int red, int green, int blue)
        {
            AssertColor(SelectionAuraService.HoverColorForDecision(
                eventVerb, ownBoxedCrate, gnomeSelected, multiselect, alternate),
                true, red, green, blue);
        }

        static void AssertColor(Color32? actual, bool hasColor, int red, int green, int blue)
        {
            Assert.AreEqual(hasColor, actual.HasValue);
            if (!hasColor) return;
            Color32 color = actual.GetValueOrDefault();
            Assert.AreEqual(red, (int)color.r);
            Assert.AreEqual(green, (int)color.g);
            Assert.AreEqual(blue, (int)color.b);
            Assert.AreEqual(255, (int)color.a);
        }
    }
}
