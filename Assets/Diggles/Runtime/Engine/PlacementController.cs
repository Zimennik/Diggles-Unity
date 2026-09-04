#nullable enable

namespace Diggles.Runtime
{
    using System.Collections.Generic;
    using Diggles.Runtime.Tcl.Engine;
    using UnityEngine;

    // Building placement mode — the port of the native ghost-placement singleton DAT_0076a338.
    //
    // • Enter (FUN_00530e80): the crate window's build button, or eventgen returning the
    // "placement" verb on a -command click (FUN_00530b90:85-105). Stores the placing handle
    // (+0x128), switches the input mode so world clicks confirm instead of walking
    // (wnd+0x134 = 4,:176-177) and puts a translucent ghost of the building under the cursor.
    // • Tick (FUN_0046b450) selects a mode. A TransportLogic elevator uses mode 3: snap to a
    // paired target-15/target-11 vertical shaft head at z=15, with no ground/box fallback.
    // A ladder uses mode 4: scan down to the bottom of a target-11 vertical shaft, select the
    // fitted leiter_* span and mount at z=11; the preview stays invalid outside a shaft
    // except for the ordinary front-band crate/box fallback. A door uses mode 5: snap to the
    // front tunnel band at z=15 and accept only the target-11 aperture framed by target-15
    // floor, roof and side samples. A nonzero class_objsnapclass uses mode 7: nearest
    // matching host in its weighted range, ghost at link point 0, host persisted as +0xec.
    // Otherwise the tick mode (+0xc0) comes from the object's place-snap mode (obj+0x224,
    // set_placesnapmode FUN_0067d8b0): snapmode 1 = wall mount (tick mode 8 — the floor-line
    // march + 4-corner-flat + |z−h|<4 wall-trap law, fallen.tcl:245 "1 == wall"), snapmode 2
    // = ceiling (tick mode 9, fallen.tcl:60), and snapmode 0 — every production
    // building/crate, genericprod.tcl:450 — is tick mode 2 (FUN_0046b450:795-920):
    // pick (FUN_0046aec0) → vertical settle (FUN_0046b190) → the depth-budget gate
    // pickZ + classBboxMaxZ·0.01 < 13.0 (:828-829, DAT_0073754c=13/DAT_00736fe0=0.01)
    // decides building vs box; overlap tests FUN_005e76a0/005e7000/005e7400 gate validity.
    // The Tcl chain re-validates authoritatively at execution time (prod_buildup_check →
    // check_ghost_coll + clean cancel, z_work_common.tcl:345-371), so the overlap radii are
    // a UX approximation and a mis-approximation is safe.
    // • Confirm (FUN_00531310): registers the persistent order ghost (FUN_0046a880,:225), then
    // branches on CSelection::IsSelected(placingHandle) (FUN_005e35a0,:227-236):
    // not selected ⇒ a gnome carries the crate — run the unchanged eventgen with the native
    // confirm txtpar ("-command -placement -asprod"/"-asbox",:237/:263) and fire the
    // resulting evt_task_buildup / evt_task_putdown on the carrier (:287-308). Ground
    // clicks never place buildings — only this confirm produces the buildup txtpar
    // (eventgen.tcl:172-178 is unreachable otherwise).
    // selected ⇒ the crate itself is selected (its window's build button) — write the
    // deferred order into the record (FUN_005ee250: pos→+0x30, asbox→+0x2d, +0x28 = 1,
    // +0x3d word = 0x100;:316-331); ProdDispatcher sends the best free gnome
    // (evt_autoprod_unpack).
    // • Cancel: drop the cursor ghost, leave the mode.
    //
    // POCO, UI-agnostic: created by DigglesTclEngine.Register (so hide_obj_ghost and headless
    // tests reach it) and registered into the VContainer scope by GameLifetimeScope for the UI;
    // ClickToMoveService routes the per-frame cursor/click input into it.
    public sealed class PlacementController
    {
        // ItemPutdown = the putdown-place mode for a plain (non-building) inventory item. The
        // native inventory interaction is drag & drop into the world, and the drag is this same
        // ghost-placement mode (eventgen returns "placement" for an in-inventory material/tool
        // -command click, eventgen.tcl:268-269/:318-319; the confirm FUN_00531310:236-239 runs
        // eventgen again with "-command -placement -asprod …" whose material/tool branch yields
        // evt_task_putdown -pos1 <spot>, eventgen.tcl:263-267/:313-317). The port's click-driven
        // equivalent: click the inventory slot to enter this mode, a world click confirms,
        // Esc/RMB cancels.
        public enum PlacementMode { None, CrateFromInventory, CrateSelected, MoveBuilt, ItemPutdown }

        // A persistent order ghost left standing at a confirmed spot (the singleton's +0xf1
        // list, stride 0x24), keyed by the placed object's handle and removed by hide_obj_ghost
        // (FUN_0069b440:58 → FUN_0046a960). The native record carries {handle, classid, meshid,
        // pos…} and the placement tick overlap-tests every candidate against the registry
        // (FUN_005e7400: the ghost mesh's bbox at the ghost pos); the port stores the
        // branch-selected bbox in raw model units (HasBBox=false ⇒ the legacy radius fallback).
        public readonly struct OrderGhost
        {
            public readonly Vector3 Pos;
            public readonly float Yaw;
            public readonly bool AsBox;
            public readonly bool HasBBox;
            public readonly Vector3 BBoxMin;
            public readonly Vector3 BBoxMax;
            public OrderGhost(Vector3 pos, float yaw, bool asBox = false,
                Vector3? bbMin = null, Vector3? bbMax = null)
            {
                Pos = pos; Yaw = yaw; AsBox = asBox;
                HasBBox = bbMin.HasValue && bbMax.HasValue;
                BBoxMin = bbMin ?? default;
                BBoxMax = bbMax ?? default;
            }
        }

        readonly DigglesTclContext _ctx;

        public PlacementController(DigglesTclContext ctx) => _ctx = ctx;

        // ---- live placement state (the DAT_0076a338 mirror) ----
        public PlacementMode Mode { get; private set; }
        public int PlacingHandle { get; private set; }          // +0x128 / wnd handle being placed
        public bool AsBox { get; private set; }                 // +0xcd — place as bare box (putdown)
        public Vector3 GhostPos { get; private set; }           // +0xd0..0xd8, native coords
        public float GhostYaw { get; private set; }             // +0xdc..0xe4 (mode 7 inherits host yaw)
        public int SnapTargetHandle { get; private set; } = -1; // +0xec — mode-7 object-snap host
        public bool Valid { get; private set; }                 // +0xcc — confirm is a no-op while false
        public bool Active => Mode != PlacementMode.None;
        public float LadderPreviewLength { get; private set; }

        // ---- validity diagnostics (port-only surface, no native counterpart) ----
        // The test that rejected the last UpdateCursor/ComputeValid spot (null while the spot
        // validates): one line with the numbers that made the decision. Logged when
        // DebugLogRejects is on, once per reason change so a mouse sweep cannot spam the console.
        public string? LastReject { get; private set; }
        public static bool DebugLogRejects = Application.isPlaying;
        string? _lastLoggedReject;
        string? _gateReject;   // written by the innermost failing test under SpotClear

        // The persistent order-ghost registry (+0xf1 list) — presenter/UI-facing.
        public readonly Dictionary<int, OrderGhost> OrderGhosts = new();

        GameObject? _cursorGhost;                                // the translucent ghost root under the cursor
        GameObject? _ghostBuildingGo;                            // child: the unpacked building model (+0xb8/+0xbc)
        GameObject? _ghostBoxGo;                                 // child: the global kiste box model (+0xc4/+0xc8)
        bool _asBoxForced;                                       // the deprecated ToggleAsBox shim (see below)
        readonly Dictionary<int, GameObject> _orderGhostGos = new();
        MaterialPropertyBlock? _mpb;
        private float _itemMeshBottomOffset;
        private string? _ladderPreviewObject;

        // Test/UI surface for the cursor ghost (which model is up, and the GOs themselves).
        public GameObject? CursorGhost => _cursorGhost;
        public GameObject? GhostBuildingGo => _ghostBuildingGo;
        public GameObject? GhostBoxGo => _ghostBoxGo;

        static readonly Color ValidTint = new Color(0.35f, 1f, 0.35f, 0.55f);
        static readonly Color InvalidTint = new Color(1f, 0.3f, 0.3f, 0.55f);
        static readonly Color OrderTint = new Color(0.65f, 0.75f, 1f, 0.45f);

        // ---- mode entry / exit (FUN_00530e80) ----

        // Enter placement for a crate/building. Returns false when already placing (the native
        // treats a click while placing as the confirm — that routing is the input layer's job,
        // FUN_00530b90:115-120) or the handle is unknown. Valid starts false: the native +0xcc
        // is 0 until the placement tick validates a spot, so the first UpdateCursor must run
        // before a Confirm can land.
        public bool EnterPlacement(int objHandle)
        {
            if (Active) return false;
            if (!_ctx.Entities.TryGetValue(objHandle, out var e)) return false;
            Mode = e.ContainedIn >= 0 ? PlacementMode.CrateFromInventory : PlacementMode.CrateSelected;
            PlacingHandle = objHandle;
            AsBox = false;
            _asBoxForced = false;
            GhostPos = e.Position;
            GhostYaw = 0f;
            SnapTargetHandle = -1;
            Valid = false;
            BuildCursorGhost(e);
            return true;
        }

        // Enter the putdown-place mode for a plain inventory item (see the enum note). The
        // carrier gnome must actually hold the item: the native placement for an inventory click
        // only ever starts from the gnome's own window, and the confirm branch relies on the
        // selection being the carrier (FUN_00531310:227-248: placing handle not selected ⇒
        // eventgen runs on selection[0] = the gnome). The item's model becomes the cursor ghost
        // (BuildCursorGhost's clone fallback covers unmapped classes); there is no box/building
        // swap in this mode — the item is placed as itself.
        public bool EnterPutdownPlace(int gnomeHandle, int itemHandle)
        {
            if (Active) return false;
            if (!_ctx.Entities.TryGetValue(gnomeHandle, out var g)) return false;
            if (!_ctx.Entities.TryGetValue(itemHandle, out var it)) return false;
            if (it.ContainedIn != gnomeHandle) return false;   // not this gnome's cargo
            Mode = PlacementMode.ItemPutdown;
            PlacingHandle = itemHandle;
            _ctx.SelectedHandle = gnomeHandle;   // the inventory window is the gnome's window
            AsBox = false;
            _asBoxForced = false;
            GhostPos = g.Position;               // start the ghost at the carrier
            GhostYaw = 0f;
            SnapTargetHandle = -1;
            Valid = false;
            BuildCursorGhost(it);
            return true;
        }

        // Esc / explicit abort: leave the mode, drop the cursor ghost. No order ghost is left
        // behind — only a confirm registers one (FUN_00531310:225).
        public void Cancel()
        {
            DestroyGo(_cursorGhost);
            _cursorGhost = null;
            _ghostBuildingGo = null;
            _ghostBoxGo = null;
            ResetMode();
        }

        // Deprecated shim — the native has no asbox toggle: +0xcd is recomputed per frame by the
        // placement tick from the depth-budget branch (mode 2: FUN_0046b450:828-829/:1970; the
        // native's own forced-box path is the +0xf0 byte this flag mirrors). Kept only so
        // existing tests/UI can force the box route; the forced flag survives the per-frame
        // recompute in UpdateCursor. Honored only for the crate placement types 2..6/8/9
        // (FUN_00531310:91-97) — a moved built building is always re-built as production, so the
        // MoveBuilt mode never forces.
        public void ToggleAsBox()
        {
            if (Mode == PlacementMode.CrateFromInventory || Mode == PlacementMode.CrateSelected)
            {
                _asBoxForced = !_asBoxForced;
                AsBox = _asBoxForced;
            }
        }

        // ---- the per-frame placement tick (ghost under the cursor) ----
        // nativePos = the cursor's terrain pick converted to native coords. The native pick
        // (CGameThread::Draw FUN_006b5d90:613-744) samples the mouse ray on the 16 depth-slab
        // planes (render z = i·0.5+7, DAT_0073708c/DAT_00737520) and keeps the deepest sample
        // whose sub-column hmap says rock (h ≥ i) — i.e. the pick is the point where the ray
        // enters the heightfield, with z an integer slab. FUN_0046aec0 then snaps x to the
        // half-tile grid and y to the 1/8 sub-row grid and leaves z untouched (:71-77).
        // The render-mesh raycast hits the same surface; Round(z) reproduces the whole-slab z.
        //
        // Then the faithful snapmode-0 tick (mode 2, FUN_0046b450:795-920):
        // • Settle (FUN_0046b190): march the vertical out of solid (≤16 rows up, h > z =
        // solid), then fall to the first solid row (≤16 rows down) — the support row at the
        // picked depth. Exhaustion ⇒ +0xcc = 0 (e.g. the front cross-section face, z = 15).
        // • Building iff pickZ + classBboxMaxZ·0.01 < 13.0 (:828-829; DAT_0073754c = 13.0,
        // DAT_00736fe0 = 0.01): the building's depth extent must fit inside the ant-farm's
        // front-face budget. Pointing at the front band (z ≥ 13 − extent) yields a box —
        // in the original, a box is only placeable at the front z block. z and x/y are never
        // overwritten by the branch; the ghost keeps the settled y and the picked depth.
        // • +0xcd (asbox) = the branch that ended active (:1970) even when invalid; an invalid
        // un-forced spot displays the building model (:1963-1967).
        // • +0xcc = settle success and the overlap tests (FUN_005e76a0/005e7000/005e7400,
        // approximated by SpotClear; the Tcl chain re-validates authoritatively).
        public void UpdateCursor(Vector3 nativePos)
        {
            if (!Active) return;
            LadderPreviewLength = 0f;
            // Native x snap law (FUN_0046aec0:71, DAT_0073708c = 0.5): x' = round(2x + 0.5)·0.5
            // under fistp round-half-to-even — non-lattice x snaps up to the next half-tile
            // boundary; exact lattice x resolves to the even neighbour (245.0→245.0, 245.5→246.0).
            nativePos.x = (float)(System.Math.Round(nativePos.x * 2.0 + 0.5, System.MidpointRounding.ToEven) * 0.5);
            nativePos.y = Mathf.Round(nativePos.y * 8f) * 0.125f;   // sub-row grid (FUN_0046aec0:72-77)
            // Whole-slab depth: the native pick z is the slab-plane index where the ray-march
            // stopped.
            nativePos.z = Mathf.Round(nativePos.z);

            GhostYaw = 0f;
            SnapTargetHandle = -1;
            bool itemMode = Mode == PlacementMode.ItemPutdown;

            // Native tick mode 7 (FUN_0046b450:1531-1651): a class with class_objsnapclass
            // searches the exact host class around the cursor using the weighted depth metric
            // (dz*0.5). The closest host inside the strict range wins; the ghost moves to host
            // link point 0 and +0xec carries the host into the confirm's -snapobj option.
            // No host nearby falls through to ordinary putdown.
            if (itemMode && TryResolveObjectSnap(nativePos, out var snapPos, out int snapHandle,
                    out float snapYaw))
            {
                GhostYaw = snapYaw;
                SnapTargetHandle = snapHandle;
                PublishCursor(snapPos, valid: true, asBox: false, reject: null);
                return;
            }

            if (!itemMode
                && _ctx.Entities.TryGetValue(PlacingHandle, out var elevatorEntity)
                && elevatorEntity.TransportLogic?.Elevator is { } elevator)
            {
                bool elevatorMounted = TryElevatorMount(
                    nativePos,
                    elevator.MaxRange,
                    out Vector3 mounted);
                bool elevatorClear = elevatorMounted
                    && SpecialOverlapsClear(mounted, asBox: false);
                string? elevatorReject = !elevatorMounted
                    ? $"elevator vertical shaft @({mounted.x:0.##},{mounted.y:0.##},{mounted.z:0.##})"
                    : !elevatorClear ? "elevator " + _gateReject
                    : null;
                PublishCursor(
                    mounted,
                    elevatorMounted && elevatorClear,
                    asBox: false,
                    elevatorReject);
                return;
            }

            if (!itemMode
                && _ctx.Entities.TryGetValue(PlacingHandle, out var doorEntity)
                && doorEntity.TransportLogic?.Door != null)
            {
                bool doorMounted = TryDoorMount(nativePos, out Vector3 mounted);
                bool doorClear = doorMounted && SpecialOverlapsClear(mounted, asBox: false);
                string? doorReject = !doorMounted
                    ? $"door horizontal-tunnel seat @({mounted.x:0.##},{mounted.y:0.##},{mounted.z:0.##})"
                    : !doorClear ? "door " + _gateReject
                    : null;
                PublishCursor(mounted, doorMounted && doorClear, asBox: false, doorReject);
                return;
            }

            if (!itemMode && !_asBoxForced
                && _ctx.Entities.TryGetValue(PlacingHandle, out var transport)
                && transport.TransportLogic?.Ladder is { } ladder)
            {
                bool ladderMounted = TryLadderMount(
                    nativePos,
                    ladder.Range,
                    out Vector3 mounted,
                    out float previewLength);
                ApplyLadderPreview(ladderMounted ? previewLength : 0f);
                bool ladderClear = ladderMounted && SpecialOverlapsClear(mounted, asBox: false);
                if (ladderClear)
                {
                    PublishCursor(mounted, valid: true, asBox: false, reject: null);
                    return;
                }

                bool frontBand = nativePos.z + _buildingDepthExtent >= BuildingDepthBudget;
                if (frontBand)
                {
                    Vector3 boxed = nativePos;
                    bool boxSettled = TrySettleSupport(ref boxed);
                    bool boxClear = boxSettled && SpotClear(boxed, asBox: true);
                    string? boxReject = !boxSettled
                        ? $"settle: no support column at ({boxed.x:0.##},{boxed.y:0.##},{boxed.z:0.##})"
                        : !boxClear ? "box " + _gateReject
                        : null;
                    PublishCursor(boxed, boxSettled && boxClear, asBox: true, boxReject);
                    return;
                }

                string ladderReject = !ladderMounted
                    ? $"ladder vertical shaft @({mounted.x:0.##},{mounted.y:0.##},{mounted.z:0.##})"
                    : "ladder " + _gateReject;
                PublishCursor(mounted, valid: false, asBox: false, ladderReject);
                return;
            }

            // Native tick modes 8/9 are selected by obj+0x224. Both try their special building
            // seat first and fall back to the box branch when that seat is not legal. This is
            // intentionally different from normal mode 2, where a blocked building never falls
            // back to a box. The two shipped users are the Medusa wall trap (1) and crushing
            // ceiling trap (2).
            if (!itemMode && !_asBoxForced
                && _ctx.Entities.TryGetValue(PlacingHandle, out var placing)
                && (placing.PlaceSnapMode == 1 || placing.PlaceSnapMode == 2))
            {
                Vector3 mounted = nativePos;
                bool specialTerrain;
                if (placing.PlaceSnapMode == 1)
                {
                    specialTerrain = TryWallMount(ref mounted);
                }
                else
                {
                    specialTerrain = TrySettleSupport(ref mounted);
                    if (specialTerrain && Mathf.Abs(mounted.z - 13f) <= 1f)
                        mounted.z = 13f; // DAT_0073754c / DAT_007372a8
                    specialTerrain = specialTerrain && CeilingTerrainFits(mounted);
                }

                bool mountedClear = specialTerrain && SpecialOverlapsClear(mounted, asBox: false);
                if (mountedClear)
                {
                    PublishCursor(mounted, valid: true, asBox: false, reject: null);
                    return;
                }

                Vector3 boxed = nativePos;
                bool boxSettled = TrySettleSupport(ref boxed);
                bool boxClear = boxSettled && SpotClear(boxed, asBox: true);
                string? boxReject = !boxSettled
                    ? $"settle: no support column at ({boxed.x:0.##},{boxed.y:0.##},{boxed.z:0.##})"
                    : !boxClear ? "box " + _gateReject
                    : null;
                PublishCursor(boxed, boxSettled && boxClear, asBox: true, boxReject);
                return;
            }

            // The mode-2 settle (FUN_0046b190) — shared with the drop settle (RuntimeWorldSpawner).
            bool settled = TrySettleSupport(ref nativePos);
            if (settled && itemMode) ClampItemDropDepthToFloor(ref nativePos);

            // The depth-budget gate (FUN_0046b450:828-829): building iff the class bbox's depth
            // extent fits in front of the pick. Overlap failure does not fall back to the box —
            // only the budget (or the forced flag) selects it (:915 exits invalid when the budget
            // had passed). The item putdown mode has no box/building branch at all — the item is
            // placed as itself anywhere it settles (the budget is a building silhouette law).
            bool buildingFits = settled && nativePos.z + _buildingDepthExtent < BuildingDepthBudget;
            bool tryBox = !itemMode && (_asBoxForced || (settled && !buildingFits));
            Vector3 clearancePos = itemMode ? ItemDropAnchorPosition(nativePos) : nativePos;
            bool clear = settled && SpotClear(clearancePos, tryBox);

            string? reject = !settled
                ? $"settle: no support column at ({nativePos.x:0.##},{nativePos.y:0.##},{nativePos.z:0.##})"
                : !clear ? (tryBox ? "box " : "bld ") + _gateReject
                : null;
            bool asBoxAllowed = Mode == PlacementMode.CrateFromInventory || Mode == PlacementMode.CrateSelected;
            PublishCursor(nativePos, settled && clear, asBoxAllowed && tryBox, reject);
        }

        void PublishCursor(Vector3 pos, bool valid, bool asBox, string? reject)
        {
            LastReject = reject;
            if (DebugLogRejects && LastReject != null && LastReject != _lastLoggedReject)
            {
                _lastLoggedReject = LastReject;
                Debug.LogWarning($"[Placement] reject: {LastReject}");
            }

            GhostPos = pos;
            AsBox = asBox;
            Valid = valid;
            if (_cursorGhost == null || _ctx.SpawnParent == null) return;
            Vector3 anchorPos = ItemDropAnchorPosition(pos);
            _cursorGhost.transform.position =
                _ctx.SpawnParent.TransformPoint(RuntimeWorldSpawner.NativeToLocal(anchorPos));
            if (SnapTargetHandle > 0
                && _ctx.Entities.TryGetValue(SnapTargetHandle, out var target)
                && target.GameObject != null)
                _cursorGhost.transform.rotation = target.GameObject.transform.rotation;
            else
                _cursorGhost.transform.localRotation = Quaternion.identity;
            ApplyGhostVisual();
        }

        // The positioned inventory route passes the confirmed floor point straight to
        // set_posbottom (z_procs.tcl:292-323), so its preview must apply the same raw-anchor
        // conversion without changing the logical GhostPos sent to Tcl. Object-snap mode keeps
        // the host/link position verbatim (eventgen.tcl:115-123).
        private Vector3 ItemDropAnchorPosition(Vector3 bottomPosition)
        {
            if (Mode != PlacementMode.ItemPutdown || SnapTargetHandle > 0) return bottomPosition;
            bottomPosition.y -= _itemMeshBottomOffset;
            return bottomPosition;
        }

        // The item cursor can arrive with a depth behind the relief because the positioned
        // putdown branch never calls vector_fix (z_procs.tcl:292-323). Use vector_fix's native
        // non-walkable seat law (FUN_00595770:255-267) after the item-only support settle.
        private void ClampItemDropDepthToFloor(ref Vector3 position)
        {
            var terrain = _ctx.Terrain;
            if (terrain == null) return;

            int subX = Mathf.RoundToInt(position.x * 8f);
            int subY = Mathf.RoundToInt(position.y * 8f);
            float supportDepth;
            if (terrain.Nav is { } nav)
            {
                if (subX < 0 || subX >= nav.SubW || subY <= 0 || subY >= nav.SubH) return;
                supportDepth = nav.GetHmapSub(subX, subY - 1) + 1f;
            }
            else
            {
                int tileX = subX >> 3;
                int tileY = (subY - 1) >> 3;
                if (tileX < 0 || tileX >= terrain.Width || tileY < 0 || tileY >= terrain.Height) return;
                supportDepth = terrain.GetHmap(tileX, tileY) + 1f;
            }

            position.z = Mathf.Min(Mathf.Max(position.z, supportDepth), 14.5f);
        }

        // Legacy clearance radii (native units) — the model-less fallback only: headless tests
        // spawn bbox-less `def_class B production` entities, and their contracts (blocked at
        // <1.0, clear beyond) predate the bbox law. With a model library every test goes through
        // the faithful AABB path below. UX-only either way: the Tcl chain re-validates at
        // execution time (prod_buildup_check → check_ghost_coll, z_work_common.tcl:345-371).
        const float BuildingClearRadius = 1f;
        const float BoxClearRadius = 0.45f;

        // The mode-2 building depth budget (DAT_0073754c = 13.0, FUN_0046b450:829) and the
        // class-bbox depth extent (rec+0x58 · DAT_00736fe0 = maxZ · 0.01, model units → slabs).
        // The extent comes from the class model's baked mesh bbox (CMesh rec+0x44..0x58 — the
        // exact record fields the native reads); without a model (headless tests) a nominal
        // 2-slab building footprint keeps the law testable.
        public const float BuildingDepthBudget = 13f;
        public const float DefaultBuildingDepthExtent = 2f;
        // Mode-4 acceptance is `FUN_0060b560 != 0` (FUN_0046b450, the accept block): the
        // selector walks the transport grid from entry 1 while `span + 1 ≥ entry.height`, so
        // slot 0 (leiter_a) alone is a reject — the minimum legal span is
        // height(leiter_b) − 1, heights registered as (bboxMaxY − bboxMinY)·DAT_00736fcc − 0.5
        // (FUN_0060ad40:163-164). Measured from the shipped transport.3db: leiter_b bboxY =
        // 1959.25 mesh units × 0.005 − 0.5 = 9.296 → minimum span 8.296 (leiter_a = 5.431
        // world tall — too short to ever satisfy slot 1; this is what reds a low corridor).
        // The live path derives the threshold from the loaded library
        // (DigglesTclEngine.TryGetLadderMinimumSpan); this constant is the library-less
        // test fallback.
        public const float MinimumLadderSpan = 8.296f;
        public const float MinimumElevatorSpan = 3f;
        float _buildingDepthExtent = DefaultBuildingDepthExtent;
        // The live extent (test/diagnostic surface — the law's only class-dependent input).
        public float BuildingDepthExtent => _buildingDepthExtent;

        // The ghost's two candidate bodies, raw model units (null = unresolved → legacy radii):
        // +0xb8 building model bbox / +0xc4 kiste bbox in the native singleton's terms.
        Vector3? _bbBuildingMin, _bbBuildingMax, _bbBoxMin, _bbBoxMax;

        // Native model→native-coordinate scales (FUN_005e7000:117-140): x/y (east + vertical)
        // × DAT_00736fcc = 0.005, depth × DAT_00736fe0 = 0.01.
        const float BodyScaleXY = 0.005f;
        const float BodyScaleZ = 0.01f;

        // The FUN_0046b190 settle on the live terrain. Sub-cell hmap when the nav layer exists;
        // fabricated test terrains (uniform per-tile blocks, no PMP sub-grid) fall back to the
        // tile-level height, which is exact for their 0/15 blocks. Headless (no terrain at all):
        // pass-through, like the old seat.
        bool TrySettleSupport(ref Vector3 p)
        {
            var t = _ctx.Terrain;
            if (t == null) return true;
            var nav = t.Nav;
            Vector3 q = p;
            if (nav != null)
            {
                if (!RuntimeWorldSpawner.TrySettleToSupport(nav, ref q)) return false;
                p = q;
                return true;
            }
            // tile-level fallback (MakeOpenTerrain-style worlds)
            int subX = Mathf.RoundToInt(p.x * 8f), row = Mathf.RoundToInt(p.y * 8f);
            float H(int r)
            {
                int tx = subX >> 3, ty = r >> 3;
                if (tx < 0 || tx >= t.Width || ty < 0 || ty >= t.Height) return 15f;
                return t.GetHmap(tx, ty);
            }
            int i = 0;
            for (; i < 16 && H(row) > p.z; i++) row--;          // up, out of solid
            if (i == 16) return false;
            for (i = 0; i < 16 && !(H(row) > p.z); i++) row++;  // down, to the support row
            if (i == 16) return false;
            p.y = row * 0.125f;
            return true;
        }

        bool TryResolveObjectSnap(Vector3 cursor, out Vector3 snapPos,
            out int targetHandle, out float targetYaw)
        {
            snapPos = default;
            targetHandle = -1;
            targetYaw = 0f;
            if (!_ctx.Entities.TryGetValue(PlacingHandle, out var item) || item.Class == null)
                return false;
            string targetClass = item.Class.ObjectSnapClass;
            float range = item.Class.ObjectSnapRange;
            if (string.IsNullOrEmpty(targetClass) || range <= 0f) return false;

            float best = range * range;
            DigglesTclEntity? bestTarget = null;
            foreach (var candidate in _ctx.Entities.Values)
            {
                if (candidate.Handle == item.Handle || candidate.Class == null
                    || candidate.ContainedIn >= 0 || candidate.DeleteInProgress
                    || !string.Equals(candidate.Class.Name, targetClass,
                        System.StringComparison.OrdinalIgnoreCase))
                    continue;
                float dx = candidate.Position.x - cursor.x;
                float dy = candidate.Position.y - cursor.y;
                float dz = (candidate.Position.z - cursor.z) * 0.5f;
                float d2 = dx * dx + dy * dy + dz * dz;
                if (d2 >= best) continue; // FUN_0046b450 uses a strict range-square comparison.
                best = d2;
                bestTarget = candidate;
            }
            if (bestTarget == null) return false;

            snapPos = bestTarget.Position;
            if (DigglesTclEngine.TryGetLinkOffsetNative(_ctx, bestTarget, 0,
                    applyRotation: true, out var linkOffset))
                snapPos += linkOffset;
            targetHandle = bestTarget.Handle;
            targetYaw = bestTarget.Rotation.y;
            return true;
        }

        bool TryLadderMount(
            Vector3 cursor,
            float range,
            out Vector3 mounted,
            out float previewLength)
        {
            mounted = cursor;
            previewLength = 0f;
            var nav = _ctx.Terrain?.Nav ?? _ctx.NavService?.World;
            if (nav == null) return false;
            // Live acceptance threshold = the real FUN_0060b560 != 0 law from the loaded
            // transport.3db rung table; the constant only backstops library-less hosts.
            float? minimumSpan = _ctx.ModelLibrary != null
                && DigglesTclEngine.TryGetLadderMinimumSpan(_ctx.ModelLibrary, out float derived)
                    ? derived
                    : (float?)null;
            return TryComputeLadderMount(
                nav, cursor, range, out mounted, out previewLength, minimumSpan);
        }

        bool TryElevatorMount(Vector3 cursor, float range, out Vector3 mounted)
        {
            mounted = cursor;
            var nav = _ctx.Terrain?.Nav ?? _ctx.NavService?.World;
            return nav != null
                && TryComputeElevatorMount(nav, cursor, range, out mounted, out _);
        }

        bool TryDoorMount(Vector3 cursor, out Vector3 mounted)
        {
            mounted = DoorSeat(cursor);
            var nav = _ctx.Terrain?.Nav ?? _ctx.NavService?.World;
            return nav != null && TryComputeDoorMount(nav, cursor, out mounted);
        }

        // The mode-5 door seat, FUN_0046b450:1875-1882 (after the:1828-1837 cursor snap the
        // generic path already applies: x half-grid, y sub-row, z from FUN_0055a600(...,0xf)):
        // x' = floor(x + 0.5):1875-1876 (local_74 = DAT_0073708c = 0.5)
        // y' = 2·floor((y − 0.5)·0.5) + 2.5:1877-1882 (DAT_007373d8 = 2.5)
        // z' = 15.0:1880 (local_cc = 0x41700000)
        // FUN_004295a0 = the CRT floor wrapper (calls FUN_006f6da0, the classic
        // exponent-mask floor); Ghidra drops its x87 return, so the raw decompile reads as
        // "+0.5/+2 with no floor" — the identical dropped-floor pattern at:1284-1290 is the
        // ladder seat, whose verified port law (TransportShaftCenterX below:
        // 2·floor((x−0.5)·0.5)+1.5, and startY = floor(y)−1.5) already pins the
        // floor-consumed reading. Cross-check: tueren.tcl:48-52 (evt_timer0, the unboxed
        // door's one-shot re-snap `x = hf2i(x+0.5)`, `y = hf2i(y)+0.5`, z=15) is idempotent
        // on this grid (integer x; y ≡ *.5) — under the floor-less reading it would shift
        // the built door off the validated ghost by up to half a tile.
        internal static Vector3 DoorSeat(Vector3 snappedCursor) => new(
            Mathf.Floor(snappedCursor.x + 0.5f),
            Mathf.Floor((snappedCursor.y - 0.5f) * 0.5f) * 2f + 2.5f,
            15f);

        public static bool TryComputeDoorMount(
            Diggles.Runtime.Navigation.NavWorld nav,
            Vector3 snappedCursor,
            out Vector3 mounted)
        {
            mounted = DoorSeat(snappedCursor);
            const float border = 4f;   // DAT_007374a0 = 4.0, FUN_0046b450:1839-1842 world-rect margin
            if (mounted.x < border || mounted.x >= nav.Width - border
                || mounted.y < border || mounted.y >= nav.Height - border)
                return false;

            int centerX = Mathf.RoundToInt(mounted.x * 8f);
            int centerY = Mathf.RoundToInt(mounted.y * 8f);
            for (int offset = 1; offset <= 13; offset++)
                if (nav.GetHmapSub(centerX, centerY - offset) != 11)
                    return false;

            if (nav.GetHmapSub(centerX, centerY) != 15
                || nav.GetHmapSub(centerX - 8, centerY) != 15
                || nav.GetHmapSub(centerX + 8, centerY) != 15)
                return false;

            int roofY = centerY - 20;
            if (nav.GetHmapSub(centerX, roofY) != 15
                || nav.GetHmapSub(centerX - 8, roofY) != 15
                || nav.GetHmapSub(centerX + 8, roofY) != 15)
                return false;

            for (int subY = roofY; subY < centerY + 4; subY++)
            for (int subX = centerX - 8; subX < centerX + 8; subX++)
                if ((nav.BaseSubAt(subX, subY)
                    & (Diggles.Runtime.Navigation.NavWorld.SubWater
                        | Diggles.Runtime.Navigation.NavWorld.SubClimb)) != 0)
                    return false;

            return true;
        }

        public static bool TryComputeLadderMount(
            Diggles.Runtime.Navigation.NavWorld nav,
            Vector3 cursor,
            float range,
            out Vector3 mounted)
            => TryComputeLadderMount(nav, cursor, range, out mounted, out _);

        // The mode-4 ladder down-scan, FUN_0046b450 (the `iVar5 == 4` arm), redone from the
        // decompile: an earlier port had invented two things not in the native — ±8 "frame"
        // probes and a missing early-stop — that left real shafts permanently red:
        // • lattice gate — the scan runs only when the seat sits on the 4-unit transport
        // lattice: `x_c == floor((x_c + 0.5)·DAT_00737058=0.25)·DAT_007374a0=4.0 +
        // DAT_007371c0=1.5` (the `local_40 == fVar19·4+1.5` guard right before the do-loop;
        // FUN_004295a0 = the CRT floor whose x87 return Ghidra drops). Off-lattice columns
        // never scan — they keep the ordinary box fallback.
        // • start row = floor(cursor.y) − 1.5 (piStack_d0 = floor(y) − DAT_007371c0), step
        // +1.0/row (DAT_00737138), at most 0x40 rows.
        // • a candidate row is a shaft bottom when exactly four probes hold (no ±8 terms
        // exist in the native): hmap(subX∓4, subY−6) == 11 (`cVar1 == '\v'`) and
        // hmap(subX∓4, subY+6) > 12 (`0xc < bVar2`) — open shaft column above, solid
        // support below both flanks.
        // • early-stop (LAB_0046e598, from step ≥ 2): both flank samples at (subX∓4, subY−2)
        // reading 15 mean the shaft has closed — the scan breaks to the box fallback
        // instead of tunnelling through rock to an unrelated cavity below.
        // • commit: the first row that passes the four probes is the candidate — after the
        // overlap tests and the reach scan the native `break`s unconditionally, valid or
        // not; failures fall to the box fallback, never to a deeper row.
        // • acceptance = the reach scan feeds the model selector and `FUN_0060b560 != 0`
        // must hold: span + 1 (DAT_00737138) ≥ the slot-1 (leiter_b) registered height —
        // rung heights = (bboxMaxY − bboxMinY)·DAT_00736fcc − 0.5 (FUN_0060ad40:163-164).
        // This is what keeps a low corridor red: its open column is too short for slot 1.
        // The live caller derives the threshold from the real transport.3db
        // (DigglesTclEngine.TryGetLadderMinimumSpan); MinimumLadderSpan is the
        // library-less fallback.
        // • the accepted base publishes (x_c, row, z=11).
        // The native also gates on the per-player tile+0x34 zone byte over the 2×2 seat
        // tiles (≥1 nonzero) — per-owner territory is not modelled in the port's world
        // (same hook note as Calc1's gate 4), so that term is knowingly skipped.
        public static bool TryComputeLadderMount(
            Diggles.Runtime.Navigation.NavWorld nav,
            Vector3 cursor,
            float range,
            out Vector3 mounted,
            out float previewLength,
            float? minimumSpanOverride = null)
        {
            mounted = cursor;
            previewLength = 0f;
            if (range <= 0f) return false;

            float center = TransportShaftCenterX(cursor.x);
            if (Mathf.Abs(Mathf.Floor((center + 0.5f) * 0.25f) * 4f + 1.5f - center) > 1e-3f)
                return false;   // off the 4-unit transport lattice — box fallback only
            int centerX = Mathf.RoundToInt(center * 8f);
            int startY = Mathf.RoundToInt((Mathf.Floor(cursor.y) - 1.5f) * 8f);
            float minimumSpan = minimumSpanOverride ?? MinimumLadderSpan;
            if (centerX < 4 || centerX >= nav.SubW - 4) return false;

            for (int step = 0, centerY = startY; step < 64; step++, centerY += 8)
            {
                if (centerY >= nav.SubH - 6) break;   // native map-bottom stop (LAB_0046e598)
                if (centerY >= 6
                    && nav.GetHmapSub(centerX - 4, centerY - 6) == 11
                    && nav.GetHmapSub(centerX + 4, centerY - 6) == 11
                    && nav.GetHmapSub(centerX - 4, centerY + 6) > 12
                    && nav.GetHmapSub(centerX + 4, centerY + 6) > 12)
                {
                    // The native commits to this row: span/overlap failures break to the box
                    // fallback rather than hunting deeper.
                    float span = DigglesTclEngine.ComputeLadderSpan(nav, centerX, centerY, range);
                    if (span + 1e-4f < minimumSpan) break;
                    mounted = new Vector3(centerX * 0.125f, centerY * 0.125f, 11f);
                    previewLength = span;
                    return true;
                }
                if (step >= 2
                    && nav.GetHmapSub(centerX - 4, centerY - 2) == 15
                    && nav.GetHmapSub(centerX + 4, centerY - 2) == 15)
                    break;   // early-stop: the shaft has closed above solid rock
            }
            return false;
        }

        public static bool TryComputeElevatorMount(
            Diggles.Runtime.Navigation.NavWorld nav,
            Vector3 cursor,
            float range,
            out Vector3 mounted)
            => TryComputeElevatorMount(nav, cursor, range, out mounted, out _);

        public static bool TryComputeElevatorMount(
            Diggles.Runtime.Navigation.NavWorld nav,
            Vector3 cursor,
            float range,
            out Vector3 mounted,
            out float shaftSpan)
        {
            mounted = cursor;
            shaftSpan = 0f;
            if (range <= 0f) return false;

            int centerX = Mathf.RoundToInt(TransportShaftCenterX(cursor.x) * 8f);
            int startY = Mathf.FloorToInt(cursor.y) * 8 + 12;
            for (int step = 0, centerY = startY; step < 64; step++, centerY -= 8)
            {
                if (centerX < 4 || centerX >= nav.SubW - 4
                    || centerY < 4 || centerY >= nav.SubH - 4)
                    continue;
                if (!HasShaftWallPair(nav, centerX, centerY - 4, 15)
                    || !HasShaftWallPair(nav, centerX, centerY + 4, 11))
                    continue;

                float span = DigglesTclEngine.ComputeElevatorSpan(
                    nav,
                    centerX,
                    centerY,
                    range);
                if (span + 1e-4f < MinimumElevatorSpan) continue;

                mounted = new Vector3(centerX * 0.125f, centerY * 0.125f, 15f);
                shaftSpan = span;
                return true;
            }
            return false;
        }

        private static float TransportShaftCenterX(float cursorX) =>
            Mathf.Floor((cursorX - 0.5f) * 0.5f) * 2f + 1.5f;

        private static bool HasShaftWallPair(
            Diggles.Runtime.Navigation.NavWorld nav,
            int centerX,
            int sampleY,
            int targetHeight) =>
            nav.GetHmapSub(centerX - 4, sampleY) == targetHeight
            && nav.GetHmapSub(centerX + 4, sampleY) == targetHeight;

        bool TryWallMount(ref Vector3 p)
        {
            var t = _ctx.Terrain;
            if (t == null) return true;
            if (!SpecialTerrainBounds(p))
            {
                _gateReject = $"wall terrain-border ({p.x:0.#},{p.y:0.#}) map={t.Width}x{t.Height}";
                return false;
            }
            int material = t.GetFtype(Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.y)) & 3;
            if (material == 3)
            {
                _gateReject = $"wall material-type {material} @({p.x:0.#},{p.y:0.#})";
                return false; // FUN_0046b450:177-181 rejects tile+0x18 low bits == 3.
            }
            var nav = t.Nav;
            byte Sub(int sx, int sy) => nav?.BaseSubAt(sx, sy) ?? 0;
            int Hmap(int sx, int sy)
            {
                if (nav != null) return nav.GetHmapSub(sx, sy);
                int tx = sx >> 3, ty = sy >> 3;
                if (tx < 0 || tx >= t.Width || ty < 0 || ty >= t.Height) return 15;
                return t.GetHmap(tx, ty);
            }
            Vector3 bbMin = _bbBuildingMin ?? new Vector3(-200f, -350f, -200f);
            Vector3 bbMax = _bbBuildingMax ?? new Vector3(200f, 0f, 200f);
            if (!TryComputeWallMount(p, bbMin, bbMax, Sub, Hmap, out var mounted))
            {
                _gateReject = $"wall seat/flat-face @({p.x:0.##},{p.y:0.##},{p.z:0.##})";
                return false;
            }
            p = mounted;
            return true;
        }

        // FUN_0046b450 mode 8 + FUN_005e8440. Public as an exact headless law surface: tests
        // can supply the original sub-grid and hmap without constructing a render world.
        public static bool TryComputeWallMount(Vector3 cursor, Vector3 bbMinRaw, Vector3 bbMaxRaw,
            System.Func<int, int, byte> baseSubAt, System.Func<int, int, int> hmapAt,
            out Vector3 mounted)
        {
            mounted = cursor;
            int sx = Mathf.RoundToInt(cursor.x * 8f);
            int sy = Mathf.RoundToInt(cursor.y * 8f);
            int offset;
            for (offset = 2; offset < 16; offset++)
            {
                bool here = (baseSubAt(sx, sy + offset) & 2) != 0;
                bool next = (baseSubAt(sx, sy + offset + 1) & 2) != 0;
                if (!here || !next) break;
            }
            if (offset >= 16) return false;

            mounted.y = cursor.y + offset * 0.125f - 0.8f; // DAT_0073703c / DAT_007370d4
            float minX = mounted.x + bbMinRaw.x * BodyScaleXY;
            float maxX = mounted.x + bbMaxRaw.x * BodyScaleXY;
            float minY = mounted.y + bbMinRaw.y * BodyScaleXY;
            float maxY = mounted.y + bbMaxRaw.y * BodyScaleXY;
            int H(float x, float y) => hmapAt(Mathf.RoundToInt(x * 8f), Mathf.RoundToInt(y * 8f));
            int h00 = H(minX, minY);
            int h01 = H(minX, maxY);
            int h10 = H(maxX, minY);
            int h11 = H(maxX, maxY);
            if (h00 != h01 || h00 != h10 || h00 != h11) return false;
            if (Mathf.Abs(cursor.z - h00) >= 4f) return false; // DAT_00737398 = 4.0 (double)

            mounted.z = h00 - bbMinRaw.z * BodyScaleZ;

            // FUN_005e8440 seats the inset back rectangle in air against the flat face.
            const float inset = 1.4f; // DAT_0073719c
            float wallDepth = mounted.z + bbMinRaw.z * BodyScaleZ / inset;
            float ix0 = mounted.x + bbMinRaw.x * BodyScaleXY / inset;
            float ix1 = mounted.x + bbMaxRaw.x * BodyScaleXY / inset;
            if (H(ix0, minY) > wallDepth || H(ix0, maxY) > wallDepth
                || H(ix1, minY) > wallDepth || H(ix1, maxY) > wallDepth)
                return false;
            return true;
        }

        bool CeilingTerrainFits(Vector3 pos)
        {
            var t = _ctx.Terrain;
            if (t == null) return true;
            if (!SpecialTerrainBounds(pos))
            {
                _gateReject = $"ceiling terrain-border ({pos.x:0.#},{pos.y:0.#}) map={t.Width}x{t.Height}";
                return false;
            }
            var nav = t.Nav;
            int Hmap(int sx, int sy)
            {
                if (nav != null) return nav.GetHmapSub(sx, sy);
                int tx = sx >> 3, ty = sy >> 3;
                if (tx < 0 || tx >= t.Width || ty < 0 || ty >= t.Height) return 15;
                return t.GetHmap(tx, ty);
            }
            Vector3 bbMin = _bbBuildingMin ?? new Vector3(-200f, -350f, -200f);
            Vector3 bbMax = _bbBuildingMax ?? new Vector3(200f, 0f, 200f);
            bool ok = ComputeCeilingTerrainFit(pos, bbMin, bbMax, Hmap);
            if (!ok) _gateReject = $"ceiling upper-corner/base-boundary @({pos.x:0.##},{pos.y:0.##},{pos.z:0.##})";
            return ok;
        }

        // FUN_005e8720: all four inset upper BBox corners must lie inside the ceiling, while
        // every lower corner must sit on the rock/air boundary rather than deep in either side.
        public static bool ComputeCeilingTerrainFit(Vector3 pos, Vector3 bbMinRaw, Vector3 bbMaxRaw,
            System.Func<int, int, int> hmapAt)
        {
            int H(float x, float y) => hmapAt(Mathf.RoundToInt(x * 8f), Mathf.RoundToInt(y * 8f));
            const float inset = 1.4f; // DAT_0073719c
            float x0 = pos.x + bbMinRaw.x * BodyScaleXY / inset;
            float x1 = pos.x + bbMaxRaw.x * BodyScaleXY / inset;
            float z0 = pos.z + bbMinRaw.z * BodyScaleZ / inset;
            float z1 = pos.z + bbMaxRaw.z * BodyScaleZ / inset;
            float topY = pos.y + bbMinRaw.y * BodyScaleXY - 0.2f; // DAT_00737054
            float requiredDepth = Mathf.Max(z0, z1);
            if (H(x0, topY) < requiredDepth || H(x1, topY) < requiredDepth) return false;

            float baseY = pos.y + bbMaxRaw.y * BodyScaleXY;
            return BoundaryCornerFits(x0, baseY, z0, H)
                && BoundaryCornerFits(x0, baseY, z1, H)
                && BoundaryCornerFits(x1, baseY, z0, H)
                && BoundaryCornerFits(x1, baseY, z1, H);
        }

        static bool BoundaryCornerFits(float x, float y, float z,
            System.Func<float, float, int> hmap)
        {
            float h = hmap(x, y);
            if (h <= z)
            {
                if (h <= z - 1f && hmap(x, y + 0.25f) <= z
                    && hmap(x + 0.375f, y) <= z && hmap(x - 0.375f, y) <= z)
                    return false;
            }
            else if (h > z + 1f && hmap(x, y - 0.375f) > z
                     && hmap(x + 0.375f, y) > z && hmap(x - 0.375f, y) > z)
                return false;
            return true;
        }

        bool SpecialTerrainBounds(Vector3 pos)
        {
            var t = _ctx.Terrain;
            return t == null || (pos.x >= 12f && pos.x <= t.Width - 12f
                && pos.y >= 12f && pos.y <= t.Height - 12f);
        }

        bool SpecialOverlapsClear(Vector3 pos, bool asBox)
        {
            Vector3? bbMin = asBox ? _bbBoxMin : _bbBuildingMin;
            Vector3? bbMax = asBox ? _bbBoxMax : _bbBuildingMax;
            float legacyRadius = asBox ? BoxClearRadius : BuildingClearRadius;
            return BodiesClear(pos, bbMin, bbMax, legacyRadius)
                && OrderGhostsClear(pos, bbMin, bbMax, legacyRadius);
        }

        // The overlap half of the +0xcc validity (UX-only; spec §10.1): no other collidable
        // un-contained world object inside `radius` (the `check_ghost_coll objects` equivalent;
        // the footprint is approximated by the centre point). The native overlap loop
        // (FUN_005e7000:73-77) gates every candidate on its per-object collision flag
        // (rec+0xb8 — set_collision / the class default): deco vegetation, info markers and
        // ground pickups never block — deco-vegetation grass entities fill the meadow and must
        // not veto placement, nor may gnomes/babies invalidate a spot — the chain shoos them
        // itself (send_gnomes_outofplacement / prod_buildup_waitforfree,
        // z_work_common.tcl:374-401). Boxed crates do not block
        // either — the kiste-pair model exemption inside the native overlap test (FUN_005e7000:80).
        static readonly System.Collections.Generic.HashSet<string> BlockingKinds = new()
        {
            // real placed structures — what the native's collidable set amounts to in practice
            "production", "transport", "elevator", "protection", "energy"
        };

        // Placed structures use their full footprint; any other still-collidable body (deco
        // trees, standing mushrooms, info barriers) uses the small trunk-base footprint below.
        static readonly System.Collections.Generic.HashSet<string> StructureKinds = new()
        {
            "production", "store", "energy", "protection", "elevator", "transport", "building"
        };

        // The native placement overlap (FUN_005e7000) tests the baked collision hull rec+0x44..
        // 0x58 — a small base box — not the render mesh. The port only has the render mesh, whose
        // bounds include a tree's whole canopy / a mushroom's cap (±1500 raw ⇒ ±7 tiles), which
        // would veto placement across most of the map. For a non-structure collidable body the
        // horizontal (model x/z) half-extent is clamped to this cap ≈ a mushroom base, so a tree
        // blocks its trunk cell, not its shadow. Raw.3db units; deco-mushroom bases (±112) pass
        // through unchanged.
        const float NonStructureBaseHalf = 150f;

        // The composite overlap/terrain half of the +0xcc validity — the three native tests the
        // mode-2 tick runs on a settled candidate (FUN_0046b450:795-920):
        // FUN_005e76a0 — terrain fit (map border, no-embed probes, base-corner boundary law);
        // FUN_005e7000 — AABB overlap vs collidable object bodies;
        // FUN_005e7400 — AABB overlap vs the pending order-ghost registry.
        bool SpotClear(Vector3 nativePos, bool asBox)
        {
            Vector3? bbMin = asBox ? _bbBoxMin : _bbBuildingMin;
            Vector3? bbMax = asBox ? _bbBoxMax : _bbBuildingMax;
            float legacyRadius = asBox ? BoxClearRadius : BuildingClearRadius;
            _gateReject = null;
            return TerrainFits(nativePos, bbMin, bbMax)
                && BodiesClear(nativePos, bbMin, bbMax, legacyRadius)
                && OrderGhostsClear(nativePos, bbMin, bbMax, legacyRadius);
        }

        // ---- FUN_005e7000: candidate box vs every collidable object body ----
        // Native: iterate the world-body records; skip non-colliding (rec+0xb8 == 0), the two
        // exclusion objects, and resolve the body's current display mesh bbox (classRec+0x34 +
        // mesh·0xE8 → +0x44..0x58) scaled ×0.005 (x/y) / ×0.01 (z) at the body position
        // (rec+0x40..0x48); reject on strict-< AABB intersection on all three axes (:117-140).
        // Port deltas: gnomes/babies never block (their mann/frau meshes carry no cmap → the
        // native default flag is clear; the Tcl chain shoos them via send_gnomes_outofplacement,
        // z_work_common.tcl:374-401); boxed crates never block (the kiste exemption,:80);
        // a body whose model can't be resolved (headless) falls back to the legacy
        // centre-distance radius so the pre-bbox test contracts hold.
        bool BodiesClear(Vector3 pos, Vector3? bbMin, Vector3? bbMax, float legacyRadius)
            => BodiesClear(pos, bbMin, bbMax, legacyRadius, PlacingHandle);

        bool BodiesClear(Vector3 pos, Vector3? bbMin, Vector3? bbMax, float legacyRadius,
            int exceptHandle)
        {
            float r2 = legacyRadius * legacyRadius;
            foreach (var e in _ctx.Entities.Values)
            {
                if (e.Handle == exceptHandle || e.ContainedIn >= 0 || e.Class == null) continue;
                var kind = e.Class.Kind;
                if (kind == "gnome" || kind == "baby") continue;
                // The per-object collision write (set_collision → rec+0xb8) is authoritative and
                // outranks every kind heuristic: a growing mushroom is `def_class Pilz food
                // material` yet its obj_init runs `set_collision this 1` (pilz.tcl:42) — the
                // native placement overlap FUN_005e7000:73-77 therefore does veto building over a
                // standing plant. Harvested drops (Pilzhut & friends) never
                // set it — they stay skippable below.
                bool explicitCollision = e.VarsScope.Vars.TryGetValue("_collision", out var cv0)
                    && (cv0 == "1" || cv0 == "true");
                // Loose resources & pickups (harvested mushrooms, treasure barrels, dropped
                // items) never veto a building spot unless explicitly collidable — the native
                // builds over them and absorbs/relocates them; their rec+0xb8 is clear (the
                // meadow is dotted with them).
                if ((kind == "material" || kind == "pickup") && !explicitCollision) continue;
                // the collision gate (FUN_005e7000 rec+0xb8): explicit set_collision wins,
                // else the class_collision default (real since has_cmap went live — animinit.tcl
                //:249-251/:327 keys it on the model cmap), else the structure-kind heuristic.
                bool collidable;
                if (e.VarsScope.Vars.TryGetValue("_collision", out var cv))
                    collidable = cv == "1" || cv == "true";
                else if (!string.IsNullOrEmpty(e.Class.Collision))
                    collidable = e.Class.Collision == "1" || e.Class.Collision == "true";
                else
                    collidable = BlockingKinds.Contains(kind ?? "");
                if (!collidable) continue;
                if (e.VarsScope.Vars.TryGetValue("_boxed", out var bx) && (bx == "1" || bx == "true"))
                    continue;   // FUN_005e7000:80 — the kiste pair never blocks
                var body = EntityBodyBBox(e);
                // Non-structure bodies (deco/props) get the trunk-base clamp — the render mesh's
                // canopy/cap is not the native collision hull (see NonStructureBaseHalf).
                if (body.HasValue && !StructureKinds.Contains(kind ?? ""))
                {
                    var bmin = body.Value.min; var bmax = body.Value.max;
                    bmin.x = Mathf.Max(bmin.x, -NonStructureBaseHalf); bmax.x = Mathf.Min(bmax.x, NonStructureBaseHalf);
                    bmin.z = Mathf.Max(bmin.z, -NonStructureBaseHalf); bmax.z = Mathf.Min(bmax.z, NonStructureBaseHalf);
                    body = (bmin, bmax);
                }
                if (bbMin.HasValue && bbMax.HasValue && body.HasValue)
                {
                    if (AabbOverlap(pos, bbMin.Value, bbMax.Value,
                                    e.Position, body.Value.min, body.Value.max))
                    {
                        _gateReject = $"body {e.Class?.Name}#{e.Handle} kind={kind} " +
                            $"@({e.Position.x:0.##},{e.Position.y:0.##},{e.Position.z:0.##}) " +
                            $"bb=({body.Value.min.x:0},{body.Value.min.y:0},{body.Value.min.z:0})..(" +
                            $"{body.Value.max.x:0},{body.Value.max.y:0},{body.Value.max.z:0})";
                        return false;
                    }
                }
                else
                {
                    float dx = e.Position.x - pos.x, dy = e.Position.y - pos.y;
                    if (dx * dx + dy * dy < r2)
                    {
                        _gateReject = $"body-r {e.Class?.Name}#{e.Handle} kind={kind} " +
                            $"@({e.Position.x:0.##},{e.Position.y:0.##},{e.Position.z:0.##}) d<{legacyRadius}";
                        return false;
                    }
                }
            }
            return true;
        }

        // ---- FUN_005e7400: candidate box vs the pending order-ghost registry ----
        // Native walks the +0xf1 list (stride 0x24), skips the record whose handle is the object
        // being placed (param_4) and dead records (classid/meshid == -1), and AABB-tests the
        // ghost mesh's bbox at the ghost pos — same axes/scales/strict-< as FUN_005e7000.
        bool OrderGhostsClear(Vector3 pos, Vector3? bbMin, Vector3? bbMax, float legacyRadius) =>
            OrderGhostsClear(pos, bbMin, bbMax, legacyRadius, PlacingHandle);

        bool OrderGhostsClear(Vector3 pos, Vector3? bbMin, Vector3? bbMax, float legacyRadius,
            int exceptHandle)
        {
            float r2 = legacyRadius * legacyRadius;
            foreach (var kv in OrderGhosts)
            {
                if (kv.Key == exceptHandle) continue;
                var g = kv.Value;
                if (bbMin.HasValue && bbMax.HasValue && g.HasBBox)
                {
                    if (AabbOverlap(pos, bbMin.Value, bbMax.Value, g.Pos, g.BBoxMin, g.BBoxMax))
                    {
                        _gateReject = $"order-ghost #{kv.Key} @({g.Pos.x:0.##},{g.Pos.y:0.##},{g.Pos.z:0.##})";
                        return false;
                    }
                }
                else
                {
                    float dx = g.Pos.x - pos.x, dy = g.Pos.y - pos.y;
                    if (dx * dx + dy * dy < r2)
                    {
                        _gateReject = $"order-ghost-r #{kv.Key} @({g.Pos.x:0.##},{g.Pos.y:0.##},{g.Pos.z:0.##})";
                        return false;
                    }
                }
            }
            return true;
        }

        // ---- check_ghost_coll's engine surface (TCL:CTclObjMethods::CheckGhostColl,
        // FUN_0067b090 — walks the same +0xf1 order-ghost registry) ----
        // The item's standing order-ghost body in native coords: min/max corners of the
        // branch-selected model bbox anchored at the confirmed ghost pos. False = no standing
        // ghost for the handle (the native loop finds no record → Tcl gets "0").
        public bool TryGetOrderGhostBody(int handle, out Vector3 min, out Vector3 max)
        {
            min = max = default;
            if (!OrderGhosts.TryGetValue(handle, out var g)) return false;
            // unresolved-model ghosts (headless) use the nominal building silhouette
            Vector3 bMin = g.HasBBox ? g.BBoxMin : new Vector3(-200f, -350f, -200f);
            Vector3 bMax = g.HasBBox ? g.BBoxMax : new Vector3(200f, 0f, 200f);
            min = new Vector3(bMin.x * BodyScaleXY + g.Pos.x,
                              bMin.y * BodyScaleXY + g.Pos.y,
                              bMin.z * BodyScaleZ + g.Pos.z);
            max = new Vector3(bMax.x * BodyScaleXY + g.Pos.x,
                              bMax.y * BodyScaleXY + g.Pos.y,
                              bMax.z * BodyScaleZ + g.Pos.z);
            return true;
        }

        // `check_ghost_coll objects` — the buildup-time authoritative re-check
        // (prod_buildup_check, z_work_common.tcl:345-371): true when the item's standing ghost
        // body overlaps any other collidable object body (the FUN_005e7000 law; the item itself
        // is the exclusion).
        public bool OrderGhostCollidesObjects(int handle)
        {
            if (!OrderGhosts.TryGetValue(handle, out var g)) return false;
            Vector3? bbMin = g.HasBBox ? g.BBoxMin : null;
            Vector3? bbMax = g.HasBBox ? g.BBoxMax : null;
            float legacyRadius = g.AsBox ? BoxClearRadius : BuildingClearRadius;
            return !BodiesClear(g.Pos, bbMin, bbMax, legacyRadius, handle);
        }

        // The native body-overlap primitive (FUN_005e7000:117-140): two anchored boxes in raw
        // model units, scaled into native coords (x/y ×0.005 DAT_00736fcc, z ×0.01 DAT_00736fe0),
        // strict-< intersection on all three axes.
        static bool AabbOverlap(Vector3 posA, Vector3 minA, Vector3 maxA,
                                Vector3 posB, Vector3 minB, Vector3 maxB)
        {
            return minB.x * BodyScaleXY + posB.x < maxA.x * BodyScaleXY + posA.x
                && minA.x * BodyScaleXY + posA.x < maxB.x * BodyScaleXY + posB.x
                && minB.y * BodyScaleXY + posB.y < maxA.y * BodyScaleXY + posA.y
                && minA.y * BodyScaleXY + posA.y < maxB.y * BodyScaleXY + posB.y
                && minB.z * BodyScaleZ + posB.z < maxA.z * BodyScaleZ + posA.z
                && minA.z * BodyScaleZ + posA.z < maxB.z * BodyScaleZ + posB.z;
        }

        // An entity's body bbox (raw model units) — the native resolves the current display
        // mesh via the anim-class record (FUN_005e7000:81-115); the port resolves the class's
        // display model and reads the same baked CMesh bbox (frame 0 — the per-frame delta is
        // sub-slab for standing props). Cached per class. Null ⇒ headless / unmapped model.
        readonly Dictionary<string, (Vector3 min, Vector3 max)?> _bodyBBoxCache = new();
        (Vector3 min, Vector3 max)? EntityBodyBBox(DigglesTclEntity e) =>
            e.Class != null ? ClassBodyBBox(e.Class) : null;

        (Vector3 min, Vector3 max)? ClassBodyBBox(DigglesClass cls)
        {
            var lib = _ctx.ModelLibrary;
            if (lib == null) return null;
            if (_bodyBBoxCache.TryGetValue(cls.Name, out var hit)) return hit;
            (Vector3 min, Vector3 max)? res = null;
            var model = ResolveClassModel(cls);
            if (model != null && lib.TryGetMeshBBox(model.Value.obj, model.Value.clip, 0,
                    out var mn, out var mx))
                res = (mn, mx);
            _bodyBBoxCache[cls.Name] = res;
            return res;
        }

        // ---- FUN_005e76a0: the terrain-fit test of the candidate body ----
        // Exact constants from Diggles.exe.rdata:
        // DAT_00737540 = 12.0 (map-border margin, tiles) DAT_00737114 = 0.95 (minZ shrink)
        // DAT_00737078 = 0.4 (probe extent factor) DAT_00737138 = 1.0 (boundary tol)
        // DAT_00737058 = 0.25 / DAT_00737070 = 0.375 (probe nudges) DAT_0073752c = 8.0 (sub grid)
        // Law: (1) pos must lie ≥ 12 tiles inside the map bounds (:37-42); (2) five no-embed
        // probes — the model's top row + mid row sampled at 0.4×/0.7× extents must read air
        // (h ≤ probe z; air ⇔ z ≥ h, the ant-farm frame) (:46-96); (3) the four base-row corners
        // (maxY row × {minX,maxX} × {minZ·0.95, maxZ}) must each sit at a rock/air boundary:
        // a corner deep in agreement-checked air (h ≤ z−1.0 — hanging over a shaft/pit) or deep
        // in rock (h > z+1.0 — buried in the face) rejects; the neighbour probes (y±nudge,
        // x±0.375) make a boundary corner (flat floor line: solid row under open pocket) escape
        // via disagreement — the flat meadow passes, a footprint over a dug shaft or a lone
        // pillar top rejects (:97-…, the 4-case do-while). Headless without terrain ⇒ pass
        // (nothing to test against); fabricated tile worlds use the per-tile height fallback.
        bool TerrainFits(Vector3 pos, Vector3? bbMinRaw, Vector3? bbMaxRaw)
        {
            var t = _ctx.Terrain;
            if (t == null) return true;
            var nav = t.Nav;

            // (1) the border margin (DAT_00737540 = 12.0 tiles off every map edge)
            if (pos.x < 12f || pos.x > t.Width - 12f ||
                pos.y < 12f || pos.y > t.Height - 12f)
            {
                _gateReject = $"terrain-border ({pos.x:0.#},{pos.y:0.#}) map={t.Width}x{t.Height}";
                return false;
            }

            // Nominal footprint when the class model is unresolved (headless): the established
            // 2-slab / ±1-tile building silhouette (base at y=0, per the.3db convention —
            // feuerstelle.standard = {−241..241, −155..1.5, −241..241}).
            Vector3 bbMin = bbMinRaw ?? new Vector3(-200f, -350f, -200f);
            Vector3 bbMax = bbMaxRaw ?? new Vector3(200f, 0f, 200f);

            float h(float x, float y)
            {
                if (nav != null) return nav.GetHmapSub(Mathf.RoundToInt(x * 8f), Mathf.RoundToInt(y * 8f));
                int tx = Mathf.RoundToInt(x * 8f) >> 3, ty = Mathf.RoundToInt(y * 8f) >> 3;
                if (tx < 0 || tx >= t.Width || ty < 0 || ty >= t.Height) return 15f;
                return t.GetHmap(tx, ty);
            }

            float minX = bbMin.x * BodyScaleXY, maxX = bbMax.x * BodyScaleXY;
            float minY = bbMin.y * BodyScaleXY, maxY = bbMax.y * BodyScaleXY;
            float minZ = bbMin.z * BodyScaleZ * 0.95f, maxZ = bbMax.z * BodyScaleZ;

            // (2) the five no-embed probes — top/mid rows at 0.4×/0.7× extents, all must be air
            float topY = pos.y + minY, baseY = pos.y + maxY, midY = (topY + baseY) * 0.5f;
            float zNear = pos.z + minZ * 0.4f, zFar = pos.z + maxZ * 0.4f;
            float x04a = pos.x + minX * 0.4f, x04b = pos.x + maxX * 0.4f;
            bool Embed(float px, float py, float pz, int n)
            {
                float hp = h(px, py);
                if (hp <= pz) return false;
                _gateReject = $"terrain-embed#{n} @({px:0.##},{py:0.##}) h={hp:0.#} > z={pz:0.##}";
                return true;
            }
            if (Embed(x04a, topY, zNear, 1) || Embed(x04a, topY, zFar, 2)) return false;
            if (Embed(x04b, topY, zNear, 3) || Embed(x04b, topY, zFar, 4)) return false;
            if (Embed((x04a + x04b) * 0.5f, topY, zNear, 5)) return false;
            float z07 = pos.z + minZ * 0.7f;
            if (Embed(pos.x + minX * 0.7f, midY, z07, 6)) return false;
            if (Embed(pos.x + maxX * 0.7f, midY, z07, 7)) return false;

            // (3) the base-corner boundary law
            var cxs = new[] { pos.x + minX, pos.x + maxX };
            var czs = new[] { pos.z + minZ, pos.z + maxZ };
            foreach (float cx in cxs)
                foreach (float cz in czs)
                {
                    float hc = h(cx, baseY);
                    if (hc <= cz)
                    {
                        // corner in air: reject when ≥1 slab deep and the neighbourhood agrees
                        if (hc <= cz - 1f
                            && h(cx, baseY + 0.25f) <= cz
                            && h(cx + 0.375f, baseY) <= cz
                            && h(cx - 0.375f, baseY) <= cz)
                        {
                            _gateReject = $"terrain-corner-air @({cx:0.##},{baseY:0.##}) h={hc:0.#} <= cz-1={cz - 1f:0.##}";
                            return false;
                        }
                    }
                    else
                    {
                        // corner in rock: reject when buried ≥1 slab and the neighbourhood agrees
                        if (hc > cz + 1f
                            && h(cx, baseY - 0.375f) > cz
                            && h(cx + 0.375f, baseY) > cz
                            && h(cx - 0.375f, baseY) > cz)
                        {
                            _gateReject = $"terrain-corner-rock @({cx:0.##},{baseY:0.##}) h={hc:0.#} > cz+1={cz + 1f:0.##}";
                            return false;
                        }
                    }
                }
            return true;
        }

        // Back-compat surface (the old public validity probe): the mode-2 spot test on the given
        // point — settle + the branch the depth budget selects + the composite clearance,
        // without mutating the controller state.
        public bool ComputeValid(Vector3 nativePos)
        {
            if (!TrySettleSupport(ref nativePos))
            {
                LastReject = $"settle: no support column at ({nativePos.x:0.##},{nativePos.y:0.##},{nativePos.z:0.##})";
                return false;
            }
            bool fits = nativePos.z + _buildingDepthExtent < BuildingDepthBudget;
            bool ok = SpotClear(nativePos, !fits);
            LastReject = ok ? null : (fits ? "bld " : "box ") + _gateReject;
            return ok;
        }

        // AI placement queries use the same class body, terrain and overlap laws as the
        // interactive building ghost, but have no live placing object and never fall back to
        // the crate body. FUN_0042dcc0 settles an ai_validlocation probe first; FUN_0042eaf0
        // already enumerates settled floor points and calls the body tests directly.
        public bool ComputeAiBuildingValid(DigglesClass cls, Vector3 nativePos, bool settle)
        {
            if (settle && !TrySettleSupport(ref nativePos))
            {
                LastReject = $"settle: no support column at ({nativePos.x:0.##},{nativePos.y:0.##},{nativePos.z:0.##})";
                return false;
            }

            var body = ClassBodyBBox(cls);
            Vector3? bbMin = body?.min;
            Vector3? bbMax = body?.max;
            float depthExtent = bbMax.HasValue
                ? bbMax.Value.z * BodyScaleZ
                : DefaultBuildingDepthExtent;
            if (nativePos.z + depthExtent > BuildingDepthBudget)
            {
                LastReject = $"building-depth {nativePos.z + depthExtent:0.##}>{BuildingDepthBudget:0.##}";
                return false;
            }

            _gateReject = null;
            bool clear = TerrainFits(nativePos, bbMin, bbMax)
                && BodiesClear(nativePos, bbMin, bbMax, BuildingClearRadius, -1)
                && OrderGhostsClear(nativePos, bbMin, bbMax, BuildingClearRadius, -1);
            LastReject = clear ? null : "ai-bld " + _gateReject;
            return clear;
        }

        // ---- the confirm (FUN_00531310) ----
        public bool Confirm()
        {
            if (!Active) return false;
            if (!Valid) return false;   // no-op while the spot is invalid (FUN_00531310:102)
            if (!_ctx.Entities.TryGetValue(PlacingHandle, out var e)) { Cancel(); return false; }

            // 1. the persistent order ghost stays at the confirmed spot (FUN_0046a880,:225);
            // removed by hide_obj_ghost when the chain places the real building (prod_buildup
            // z_work_common.tcl:416, unpackfrombox genericprod.tcl:204) or cancels the order
            // (prod_buildup_check failure:364, stop_prod z_procs.tcl:95/:124).
            RegisterOrderGhost(e);

            // The move confirm (the ALT-pack flow): the ghost spot becomes the deferred order pos and
            // the pack is armed with the rec+0x3c move flag (FUN_005242b0:329 + FUN_005ee250's
            // order write). The building packs down where it stands; set_boxed's boxing side
            // effects then convert the order into the unpack at OrderPos (FUN_005edea0:122-138).
            if (Mode == PlacementMode.MoveBuilt)
            {
                if (e.ProdSlots.Count > 0)
                {
                    var keys = new List<string>(e.ProdSlots.Keys);
                    foreach (var k in keys) e.ProdSlots[k] = 0;   // the case-2 slot clear (:39)
                }
                e.OrderPos = GhostPos;
                e.OrderAsBox = false;                  // a moved building re-builds as production
                e.PackRequested = true;
                e.BuildupRequested = false;
                e.MoveAfterPack = true;                // rec+0x3c = 1
                e.OrderGhostShown = true;
                e.VarsScope.Vars["_prod_enabled"] = "1";
                ResetMode();
                return true;
            }

            // 2/3. the native branch = CSelection::IsSelected(placingHandle) (:227-236 via
            // FUN_005e35a0 — the membership test on the selection list).
            if (_ctx.SelectedHandle != PlacingHandle)
            {
                // not selected ⇒ the selection is a gnome (the crate/item is its cargo): run the
                // unchanged eventgen for selection[0] (:248 — only one gnome gets the order even
                // in multi-select) with the native confirm txtpar (:237/:263) and fire the
                // resulting evt_task_buildup / evt_task_putdown. For the ItemPutdown mode this
                // same round hits the material/tool in-inventory branch → evt_task_putdown
                // -subject1 <item> -pos1 <spot> (eventgen.tcl:263-267/:313-317) — the walk-there-
                // and-bend chain (evt_task_putdown_proc → putdown_tasklist → walk_near_item +
                // benda/beam_from_inv_to_pos/bendb, z_events.tcl:738-815 + z_procs.tcl:708-733).
                // The carrier chain
                // (walk → unpack_pos_unfixed → prod_buildup_new → prod_buildup_rep → bau anims →
                // unpackfrombox) is already-loaded original Tcl; it re-verifies the crate is in
                // `this`'s inventory (z_procs.tcl:1177-1179) and aborts cleanly otherwise.
                string txt = AsBox ? "-command -placement -asbox" : "-command -placement -asprod";
                if (SnapTargetHandle > 0)
                    txt += " -snapobj " + SnapTargetHandle.ToString(System.Globalization.CultureInfo.InvariantCulture);
                try
                {
                    DigglesTclEngine.EvtGenRun(_ctx, _ctx.SelectedHandle, PlacingHandle, GhostPos, txt);
                    DigglesTclEngine.EvtGenFire(_ctx, _ctx.SelectedHandle);
                }
                catch (System.Exception ex) { Debug.LogWarning($"[Placement] confirm eventgen: {ex.Message}"); }
            }
            else
            {
                // the crate itself is selected (its window's build button started this) ⇒ write
                // the deferred order directly into the record (:316-331 = FUN_005ee250: the
                // ordered pos → +0x30, asbox → +0x2d, +0x28 = 1 unpack request, +0x3d word =
                // 0x100 → "order ghost outstanding"); ProdDispatcher's kind-1 collector sends
                // the best free gnome via evt_autoprod_unpack. (+0x2b, the deferred-tick
                // companion flag, is unmodeled — the MoveBuilt tick is its only reader.)
                e.OrderPos = GhostPos;
                e.OrderAsBox = AsBox;
                e.UnpackRequested = true;
                e.OrderGhostShown = true;
            }

            // exit placement mode (+0x124 = 0 / +0x128 = -1, FUN_00531310:304-305/:332-333/:348-349)
            ResetMode();
            return true;
        }

        // hide_obj_ghost <obj> (FUN_0069b440:58 → FUN_0046a960): drop the standing order ghost +
        // the rec+0x3e "order outstanding" flag (spec §10.2).
        public void HideOrderGhost(int handle)
        {
            OrderGhosts.Remove(handle);
            if (_orderGhostGos.TryGetValue(handle, out var go))
            {
                _orderGhostGos.Remove(handle);
                DestroyGo(go);
            }
            if (_ctx.Entities.TryGetValue(handle, out var e)) e.OrderGhostShown = false;
        }

        // ---- UI seam (spec §10.4 — widgets authored natively in the Unity editor) ----

        // The button surface of the original building window for one object (build: FUN_00521c80,
        // click: FUN_005242b0) — a POCO snapshot the UI binds to.
        public sealed class BuildingOrderButtons
        {
            public bool ShowBuild;      // boxed crate → "unpack"/build button (icons/unpack, FUN_00521c80:361)
            public bool BuildPending;   // the S1 variant while an order is outstanding
            public bool ShowResume;     // placed, not built → "aufbau"/resume button (:503)
            public bool ResumePending;  // S1aufbau while rec+0x3e or +0x2a pending (:504-518)
            public bool ShowPack;       // placed + built → "pack" button (FUN_005242b0 case 2)
        }

        public BuildingOrderButtons QueryButtons(int objHandle)
        {
            var b = new BuildingOrderButtons();
            if (!_ctx.Entities.TryGetValue(objHandle, out var e)) return b;
            bool boxed = e.VarsScope.Vars.TryGetValue("_boxed", out var v) && (v == "1" || v == "true");
            b.ShowBuild = boxed;
            b.BuildPending = boxed && (e.UnpackRequested || e.OrderGhostShown);
            b.ShowResume = !boxed && !e.BuildupComplete;
            b.ResumePending = b.ShowResume && (e.BuildupRequested || e.OrderGhostShown);
            b.ShowPack = !boxed && e.BuildupComplete;
            return b;
        }

        // The crate window's build button (FUN_005242b0 case 1,:292-303): production re-enabled
        // (+0x64 = 1) + enter placement mode (FUN_00530e80(obj, 1, 0)). The native window only
        // exists for the selected object, and Confirm's deferred-vs-carrier branch is the
        // selection test (FUN_00531310:227-236) — so the button establishes that precondition.
        public bool OrderBuild(int objHandle)
        {
            if (!_ctx.Entities.TryGetValue(objHandle, out var e)) return false;
            e.VarsScope.Vars["_prod_enabled"] = "1";   // +0x64 = 1 (:299)
            _ctx.SelectedHandle = objHandle;            // the window is the selection's window
            return EnterPlacement(objHandle);
        }

        // The half-built building's resume ("aufbau") button (FUN_005242b0 case 4,:341-349):
        // the u16 write at +0x29 = 0x100 arms the buildup byte and zeroes the pack byte; +0x3d
        // word = 0; +0x64 = 1. The single-player dispatcher then sends any free gnome a kind-1
        // job (FUN_00436440:33-52 → evt_autoprod_unpack, whose not-boxed branch resumes
        // prod_buildup_rep from the building's saved buildup_step) — player-driven
        // interrupted-construction recovery (spec §6).
        public void OrderResume(int objHandle)
        {
            if (!_ctx.Entities.TryGetValue(objHandle, out var e)) return;
            e.PackRequested = false;                   // byte +0x29 = 0 (the u16 write,:342)
            e.BuildupRequested = true;                 // byte +0x2a = 1
            e.OrderGhostShown = false;                 // +0x3d word = 0 (:344)
            e.VarsScope.Vars["_prod_enabled"] = "1";   // +0x64 = 1 (:347)
        }

        // The built building's pack button — the direct variant of FUN_005242b0 case 2
        // (:304-330): clear every production slot (the FUN_005edc70 loop,:39), then arm
        // the pack request. The dispatcher sends a gnome (evt_autoprod_pack → prod_builddown →
        // packtobox). The chain re-reads the byte mid-flight (z_work_common.tcl:444) — clearing
        // it via CancelPack is the native cancel.
        public void OrderPack(int objHandle)
        {
            if (!_ctx.Entities.TryGetValue(objHandle, out var e)) return;
            if (e.ProdSlots.Count > 0)
            {
                var keys = new List<string>(e.ProdSlots.Keys);
                foreach (var k in keys) e.ProdSlots[k] = 0;
            }
            e.PackRequested = true;                    // u16 +0x29 = 1 (:316; byte +0x2a = 0 with it)
            e.BuildupRequested = false;
            e.MoveAfterPack = false;
            e.OrderGhostShown = false;                 // +0x3d word = 0 (:318)
            e.VarsScope.Vars["_prod_enabled"] = "1";   // +0x64 = 1 (:312)
        }

        // The armed pack button's second click — the native window relabels the S1packtobox
        // button to "Cancel" while the request is armed (FUN_00521c80:743-757: S1packtobox icon,
        // btn+0x5c = 1 active lamp, tooltip "Cancel") and the cancel drops the request byte: an
        // already-walking packer stands down at the prod_builddown gate (eventuell ist das
        // Abbauen inzwischen gecancelt, z_work_common.tcl:443-446).
        public void CancelPack(int objHandle)
        {
            if (!_ctx.Entities.TryGetValue(objHandle, out var e)) return;
            e.PackRequested = false;
            e.MoveAfterPack = false;
        }

        // The move variant of the pack button (ALT held — FUN_005242b0:314/:324-329: the key
        // byte FUN_004ecd40+0x11a = GetAsyncKeyState(VK_MENU), FUN_004ed0b0:74; the window
        // relabels the button to icons/unpack + "Umzug", FUN_00521c80:692-731): enter the ghost
        // placement for the built building (FUN_00530e80(obj, 1, 1)) and mark the record for the
        // deferred pack-then-unpack (rec+0x3c = 1,:329). The confirm stores the target pos and
        // arms the pack; once the building boxes, set_boxed's boxing side effects convert the
        // stored order into the unpack (the FUN_005edea0:122-138 idle-tick law).
        public bool OrderMove(int objHandle)
        {
            if (Active) return false;
            if (!_ctx.Entities.TryGetValue(objHandle, out var e)) return false;
            if (!e.BuildupComplete) return false;      // only a built building moves
            Mode = PlacementMode.MoveBuilt;
            PlacingHandle = objHandle;
            _ctx.SelectedHandle = objHandle;           // the window is the selection's window
            AsBox = false;
            _asBoxForced = false;
            GhostPos = e.Position;
            GhostYaw = 0f;
            Valid = false;
            BuildCursorGhost(e);
            return true;
        }

        // An item icon clicked in a gnome's inventory window: the original reaches placement via
        // eventgen's "placement" return (in MY inventory + -command without -placement:
        // boxed crate eventgen.tcl:164-182, material:260-274, tool/transport:34). Routing
        // the UI click through the unchanged generator keeps the decision in the Tcl (spec
        // §10.4); a boxed crate opens the building-ghost placement, a plain item the putdown-
        // place mode, and any other verb fires its event spec as usual (ALT+use etc. are the
        // caller's txtpar).
        // The inventory window is the gnome's window — the gnome is the selection (the native
        // precondition for the later confirm branch, where "placing handle not selected" routes
        // the order to the carrier, FUN_00531310:227-236).
        public bool ClickInventoryCrate(int gnomeHandle, int crateHandle)
        {
            if (!_ctx.Entities.TryGetValue(gnomeHandle, out var g)) return false;
            string verb;
            try { verb = DigglesTclEngine.EvtGenRun(_ctx, gnomeHandle, crateHandle, g.Position, "-command").Trim(); }
            catch (System.Exception ex) { Debug.LogWarning($"[Placement] inv-click eventgen: {ex.Message}"); return false; }
            if (verb == "placement")
            {
                // A boxed crate opens the full building-ghost placement (box/building swap,
                // depth budget). A plain item (Pilzhut, tools, …) coming back "placement" —
                // the native drag&drop arming (Hand.tga) — opens the lightweight putdown-place
                // mode instead: click → pick a world spot → the gnome
                // walks there and puts the item down; the confirm runs the same native
                // "-command -placement" eventgen round whose material/tool branch raises
                // evt_task_putdown -pos1 <spot>, eventgen.tcl:263-267/:313-317).
                bool boxed = _ctx.Entities.TryGetValue(crateHandle, out var it)
                    && it.VarsScope.Vars.TryGetValue("_boxed", out var bv) && (bv == "1" || bv == "true");
                if (!boxed) return EnterPutdownPlace(gnomeHandle, crateHandle);
                _ctx.SelectedHandle = gnomeHandle;
                return EnterPlacement(crateHandle);
            }
            DigglesTclEngine.EvtGenFire(_ctx, gnomeHandle);
            return false;
        }

        // ---- internals ----

        void ResetMode()
        {
            Mode = PlacementMode.None;
            PlacingHandle = 0;
            AsBox = false;
            _asBoxForced = false;
            SnapTargetHandle = -1;
            GhostYaw = 0f;
            Valid = false;
            LadderPreviewLength = 0f;
            _buildingDepthExtent = DefaultBuildingDepthExtent;
            _itemMeshBottomOffset = 0f;
            _ladderPreviewObject = null;
            _bbBuildingMin = _bbBuildingMax = _bbBoxMin = _bbBoxMax = null;
        }

        // The confirmed cursor ghost is repurposed as the standing order-ghost visual (the native
        // keeps a separate +0xf1 record; one translucent model at the ordered spot is the same
        // observable — and the persistent order ghost makes the same building-vs-kiste model
        // choice the confirmed +0xcd selected, FUN_00531310:126-219/:221-223). Headless (no
        // GameObject) keeps the data record only.
        void RegisterOrderGhost(DigglesTclEntity e)
        {
            // The registry record keeps the branch-selected body (FUN_005e7400 tests candidates
            // against the ghost's mesh — the box branch registers the kiste mesh).
            Vector3? gMin = AsBox ? _bbBoxMin : _bbBuildingMin;
            Vector3? gMax = AsBox ? _bbBoxMax : _bbBuildingMax;
            OrderGhosts[PlacingHandle] = new OrderGhost(
                ItemDropAnchorPosition(GhostPos), GhostYaw, AsBox, gMin, gMax);
            if (_orderGhostGos.TryGetValue(PlacingHandle, out var old)) DestroyGo(old);
            if (_cursorGhost != null)
            {
                TintGhost(_cursorGhost, OrderTint);
                _orderGhostGos[PlacingHandle] = _cursorGhost;
                _cursorGhost = null;
                _ghostBuildingGo = null;
                _ghostBoxGo = null;
            }
            else _orderGhostGos.Remove(PlacingHandle);
        }

        // CAIManager writes the same order-ghost record when ai_setbuildup arms a boxed
        // production site (FUN_0042d9b0: the +0x3d word). Opponent orders have no interactive
        // cursor GameObject, but must still reserve their body against later placement scans.
        public void RegisterAiOrderGhost(DigglesTclEntity entity, Vector3 nativePos)
        {
            var body = ClassBodyBBox(entity.Class);
            OrderGhosts[entity.Handle] = new OrderGhost(
                nativePos,
                0f,
                false,
                body?.min,
                body?.max);
            entity.OrderGhostShown = true;
        }

        // The translucent cursor ghost. The native enter (FUN_00530e80) loads two model pairs
        // into the placement singleton:
        // • +0xb8/+0xbc = the unpacked building's display model, resolved from the target
        // class's anim tables (class+0x10[obj+0x230 % class+0x14] anim-class id +
        // class+0x18[obj+0x230 % class+0x1c] variant → DAT_00772400+0x5c8[classid] →
        // rec+0x4c + variant*0x3c → models[0];:26-68, stored:93/:109/:168/:174) — i.e. the
        // same visual unpackfrombox later produces, not the crate the object currently shows;
        // • +0xc4/+0xc8 = the global "kiste"."standard" box model (loaded once by
        // FUN_00460460:310-337) for the box-only state.
        // Port: the building model resolves through DigglesModelLibrary by the same path
        // RuntimeWorldSpawner.Spawn uses for the unboxed visual (class→TSV obj + default anim,
        // class_defaultanim override — deterministic per class, the port's variant-stable
        // equivalent); the kiste model is lib.GetClip("kiste","standard"). Both become children
        // of one ghost root, swapped per frame by the spot outcome (ApplyGhostVisual). No
        // colliders, so the cursor raycast never hits the ghost itself. Tinting goes through a
        // MaterialPropertyBlock (_BaseColor/_Color) — no material instantiation/leak; shaders
        // without either property simply ignore it (the ghost then shows untinted — UX-only).
        void BuildCursorGhost(DigglesTclEntity e)
        {
            _ghostBuildingGo = null;
            _ghostBoxGo = null;
            LadderPreviewLength = 0f;
            _ladderPreviewObject = null;
            _buildingDepthExtent = DefaultBuildingDepthExtent;
            _itemMeshBottomOffset = Mode == PlacementMode.ItemPutdown
                ? DigglesTclEngine.GetItemMeshBottomOffset(_ctx, e)
                : 0f;
            _bbBuildingMin = _bbBuildingMax = _bbBoxMin = _bbBoxMax = null;
            // The class bbox (the anim-class record's rec+0x44..0x58 the native reads —
            // FUN_0046b450:828 depth budget, FUN_005e7000/005e76a0 overlap/terrain): the baked
            // CMesh bbox of the display model's frame 0, raw model units. The kiste bbox is the
            // box branch's body (+0xc4 in the singleton). Depth extent = max.z × DAT_00736fe0.
            var model = ResolveClassModel(e);
            _ladderPreviewObject = model?.obj;
            if (model != null && _ctx.ModelLibrary != null
                && _ctx.ModelLibrary.TryGetMeshBBox(model.Value.obj, model.Value.clip, 0,
                       out var bMin, out var bMax))
            {
                _bbBuildingMin = bMin;
                _bbBuildingMax = bMax;
                _buildingDepthExtent = Mathf.Max(0f, bMax.z * BodyScaleZ);
            }
            if (_ctx.ModelLibrary != null
                && _ctx.ModelLibrary.TryGetMeshBBox("kiste", "standard", 0, out var kMin, out var kMax))
            {
                _bbBoxMin = kMin;
                _bbBoxMax = kMax;
            }
            if (_ctx.SpawnParent == null) return;   // headless — the data-only ghost
            var root = new GameObject($"PlacementGhost_{e.Class?.Name}#{e.Handle}");
            root.transform.SetParent(_ctx.SpawnParent, false);

            _ghostBuildingGo = BuildGhostModel(root.transform, "BuildingModel", model);
            if (_ghostBuildingGo == null && e.GameObject != null)
            {
                // Class outside the model map — fall back to the legacy render-only clone of the
                // object so the player still sees something under the cursor (renderers force-
                // enabled: a carried crate renders disabled while contained).
                var clone = Object.Instantiate(e.GameObject, root.transform);
                clone.name = "BuildingModel(clone)";
                clone.transform.localPosition = Vector3.zero;
                clone.transform.localRotation = Quaternion.identity;
                foreach (var c in clone.GetComponentsInChildren<Collider>(true)) c.enabled = false;
                foreach (var r in clone.GetComponentsInChildren<Renderer>(true)) r.enabled = true;
                _ghostBuildingGo = clone;
            }
            _ghostBoxGo = BuildGhostModel(root.transform, "KisteModel", ("kiste", "standard"));

            if (_ghostBuildingGo == null && _ghostBoxGo == null) { DestroyGo(root); return; }
            _cursorGhost = root;
            root.transform.position = _ctx.SpawnParent.TransformPoint(
                RuntimeWorldSpawner.NativeToLocal(ItemDropAnchorPosition(GhostPos)));
            ApplyGhostVisual();
        }

        // FUN_0060ad40 registers leiter_a..i by bbox height and FUN_0060b560 selects the longest
        // entry whose threshold fits scanSpan+1. Mode 4 uses that selected mesh for the ghost.
        void ApplyLadderPreview(float span)
        {
            LadderPreviewLength = span;
            var library = _ctx.ModelLibrary;
            if (library == null
                || !DigglesTclEngine.TrySelectLadderPresentation(
                    library,
                    span,
                    out string objectName,
                    out Vector3 minimum,
                    out Vector3 maximum))
                return;
            _bbBuildingMin = minimum;
            _bbBuildingMax = maximum;
            _buildingDepthExtent = Mathf.Max(0f, maximum.z * BodyScaleZ);
            if (string.Equals(
                    _ladderPreviewObject,
                    objectName,
                    System.StringComparison.Ordinal))
                return;

            if (_cursorGhost == null)
            {
                _ladderPreviewObject = objectName;
                return;
            }
            var replacement = BuildGhostModel(
                _cursorGhost.transform,
                "BuildingModel",
                (objectName, "standard"));
            if (replacement == null) return;
            DestroyGo(_ghostBuildingGo);
            _ghostBuildingGo = replacement;
            _ladderPreviewObject = objectName;
        }

        // Per-frame model choice + tint (FUN_0046b450 mode 2): valid box spot → the kiste model
        // (+0xc4/+0xc8 branch,:833-834); an invalid un-forced spot reverts to the building
        // model even when the box branch was active (:1963-1967, the +0xf0==0 revert). The tint
        // is the draw's valid flag — a valid box spot is green (the tutorial's "1 1 1").
        void ApplyGhostVisual()
        {
            bool showBox = _ghostBoxGo != null &&
                ((AsBox && (Valid || _asBoxForced)) || _ghostBuildingGo == null);
            if (_ghostBuildingGo != null) _ghostBuildingGo.SetActive(!showBox);
            if (_ghostBoxGo != null) _ghostBoxGo.SetActive(showBox);
            if (_cursorGhost != null) TintGhost(_cursorGhost, Valid ? ValidTint : InvalidTint);
        }

        // Resolve the class's unboxed display model — the spawner's model path (the same
        // resolution RuntimeWorldSpawner.Spawn / `set_boxed 0` restore use): TSV class→obj with
        // its default anim, a class_defaultanim that targets the same obj wins, then the
        // class-named obj fallback. Returns (obj, clip-without-prefix) for DigglesModelLibrary.
        (string obj, string clip)? ResolveClassModel(DigglesTclEntity e) =>
            ResolveClassModel(e.Class);

        (string obj, string clip)? ResolveClassModel(DigglesClass? cls)
        {
            var lib = _ctx.ModelLibrary;
            if (lib == null || cls == null) return null;
            var rc = lib.ResolveClass(cls.Name);
            string obj, anim;
            if (rc != null && !string.IsNullOrEmpty(rc.Value.Obj))
            {
                obj = rc.Value.Obj;
                anim = !string.IsNullOrEmpty(cls.DefaultAnim) &&
                       cls.DefaultAnim!.StartsWith(obj + ".", System.StringComparison.Ordinal)
                    ? cls.DefaultAnim!
                    : obj + "." + (string.IsNullOrEmpty(rc.Value.DefaultAnim) ? "standard" : rc.Value.DefaultAnim);
            }
            else if (!string.IsNullOrEmpty(cls.DefaultAnim) && cls.DefaultAnim!.IndexOf('.') > 0)
            {
                int dot = cls.DefaultAnim!.IndexOf('.');
                obj = cls.DefaultAnim!.Substring(0, dot);
                anim = cls.DefaultAnim!;
            }
            else if (lib.GetObjDb(cls.Name) != null)
            {
                obj = cls.Name;
                anim = obj + ".standard";
            }
            else return null;
            string clip = anim.StartsWith(obj + ".", System.StringComparison.Ordinal)
                ? anim.Substring(obj.Length + 1) : anim;
            return (obj, clip);
        }

        // One ghost model child: frame 0 of the resolved clip with the engine's inner-model
        // transform (Q_x(-90) + the canonical model→world scale — RuntimeWorldSpawner.BuildInner).
        GameObject? BuildGhostModel(Transform parent, string name, (string obj, string clip)? model)
        {
            if (model == null || _ctx.ModelLibrary == null) return null;
            var clip = _ctx.ModelLibrary.GetClip(model.Value.obj, model.Value.clip);
            if (clip?.Frames == null || clip.Frames.Length == 0 || clip.Frames[0] == null) return null;
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            go.transform.localScale = Vector3.one * DigglesModelLibrary.ModelToWorldScale;
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = clip.Frames[0];
            var mr = go.AddComponent<MeshRenderer>();
            if (clip.Materials != null && clip.Materials.Length > 0) mr.sharedMaterials = clip.Materials;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return go;
        }

        void TintGhost(GameObject go, Color c)
        {
            _mpb ??= new MaterialPropertyBlock();
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                r.GetPropertyBlock(_mpb);
                _mpb.SetColor("_BaseColor", c);
                _mpb.SetColor("_Color", c);
                r.SetPropertyBlock(_mpb);
            }
        }

        static void DestroyGo(GameObject? go)
        {
            if (go == null) return;
            if (Application.isPlaying) Object.Destroy(go);
            else Object.DestroyImmediate(go);
        }
    }
}
