#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using Diggles.Runtime;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class ObjClearTests
    {
        sealed class TestSpawner : IDigglesEntitySpawner
        {
            public GameObject? Spawn(DigglesTclEntity entity, Transform parent)
            {
                var gameObject = new GameObject($"obj_clear_{entity.Handle}");
                gameObject.transform.SetParent(parent, false);
                return gameObject;
            }

            public void Destroy(GameObject gameObject) => Object.DestroyImmediate(gameObject);

            public void ApplyPosition(GameObject gameObject, Vector3 nativePosition) { }

            public void ApplyRotation(GameObject gameObject, Vector3 nativeRotationRadians) { }

            public void ApplyAnim(
                GameObject gameObject,
                string animationName,
                int startFrame,
                DigglesAnimator.AnimMode mode) { }
        }

        readonly List<GameObject> _roots = new();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject root in _roots)
                if (root != null) Object.DestroyImmediate(root);
            _roots.Clear();
        }

        [Test]
        public void ObjClear_UsesDelTeardownAndClearsPresentationState()
        {
            var (interpreter, context, root) = BuildHost();
            // `global` inside obj_exit resolves to the OBJECT's scope, not the root interp
            // (the per-object interp model — see TclEngineTests.Del_Runs_ObjExit_…, which reads
            // entity.VarsScope after del). Assert through captured entity refs, not a root var.
            interpreter.Eval(@"
                def_class Fixture none dummy 0 {} {
                    obj_exit { global exited; set exited yes }
                }");

            var handles = new int[3];
            var spawnedObjects = new GameObject[handles.Length];
            var spawnedEntities = new DigglesTclEntity[handles.Length];
            for (int index = 0; index < handles.Length; index++)
            {
                handles[index] = int.Parse(interpreter.Eval("new Fixture"));
                spawnedEntities[index] = context.Entities[handles[index]];
                spawnedObjects[index] = spawnedEntities[index].GameObject!;
            }

            context.SelectedHandle = handles[0];
            Assert.That(context.Placement!.EnterPlacement(handles[0]), Is.True);
            context.StoryDigWeight = 77;
            context.StoryMap[(2, 3)] = 1;
            var levelGenerator = context.LevelGen;
            int newsId = int.Parse(interpreter.Eval("newsticker new 0 -text stale"));
            context.NewsTicker.ReplaceDisplayQueue(new[]
            {
                new NewsTickerDisplayQueueEntry(newsId, 0.5f),
            });

            Assert.That(context.Entities.Count, Is.EqualTo(3));
            Assert.That(context.Selection.Count, Is.EqualTo(1));
            Assert.That(context.Placement.Active, Is.True);
            Assert.That(context.NewsTicker.ActiveEntries.Count, Is.EqualTo(1));
            Assert.That(context.NewsTicker.HistoryEntries.Count, Is.EqualTo(1));
            Assert.That(context.NewsTicker.DisplayQueueEntries.Count, Is.EqualTo(1));

            Assert.That(interpreter.Eval("obj_clear"), Is.Empty);

            Assert.That(context.Entities, Is.Empty);
            Assert.That(context.Selection.Count, Is.Zero);
            Assert.That(context.Placement.Active, Is.False);
            Assert.That(context.NewsTicker.ActiveEntries, Is.Empty);
            Assert.That(context.NewsTicker.HistoryEntries, Is.Empty);
            Assert.That(context.NewsTicker.DisplayQueueEntries, Is.Empty);
            Assert.That(context.Classes.ContainsKey("Fixture"), Is.True,
                "obj_clear must not reset the class registry");
            Assert.That(context.StoryDigWeight, Is.EqualTo(77));
            Assert.That(context.StoryMap[(2, 3)], Is.EqualTo(1),
                "obj_clear must not reset StoryMgr state");
            Assert.That(context.LevelGen, Is.SameAs(levelGenerator));
            foreach (DigglesTclEntity entity in spawnedEntities)
                Assert.That(entity.VarsScope.Vars.ContainsKey("exited"), Is.False,
                    "obj_clear is the native RAW sweep (FUN_005d2920: vtbl dtor, no Tcl) — " +
                    "obj_exit chains belong to `del` (FUN_00623810) only; a sequencer's " +
                    "obj_exit set_view restore must NOT fire across the world swap");
            foreach (GameObject gameObject in spawnedObjects)
                Assert.That(gameObject == null, Is.True, "entity GameObject survived obj_clear");
            Assert.That(root.transform.childCount, Is.Zero,
                "entity objects or the placement cursor ghost survived obj_clear");
        }

        [Test]
        public void ObjClear_EmptyWorld_IsSafeNoOp()
        {
            var (interpreter, context, root) = BuildHost();

            Assert.DoesNotThrow(() => interpreter.Eval("obj_clear"));
            Assert.That(context.Entities, Is.Empty);
            Assert.That(context.Selection.Count, Is.Zero);
            Assert.That(context.Placement!.Active, Is.False);
            Assert.That(context.NewsTicker.ActiveEntries, Is.Empty);
            Assert.That(root.transform.childCount, Is.Zero);
        }

        [Test]
        public void ObjClear_DeferredSourceSelfDelete_ContinuesRemainingScript()
        {
            var (interpreter, context, _) = BuildHost();
            var simulation = new DigglesGameSim(context);
            interpreter.Eval(@"
                def_class Fixture none dummy 0 {} {}
                set obj_clear_marker pending
                set source [new Fixture]
                action $source wait 0 {
                    exec_deferred {obj_clear; set obj_clear_marker complete}
                }");
            GameObject sourceObject = context.Entities[int.Parse(interpreter.Eval("set source"))].GameObject!;

            Assert.DoesNotThrow(() =>
            {
                for (int step = 0; step < 5; step++) simulation.Step(0.1);
            });

            Assert.That(interpreter.Eval("set obj_clear_marker"), Is.EqualTo("complete"),
                "the statement after self-deleting obj_clear did not execute");
            Assert.That(context.Entities, Is.Empty);
            Assert.That(sourceObject == null, Is.True);
        }

        (TclInterpreter interpreter, DigglesTclContext context, GameObject root) BuildHost()
        {
            var interpreter = new TclInterpreter();
            DigglesTclContext context = DigglesTclEngine.Register(interpreter);
            var root = new GameObject("ObjClearTests");
            _roots.Add(root);
            context.SpawnParent = root.transform;
            context.EntitySpawner = new TestSpawner();
            return (interpreter, context, root);
        }
    }
}
