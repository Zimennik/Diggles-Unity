#nullable enable

namespace Diggles.Tests
{
    using System;
    using System.IO;
    using System.Linq;
    using Diggles.Runtime;
    using Diggles.Runtime.Navigation;
    using Diggles.Runtime.Save;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class TutorialBootE2ETests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

        [Test]
        public void NewGameVariants_MapToNativeBootTable_WithoutGivingTutorialAStoryManager()
        {
            // Native dispatch table (decompile strings).
            Assert.That(
                DigglesWorldRuntime.GetBootScript(DigglesWorldRuntime.NewGameVariant.Introduction),
                Is.EqualTo("data/GameScripts/single_Starttut.tcl"));
            Assert.That(
                DigglesWorldRuntime.GetBootScript(DigglesWorldRuntime.NewGameVariant.Campaign),
                Is.EqualTo("data/GameScripts/single_Startcamp.tcl"));
            Assert.That(
                DigglesWorldRuntime.HasStoryManager(DigglesWorldRuntime.NewGameVariant.Introduction),
                Is.False);
            Assert.That(
                DigglesWorldRuntime.HasStoryManager(DigglesWorldRuntime.NewGameVariant.Campaign),
                Is.True);
        }

        [Test]
        public void TutorialBoot_EmitsPlayableWorldAndStartsLessonMachine()
        {
            UnityEngine.Random.InitState(202608071);
            using var harness = new TutorialHarness();

            harness.RunGameScript("GameScripts/single_Tutorial.tcl");

            DigglesTclContext context = harness.Engine.Ctx;
            DiggableTerrain terrain = RequireTerrain(context);
            DigglesTclEntity tutorial = FindSingle(context, "Trigger_Tutorial");
            FogOfWarState fog = context.FogOfWar!;
            var removers = context.Entities.Values
                .Where(entity => entity.Class?.Name == "FogRemover")
                .ToArray();

            // single_Tutorial.tcl:2-4 loads oberwelt.tcl; oberwelt.tcl:1 creates 500x200;
            // obw_tutorial.tcl:285 authors Trigger_Tutorial.
            Assert.That(terrain.Width, Is.EqualTo(500));
            Assert.That(terrain.Height, Is.EqualTo(200));
            Assert.That(CountPlayerGnomes(context), Is.GreaterThan(0));
            Assert.That(removers.Length, Is.GreaterThanOrEqualTo(7));
            Assert.That(removers.All(entity => entity.GameObject != null), Is.True);
            Assert.That(fog, Is.Not.Null);
            Assert.That(fog.Width, Is.EqualTo(500));
            Assert.That(fog.Height, Is.EqualTo(200));
            Assert.That(context.IsStoryMgr, Is.False);
            Assert.That(
                ((RuntimeWorldSpawner)context.EntitySpawner!).FogState,
                Is.SameAs(fog));
            Assert.That(((RuntimeMapBuilder)context.MapBuilder!).WorldRecreationPending, Is.False);
            Assert.That(harness.Runtime.CompletedWorldSwapCount, Is.Zero,
                "the first map create is an initial emit, not a mid-session recreation");

            for (int step = 0; step < 200; step++)
            {
                harness.Engine.Sim.Step(0.1);
                if (context.SequenceActive) context.SequenceBreaked = true;
            }
            DrainSequences(harness.Engine, 800);
            for (int step = 0; step < 50 && RefValue(tutorial, "gnome_ref1") == "0"; step++)
            {
                harness.Engine.Sim.Step(0.1);
                if (context.SequenceActive) context.SequenceBreaked = true;
            }

            // single_Tutorial.tcl:26-27 arms evt_timer0/+13 and evt_timer1/+18. The latter
            // enables Trigger_Tutorial.tcl:46 checking, whose truereturn branch (:64-74)
            // captures gnome_ref1, changes tocheck and feeds the task list.
            Assert.That(RefValue(tutorial, "gnome_ref1"), Is.Not.EqualTo("0"));
            Assert.That(RefValue(tutorial, "tocheck"), Is.Not.EqualTo("truereturn"));
            Assert.That(tutorial.TaskListDirty, Is.True);
            Assert.That(tutorial.CurrentState,
                Is.EqualTo("checking").Or.EqualTo("task").Or.EqualTo("correction"));
        }

        [Test]
        public void IntroToTutorialSwap_DefersEmissionThenActivatesOneFullyWiredCandidate()
        {
            UnityEngine.Random.InitState(202608072);
            using var harness = new TutorialHarness();

            harness.RunGameScript("GameScripts/single_Starttut.tcl");

            DigglesTclContext context = harness.Engine.Ctx;
            RuntimeGameSession introSession = harness.Active.Current;
            GameObject introRoot = introSession.ContentRoot;
            DiggableTerrain introTerrain = RequireTerrain(context);
            FogOfWarState introFog = context.FogOfWar!;
            int wireCountBeforeSwap = harness.Runtime.PresentationWireCount;
            var introObjects = context.Entities.Values
                .Select(entity => entity.GameObject)
                .Where(gameObject => gameObject != null)
                .Cast<GameObject>()
                .ToArray();

            // startseqs.tcl:2 creates the 300x300 Odin world.
            Assert.That(introTerrain.Width, Is.EqualTo(300));
            Assert.That(introTerrain.Height, Is.EqualTo(300));
            Assert.That(FindSingle(context, "Trigger_Auftrag_Odin"), Is.Not.Null);

            for (int guard = 0; guard < 300 && !context.SequenceActive; guard++)
                harness.Engine.Sim.Step(0.1);
            Assert.That(context.SequenceActive, Is.True, "odin_001 never engaged");

            context.SequenceBreaked = true;
            for (int guard = 0; guard < 800 && context.SequenceActive; guard++)
                harness.Engine.Sim.Step(0.1);

            // Trigger_Tournament.tcl:678-685 switches tutorial/tournament and queues the
            // matching single_* script. Skip any follow-up transition sequence until its
            // deferred tutorial batch reaches oberwelt.tcl:1 map create 500 200.
            for (int guard = 0; guard < 1600 && !harness.Runtime.HasPendingWorldSwap; guard++)
            {
                if (context.SequenceActive) context.SequenceBreaked = true;
                harness.Engine.Sim.Step(0.1);
            }

            RuntimeMapBuilder mapBuilder = (RuntimeMapBuilder)context.MapBuilder!;
            Assert.That(harness.Runtime.HasPendingWorldSwap, Is.True);
            Assert.That(mapBuilder.WorldRecreationPending, Is.True);
            Assert.That(mapBuilder.Width, Is.EqualTo(500));
            Assert.That(mapBuilder.Height, Is.EqualTo(200));
            Assert.That(mapBuilder.WorldResult, Is.Null,
                "map create rebuilt eagerly before the Tcl template batch completed");
            Assert.That(harness.Active.Current, Is.SameAs(introSession));
            Assert.That(introTerrain == null, Is.False,
                "the live 300x300 terrain was retired before candidate activation");
            Assert.That(introObjects.All(gameObject => gameObject == null), Is.True,
                "obj_clear did not delete every intro entity before the root swap");
            Assert.That(context.Entities.Values.Any(entity => entity.Class?.Name == "Koenig_im_Bett"),
                Is.False);
            Transform candidateSpawnRoot = context.SpawnParent!;
            Assert.That(candidateSpawnRoot.gameObject.activeSelf, Is.False);
            Assert.That(candidateSpawnRoot.GetComponent<DiggableTerrain>(), Is.Null);
            Assert.That(FindSingle(context, "Trigger_Tutorial").GameObject!.transform.parent,
                Is.EqualTo(candidateSpawnRoot));

            Assert.That(harness.Runtime.FlushPendingWorldSwap(), Is.True);

            RuntimeGameSession tutorialSession = harness.Active.Current;
            DiggableTerrain tutorialTerrain = RequireTerrain(context);
            FogOfWarState tutorialFog = context.FogOfWar!;
            NavWorld navWorld = context.NavService!.World!;

            Assert.That(tutorialSession, Is.Not.SameAs(introSession));
            Assert.That(tutorialSession.State, Is.EqualTo(RuntimeGameSessionState.Active));
            Assert.That(introSession.State, Is.EqualTo(RuntimeGameSessionState.Disposed));
            Assert.That(tutorialSession.ContentRoot.activeSelf, Is.True);
            Assert.That(context.SpawnParent, Is.EqualTo(tutorialSession.ContentRoot.transform));
            Assert.That(tutorialTerrain.Width, Is.EqualTo(500));
            Assert.That(tutorialTerrain.Height, Is.EqualTo(200));
            Assert.That(context.IsStoryMgr, Is.False);
            Assert.That(navWorld.Width, Is.EqualTo(500));
            Assert.That(navWorld.Height, Is.EqualTo(200));
            Assert.That(tutorialFog, Is.Not.SameAs(introFog));
            Assert.That(tutorialFog.Width, Is.EqualTo(500));
            Assert.That(tutorialFog.Height, Is.EqualTo(200));
            Assert.That(((RuntimeWorldSpawner)context.EntitySpawner!).FogState, Is.SameAs(tutorialFog));
            Assert.That(CountPlayerGnomes(context), Is.GreaterThan(0));
            Assert.That(FindSingle(context, "Trigger_Tutorial"), Is.Not.Null);
            Assert.That(context.Entities.Values.Count(entity => entity.Class?.Name == "FogRemover"),
                Is.GreaterThanOrEqualTo(7));
            Assert.That(introRoot == null, Is.True);
            Assert.That(introTerrain == null, Is.True);
            Assert.That(mapBuilder.WorldRecreationPending, Is.False);
            Assert.That(harness.Runtime.CompletedWorldSwapCount, Is.EqualTo(1));

            DigglesTclEntity tutorialRemover = context.Entities.Values.First(
                entity => entity.Class?.Name == "FogRemover" && entity.Owner == 0);
            // Fog reveal work is striped across simulation passes. Six seconds crosses every
            // entity slot without reaching the tutorial's newly armed +13/+18 lesson timers.
            for (int step = 0; step < 60; step++) harness.Engine.Sim.Step(0.1);
            Assert.That(
                tutorialFog.FogAt(
                    (int)tutorialRemover.Position.x,
                    (int)tutorialRemover.Position.y,
                    0),
                Is.GreaterThan(0),
                "the tutorial FogRemover did not feed the recreated fog state");

            // oberwelt.tcl:8 is the authoritative post-swap set_view.
            var camera = context.Camera!;
            Assert.That(camera.Focus.x, Is.EqualTo(131.78f).Within(0.001f));
            Assert.That(camera.Focus.y, Is.EqualTo(23.95f).Within(0.001f));
            Assert.That(camera.Zoom, Is.EqualTo(2.5f).Within(0.001f));
            Assert.That(harness.CameraController.FocusX, Is.EqualTo(131.78f).Within(0.001f));
            Assert.That(harness.CameraController.FocusY, Is.EqualTo(23.95f).Within(0.001f));
            Assert.That(harness.CameraController.Zoom, Is.EqualTo(2.5f).Within(0.001f));
            Assert.That(harness.CameraController.FocusMax, Is.EqualTo(new Vector2(500f, 200f)));
            Assert.That(harness.CameraController.WorldRoot,
                Is.EqualTo(tutorialSession.ContentRoot.transform));

            Assert.That(harness.Runtime.PresentationWireCount, Is.EqualTo(wireCountBeforeSwap + 1));
            FogOfWarState wiredFog = context.FogOfWar!;
            harness.Runtime.BindSession(tutorialSession);
            Assert.That(harness.Runtime.PresentationWireCount, Is.EqualTo(wireCountBeforeSwap + 1));
            Assert.That(context.FogOfWar, Is.SameAs(wiredFog));
            Assert.That(tutorialSession.ContentRoot.GetComponents<FogOfWarPresenter>().Length, Is.EqualTo(1));
        }

        [Test]
        public void LessonJumps_RewireTutorialTriggerWithoutTclErrors()
        {
            UnityEngine.Random.InitState(202608073);
            using var harness = new TutorialHarness();
            harness.RunGameScript("GameScripts/single_Tutorial.tcl");

            DigglesTclEntity tutorial = FindSingle(harness.Engine.Ctx, "Trigger_Tutorial");

            AssertJump(harness.Engine, tutorial, "s2010", "gnome1deselected...");
            AssertJump(harness.Engine, tutorial, "s2015", "harvested1");
            AssertJump(harness.Engine, tutorial, "s2020", "zeltunboxed");
        }

        [Test]
        public void TournamentContext_LoadsNativeClassesAndResolvesBothSwitchBranches()
        {
            using var harness = new TutorialHarness();
            DigglesTclContext context = harness.Engine.Ctx;

            Assert.That(context.Classes.ContainsKey("Trigger_Auftrag_Odin"), Is.True);
            Assert.That(context.Classes.ContainsKey("Trigger_Tournament"), Is.True);
            Assert.That(context.Classes.ContainsKey("Trigger_tourn_2170"), Is.True);

            int odin = int.Parse(harness.Engine.Interp.Eval("new Trigger_Auftrag_Odin"));
            Assert.DoesNotThrow(() =>
                harness.Engine.Interp.Eval($"call_method {odin} set_context tutorial"));
            Assert.That(harness.Engine.Interp.Eval($"ref_get {odin} context"), Is.EqualTo("tutorial"));

            AssertContextSwitch(
                harness.Engine,
                "tutorial",
                "call data/GameScripts/single_Tutorial.tcl");
            AssertContextSwitch(
                harness.Engine,
                "tournament",
                "call data/GameScripts/single_Tournament.tcl");
        }

        private static void AssertJump(
            DigglesEngine engine,
            DigglesTclEntity tutorial,
            string procedure,
            string expectedCheck)
        {
            engine.Interp.Eval($"tasklist_add {tutorial.Handle} sentinel");
            string previousCheck = engine.Interp.Eval($"ref_get {tutorial.Handle} tocheck");

            // oberwelt.tcl:22-31,:42-60 and:62-77 are the shipped developer jumps.
            Assert.DoesNotThrow(() => engine.Interp.Eval(procedure));

            string currentCheck = engine.Interp.Eval($"ref_get {tutorial.Handle} tocheck");
            Assert.That(currentCheck, Is.EqualTo(expectedCheck));
            Assert.That(currentCheck, Is.Not.EqualTo(previousCheck));
            Assert.That(tutorial.TaskList, Is.Empty);
            Assert.That(tutorial.CurrentState, Is.EqualTo("checking"));
            Assert.That(tutorial.TaskListDirty, Is.True);
        }

        private static void AssertContextSwitch(
            DigglesEngine engine,
            string contextName,
            string expectedDeferredScript)
        {
            int handoff = int.Parse(engine.Interp.Eval("new Trigger_tourn_2170"));
            engine.Interp.Eval($"call_method {handoff} set_context {contextName}");

            // Trigger_Tournament.tcl:677-688 owns the real switch/exec_deferred chain.
            Assert.DoesNotThrow(() =>
                engine.Interp.Eval($"call_method {handoff} destroy_overload"));
            Assert.That(engine.Ctx.DeferredScripts.Count, Is.EqualTo(1));
            Assert.That(engine.Ctx.DeferredScripts.Dequeue(), Is.EqualTo(expectedDeferredScript));
        }

        private static void DrainSequences(DigglesEngine engine, int guardLimit)
        {
            for (int guard = 0; guard < guardLimit && engine.Ctx.SequenceActive; guard++)
            {
                engine.Ctx.SequenceBreaked = true;
                engine.Sim.Step(0.1);
            }
            Assert.That(engine.Ctx.SequenceActive, Is.False, "sequence skip drain did not finish");
        }

        private static DiggableTerrain RequireTerrain(DigglesTclContext context)
        {
            Assert.That(context.Terrain, Is.Not.Null);
            return context.Terrain!;
        }

        private static DigglesTclEntity FindSingle(
            DigglesTclContext context,
            string className) => context.Entities.Values.Single(
                entity => entity.Class?.Name == className);

        private static int CountPlayerGnomes(DigglesTclContext context) =>
            context.Entities.Values.Count(
                entity => entity.Class?.Name == "Zwerg" && entity.Owner == 0);

        private static string RefValue(DigglesTclEntity entity, string name) =>
            entity.VarsScope.Vars.TryGetValue(name, out var value) ? value : string.Empty;

        private sealed class TutorialHarness : IDisposable
        {
            private readonly GameObject stableRoot;
            private readonly GameObject runtimeObject;
            private readonly GameObject cameraObject;

            internal TutorialHarness()
            {
                stableRoot = new GameObject("TutorialBoot.StableRoot");
                stableRoot.transform.localRotation = Quaternion.Euler(270f, 0f, 0f);
                var contentRoot = new GameObject("WorldContent.Initial");
                contentRoot.transform.SetParent(stableRoot.transform, false);
                cameraObject = new GameObject("TutorialBoot.MainCamera") { tag = "MainCamera" };
                Camera camera = cameraObject.AddComponent<Camera>();
                CameraController = cameraObject.AddComponent<DigglesCameraController>();
                CameraController.TargetCamera = camera;

                var factory = new DigglesEngineFactory();
                Engine = factory.Create(DataRoot, contentRoot.transform, "BlockDB2.BDB");
                Engine.LoadRuntimeDefinitions();
                Engine.Ctx.IsStoryMgr = false;

                RuntimeGameSession initial = RuntimeGameSession.CreateInitial(Engine, contentRoot);
                Active = new ActiveGameSession(initial);
                runtimeObject = new GameObject("TutorialBoot.Runtime");
                Runtime = runtimeObject.AddComponent<DigglesWorldRuntime>();
                Runtime.dataRoot = DataRoot;
                Runtime.bdbRel = "BlockDB2.BDB";
                var binder = new RuntimePresentationBinder(
                    new IRuntimeSessionPresentationParticipant[]
                    {
                        Runtime,
                        new OriginalSaveRuntimePresentationParticipant(),
                    });
                var host = new RuntimeWorldSessionHost(
                    factory,
                    Active,
                    new GameMutationGate(),
                    binder);
                Runtime.ConfigureWorldSwapHost(host);
                Runtime.ConfigureCameraController(CameraController);
                Runtime.BindSession(initial);
            }

            internal DigglesEngine Engine { get; }
            internal ActiveGameSession Active { get; }
            internal DigglesWorldRuntime Runtime { get; }
            internal DigglesCameraController CameraController { get; }

            internal void RunGameScript(string relativePath)
            {
                Engine.Ctx.IsStoryMgr = false;
                string absolutePath = Path.Combine(
                    DataRoot,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
                Runtime.RunWorldScript(DigglesText.ReadAllText(absolutePath));
            }

            public void Dispose()
            {
                if (runtimeObject != null) UnityEngine.Object.DestroyImmediate(runtimeObject);
                Active.Current.Dispose();
                if (cameraObject != null) UnityEngine.Object.DestroyImmediate(cameraObject);
                if (stableRoot != null) UnityEngine.Object.DestroyImmediate(stableRoot);
            }
        }
    }
}
