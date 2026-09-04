#nullable enable

namespace Diggles.Tests
{
    using Diggles.Runtime;
    using NUnit.Framework;
    using UnityEngine;

    // Headless (EditMode) coverage for the two Play bugs:
    //
    // 1. SELECTION LOSS — the dig-marquee mouse ownership: Input.GetMouseButtonUp is a polled
    // EDGE; losing focus mid-drag drops it, _digDrag then stayed true forever and every
    // ClickToMoveService.Tick early-returned (nothing selectable). The release decision is
    // the pure ClickToMoveService.MarqueeReleaseFor — the stuck state is simulated here.
    //
    // 2. AURA invisible in the Game view — the visual half moved to
    // RenderPipelineManager.endCameraRendering; the DATA level
    // (vertex projection → convex hull → band triangulation, the faithful SelHoverGlow
    // reconstruction of FUN_00473cc0:669-714) is smoke-tested here through
    // AuraBandRenderer.BuildBands on a real Camera + readable mesh, no rendering needed.
    public sealed class SelectionAuraAndClickEaterTests
    {
        // ---- 1. the marquee release guard (the selection-loss eater) ----

        [Test]
        public void MarqueeRelease_MouseHeld_ContinuesDragging()
        {
            Assert.AreEqual(ClickToMoveService.MarqueeRelease.Continue,
                ClickToMoveService.MarqueeReleaseFor(mouseHeld: true, mouseUpEdge: false));
        }

        [Test]
        public void MarqueeRelease_UpEdge_Commits()
        {
            // The normal FUN_005300f0 mouse-up commit (GetMouseButton(0) is already false on
            // the same frame the up edge fires — the edge must win over "not held").
            Assert.AreEqual(ClickToMoveService.MarqueeRelease.Commit,
                ClickToMoveService.MarqueeReleaseFor(mouseHeld: false, mouseUpEdge: true));
        }

        [Test]
        public void MarqueeRelease_LostUpEdge_Cancels_NeverWedgesTheMouse()
        {
            // THE stuck state: button no longer held, but the up edge never arrived (focus
            // loss mid-drag). Before the guard this kept _digDrag true forever — the "nothing
            // is selectable anymore" eater.
            Assert.AreEqual(ClickToMoveService.MarqueeRelease.CancelLostRelease,
                ClickToMoveService.MarqueeReleaseFor(mouseHeld: false, mouseUpEdge: false));
        }

        // ---- 1b. the work-clock capture law (the CONFIRMED clock eater) ----
        // One click on the clock latched PointerCaptured until a second click: a drag armed on
        // pointer-down lost its pointer-up (Unity delivers the up only to the ACTIVE press
        // object; UpdateMarkers hides the sun/moon handles mid-drag). The pure law: capture =
        // hovered OR (drag live AND the button actually held).

        [Test]
        public void ClockCapture_HoverOnly_Captures()
        {
            Assert.IsTrue(WorkClockWidget.ComputeCapture(
                contentActive: true, pointerOver: 1, dragActive: false, mouseHeld: false));
        }

        [Test]
        public void ClockCapture_LiveDrag_CapturesEvenOffHover()
        {
            Assert.IsTrue(WorkClockWidget.ComputeCapture(
                contentActive: true, pointerOver: 0, dragActive: true, mouseHeld: true));
        }

        [Test]
        public void ClockCapture_StaleDragWithButtonUp_DoesNotCapture()
        {
            // THE latch: _drag stuck true but the button is no longer held — must not eat clicks.
            Assert.IsFalse(WorkClockWidget.ComputeCapture(
                contentActive: true, pointerOver: 0, dragActive: true, mouseHeld: false));
        }

        [Test]
        public void ClockCapture_HiddenContent_NeverCaptures()
        {
            Assert.IsFalse(WorkClockWidget.ComputeCapture(
                contentActive: false, pointerOver: 1, dragActive: true, mouseHeld: true));
        }

        // ---- 1c. the ALT alternate-action token law (RE: FUN_00530b90:51-58 — VK_MENU/ALT
        // appends " -alternate", VK_SHIFT appends " -multiselect"; the append precedes the
        // cursor-vs-command branch so BOTH txtpars carry it; eventgen.tcl:100-103 = -invclick).

        [Test]
        public void CommandTxtPar_MatchesTheNativeTokenLaw()
        {
            Assert.AreEqual("-command", ClickToMoveService.CommandTxtPar(false));
            Assert.AreEqual("-command -alternate", ClickToMoveService.CommandTxtPar(true));
        }

        [Test]
        public void HoverTxtPar_MatchesTheNativeCursorQuery()
        {
            Assert.AreEqual("", SelectionAuraService.HoverTxtPar(false));
            Assert.AreEqual("-alternate", SelectionAuraService.HoverTxtPar(true));
        }

        [Test]
        public void InventoryAltClick_TxtPar_CarriesAlternateAndInvClick()
        {
            Assert.AreEqual("-command -alternate -invclick",
                Diggles.Runtime.UI.InventoryPanelController.AltClickTxtPar());
        }

        [Test]
        public void HoverKey_AltToggle_ForcesVerbReeval()
        {
            // same target, ALT pressed/released → the eventgen verb (and aura colour) re-runs
            Assert.IsTrue(SelectionAuraService.HoverNeedsRefresh(42, false, 42, true));
            Assert.IsTrue(SelectionAuraService.HoverNeedsRefresh(42, true, 42, false));
            // unchanged target + unchanged ALT → cached
            Assert.IsFalse(SelectionAuraService.HoverNeedsRefresh(42, true, 42, true));
            // target change always refreshes
            Assert.IsTrue(SelectionAuraService.HoverNeedsRefresh(42, false, 7, false));
        }

        // ---- 2. aura band building (data level) ----

        static Camera MakeCamera()
        {
            var go = new GameObject("test-cam");
            var cam = go.AddComponent<Camera>();
            cam.transform.position = Vector3.zero;
            cam.transform.rotation = Quaternion.identity;   // looks down +Z
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 1000f;
            return cam;
        }

        // A readable 8-corner box mesh in front of the camera; BuildBands only reads vertices
        // (the hull input), triangles are not required.
        static GameObject MakeMeshEntity(Vector3 center, float half = 1f)
        {
            var go = new GameObject("test-entity");
            go.transform.position = center;
            var mesh = new Mesh
            {
                vertices = new[]
                {
                    new Vector3(-half, -half, -half), new Vector3(half, -half, -half),
                    new Vector3(-half,  half, -half), new Vector3(half,  half, -half),
                    new Vector3(-half, -half,  half), new Vector3(half, -half,  half),
                    new Vector3(-half,  half,  half), new Vector3(half,  half,  half),
                },
            };
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            return go;
        }

        [Test]
        public void BuildBands_EntityInFront_ProducesHullBandTris()
        {
            var camGo = MakeCamera();
            var entity = MakeMeshEntity(new Vector3(0f, 0f, 10f));
            var band = new GameObject("band").AddComponent<AuraBandRenderer>();
            try
            {
                band.SetSlotDirect(0, entity, Color.white);
                int tris = band.BuildBands(camGo);
                // hull of a projected box has ≥ 4 corners; the band is 4 tris per hull edge
                // (hull→outer + inner→hull quad strips) — so at least 16 triangles.
                Assert.GreaterOrEqual(tris, 16, "band should triangulate around the projected hull");
            }
            finally
            {
                Object.DestroyImmediate(band.gameObject);
                Object.DestroyImmediate(entity);
                Object.DestroyImmediate(camGo.gameObject);
            }
        }

        [Test]
        public void BuildBands_BothSlots_SelectionPlusHover()
        {
            var camGo = MakeCamera();
            var sel = MakeMeshEntity(new Vector3(-3f, 0f, 10f));
            var hov = MakeMeshEntity(new Vector3(3f, 0f, 10f));
            var band = new GameObject("band").AddComponent<AuraBandRenderer>();
            try
            {
                band.SetSlotDirect(0, sel, Color.white);
                int selOnly = band.BuildBands(camGo);
                band.SetSlotDirect(1, hov, Color.green);
                int both = band.BuildBands(camGo);
                Assert.Greater(selOnly, 0);
                Assert.Greater(both, selOnly, "the hover slot must add its own band");
            }
            finally
            {
                Object.DestroyImmediate(band.gameObject);
                Object.DestroyImmediate(sel);
                Object.DestroyImmediate(hov);
                Object.DestroyImmediate(camGo.gameObject);
            }
        }

        [Test]
        public void BuildBands_EntityBehindCamera_DrawsNothing()
        {
            var camGo = MakeCamera();
            var entity = MakeMeshEntity(new Vector3(0f, 0f, -10f));   // behind — sp.z ≤ 0 skip
            var band = new GameObject("band").AddComponent<AuraBandRenderer>();
            try
            {
                band.SetSlotDirect(0, entity, Color.white);
                Assert.AreEqual(0, band.BuildBands(camGo));
            }
            finally
            {
                Object.DestroyImmediate(band.gameObject);
                Object.DestroyImmediate(entity);
                Object.DestroyImmediate(camGo.gameObject);
            }
        }

        [Test]
        public void BuildBands_ClearedSlot_DrawsNothing()
        {
            var camGo = MakeCamera();
            var entity = MakeMeshEntity(new Vector3(0f, 0f, 10f));
            var band = new GameObject("band").AddComponent<AuraBandRenderer>();
            try
            {
                band.SetSlotDirect(0, entity, Color.white);
                Assert.Greater(band.BuildBands(camGo), 0);
                band.SetSlotDirect(0, null, default);                 // deselect
                Assert.AreEqual(0, band.BuildBands(camGo));
            }
            finally
            {
                Object.DestroyImmediate(band.gameObject);
                Object.DestroyImmediate(entity);
                Object.DestroyImmediate(camGo.gameObject);
            }
        }
    }
}
