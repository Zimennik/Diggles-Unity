#nullable enable

namespace Diggles.Tests.PlayMode
{
    using System;
    using System.Collections;
    using System.Linq;
    using Diggles.Runtime;
    using Diggles.Runtime.Save;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;
    using UnityEngine;
    using UnityEngine.SceneManagement;
    using UnityEngine.TestTools;
    using VContainer;
    using VContainer.Unity;

    // Phase C exit gate (slice C7): prove in PLAYMODE that loading real original saves through the
    // production load service yields a playable session — the world swaps atomically, restored
    // dwarves resume acting, the clock advances, and production continues. EditMode proves staging;
    // only PlayMode proves the live swap + presentation + simulation composition.
    //
    // Run (Editor must be CLOSED — project lock):
    // Unity.exe -batchmode -projectPath <proj> -runTests -testPlatform PlayMode \
    // -testFilter "OriginalSaveActivationPlayModeTests" -testResults pm-save.xml -logFile pm-save.log
    public class OriginalSaveActivationPlayModeTests
    {
        const string ScenePath = "Assets/Diggles/Scenes/RuntimeWorldGeneration.unity";

        // Directory holding player-quicksave fixtures these gates copy into Gamesave/ when absent.
        static string SaveFixtureDir =>
            Environment.GetEnvironmentVariable("DIGGLES_SAVE_FIXTURES") ?? System.IO.Path.GetTempPath();

        DigglesWorldRuntime _runtime = null!;
        IOriginalSaveGameLoadService _loadService = null!;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            // Same deterministic-time law as CampaignPlayModeTests: pin frames to 0.025 s so each
            // `yield return null` advances one 40 Hz sim tick regardless of headless frame speed.
            Time.captureDeltaTime = 0.025f;
            LogAssert.ignoreFailingMessages = true;

            SceneManager.LoadScene(ScenePath, LoadSceneMode.Single);
            yield return null;

            _runtime = UnityEngine.Object.FindFirstObjectByType<DigglesWorldRuntime>();
            Assert.IsNotNull(_runtime, "scene must contain a DigglesWorldRuntime");
            yield return WaitUntil(() => _runtime.WorldReady, 3000, "menu world never became ready");

            // A live ACTIVE session must exist before a load can replace it (the load service
            // resolves save names against the active session's data root).
            _runtime.campaignIntroCutscene = "";
            _runtime.NewGame();
            yield return null;
            yield return WaitUntil(() => _runtime.WorldReady, 3000, "campaign world never became ready");

            LifetimeScope scope = UnityEngine.Object.FindFirstObjectByType<LifetimeScope>();
            Assert.IsNotNull(scope, "scene must contain the game lifetime scope");
            _loadService = scope.Container.Resolve<IOriginalSaveGameLoadService>();
        }

        [TearDown]
        public void TearDown()
        {
            LogAssert.ignoreFailingMessages = false;
            Time.captureDeltaTime = 0f;
        }

        [UnityTest]
        public IEnumerator TutorialLoadsAndPlays()
        {
            DigglesTclContext before = _runtime.Context!;

            OriginalSaveGameLoadResult result = _loadService.LoadGame("save_tutorial");
            Assert.That(result.EntityCount, Is.EqualTo(484));
            yield return null; // first ordinary post-commit tick: deferred events dispatch here.

            DigglesTclContext ctx = _runtime.Context!;
            Assert.That(ctx, Is.Not.SameAs(before), "the runtime must observe the swapped session");

            // Movement baseline IMMEDIATELY after the load: a restored mid-flight walk resumes
            // and ARRIVES within the first seconds (dwarf 51's archived walk target is 0.45
            // units out) — sampling after a settle window used to miss that resumption and
            // flagged an archive-faithful sparetime idle as "frozen".
            Vector3[] dwarfStarts = PlayerDwarves(ctx).Select(e => e.Position).ToArray();
            Assert.That(dwarfStarts, Is.Not.Empty, "the tutorial save must contain player dwarves");

            // Let the post-load hitches pass before measuring the CLOCK: heavy world-build
            // frames hit the sim driver's catch-up cap and its anti-spiral backlog drop
            // (EngineTickService), so the first wall seconds advance the clock at < 1×.
            for (int frame = 0; frame < 200; frame++) yield return null;
            double loadedTime = ctx.Sim!.GameTime;

            // ~30 simulated seconds at the pinned 0.025 s frame step.
            for (int frame = 0; frame < 1200; frame++) yield return null;

            Assert.That(ctx.Sim!.GameTime - loadedTime, Is.GreaterThan(20.0),
                "the restored clock must advance under ordinary ticking");
            Vector3[] dwarfNow = PlayerDwarves(ctx).Select(e => e.Position).ToArray();
            bool anyMoved = dwarfStarts.Zip(dwarfNow, (a, b) => (a - b).sqrMagnitude > 0.01f)
                .Any(moved => moved);
            Assert.That(anyMoved, Is.True,
                "at least one restored dwarf must resume moving within 30 sim seconds");
        }

        [UnityTest]
        public IEnumerator CampaignSaveLoadsAndPlays()
        {
            OriginalSaveGameLoadResult result = _loadService.LoadGame("save_SaveGame");
            Assert.That(result.EntityCount, Is.GreaterThan(0));
            yield return null;

            DigglesTclContext ctx = _runtime.Context!;
            double loadedTime = ctx.Sim!.GameTime;
            Assert.That(ctx.StoryEvents, Is.Not.Empty,
                "a campaign save must restore StoryMgr events");

            for (int frame = 0; frame < 400; frame++) yield return null;
            Assert.That(ctx.Sim!.GameTime - loadedTime, Is.GreaterThan(8.0),
                "the campaign clock must advance under ordinary ticking");
        }

        [UnityTest]
        public IEnumerator StoryHeavyQuicksaveLoadsAndPlays()
        {
            OriginalSaveGameLoadResult result = _loadService.LoadGame("quick_Quicksave_20201220_1512");
            Assert.That(result.EntityCount, Is.GreaterThan(300),
                "the story-heavy quicksave must restore its full population");
            yield return null;

            DigglesTclContext ctx = _runtime.Context!;
            double loadedTime = ctx.Sim!.GameTime;
            for (int frame = 0; frame < 400; frame++) yield return null;
            Assert.That(ctx.Sim!.GameTime - loadedTime, Is.GreaterThan(8.0),
                "the quicksave clock must advance under ordinary ticking");
        }

        // D5: a FRESH campaign (no baseline archive) saved through the player backend must
        // reload into the identical runtime state and keep playing. This exercises the
        // no-baseline Assemble writer end-to-end in the live scene: camera + viewBegin
        // synthesis, gzip write, reparse, stage, activation.
        [UnityTest]
        public IEnumerator NewCampaignSaveReloadsIdentically()
        {
            DigglesTclContext ctx = _runtime.Context!;
            // Save inside the [3.5 s, 5 s] window: AFTER the founder init timers (zwerg.tcl
            // `-attime 3`: gender/clothes/frau reskin) but BEFORE Trigger_Urw_006 matures at
            // t=5 and starts the urw_3 opening cinematic — the capture faithfully refuses a
            // mid-cutscene save (the original blocks saving during sequences too).
            for (int frame = 0; frame < 168; frame++) yield return null;
            yield return WaitUntil(
                () => !ctx.SequenceActive && ctx.This == null && ctx.CurrentEvent == null,
                40,
                "the fresh campaign never reached a safe save boundary");

            IGameSaveBackend? backend = ctx.SaveGames;
            Assert.That(backend, Is.Not.Null, "the save backend must be bound after NewGame");

            int entitiesBefore = ctx.Entities.Count;
            double timeBefore = ctx.Sim!.GameTime;
            (int Handle, Vector3 Position)[] dwarfBefore = PlayerDwarves(ctx)
                .OrderBy(e => e.Handle)
                .Select(e => (e.Handle, e.Position))
                .ToArray();
            Assert.That(dwarfBefore, Is.Not.Empty);

            string savedPath = backend!.Save("d5_newcampaign_gate");
            try
            {
                Assert.That(System.IO.File.Exists(savedPath), Is.True,
                    "the backend must write the fresh-campaign archive");

                OriginalSaveGameLoadResult result = _loadService.LoadGame("d5_newcampaign_gate");

                // Read the restored clock BEFORE yielding — the first ordinary frame after the
                // swap already steps the restored sim by one 0.025 s tick.
                DigglesTclContext reloaded = _runtime.Context!;
                Assert.That(reloaded, Is.Not.SameAs(ctx), "the reload must swap the session");
                Assert.That(result.EntityCount, Is.EqualTo(entitiesBefore),
                    "every fresh-campaign entity must survive the round trip");
                Assert.That(reloaded.Sim!.GameTime, Is.EqualTo(timeBefore),
                    "the fresh-campaign clock must reload bit-exactly");
                yield return null;

                (int Handle, Vector3 Position)[] dwarfAfter = PlayerDwarves(reloaded)
                    .OrderBy(e => e.Handle)
                    .Select(e => (e.Handle, e.Position))
                    .ToArray();
                Assert.That(dwarfAfter.Select(d => d.Handle).ToArray(),
                    Is.EqualTo(dwarfBefore.Select(d => d.Handle).ToArray()),
                    "the founder population must reload with identical ids");
                for (int i = 0; i < dwarfBefore.Length; i++)
                {
                    Assert.That(
                        (dwarfAfter[i].Position - dwarfBefore[i].Position).magnitude,
                        Is.LessThan(1e-4f),
                        $"dwarf {dwarfBefore[i].Handle} position must survive the round trip");
                }

                double loadedTime = reloaded.Sim!.GameTime;
                for (int frame = 0; frame < 400; frame++) yield return null;
                Assert.That(reloaded.Sim!.GameTime - loadedTime, Is.GreaterThan(8.0),
                    "the reloaded fresh campaign must keep playing");

                // Presentation must survive the round trip too — the manual test found
                // every restored entity spawning as a wireframe placeholder while all sim-level
                // asserts stayed green. A healthy restored world keeps a real-model majority.
                int restoredModels = 0, restoredEmpty = 0;
                foreach (DigglesTclEntity entity in reloaded.Entities.Values)
                {
                    GameObject? gameObject = entity.GameObject;
                    if (gameObject == null) continue;
                    bool hasMesh = false;
                    foreach (MeshFilter filter in
                             gameObject.GetComponentsInChildren<MeshFilter>(true))
                        if (filter.sharedMesh != null &&
                            !RuntimeWorldSpawner.IsFallbackMesh(filter.sharedMesh))
                        { hasMesh = true; break; }
                    if (hasMesh) restoredModels++; else restoredEmpty++;
                }
                Assert.That(restoredModels, Is.GreaterThan(restoredEmpty),
                    $"restored presentation must keep a real-model majority " +
                    $"(models={restoredModels}, empty/placeholder={restoredEmpty})");
            }
            finally
            {
                // The Gamesave directory is SHARED with the corpus gates — a leftover test
                // save would trip their artifact-leak invariant. DIGGLES_D5_KEEP=1 preserves
                // a copy in %TEMP% for the original-exe acceptance harness.
                if (System.IO.File.Exists(savedPath))
                {
                    if (Environment.GetEnvironmentVariable("DIGGLES_D5_KEEP") == "1")
                    {
                        System.IO.File.Copy(
                            savedPath,
                            System.IO.Path.Combine(
                                System.IO.Path.GetTempPath(),
                                "diggles-d5-freshcampaign.sav"),
                            overwrite: true);
                    }
                    System.IO.File.Delete(savedPath);
                }
            }
        }

        // Loads an arbitrary archive given via DIGGLES_PM_LOAD (absolute path) in the LIVE
        // scene and reports the restored presentation — the bridge between "works in batch"
        // and "all placeholders in the player's interactive session".
        [UnityTest]
        public IEnumerator EnvironmentSaveLoadsWithModels()
        {
            string? target = Environment.GetEnvironmentVariable("DIGGLES_PM_LOAD");
            if (string.IsNullOrEmpty(target))
            {
                Assert.Ignore("DIGGLES_PM_LOAD is not set — diagnostic-only test.");
                yield break;
            }

            OriginalSaveGameLoadResult result = _loadService.LoadGame(target);
            DigglesTclContext reloaded = _runtime.Context!;
            yield return null;

            int models = 0, empty = 0;
            foreach (DigglesTclEntity entity in reloaded.Entities.Values)
            {
                GameObject? gameObject = entity.GameObject;
                if (gameObject == null) continue;
                bool hasMesh = false;
                foreach (MeshFilter filter in
                         gameObject.GetComponentsInChildren<MeshFilter>(true))
                    if (filter.sharedMesh != null &&
                        !RuntimeWorldSpawner.IsFallbackMesh(filter.sharedMesh))
                    { hasMesh = true; break; }
                if (hasMesh) models++; else empty++;
            }
            Debug.Log($"[DIAG-PM] {System.IO.Path.GetFileName(target)}: " +
                $"entities={result.EntityCount} models={models} empty={empty} " +
                $"IsStoryMgr={reloaded.IsStoryMgr}");
            Assert.That(models, Is.GreaterThan(empty),
                $"restored presentation must keep a real-model majority " +
                $"(models={models}, empty/placeholder={empty})");
        }

        // user-flow gate: play PAST the early window, quicksave
        // through the player backend (the no-baseline assembler path), reload, and require
        // exactly what the interactive manual test found dead: the Tcl state machines revive
        // (think → the idle discipline produces actions again), founders keep their
        // set_objname names (obj+0xD8), and the dwarves stay selection-eligible with pick
        // colliders. Before the state-word/name fixes every one of these failed.
        [UnityTest]
        public IEnumerator PlayedCampaignQuickSaveReloadRevivesAiNamesAndSelection()
        {
            DigglesTclContext ctx = _runtime.Context!;
            // The exact user flow: let Trigger_Urw_006 start the urw_3 opening cinematic
            // (t=5), skip it like the player (ESC = StopCutscene), then play on.
            for (int frame = 0; frame < 150; frame++) yield return null;
            bool introStarted = false;
            for (int frame = 0; frame < 400 && !introStarted; frame++)
            {
                yield return null;
                introStarted = ctx.SequenceActive;
            }
            if (introStarted)
            {
                for (int frame = 0; frame < 40; frame++) yield return null;   // ~1 s in
                _runtime.StopCutscene();
                yield return WaitUntil(() => !ctx.SequenceActive, 600,
                    "the intro cinematic did not end on skip");
            }
            yield return WaitUntil(
                () => !ctx.SequenceActive && ctx.This == null && ctx.CurrentEvent == null,
                80, "the played campaign never reached a safe save boundary");

            // A REAL pre-save order (the exact click path): select every dwarf, walk them a few
            // tiles — this bakes a genuine mid-play state into the save (task vars, turned
            // facing, walk history) like the player's session, and provides the pre-save
            // baseline that orders DO work.
            DigglesTclEntity[] preDwarves = PlayerDwarves(ctx).OrderBy(e => e.Handle).ToArray();
            Assert.That(preDwarves, Is.Not.Empty);
            var orderService = new ClickToMoveService(ctx);
            foreach (DigglesTclEntity dwarf in preDwarves)
                ctx.Selection.BoxIncludeStep(dwarf.Handle);
            Vector3 preTarget = preDwarves[0].Position + new Vector3(6f, 0f, 0f);
            int preWalked = orderService.FireWalkOrder(preTarget);
            Assert.That(preWalked, Is.GreaterThan(0), "the pre-save walk order must fan out");
            Vector3[] preStarts = preDwarves.Select(d => d.Position).ToArray();
            bool preMoved = false;
            for (int frame = 0; frame < 480 && !preMoved; frame++)
            {
                yield return null;
                for (int i = 0; i < preDwarves.Length; i++)
                    if ((preDwarves[i].Position - preStarts[i]).sqrMagnitude > 1f)
                    { preMoved = true; break; }
            }
            Assert.That(preMoved, Is.True, "orders must move dwarves BEFORE the save (baseline)");
            ctx.Selection.Clear();
            yield return WaitUntil(
                () => !ctx.SequenceActive && ctx.This == null && ctx.CurrentEvent == null,
                40, "no safe boundary after the pre-save order");

            (string Name, string? State)[] before = PlayerDwarves(ctx)
                .OrderBy(e => e.Handle)
                .Select(e => (e.InstanceName, e.CurrentState))
                .ToArray();
            Assert.That(before, Is.Not.Empty);
            TestContext.WriteLine(
                "founder names before save: " +
                string.Join(", ", before.Select(d => $"'{d.Name}'")));
            Quaternion preFacing = ModelRotationOf(preDwarves[0]);

            IGameSaveBackend? backend = ctx.SaveGames;
            Assert.That(backend, Is.Not.Null);
            string? themeBefore = ctx.Audio?.CurrentTheme ?? ctx.Audio?.PrimaryTheme;
            bool musicBefore = ctx.Audio?.MusicPlaying ?? false;
            string savedPath = backend!.Save("wave3_userflow_gate");
            try
            {
                _loadService.LoadGame("wave3_userflow_gate");
                DigglesTclContext reloaded = _runtime.Context!;
                Assert.That(reloaded, Is.Not.SameAs(ctx), "the reload must swap the session");
                yield return null;

                // The native adaptive-sound manager is a load-surviving singleton: whatever
                // theme played before F5 keeps playing after (a.sav carries markers only).
                if (musicBefore)
                {
                    Assert.That(reloaded.Audio?.MusicPlaying, Is.True,
                        "music must survive the load swap");
                    Assert.That(reloaded.Audio?.CurrentTheme ?? reloaded.Audio?.PrimaryTheme,
                        Is.EqualTo(themeBefore), "the playing theme must survive the load swap");
                }

                DigglesTclEntity[] dwarves =
                    PlayerDwarves(reloaded).OrderBy(e => e.Handle).ToArray();
                Assert.That(dwarves, Is.Not.Empty);
                Assert.That(
                    dwarves.Select(d => d.InstanceName).ToArray(),
                    Is.EqualTo(before.Select(d => d.Name).ToArray()),
                    "founder names must survive the round trip unchanged (obj+0xD8)");
                Assert.That(dwarves.Any(d => d.CurrentState != null), Is.True,
                    "restored dwarves must carry a live state machine (obj+0x200/+0x204)");

                // Revival: a frozen dwarf never acquires an action; a thinking one re-enters
                // the idle discipline (wait/walk) within seconds. ~25 sim seconds.
                bool anyActed = false;
                for (int frame = 0; frame < 1000 && !anyActed; frame++)
                {
                    yield return null;
                    foreach (DigglesTclEntity dwarf in dwarves)
                        if (dwarf.ActiveAction != null) { anyActed = true; break; }
                }
                Assert.That(anyActed, Is.True,
                    "at least one restored dwarf must think and act again " +
                    "(state-machine revival after load)");

                // Selection eligibility — the manual test's band box found 0 candidates.
                int eligible = 0, withColliders = 0;
                foreach (DigglesTclEntity dwarf in dwarves)
                {
                    dwarf.VarsScope.Vars.TryGetValue("_cloaked", out string? cloaked);
                    dwarf.VarsScope.Vars.TryGetValue("_selectable", out string? selectable);
                    TestContext.WriteLine(
                        $"dwarf #{dwarf.Handle} '{dwarf.InstanceName}': owner={dwarf.Owner} " +
                        $"local={reloaded.LocalPlayer} kind='{dwarf.Class?.Kind}' " +
                        $"cloaked={cloaked ?? "-"} selectable={selectable ?? "-"} " +
                        $"eligible={ClickToMoveService.BoxEligible(dwarf, reloaded.LocalPlayer)} " +
                        $"go={(dwarf.GameObject == null ? "null" : dwarf.GameObject.activeInHierarchy ? "active" : "INACTIVE")}");
                    if (ClickToMoveService.BoxEligible(dwarf, reloaded.LocalPlayer)
                        && dwarf.GameObject != null && dwarf.GameObject.activeInHierarchy)
                        eligible++;
                    if (dwarf.GameObject != null &&
                        dwarf.GameObject.GetComponentInChildren<Collider>() != null)
                        withColliders++;
                }
                Assert.That(eligible, Is.GreaterThan(0),
                    "restored dwarves must stay band-select eligible");
                Assert.That(withColliders, Is.GreaterThan(0),
                    "restored dwarves must carry pick colliders");

                // Facing must survive: the first dwarf turned toward the pre-save walk target;
                // a reload that faces everyone at the spawn default is the manual test's
                Quaternion restoredFacing = ModelRotationOf(dwarves[0]);
                Assert.That(Quaternion.Angle(preFacing, restoredFacing), Is.LessThan(20f),
                    "the walked dwarf's facing must survive the round trip");

                // A boxed building must restore its crate pose (the manual test fireplace came
                // back visually unpacked).
                DigglesTclEntity? boxed = reloaded.Entities.Values.FirstOrDefault(e =>
                    e.VarsScope.Vars.TryGetValue("_boxed", out string? bx) &&
                    (bx == "1" || bx == "true"));
                if (boxed?.GameObject != null)
                {
                    DigglesAnimator? boxedAnimator =
                        boxed.GameObject.GetComponentInChildren<DigglesAnimator>();
                    Assert.That(boxedAnimator?.CurrentClip?.SourceObjName, Is.EqualTo("kiste"),
                        $"boxed {boxed.Class?.Name}#{boxed.Handle} must restore its crate pose");
                }

                // ORDERS after the reload — the manual test's core failure: select one dwarf,
                // fire the same click-path walk order, and require actual movement.
                var reloadedOrders = new ClickToMoveService(reloaded);
                DigglesTclEntity commanded = dwarves[0];
                reloaded.Selection.Clear();
                reloaded.Selection.BoxIncludeStep(commanded.Handle);
                Vector3 postTarget = commanded.Position + new Vector3(-6f, 0f, 0f);
                Assert.That(reloadedOrders.FireWalkOrder(postTarget), Is.GreaterThan(0),
                    "the post-reload walk order must reach the selected dwarf");
                Vector3 postStart = commanded.Position;
                bool postMoved = false;
                for (int frame = 0; frame < 800 && !postMoved; frame++)
                {
                    yield return null;
                    postMoved = (commanded.Position - postStart).sqrMagnitude > 1f;
                }
                Assert.That(postMoved, Is.True,
                    "a restored dwarf must OBEY a walk order (position must change)");
            }
            finally
            {
                if (System.IO.File.Exists(savedPath)) System.IO.File.Delete(savedPath);
            }
        }

        // (#12): loading the ORIGINAL's own tutorial save left visible deco
        // holes (the meadow reduced to bare ground). Census which archived objects never got a
        // GameObject and which classes they are — spawn-side loss, not archive-side.
        [UnityTest]
        public IEnumerator TutorialSaveSpawnsItsDecoObjects()
        {
            yield return SpawnCensus("save_tutorial");
        }

        [UnityTest]
        public IEnumerator BigCampaignSaveSpawnsItsDecoObjects()
        {
            yield return SpawnCensus("quick_Quicksave_20201220_1512");
        }

        // (save_test — the big late-game native save): mass-missing deco
        // (cave props, moss, rocks, the meadow) and broken production icons. The census names
        // exactly which classes fail to spawn or stay meshless.
        [UnityTest]
        public IEnumerator LateGameSaveTestSpawnsItsDeco()
        {
            yield return SpawnCensus("save_test");
        }

        // (marker-7 deco): the late-game save loaded with BARE dug caves —
        // every archived object body spawns (census above), but the moss/beams/walls/rock props
        // live in the marker-7 spatial index (B3 §3), which the port retained as bytes yet never
        // materialized ("empty allowed" was the fresh-world law). save_test carries 1867 such
        // seeds (Dummy_*/Urw_caves_*). They must now spawn as ctx.MapDecoSeeds — SEPARATE from
        // ctx.Entities so the save-capture object graph stays byte-identical to the archive and
        // the retained marker-7 bytes round-trip verbatim — with their classes resolving to models.
        [UnityTest]
        public IEnumerator LateGameSaveMaterializesMarker7DecoSeeds()
        {
            EnsureSaveFixture("save_test");
            _loadService.LoadGame("save_test");
            DigglesTclContext ctx = _runtime.Context!;
            for (int frame = 0; frame < 200; frame++) yield return null;

            System.Collections.Generic.List<DigglesTclEntity> seeds = ctx.MapDecoSeeds;
            Assert.That(seeds.Count, Is.GreaterThan(0),
                "marker-7 spatial records must materialize as dormant deco seeds (B3 §3) — a " +
                "loaded late-game world was showing bare tunnels with no moss/beams/walls");

            int materialized = 0, dormant = 0, withModel = 0, meshless = 0;
            var classes = new System.Collections.Generic.HashSet<string>();
            foreach (DigglesTclEntity seed in seeds)
            {
                Assert.That(seed.MapDecoSaveSeed, Is.True, "a materialized seed must carry the save-seed tag");
                Assert.That(seed.Class, Is.Not.Null, "every materialized seed must resolve its class");
                classes.Add(seed.Class!.Name);
                GameObject? go = seed.GameObject;
                if (go == null)
                {
                    dormant++;
                    Assert.That(ctx.Entities.ContainsValue(seed), Is.False,
                        "deco seeds must stay OUT of ctx.Entities (capture round-trips the retained bytes)");
                    continue;
                }
                materialized++;
                bool hasMesh = false;
                foreach (MeshFilter mf in go.GetComponentsInChildren<MeshFilter>(true))
                    if (mf.sharedMesh != null && !RuntimeWorldSpawner.IsFallbackMesh(mf.sharedMesh))
                    { hasMesh = true; break; }
                if (hasMesh) withModel++; else meshless++;
                // Seeds must NOT leak into the save-capture object graph (that would double them
                // against the retained marker-7 bytes and drop the retained spatial index).
                Assert.That(ctx.Entities.ContainsValue(seed), Is.False,
                    "deco seeds must stay OUT of ctx.Entities (capture round-trips the retained bytes)");
            }
            TestContext.WriteLine(
                $"[deco-seed] seeds={seeds.Count} distinctClasses={classes.Count} " +
                $"materialized={materialized} dormant={dormant} withModel={withModel} meshless={meshless}");
            Assert.That(materialized, Is.GreaterThan(0),
                "the LOD pump must materialize seed props near the restored camera");
            Assert.That(dormant, Is.GreaterThan(0),
                "distant marker-7 records must remain dormant instead of all 1867 loading synchronously");
            Assert.That(withModel, Is.GreaterThan(0),
                "at least one nearby marker-7 seed must resolve to a real model");
        }

        // 21:20 (the player's own port-written quicksave): phantom "gnomes"
        // on the meadow + the meadow deco meshes gone (the zero-latch = mann.anim0 bug), and a
        // dig order at x8 froze the editor. Census + a real dig-order soak on that exact file.
        [UnityTest]
        public IEnumerator UserQuicksave2119SpawnsDecoAndSurvivesDigging()
        {
            const string userSave = "quick_Quicksave_20260718_2119";
            string savePath = System.IO.Path.Combine(
                _runtime.Context!.DataRoot, "Gamesave", userSave + ".sav");
            if (!System.IO.File.Exists(savePath))
            {
                string kept = System.IO.Path.Combine(SaveFixtureDir, userSave + ".sav");
                Assume.That(System.IO.File.Exists(kept), $"user quicksave missing: {savePath}");
                System.IO.File.Copy(kept, savePath);
            }

            yield return SpawnCensus(userSave);
            DigglesTclContext ctx = _runtime.Context!;

            // No phantom dwarf meshes: the world must show exactly the archived Zwerg count of
            // 'mann/frau'-database models among VISIBLE renderers... approximated structurally:
            // no entity outside gnome kinds may carry a mann-database clip as its spawn pose.
            int phantomGnomes = 0;
            foreach (DigglesTclEntity e in ctx.Entities.Values)
            {
                if (e.Class?.Kind is "gnome" or "baby") continue;
                if (e.Class?.Name == "PseudoZwerg") continue;
                if (e.CurrentAnim != null &&
                    (e.CurrentAnim.StartsWith("mann.", StringComparison.Ordinal) ||
                     e.CurrentAnim.StartsWith("frau.", StringComparison.Ordinal)))
                {
                    phantomGnomes++;
                    TestContext.WriteLine(
                        $"[census] PHANTOM {e.Class?.Name}#{e.Handle} anim={e.CurrentAnim}");
                }
            }
            Assert.That(phantomGnomes, Is.Zero,
                "no deco object may spawn wearing a gnome model (zero-latch law)");

            // The player's dig marquee + order, then a fast soak: the editor froze here.
            var terrain = ctx.Terrain!;
            for (int ty = 33; ty <= 44; ty++)
                for (int tx = 257; tx <= 258; tx++)
                    terrain.SetDigMark(tx, ty, 0, 1);
            DigglesTclEntity[] dwarves = PlayerDwarves(ctx).OrderBy(e => e.Handle).ToArray();
            Assert.That(dwarves, Is.Not.Empty);
            var orders = new ClickToMoveService(ctx);
            ctx.Selection.Clear();
            ctx.Selection.BoxIncludeStep(dwarves[0].Handle);
            DigglesTclEngine.EvtGenRun(
                ctx, dwarves[0].Handle, 1, new Vector3(257.5f, 34f, 8f), "-command");
            DigglesTclEngine.EvtGenFire(ctx, dwarves[0].Handle);

            var wall = System.Diagnostics.Stopwatch.StartNew();
            long worstFrameMs = 0;
            for (int frame = 0; frame < 2400; frame++)
            {
                long beforeMs = wall.ElapsedMilliseconds;
                yield return null;
                long frameMs = wall.ElapsedMilliseconds - beforeMs;
                if (frameMs > worstFrameMs) worstFrameMs = frameMs;
                if (frame % 600 == 0)
                {
                    TestContext.WriteLine(
                        $"[dig] frame {frame} t={ctx.Sim!.GameTime:F0} worstFrame={worstFrameMs}ms");
                }
            }
            TestContext.WriteLine(
                $"[dig] soak done t={ctx.Sim!.GameTime:F0} worstFrame={worstFrameMs}ms");
            Assert.That(worstFrameMs, Is.LessThan(5000),
                "no single frame may stall for seconds during a dig soak (the 21:20 freeze)");
        }

        private IEnumerator SpawnCensus(string saveName)
        {
            _loadService.LoadGame(saveName);
            DigglesTclContext ctx = _runtime.Context!;
            for (int frame = 0; frame < 200; frame++) yield return null;

            int total = 0, withGo = 0, contained = 0;
            var missingByClass = new System.Collections.Generic.Dictionary<string, int>();
            var invisibleByClass = new System.Collections.Generic.Dictionary<string, int>();
            foreach (DigglesTclEntity e in ctx.Entities.Values)
            {
                total++;
                if (e.ContainedIn >= 0) { contained++; continue; }
                string cls = e.Class?.Name ?? "<null-class>";
                if (e.GameObject != null)
                {
                    withGo++;
                    // A spawned-but-meshless object is the manual test's invisible deco.
                    var mf = e.GameObject.GetComponentInChildren<MeshFilter>();
                    var smr = e.GameObject.GetComponentInChildren<SkinnedMeshRenderer>();
                    bool hasMesh = (mf != null && mf.sharedMesh != null &&
                                    mf.sharedMesh.vertexCount > 0)
                                   || (smr != null && smr.sharedMesh != null);
                    if (!hasMesh)
                    {
                        invisibleByClass.TryGetValue(cls, out int iv);
                        invisibleByClass[cls] = iv + 1;
                    }
                    continue;
                }
                missingByClass.TryGetValue(cls, out int n);
                missingByClass[cls] = n + 1;
            }
            foreach (var kv in invisibleByClass.OrderByDescending(kv => kv.Value))
                TestContext.WriteLine($"[census] MESHLESS {kv.Value,4} × {kv.Key}");
            var missing = missingByClass.OrderByDescending(kv => kv.Value).ToArray();
            TestContext.WriteLine(
                $"[census] entities={total} withGO={withGo} contained={contained} " +
                $"missingClasses={missing.Length}");
            foreach (var kv in missing)
                TestContext.WriteLine($"[census]   {kv.Value,4} × {kv.Key}");

            // The world-visible deco (Dummy_*) must spawn — the manual test hole. Invisible helper
            // classes (Info_Coll_*, FogRemover, ProductionManager…) legitimately have no model.
            int visibleMissing = missing
                .Where(kv => !kv.Key.StartsWith("Info_") && kv.Key != "FogRemover"
                    && kv.Key != "ProductionManager" && kv.Key != "<null-class>")
                .Sum(kv => kv.Value);
            Assert.That(visibleMissing, Is.Zero,
                "every world-visible archived object must get a spawned GameObject " +
                "(playtest 2026-07-18: the tutorial meadow deco never appeared)");
        }

        // Acceptance-artifact producer (not an assertion gate): plays a fresh campaign for a few
        // sim seconds and KEEPS the resulting archive in savework/ for the original-exe
        // acceptance harness (Run-SaveAcceptance.ps1 -RoundTrip). The other save-writing gates
        // delete their artifacts on teardown, so E2E acceptance needs this dedicated export.
        [UnityTest]
        public IEnumerator ExportFreshCampaignAcceptanceArtifact()
        {
            DigglesTclContext ctx = _runtime.Context!;
            IGameSaveBackend? backend = ctx.SaveGames;
            Assert.That(backend, Is.Not.Null, "the save backend must be bound after NewGame");

            // Let the campaign settle a few sim seconds so the artifact carries live state
            // (started idle discipline, event-ring history, armed timers). The t=5 intro
            // cinematic is skipped like the player (ESC) — saving inside it is not a safe
            // native boundary.
            for (int frame = 0; frame < 150; frame++) yield return null;
            bool introStarted = false;
            for (int frame = 0; frame < 400 && !introStarted; frame++)
            {
                yield return null;
                introStarted = ctx.SequenceActive;
            }
            if (introStarted)
            {
                for (int frame = 0; frame < 40; frame++) yield return null;
                _runtime.StopCutscene();
                yield return WaitUntil(() => !ctx.SequenceActive, 600,
                    "the intro cinematic did not end on skip");
            }
            yield return WaitUntil(
                () => !ctx.SequenceActive && ctx.This == null && ctx.CurrentEvent == null,
                80, "the fresh campaign never reached a safe save boundary");

            string savedPath = backend!.Save("w6_acceptance");
            Assert.That(System.IO.File.Exists(savedPath), Is.True,
                "the backend must write the acceptance archive");
            string kept = System.IO.Path.Combine(SaveFixtureDir, "w6_acceptance.sav");
            try
            {
                System.IO.File.Copy(savedPath, kept, overwrite: true);
                TestContext.WriteLine($"acceptance artifact kept at {kept}");
            }
            finally
            {
                System.IO.File.Delete(savedPath);
            }
        }

        // repro: the EXACT user flow — a fresh campaign with the REAL intro
        // cinematic (the other gates mute it in SetUp, which is why they stayed green), an ESC
        // skip, then loading the player's actual failing quicksave. After the swap the sequence
        // gate must be DOWN and a click-path order must both DELIVER and MOVE the dwarf; the
        // manual test showed fired=True clicks piling into PendingEvents forever.
        [UnityTest]
        public IEnumerator UserQuicksaveReloadKeepsOrdersAliveAfterARealIntro()
        {
            const string userSave = "quick_Quicksave_20260718_1528";
            string saveDir = System.IO.Path.Combine(
                _runtime.Context!.DataRoot, "Gamesave");
            string savePath = System.IO.Path.Combine(saveDir, userSave + ".sav");
            if (!System.IO.File.Exists(savePath))
            {
                string kept = System.IO.Path.Combine(SaveFixtureDir, userSave + ".sav");
                Assume.That(System.IO.File.Exists(kept),
                    $"user quicksave fixture not found: {savePath} / {kept}");
                System.IO.File.Copy(kept, savePath);
            }

            // Rebuild the campaign WITH the real opening cinematic staged (SetUp muted it).
            _runtime.campaignIntroCutscene = "urw_3";
            _runtime.NewGame();
            yield return WaitUntil(() => _runtime.WorldReady, 3000,
                "campaign world never became ready with the intro armed");
            DigglesTclContext ctx = _runtime.Context!;

            // Let the t=5 intro actually START (the player watched this happen), then skip it
            // the player's way: ESC = raise the break flag; the sequencer drains itself.
            bool introStarted = false;
            for (int frame = 0; frame < 800 && !introStarted; frame++)
            {
                yield return null;
                introStarted = ctx.SequenceActive;
            }
            TestContext.WriteLine($"[repro] intro started = {introStarted}");
            if (introStarted)
            {
                for (int frame = 0; frame < 40; frame++) yield return null;
                ctx.SequenceBreaked = true;   // the faithful ESC skip
                yield return WaitUntil(() => !ctx.SequenceActive, 1200,
                    "the intro cinematic did not end on the ESC break flag");
            }
            yield return WaitUntil(
                () => !ctx.SequenceActive && ctx.This == null && ctx.CurrentEvent == null,
                80, "no safe boundary after the intro");

            _loadService.LoadGame(userSave);
            DigglesTclContext reloaded = _runtime.Context!;
            Assert.That(reloaded, Is.Not.SameAs(ctx), "the load must swap the session");
            yield return null;

            // ~20 sim seconds of ordinary play; the flag must never wedge up (a stuck flag
            // swallows all event delivery — the dead-orders symptom).
            int flaggedFrames = 0;
            for (int frame = 0; frame < 800; frame++)
            {
                yield return null;
                if (reloaded.SequenceActive) flaggedFrames++;
            }
            TestContext.WriteLine(
                $"[repro] post-load: SequenceActive={reloaded.SequenceActive} " +
                $"flaggedFrames={flaggedFrames}/800 gameTime={reloaded.Sim!.GameTime:F1}");
            Assert.That(reloaded.SequenceActive, Is.False,
                "no cutscene may be running after loading a mid-play quicksave");

            // The click-path order: select a dwarf, fire the walk, require DELIVERY + MOTION.
            DigglesTclEntity[] dwarves = PlayerDwarves(reloaded).OrderBy(e => e.Handle).ToArray();
            Assert.That(dwarves, Is.Not.Empty, "the user save must restore player dwarves");
            var orders = new ClickToMoveService(reloaded);
            DigglesTclEntity commanded = dwarves[0];
            reloaded.Selection.Clear();
            reloaded.Selection.BoxIncludeStep(commanded.Handle);
            Vector3 target = commanded.Position + new Vector3(5f, 0f, 0f);
            TestContext.WriteLine(
                $"[chain] pre-order: navService={(reloaded.NavService == null ? "NULL" : reloaded.NavService.HasWorld ? "hasWorld" : "NO-WORLD")} " +
                $"terrainNav={(reloaded.Terrain?.Nav == null ? "NULL" : "ok")} " +
                $"go={(commanded.GameObject == null ? "NULL" : "ok")} " +
                $"goActive={(commanded.GameObject != null && commanded.GameObject.activeInHierarchy)}");
            DigglesTclEngine.WorkDebug = true;
            Diggles.Runtime.Diagnostics.DigglesDebugLog.Begin(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "workdbg.log"));
            Diggles.Runtime.Diagnostics.DigglesDebugLog.Enabled = true;
            string fixedTarget = "?";
            DigglesTclEntity? prevThis = reloaded.This;
            reloaded.This = commanded;
            try
            {
                var inv2 = System.Globalization.CultureInfo.InvariantCulture;
                fixedTarget = reloaded.Interp.Eval(
                    "vector_fix {" + target.x.ToString(inv2) + " " + target.y.ToString(inv2) +
                    " " + target.z.ToString(inv2) + "}");
            }
            finally { reloaded.This = prevThis; }
            TestContext.WriteLine($"[chain] vector_fix({target.x:F1} {target.y:F1} {target.z:F1}) -> '{fixedTarget}'");
            Assert.That(orders.FireWalkOrder(target), Is.GreaterThan(0),
                "the walk order must reach the selected dwarf");
            yield return null;
            yield return null;
            Assert.That(commanded.PendingEvents.Count, Is.Zero,
                "the order event must DELIVER on the next sim ticks — a growing queue is the " +
                "exact playtest failure (clicks fired=True, dwarves inert)");
            Vector3 start = commanded.Position;
            bool moved = false;
            for (int frame = 0; frame < 800 && !moved; frame++)
            {
                yield return null;
                moved = (commanded.Position - start).sqrMagnitude > 1f;
                if (frame % 80 == 0)
                {
                    TestContext.WriteLine(
                        $"[chain] f{frame} #{commanded.Handle} state='{commanded.CurrentState}' " +
                        $"enab={commanded.StateEnabledCount} " +
                        $"tasks=[{string.Join(", ", commanded.TaskList)}] " +
                        $"dirty={commanded.TaskListDirty} " +
                        $"action={commanded.ActiveAction?.GetType().Name ?? "none"} " +
                        $"walkResult={commanded.WalkResult} pend={commanded.PendingEvents.Count} " +
                        $"pos=({commanded.Position.x:F1},{commanded.Position.y:F1},{commanded.Position.z:F1})");
                }
            }
            DigglesTclEngine.WorkDebug = false;
            Diggles.Runtime.Diagnostics.DigglesDebugLog.Flush();
            Diggles.Runtime.Diagnostics.DigglesDebugLog.Enabled = false;
            Assert.That(moved, Is.True,
                "a restored dwarf must OBEY a click walk order after the real-intro flow");
        }

        static Quaternion ModelRotationOf(DigglesTclEntity entity)
        {
            Transform? model = entity.GameObject != null
                ? entity.GameObject.transform.Find("Model")
                : null;
            return model != null ? model.localRotation : Quaternion.identity;
        }

        // ---- helpers ----

        // Ensure a working-set save (savework/) is visible to the load service, which resolves
        // names against DataRoot/Gamesave. Mirrors the copy path player-quicksave gates use.
        void EnsureSaveFixture(string saveName)
        {
            string savePath = System.IO.Path.Combine(
                _runtime.Context!.DataRoot, "Gamesave", saveName + ".sav");
            if (System.IO.File.Exists(savePath)) return;
            string kept = System.IO.Path.Combine(SaveFixtureDir, saveName + ".sav");
            Assume.That(System.IO.File.Exists(kept), $"save fixture not found: {savePath} / {kept}");
            System.IO.File.Copy(kept, savePath);
        }

        static System.Collections.Generic.IEnumerable<DigglesTclEntity> PlayerDwarves(
            DigglesTclContext ctx) =>
            ctx.Entities.Values.Where(e =>
                e.Class?.Name == "Zwerg" || e.Class?.Name == "Frau");

        IEnumerator WaitUntil(Func<bool> cond, int maxFrames, string failMsg)
        {
            for (int f = 0; f < maxFrames; f++)
            {
                if (cond()) yield break;
                yield return null;
            }
            Assert.Fail(failMsg);
        }
    }
}
