#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using Diggles.Runtime;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;
    using UnityEngine;

    // Phase 4 — Tcl ↔ Unity scene bridge.
    // Verifies that with an IDigglesEntitySpawner wired into the
    // DigglesTclContext, Tcl `new` / `del` / `set_pos` / `set_rot` /
    // `set_anim` invocations reach the spawner with the right args.
    // Uses a recording mock so tests don't depend on Editor assemblies
    // (MarkerModelResolver, AssetImportContext) or real.3db assets.
    public sealed class TclSceneBridgeTests
    {
        sealed class RecordingSpawner : IDigglesEntitySpawner
        {
            public List<DigglesTclEntity> SpawnedEnts = new();
            public List<GameObject> DestroyedGOs = new();
            public List<(GameObject go, Vector3 pos)> Positions = new();
            public List<(GameObject go, Vector3 rot)> Rotations = new();
            public List<(GameObject go, string anim, int frame, DigglesAnimator.AnimMode mode)> Anims = new();

            public GameObject? Spawn(DigglesTclEntity ent, Transform parent)
            {
                SpawnedEnts.Add(ent);
                var go = new GameObject($"mock_{ent.Class.Name}_{ent.Handle}");
                go.transform.SetParent(parent, false);
                return go;
            }

            public void Destroy(GameObject go)
            {
                DestroyedGOs.Add(go);
                Object.DestroyImmediate(go);
            }

            public void ApplyPosition(GameObject go, Vector3 nativePos) =>
                Positions.Add((go, nativePos));

            public void ApplyRotation(GameObject go, Vector3 nativeRotRadians) =>
                Rotations.Add((go, nativeRotRadians));

            public void ApplyAnim(GameObject go, string animName, int startFrame,
                DigglesAnimator.AnimMode mode) => Anims.Add((go, animName, startFrame, mode));
        }

        readonly System.Collections.Generic.List<GameObject> _spawnedRoots = new();

        (TclInterpreter interp, DigglesTclContext ctx, RecordingSpawner mock, GameObject root)
            BuildHost()
        {
            var interp = new TclInterpreter();
            var ctx = DigglesTclEngine.Register(interp);
            var root = new GameObject("BridgeTestRoot");
            _spawnedRoots.Add(root);
            var mock = new RecordingSpawner();
            ctx.SpawnParent = root.transform;
            ctx.EntitySpawner = mock;

            // Minimal class so `new` doesn't throw.
            interp.Eval("def_class Pilz food material 0 {} { class_defaultanim pilz.standard }");
            return (interp, ctx, mock, root);
        }

        [TearDown]
        public void Cleanup()
        {
            // Destroy roots only — children get cascade-destroyed. Unity's
            // overloaded `== null` returns true for already-destroyed objects,
            // so we can safely skip them. Avoids the "iterate destroyed
            // entries in a stale FindObjectsByType array" pitfall.
            foreach (var root in _spawnedRoots)
            {
                if (root == null) continue;
                Object.DestroyImmediate(root);
            }
            _spawnedRoots.Clear();
        }

        [Test]
        public void New_WithoutSpawner_DoesNotCreateGO()
        {
            var interp = new TclInterpreter();
            var ctx = DigglesTclEngine.Register(interp);
            // SpawnParent + EntitySpawner left null.
            interp.Eval("def_class Pilz food material 0 {} {}");
            string h = interp.Eval("new Pilz \"\" \"1 2 3\" \"0 0 0\"");

            var ent = ctx.GetEntity(h);
            Assert.NotNull(ent);
            Assert.IsNull(ent!.GameObject);
        }

        [Test]
        public void New_WithSpawner_CallsSpawnAndStoresGO()
        {
            var (interp, ctx, mock, root) = BuildHost();
            string h = interp.Eval("new Pilz \"\" \"10 20 5\" \"0 1.57 0\"");

            Assert.AreEqual(1, mock.SpawnedEnts.Count);
            var ent = ctx.GetEntity(h);
            Assert.NotNull(ent);
            Assert.NotNull(ent!.GameObject);
            Assert.AreSame(root.transform, ent.GameObject!.transform.parent);
            // Position stored on the entity is Diggles native (10, 20, 5).
            Assert.AreEqual(new Vector3(10, 20, 5), ent.Position);
        }

        [Test]
        public void Del_DestroysGOAndRemovesFromRegistry()
        {
            var (interp, ctx, mock, _) = BuildHost();
            string h = interp.Eval("new Pilz \"\" \"1 1 1\" \"0 0 0\"");
            var ent = ctx.GetEntity(h);
            var go = ent!.GameObject;

            interp.Eval($"del {h}");

            Assert.AreEqual(1, mock.DestroyedGOs.Count);
            Assert.AreSame(go, mock.DestroyedGOs[0]);
            Assert.IsNull(ctx.GetEntity(h));
        }

        [Test]
        public void SetPos_ForwardsNativeCoordsToSpawner()
        {
            var (interp, _, mock, _) = BuildHost();
            interp.Eval("set h [new Pilz \"\" \"0 0 0\" \"0 0 0\"]");
            interp.Eval("set_pos $h 11 22 33");

            // 1 call from spawn (initial position), then 1 from set_pos.
            // Spawn itself doesn't go through ApplyPosition in this
            // mock — only set_pos / set_rot mutators do.
            Assert.AreEqual(1, mock.Positions.Count);
            Assert.AreEqual(new Vector3(11, 22, 33), mock.Positions[0].pos);
        }

        [Test]
        public void SetRot_ForwardsNativeRadiansToSpawner()
        {
            var (interp, _, mock, _) = BuildHost();
            interp.Eval("set h [new Pilz \"\" \"0 0 0\" \"0 0 0\"]");
            interp.Eval("set_rot $h 0 4.71 0");

            Assert.AreEqual(1, mock.Rotations.Count);
            Assert.AreEqual(new Vector3(0f, 4.71f, 0f), mock.Rotations[0].rot);
        }

        [Test]
        public void SetAnim_PassesFullObjAnimNameAndModeFrame()
        {
            var (interp, _, mock, _) = BuildHost();
            interp.Eval("set h [new Pilz \"\" \"0 0 0\" \"0 0 0\"]");
            // mode 2 = ANIM_LOOP per animinit.tcl
            interp.Eval("set_anim $h pilz.wachsen01 0 2");

            Assert.AreEqual(1, mock.Anims.Count);
            // Engine passes the FULL "obj.anim" through — the controller's PlayClip resolves the
            // prefix and keys CROSS-object clips (e.g. kiste_tragen.standard) distinctly. (#4)
            Assert.AreEqual("pilz.wachsen01", mock.Anims[0].anim);
            Assert.AreEqual(0, mock.Anims[0].frame);
            Assert.AreEqual(DigglesAnimator.AnimMode.Loop, mock.Anims[0].mode);
        }

        [Test]
        public void SetAnim_DefaultsModeLoopAndFrameZero()
        {
            var (interp, _, mock, _) = BuildHost();
            interp.Eval("set h [new Pilz \"\" \"0 0 0\" \"0 0 0\"]");
            interp.Eval("set_anim $h pilz.standard");

            Assert.AreEqual(1, mock.Anims.Count);
            Assert.AreEqual("pilz.standard", mock.Anims[0].anim);
            Assert.AreEqual(0, mock.Anims[0].frame);
            Assert.AreEqual(DigglesAnimator.AnimMode.Loop, mock.Anims[0].mode);
        }

        [Test]
        public void SetAnim_AcceptsBareAnimNameWithoutObjectPrefix()
        {
            var (interp, _, mock, _) = BuildHost();
            interp.Eval("set h [new Pilz \"\" \"0 0 0\" \"0 0 0\"]");
            interp.Eval("set_anim $h wachsen01 0 1");

            Assert.AreEqual(1, mock.Anims.Count);
            Assert.AreEqual("wachsen01", mock.Anims[0].anim);
            Assert.AreEqual(DigglesAnimator.AnimMode.Once, mock.Anims[0].mode);
        }
    }
}
