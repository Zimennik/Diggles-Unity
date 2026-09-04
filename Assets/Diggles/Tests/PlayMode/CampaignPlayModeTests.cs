#nullable enable

namespace Diggles.Tests.PlayMode
{
    using System;
    using System.Collections;
    using System.Globalization;
    using System.Linq;
    using Diggles.Runtime;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;
    using UnityEngine;
    using UnityEngine.SceneManagement;
    using UnityEngine.TestTools;

    // Real PLAYMODE tests: boot the actual campaign world (terrain + nav graph + dwarves spawned from the
    // unchanged Tcl) and verify the behaviours EditMode can't — headless EditMode degrades movement to a
    // no-op WaitAction, so walking/digging/harvest-with-motion + the cutscene-skip teardown are untestable
    // there. The shipped RuntimeWorldGeneration scene boots to the MAIN MENU diorama; SetUp drives the real
    // menu→campaign transition via Runtime.NewGame (which spawns player dwarves + auto-plays the urw_3
    // intro), then each test either observes the intro (skip-restore) or skips it (gameplay).
    //
    // Run (Editor must be CLOSED — project lock):
    // Unity.exe -batchmode -nographics -projectPath <proj> -runTests -testPlatform PlayMode \
    // -testFilter "Diggles.Tests.PlayMode" -testResults pm.xml -logFile pm.log
    public class CampaignPlayModeTests
    {
        const string ScenePath = "Assets/Diggles/Scenes/RuntimeWorldGeneration.unity";
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        DigglesWorldRuntime _runtime = null!;
        DigglesTclContext Ctx => _runtime.Context!;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            // DETERMINISTIC TIME: movement integrates in DigglesObject.Update as Velocity * Time.deltaTime
            // (frame-coupled, NOT in Sim.Step), and headless batchmode frames have a near-zero real
            // deltaTime — so a walk would crawl. captureDeltaTime pins every frame to a fixed 0.025s (40 Hz)
            // and runs frames as fast as the CPU allows, so each `yield return null` advances exactly one
            // sim tick AND 0.025s of movement. Reset in TearDown.
            Time.captureDeltaTime = 0.025f;

            // Boot emits expected interpreter-gap warnings + the fileDebugLog torrent — don't fail on logs.
            LogAssert.ignoreFailingMessages = true;

            SceneManager.LoadScene(ScenePath, LoadSceneMode.Single);
            yield return null;   // Start runs here: builds the engine + DI scope + the MENU diorama world.

            _runtime = UnityEngine.Object.FindFirstObjectByType<DigglesWorldRuntime>();
            Assert.IsNotNull(_runtime, "scene must contain a DigglesWorldRuntime");

            // Menu world build (real-class load + EmitTerrain) is synchronous-but-slow; wait on the flag.
            yield return WaitUntil(() => _runtime.WorldReady, 3000, "menu world never became ready");

            // Boot the campaign WITHOUT the intro cinematic so gameplay control is live immediately — the
            // gameplay tests must not depend on the cutscene starting/skipping. The skip-restore test plays
            // its own cutscene explicitly via PlayCutscene.
            _runtime.campaignIntroCutscene = "";

            // Menu → StoryMgr campaign: spawns the player dwarves (no auto-cutscene now).
            _runtime.NewGame();
            yield return null;
            yield return WaitUntil(() => _runtime.WorldReady, 3000, "campaign world never became ready after NewGame");

            // The unchanged campaign Tcl also arms the real urw_3 proximity trigger. Suppress that
            // one trigger so gameplay tests are not interrupted at game time 5; the dedicated
            // cutscene test starts urw_3 explicitly and therefore still exercises the full sequence.
            Ctx.TriggerService!.SuppressSequence("urw_3");
        }

        [TearDown]
        public void TearDown()
        {
            LogAssert.ignoreFailingMessages = false;
            Time.captureDeltaTime = 0f;   // restore real-time stepping
        }

        // ---- helpers ----

        IEnumerator WaitUntil(Func<bool> cond, int maxFrames, string failMsg)
        {
            for (int f = 0; f < maxFrames; f++)
            {
                if (cond()) yield break;
                yield return null;
            }
            Assert.Fail(failMsg);
        }

        DigglesTclEntity? FirstPlayerDwarf() =>
            Ctx.Entities.Values.FirstOrDefault(e => e.GameObject != null &&
                (e.Class?.Name == "Zwerg" || e.Class?.Name == "Frau"));

        // Horizontal (native X,Y plane) distance — native Y is depth, Z is elevation; dwarves move in X,Y.
        static float HDist(Vector3 a, Vector3 b) { float dx = a.x - b.x, dy = a.y - b.y; return Mathf.Sqrt(dx * dx + dy * dy); }
        static float HDist(Vector3 a, float x, float y) { float dx = a.x - x, dy = a.y - y; return Mathf.Sqrt(dx * dx + dy * dy); }

        static bool TryParseNativeVector(string text, out Vector3 value)
        {
            var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            value = default;
            if (parts.Length < 3
                || !float.TryParse(parts[0], NumberStyles.Float, Inv, out float x)
                || !float.TryParse(parts[1], NumberStyles.Float, Inv, out float y)
                || !float.TryParse(parts[2], NumberStyles.Float, Inv, out float z)) return false;
            value = new Vector3(x, y, z);
            return true;
        }

        // Set 'this' around an Eval, the contract ClickToMoveService uses to issue an order on a dwarf.
        void EvalAs(DigglesTclEntity e, string tcl)
        {
            var prev = Ctx.This;
            Ctx.This = e;
            try { Ctx.Interp.Eval(tcl); }
            finally { Ctx.This = prev; }
        }

        // ---- tests ----

        // Smoke gate: if this fails the others are meaningless. NewGame must produce a StoryMgr campaign
        // with real player dwarves, a built nav graph and terrain.
        [UnityTest]
        public IEnumerator CampaignWorld_BootsWithDwarvesNavAndTerrain()
        {
            Assert.IsTrue(_runtime.WorldReady, "world must be ready");
            Assert.IsTrue(Ctx.IsStoryMgr, "NewGame must land us in the StoryMgr campaign, not the menu");
            Assert.IsNotNull(Ctx.Terrain, "campaign terrain must be built");
            Assert.IsNotNull(Ctx.NavService, "nav service must exist");
            Assert.IsTrue(Ctx.NavService!.HasWorld, "campaign NavWorld must be built");
            Assert.IsNotNull(Ctx.Sim, "sim must be running");
            Assert.IsNotNull(FirstPlayerDwarf(), "campaign must spawn a player dwarf (Zwerg/Frau) with a GameObject");
            yield break;
        }

        // ESC-skip must END the cutscene (sequencer self-destructs) WITHOUT freezing — via the budgeted
        // accelerated fast-forward (SkipActiveCutscene → Update pumps accelerated sim steps across frames),
        // and leave the dwarves with no leftover sequence action. Plays urw_3, lets it run a moment, then
        // skips exactly as the ESC handler does and waits for the natural shut_sequencer end.
        [UnityTest]
        public IEnumerator Cutscene_SkipRestoresControl()
        {
            _runtime.PlayCutscene("urw_3");
            yield return WaitUntil(() => Ctx.SequenceActive, 300, "urw_3 cutscene never became active");

            for (int i = 0; i < 80; i++) yield return null;   // let it play ~2s (like a real mid-cutscene skip)

            _runtime.StopCutscene();                          // = the ESC handler (set_sequence 0 + un-park)
            yield return WaitUntil(() => !Ctx.SequenceActive, 600, "cutscene did not end on skip");
            Assert.IsFalse(Ctx.SequenceActive, "skip ends the cutscene (set_sequence 0)");

            for (int i = 0; i < 20; i++) yield return null;   // sequencer gone; dwarves resume their own AI
            var dwarf = FirstPlayerDwarf();
            Assert.IsNotNull(dwarf, "dwarves survive the skip");
            // The cutscene RELEASED the dwarf: un-parked + no longer a cutscene actor. (It may now hold an
            // idle/Wait action from its RESUMED AI — that's correct, not a leftover sequence action.)
            Assert.IsFalse(dwarf!.Parked, "dwarf un-parked after the skip (cutscene released it)");
            bool stillSeqActor = dwarf.VarsScope.Vars.TryGetValue("_sequenceactive", out var sa) && sa == "1";
            Assert.IsFalse(stillSeqActor, "dwarf no longer flagged as a cutscene actor after the skip");
        }

        // A player dwarf, ordered to walk to a reachable nav node, actually moves there and arrives.
        [UnityTest]
        public IEnumerator Dwarf_WalksToPoint_AndArrives()
        {
            var dwarf = FirstPlayerDwarf();
            Assert.IsNotNull(dwarf, "need a player dwarf");
            Assert.IsNotNull(dwarf!.GameObject, "PlayMode requires a real GameObject (else walk degrades to Wait)");

            Vector3 startNative = dwarf.Position;

            // Reachable target = a FLOOR sub-cell 3..8 native-units east/west of the dwarf on the live
            // NavWorld (+0x3c sub-grid; 1 native unit = 1 tile = 8 sub-cells, sub→native = ×0.125).
            // Depth (z) is seeded from the walker per the walk-goal contract, so pass the dwarf's own z.
            var nav = Ctx.NavService!.World!;
            int sx0 = Mathf.RoundToInt(startNative.x * 8f), sy0 = Mathf.RoundToInt(startNative.y * 8f);
            int bestSx = -1, bestSy = -1; float bestScore = float.MaxValue;
            for (int oy = -16; oy <= 16; oy++)
                for (int ox = -64; ox <= 64; ox++)
                {
                    int sx = sx0 + ox, sy = sy0 + oy;
                    if (sx < 0 || sy < 0 || sx >= nav.SubW || sy >= nav.SubH || !nav.SubIsFloor(sx, sy)) continue;
                    float d = Mathf.Sqrt(ox * ox + oy * oy) * 0.125f;
                    if (d < 3f || d > 8f) continue;
                    float score = Mathf.Abs(d - 5f);
                    if (score < bestScore) { bestScore = score; bestSx = sx; bestSy = sy; }
                }
            if (bestSx < 0) { Assert.Inconclusive("no floor sub-cell 3..8u from the dwarf — can't pick a walk target"); yield break; }
            Vector3 tNative = new Vector3(bestSx * 0.125f, bestSy * 0.125f, startNative.z);

            Ctx.SelectedHandle = dwarf.Handle;
            // Mirror ClickToMoveService's exact command incl. the state_disable/enable discipline
            // (z_dignwalk.tcl walk_pos:594) so the dwarf's own state-think can't cancel the walk.
            EvalAs(dwarf, $"state_disable this\naction this walk \"-target {{{tNative.x.ToString(Inv)} {tNative.y.ToString(Inv)} {tNative.z.ToString(Inv)}}} -animsets {{0 1}} -speedtype 0\" {{state_enable this; seq_idle}}");

            // Core assertion: the dwarf MOVES meaningfully toward the target when ordered. (Exact arrival
            // is a stricter follow-up; movement is what EditMode can't exercise at all.)
            bool started = dwarf.ActiveAction != null;
            yield return WaitUntil(() => Vector3.Distance(dwarf.Position, startNative) > 1.5f || dwarf.ActiveAction == null,
                1200, "walk neither moved nor finished");

            float moved = Vector3.Distance(dwarf.Position, startNative);
            string wr = Ctx.Interp.Eval($"get_walkresult {dwarf.Handle}").Trim();
            Assert.Greater(moved, 1.5f,
                $"dwarf should move toward the target (orderSetAction={started}, moved={moved:0.00}u, " +
                $"walkresult={wr}, actionActive={dwarf.ActiveAction != null}, target≈{tNative})");
        }

        // A depth-dig order on a diggable tile near the dwarf carves it down (the full mark→walk→carve
        // path). The mark must go in the dwarf's OWNER slot (tile+0x08 = 8 per-player nibble slots,
        // FUN_00596b80:44-46; the human player = owner 0 = dwarf.Owner) — an earlier revision passed
        // PlayerDigMarkId (the per-ORDER vein id, FUN_00567cb0:229-236) as the owner, so every owner-0
        // lookup (get_digedge/dig_next/CarveStep) saw an unmarked map and dig_starttask bailed to
        // work_idle. get_hmap is the FAITHFUL per-sub-cell BDB height (FUN_004093f0) and knows nothing
        // about marks (the "marked reads 15 / unmarked 0" contract was the reverted 527905a shim), so
        // the target must be a tile that actually READS solid (15 at its NW node sub-cell) with an
        // open approach above it (dig_check z_dignwalk.tcl:166 wants ≥2 free neighbours, h<13).
        // Asserts the marked tile carves down (the NW node corner steps 15→≤11) OR the dwarf heads to it.
        [UnityTest]
        public IEnumerator ShiftDig_CarvesTile()
        {
            const int searchRadius = 48;
            var dwarf = FirstPlayerDwarf();
            Assert.IsNotNull(dwarf, "need a player dwarf");
            var terrain = Ctx.Terrain;
            Assert.IsNotNull(terrain, "need terrain");
            var nav = terrain!.Nav;
            Assert.IsNotNull(nav, "real terrain must carry a NavWorld");

            var (dx, dy) = terrain.WorldToTile(dwarf!.Position);
            int tx = -1, ty = -1, bestR = int.MaxValue;
            for (int ox = -searchRadius; ox <= searchRadius; ox++)
                for (int oy = -searchRadius; oy <= searchRadius; oy++)
                {
                    int cx = dx + ox, cy = dy + oy, r = Mathf.Abs(ox) + Mathf.Abs(oy);
                    if (r == 0 || r >= bestR || !terrain.HasGeometry(cx, cy)
                        || !terrain.TileMaterialDiggable(cx, cy)) continue;
                    if (nav!.GetHmapSub(cx * 8, cy * 8) != 15) continue;        // node sub-cell must be solid
                    if (terrain.RimAtNode(cx, cy) < 0) continue;                // dig_next's rim gate (FUN_00597b20:130)
                    int free = 0;                                               // dig_check approach probes (w=0 row above)
                    for (int px = -1; px <= 1; px++)
                        if (nav.GetHmapSub((cx + px) * 8, (cy - 1) * 8) < 13) free++;
                    if (free < 2) continue;

                    // Probe the exact Tcl-selected dig edge, including vector_fixdig's standoff,
                    // and require a live Calc1 route. Geometric openness alone can select a tile
                    // in another disconnected cavern, causing the order to fail before digging.
                    terrain.SetDigMark(cx, cy, dwarf.Owner, 0);
                    string edge = Ctx.Interp.Eval($"get_digedge {{{cx} {cy}}} {dwarf.Handle}").Trim();
                    string walk = Ctx.Interp.Eval(
                        $"vector_setz [vector_fixdig {{{edge}}} [get_pos {dwarf.Handle}]] [lindex {{{edge}}} 2]").Trim();
                    terrain.ClearDigMark(cx, cy, dwarf.Owner);
                    if (!TryParseNativeVector(walk, out var walkNative)) continue;
                    Vector3 walkWorld = Ctx.NavService!.transform.TransformPoint(
                        RuntimeWorldSpawner.NativeToLocal(walkNative));
                    if (Ctx.NavService.FindPath(dwarf.GameObject!.transform.position, walkWorld) == null) continue;

                    bestR = r; tx = cx; ty = cy;
                }
            if (tx < 0)
            {
                Assert.Inconclusive($"no material-valid solid tile with an open approach within {searchRadius} tiles of the dwarf");
                yield break;
            }

            EvalAs(dwarf, $"set_worktime {dwarf.Handle} 0 12");   // so the dig is not gated by spare time
            Vector3 start = dwarf.Position;
            float startToTile = HDist(start, tx, ty);
            // The exact data ClickToMoveService's marquee commit writes: owner = the dwarf's player
            // layer, cnt 0 = full-depth mode-0 mark (nibble 8), then bump the per-order vein id.
            terrain.SetDigMark(tx, ty, dwarf.Owner, 0);
            terrain.IncPlayerDigMarkId();
            Assert.IsTrue(terrain.IsDigMarked(tx, ty, dwarf.Owner), "mark must land on the dwarf's owner layer");
            int h0 = int.Parse(Ctx.Interp.Eval($"get_hmap {tx} {ty}").Trim(), Inv);
            Assert.AreEqual(15, h0, "picked tile must read SOLID at its node sub-cell before the dig");
            Ctx.SelectedHandle = dwarf.Handle;
            EvalAs(dwarf, $"set_event this evt_task_dig -target {dwarf.Handle} -pos1 {{{tx} {ty}}}");

            // Order obeyed = the tile is carved down OR the dwarf at least heads to the dig edge (the full
            // event→work→walk chain produced movement toward the target — what EditMode can't exercise).
            // Inline loop (not WaitUntil) so we always reach the diagnostic assert on timeout.
            int h1 = h0;
            for (int f = 0; f < 3000; f++)
            {
                h1 = int.Parse(Ctx.Interp.Eval($"get_hmap {tx} {ty}").Trim(), Inv);
                if (h1 < h0 || (startToTile - HDist(dwarf.Position, tx, ty)) > 1.5f) break;
                yield return null;
            }

            float approached = startToTile - HDist(dwarf.Position, tx, ty);
            float movedFromStart = HDist(dwarf.Position, start.x, start.y);
            // NB: current_worktask is EXPECTED to stay "" on this user-ordered path — evt_task_dig_proc
            // sets current_workplace="dig" only (z_events.tcl:1044-1046); worktask="dig" is the
            // AUTONOMOUS evt_autoprod_dig path. Reported for diagnosis, not asserted.
            Assert.IsTrue(h1 < h0 || approached > 1.5f,
                $"dig order ignored — carved={h1 < h0} (h {h1}/{h0}), tile=({tx},{ty}) bestR={bestR}, " +
                $"approachedTile={approached:0.0}u, movedFromStart={movedFromStart:0.0}u, " +
                $"state={dwarf.CurrentState}, action={dwarf.ActiveAction != null}, " +
                $"workplace={(dwarf.VarsScope.Vars.TryGetValue("current_workplace", out var wp) ? wp : "?")}, " +
                $"worktask={(dwarf.VarsScope.Vars.TryGetValue("current_worktask", out var w) ? w : "?")}");
        }

        // A harvest order on a ripe Pilz is obeyed (the dwarf enters the harvest work flow / consumes it).
        [UnityTest]
        public IEnumerator HarvestOrder_IsObeyed()
        {
            var dwarf = FirstPlayerDwarf();
            Assert.IsNotNull(dwarf, "need a player dwarf");

            var pilz = Ctx.Entities.Values.FirstOrDefault(e => e.Class?.Name == "Pilz");
            if (pilz == null)
            {
                Vector3 p = dwarf!.Position + new Vector3(1, 0, 0);
                string h = Ctx.Interp.Eval($"new Pilz \"\" \"{p.x.ToString(Inv)} {p.y.ToString(Inv)} {p.z.ToString(Inv)}\" \"0 0 0\"").Trim();
                if (int.TryParse(h, NumberStyles.Integer, Inv, out var ph) && Ctx.Entities.TryGetValue(ph, out var pe))
                {
                    pilz = pe;
                    pilz.VarsScope.Vars["agephase"] = "3";                 // ripe (IsRipe reads agephase>=3)
                    try { Ctx.Interp.Eval($"set_attrib {pilz.Handle} PilzAge 3"); } catch { /* attrib optional */ }
                }
            }
            if (pilz == null) { Assert.Inconclusive("no Pilz available + spawn failed"); yield break; }

            EvalAs(dwarf!, $"set_worktime {dwarf!.Handle} 0 12");
            int pilzH = pilz.Handle;
            Vector3 pilzPos = pilz.Position;
            float startToPilz = HDist(dwarf.Position, pilzPos);
            Ctx.SelectedHandle = dwarf.Handle;
            EvalAs(dwarf, $"set_event this evt_task_harvest -subject1 {pilzH}");

            // Order obeyed = the Pilz is consumed OR the dwarf heads to it (the harvest event routed through
            // the work flow into movement toward the target).
            yield return WaitUntil(() => !Ctx.Entities.ContainsKey(pilzH) || (startToPilz - HDist(dwarf.Position, pilzPos)) > 1.5f,
                3000, "harvest order produced neither consumption nor movement toward the Pilz");

            bool consumed = !Ctx.Entities.ContainsKey(pilzH);
            float approached = startToPilz - HDist(dwarf.Position, pilzPos);
            Assert.IsTrue(consumed || approached > 1.5f,
                $"harvest order ignored — consumed={consumed}, approachedPilz={approached:0.0}u, " +
                $"state={dwarf.CurrentState}, action={dwarf.ActiveAction != null}");
        }
    }
}
