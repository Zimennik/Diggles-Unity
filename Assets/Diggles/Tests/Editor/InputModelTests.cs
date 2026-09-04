#nullable enable

namespace Diggles.Tests
{
    using Diggles.Runtime;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;
    using UnityEngine;

    // The native input / selection contract vs the original Steam game. Pure-logic seams
    // extracted from the two input services so the decisions are headless-testable:
    // • HoverEligible — hover-aura candidate filtering by selection state;
    // • IsCursorTransparent — the set_hoverable-0 / cloaked skip (deco spawns never highlight);
    // • ResolveRmbClick — the RMB precedence ladder;
    // • FirePickupOrder — a boxed crate clicked with a gnome selected emits evt_task_pickup
    // (plain-click = pick up);
    // • RightPanelModel — the RMB-nothing-selected right-panel toggle seam;
    // • the Shift multi-select laws still compose with the hover gate.
    public sealed class InputModelTests
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
            interp.Eval("def_class Pilz none food 0 {} {}");
            interp.Eval("def_class Mat none material 0 {} {}");
            interp.Eval("def_class Mon none monster 0 {} {}");
            interp.Eval("def_class Beam none item 0 {} {}");   // a deco beautifier prop
            return (interp, ctx);
        }

        static DigglesTclEntity Spawn(TclInterpreter interp, DigglesTclContext ctx, string cls, int owner = 0)
        {
            var e = ctx.GetEntity(interp.Eval($"new {cls} \"\" \"0 0 0\" \"0 0 0\"").Trim())!;
            e.Owner = owner;
            return e;
        }

        // ------------------------------------------------------------------
        // hover-candidate filtering by selection state
        // ------------------------------------------------------------------

        [Test]
        public void HoverEligible_NothingSelected_OwnGnomeAndBuildingOnly()
        {
            var (interp, ctx) = Host();
            var ownGnome = Spawn(interp, ctx, "G", 0);
            var ownBuilding = Spawn(interp, ctx, "B", 0);
            var pilz = Spawn(interp, ctx, "Pilz", 0);
            var material = Spawn(interp, ctx, "Mat", 0);
            var enemyGnome = Spawn(interp, ctx, "G", 1);
            var enemyMonster = Spawn(interp, ctx, "Mon", 1);

            // Nothing selected: only own gnome + own building light up.
            Assert.IsTrue(SelectionAuraService.HoverEligible(ownGnome, 0, hasSelection: false),
                "own gnome hovers with no selection");
            Assert.IsTrue(SelectionAuraService.HoverEligible(ownBuilding, 0, hasSelection: false),
                "own building hovers with no selection");
            Assert.IsFalse(SelectionAuraService.HoverEligible(pilz, 0, hasSelection: false),
                "a mushroom must NOT hover with no selection (no verbs live)");
            Assert.IsFalse(SelectionAuraService.HoverEligible(material, 0, hasSelection: false),
                "a material must NOT hover with no selection");
            Assert.IsFalse(SelectionAuraService.HoverEligible(enemyGnome, 0, hasSelection: false),
                "an enemy gnome must NOT hover (owner gate)");
            Assert.IsFalse(SelectionAuraService.HoverEligible(enemyMonster, 0, hasSelection: false),
                "an enemy monster must NOT hover");
        }

        [Test]
        public void HoverEligible_GnomeSelected_EverythingPickableBecomesCandidate()
        {
            var (interp, ctx) = Host();
            var pilz = Spawn(interp, ctx, "Pilz", 0);
            var boxed = Spawn(interp, ctx, "B", 0);
            var material = Spawn(interp, ctx, "Mat", 0);
            // With a unit selected the verb path is live — the colour law decides afterwards, so
            // the candidate gate opens for any pickable entity.
            Assert.IsTrue(SelectionAuraService.HoverEligible(pilz, 0, hasSelection: true), "pilz verbs live when selected");
            Assert.IsTrue(SelectionAuraService.HoverEligible(boxed, 0, hasSelection: true), "crate verbs live when selected");
            Assert.IsTrue(SelectionAuraService.HoverEligible(material, 0, hasSelection: true));
        }

        [Test]
        public void IsCursorTransparent_DecoSpawnNeverEntersCandidateSet()
        {
            // The deco agent marks MapDecoBeautifier spawns `set_hoverable 0` (→ _hoverable="0")
            // at spawn; the dug-cave vault BEAMS must never highlight or be clicked. This is the
            // pick-path gate that guarantees it — both the click pick and the hover pick skip a
            // cursor-transparent entity and fall through to what's behind (item 1).
            var (interp, ctx) = Host();
            var beam = Spawn(interp, ctx, "Beam", 0);
            Assert.IsFalse(ClickToMoveService.IsCursorTransparent(beam), "a fresh prop is pickable by default");
            beam.VarsScope.Vars["_hoverable"] = "0";   // set_hoverable 0 at deco spawn
            Assert.IsTrue(ClickToMoveService.IsCursorTransparent(beam),
                "a deco beam (set_hoverable 0) never enters the hover/pick candidate set");

            var cloaked = Spawn(interp, ctx, "G", 0);
            cloaked.VarsScope.Vars["_cloaked"] = "1";
            Assert.IsTrue(ClickToMoveService.IsCursorTransparent(cloaked), "a cloaked object is cursor-transparent too");
        }

        // ------------------------------------------------------------------
        // the RMB precedence ladder (table)
        // ------------------------------------------------------------------

        [Test]
        public void ResolveRmbClick_PrecedenceLadder_TopWins()
        {
            // over-UI beats everything.
            Assert.AreEqual(ClickToMoveService.RmbClickAction.OverUI,
                ClickToMoveService.ResolveRmbClick(pointerOverUI: true, placementActive: true,
                    digToolArmed: true, hasSelection: true));
            // placement beats disarm/deselect.
            Assert.AreEqual(ClickToMoveService.RmbClickAction.CancelPlacement,
                ClickToMoveService.ResolveRmbClick(false, placementActive: true, digToolArmed: true, hasSelection: true));
            // dig tool armed beats deselect.
            Assert.AreEqual(ClickToMoveService.RmbClickAction.DisarmDigTool,
                ClickToMoveService.ResolveRmbClick(false, false, digToolArmed: true, hasSelection: true));
            // something selected → deselect.
            Assert.AreEqual(ClickToMoveService.RmbClickAction.Deselect,
                ClickToMoveService.ResolveRmbClick(false, false, false, hasSelection: true));
            // nothing selected → the right-panel toggle request.
            Assert.AreEqual(ClickToMoveService.RmbClickAction.ToggleRightPanel,
                ClickToMoveService.ResolveRmbClick(false, false, false, hasSelection: false));
        }

        // ------------------------------------------------------------------
        // the right-panel toggle seam
        // ------------------------------------------------------------------

        [Test]
        public void RightPanelModel_ToggleSeam_FlipsAndNotifies()
        {
            RightPanelModel.ResetForTests();
            try
            {
                bool? last = null;
                int fires = 0;
                RightPanelModel.ToggleRequested += o => { last = o; fires++; };
                Assert.IsFalse(RightPanelModel.Open);
                RightPanelModel.RequestToggle();
                Assert.IsTrue(RightPanelModel.Open, "first request opens");
                Assert.AreEqual(true, last);
                RightPanelModel.RequestToggle();
                Assert.IsFalse(RightPanelModel.Open, "second request closes");
                Assert.AreEqual(false, last);
                Assert.AreEqual(2, fires, "the finder view is notified on every request");
            }
            finally { RightPanelModel.ResetForTests(); }
        }

        // ------------------------------------------------------------------
        // boxed crate click with a gnome selected → pickup order
        // ------------------------------------------------------------------

        [Test]
        public void FirePickupOrder_BoxedCrate_EmitsEvtTaskPickupOnSelectedGnome()
        {
            var (interp, ctx) = Host();
            var g = Spawn(interp, ctx, "G", 0);
            var crate = Spawn(interp, ctx, "B", 0);
            crate.VarsScope.Vars["_boxed"] = "1";
            ctx.Selection.Include(g.Handle);
            var svc = new ClickToMoveService(ctx);

            int fired = svc.FirePickupOrder(crate.Handle);
            Assert.AreEqual(1, fired, "the pickup order reaches the selected gnome");
            EntityEvent? ev = null;
            foreach (var e in g.PendingEvents) if (e.Name == "evt_task_pickup") ev = e;
            Assert.IsNotNull(ev, "the gnome got evt_task_pickup (z_events.tcl:406)");
            Assert.IsTrue(ev!.Value.Payload != null
                    && ev.Value.Payload.TryGetValue("-subject1", out var subj)
                    && subj == crate.Handle.ToString(),
                "the crate is -subject1 (event_get this -subject1, eventgen.tcl:188)");
        }

        [Test]
        public void FirePickupOrder_NeverCommandsAForeignGnome()
        {
            var (interp, ctx) = Host();
            var foreign = Spawn(interp, ctx, "G", 1);   // not the local player's
            var crate = Spawn(interp, ctx, "B", 0);
            // Force the foreign gnome to be primary via the ungated seam (input paths could never
            // select it, but the guard must still hold).
            ctx.Selection.ReplaceWith(foreign.Handle);
            var svc = new ClickToMoveService(ctx);
            Assert.AreEqual(0, svc.FirePickupOrder(crate.Handle), "a foreign unit is never ordered");
            Assert.AreEqual(0, foreign.PendingEvents.Count);
        }

        // ------------------------------------------------------------------
        // Shift multi-select composes with the hover gate
        // ------------------------------------------------------------------

        // ------------------------------------------------------------------
        // Shift is purely the multiselect key; it must never arm the dig marker/ghost, never
        // start a dig gesture, and while held it must yield the drag box even with a tool armed.
        // ------------------------------------------------------------------

        [Test]
        public void DigMarker_ShiftHeld_NeverShowsAndNeverDigs()
        {
            // Regression: holding Shift (no menu tool armed) lit the dig hover ring.
            Assert.IsFalse(ClickToMoveService.DigMarkerActive(digToolArmed: false, multiselectHeld: true),
                "Shift alone must NOT arm the dig marker/gesture (Shift = multiselect only)");
            // Shift takes precedence even when a dig tool IS armed: multi-select wins, no ghost.
            Assert.IsFalse(ClickToMoveService.DigMarkerActive(digToolArmed: true, multiselectHeld: true),
                "Shift held suppresses the dig marker/gesture even with a tool armed");
            // The only way the marker/gesture lives: a menu-armed tool with Shift NOT held.
            Assert.IsTrue(ClickToMoveService.DigMarkerActive(digToolArmed: true, multiselectHeld: false),
                "a menu-armed tool with no Shift shows the marker and can dig");
            // No tool, no Shift → plainly nothing.
            Assert.IsFalse(ClickToMoveService.DigMarkerActive(digToolArmed: false, multiselectHeld: false));
        }

        // ------------------------------------------------------------------
        // Regression: the band box only started when the click landed on a collider; on empty
        // air the drag did nothing. Native ground truth: the mode-1 press anchor is stored
        // screen-space only (FUN_0052fe80:53-54 — down coords → +0x138/+0x13c, no world pick)
        // and the 1→3 band promotion (:64-74) tests only the screen deltas; the world pick
        // happens at the UP consumption (:85 → FUN_005309c0).
        // ------------------------------------------------------------------

        [Test]
        public void PressArms_OnEmptyAir_BoxDragNeedsNoWorldHit()
        {
            // Repro: a pick miss must still arm the deferred press so the drag can band-box.
            Assert.IsTrue(ClickToMoveService.PressArmsDeferred(worldHit: false, digGestureStarted: false),
                "a pick MISS still arms the deferred press — empty-air drags must band-box");
            Assert.IsTrue(ClickToMoveService.PressArmsDeferred(worldHit: true, digGestureStarted: false),
                "a world hit that started no dig gesture defers as before");
            Assert.IsFalse(ClickToMoveService.PressArmsDeferred(worldHit: true, digGestureStarted: true),
                "a started dig gesture owns the mouse (native mode 2 — never bands)");
            // Composition: from an air-armed press the box path needs only screen motion +
            // eligibility — no world state anywhere. 20×20 px = 400 px² > the 250 px² gate.
            Assert.IsTrue(ClickToMoveService.BoxDragEligible(digToolArmed: false, multiselectHeld: false));
            Assert.IsTrue(ClickToMoveService.BoxDragStarts(new Vector2(100, 100), new Vector2(120, 120)),
                "the band promotion gate is screen-space only (FUN_0052fe80:70)");
        }

        [Test]
        public void AirClick_WithoutDrag_StaysInert()
        {
            // A press on empty air that never crosses the band gate keeps the old behavior:
            // the release is the deferred click, which re-picks at the UP point (HandleClick's
            // TryPickWorld gate) and no-ops on a miss — the native also picks only at the UP
            // consumption (FUN_0052fe80:85 → FUN_005309c0). The pure pieces of that path:
            Assert.IsTrue(ClickToMoveService.PressArmsDeferred(false, false), "the air press arms");
            Assert.IsFalse(ClickToMoveService.BoxDragStarts(new Vector2(100, 100), new Vector2(104, 104)),
                "a sub-gate wiggle (16 px² < 250) never bands — the release stays a plain click");
            Assert.AreEqual(ClickToMoveService.MarqueeRelease.Commit,
                ClickToMoveService.MarqueeReleaseFor(mouseHeld: false, mouseUpEdge: true),
                "the up edge commits to HandleClick, whose pick-miss early-out keeps an air click inert");
        }

        [Test]
        public void BoxDrag_ShiftWinsOverArmedTool()
        {
            // No tool armed → the drag box is always eligible (plain multi-select drag).
            Assert.IsTrue(ClickToMoveService.BoxDragEligible(digToolArmed: false, multiselectHeld: false));
            Assert.IsTrue(ClickToMoveService.BoxDragEligible(digToolArmed: false, multiselectHeld: true));
            // Tool armed, no Shift → the dig gesture owns the mouse, the box must not start.
            Assert.IsFalse(ClickToMoveService.BoxDragEligible(digToolArmed: true, multiselectHeld: false),
                "an armed tool without Shift owns the drag (dig marquee), no band box");
            // Tool armed but Shift held → multi-select wins: the box drag runs anyway.
            Assert.IsTrue(ClickToMoveService.BoxDragEligible(digToolArmed: true, multiselectHeld: true),
                "Shift+drag band-boxes even with a dig tool armed (multiselect precedence)");
        }

        [Test]
        public void ShiftMultiSelect_ComposesWith_HoverGate()
        {
            // The hover gate reads ctx.Selection.Count; multi-selecting must keep hasSelection
            // true so verbs stay live over the whole set — Shift+click add is unaffected.
            var (interp, ctx) = Host();
            var g1 = Spawn(interp, ctx, "G", 0);
            var g2 = Spawn(interp, ctx, "G", 0);
            var pilz = Spawn(interp, ctx, "Pilz", 0);
            ctx.Selection.ClickSelect(g1.Handle, additive: false);
            ctx.Selection.ClickSelect(g2.Handle, additive: true);   // Shift add still works
            Assert.AreEqual(2, ctx.Selection.Count);
            Assert.IsTrue(SelectionAuraService.HoverEligible(pilz, 0, hasSelection: ctx.Selection.Count > 0),
                "with a multi-selection live, item verbs are still hoverable");
        }
    }
}
