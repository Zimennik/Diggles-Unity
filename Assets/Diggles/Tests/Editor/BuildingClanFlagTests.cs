#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using Diggles.Runtime;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class BuildingClanFlagTests
    {
        private sealed class TestSpawner : IDigglesEntitySpawner
        {
            public GameObject? Spawn(DigglesTclEntity entity, Transform parent)
            {
                var gameObject = new GameObject($"flag_test_{entity.Handle}");
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

        private readonly List<GameObject> _roots = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var root in _roots)
                if (root != null) Object.DestroyImmediate(root);
            _roots.Clear();
        }

        [Test]
        public void CompletingFlaggedClass_AttachesOwnerVariantAtNativeOffset()
        {
            var (interpreter, context) = BuildHost();
            string handle = interpreter.Eval("new FlaggedBuilding");
            var entity = context.GetEntity(handle)!;

            interpreter.Eval("set_ownerrace 2 2");
            interpreter.Eval($"set_owner {handle} 2");
            Assert.That(entity.GameObject!.transform.Find(BuildingClanFlag.ChildName), Is.Null);

            interpreter.Eval($"set_buildupstate {handle} 1");

            var flag = RequireFlag(entity);
            Assert.That(flag.GetComponent<SpriteRenderer>(), Is.Not.Null);
            Assert.That(flag.OwnerVariant, Is.EqualTo(2));
            Assert.That(flag.TextureName, Is.EqualTo("fahne_rot.tga"));
            Assert.That(flag.NativeOffset, Is.EqualTo(new Vector2(2.5f, 4f)));
            // FUN_004c3c90:46-59 with DAT_00736f78 = 0.0 mapped to the port's depth-tested
            // rendering: height lifts along the gameplay vertical (local z = −nativeY), the
            // depth axis carries only the 1-unit viewer-side bias (local y = −0.5) — the old
            // literal (2.5, 2, −1) buried the sprite height·0.5 units INTO the scene.
            Assert.That(flag.transform.localPosition, Is.EqualTo(new Vector3(2.5f, -0.5f, 4f)));

            interpreter.Eval("set_ownerrace 2 3");
            var recolored = RequireFlag(entity);
            Assert.That(recolored, Is.SameAs(flag));
            Assert.That(recolored.OwnerVariant, Is.EqualTo(3));
            Assert.That(recolored.TextureName, Is.EqualTo("fahne_weiss.tga"));
        }

        [Test]
        public void CompletingUnflaggedClass_DoesNotAttachFlag()
        {
            var (interpreter, context) = BuildHost();
            string handle = interpreter.Eval("new PlainBuilding");
            var entity = context.GetEntity(handle)!;

            interpreter.Eval($"set_owner {handle} 1");
            interpreter.Eval($"set_buildupstate {handle} 1");

            Assert.That(entity.GameObject!.transform.Find(BuildingClanFlag.ChildName), Is.Null);
        }

        [Test]
        public void SpawnMaterialization_AttachesFlagCompletedDuringObjectInit()
        {
            var (interpreter, context) = BuildHost();
            context.OwnerRace[2] = 1;

            string handle = interpreter.Eval("new InitializedFlaggedBuilding");

            var flag = RequireFlag(context.GetEntity(handle)!);
            Assert.That(flag.OwnerVariant, Is.EqualTo(1));
            Assert.That(flag.TextureName, Is.EqualTo("fahne_gruen.tga"));
        }

        [Test]
        public void PackingAndDestroyingFlaggedClass_RemoveFlagWithOwner()
        {
            var (interpreter, context) = BuildHost();
            string handle = interpreter.Eval("new FlaggedBuilding");
            var entity = context.GetEntity(handle)!;
            interpreter.Eval($"set_owner {handle} 1");
            interpreter.Eval($"set_buildupstate {handle} 1");
            RequireFlag(entity);

            interpreter.Eval($"set_boxed {handle} 1");
            Assert.That(entity.GameObject!.transform.Find(BuildingClanFlag.ChildName), Is.Null);

            interpreter.Eval($"set_boxed {handle} 0");
            interpreter.Eval($"set_buildupstate {handle} 1");
            var flagObject = RequireFlag(entity).gameObject;

            interpreter.Eval($"del {handle}");
            Assert.That(flagObject == null, Is.True);
            Assert.That(context.GetEntity(handle), Is.Null);
        }

        // The full player-build producer chain: the crate is created ownerless (native CObj
        // ctor, FUN_005c83f0:196), inherits the producing gnome's owner via
        // prod_createproduct_* (`set_owner $item [get_owner this]`, z_work_prod.tcl:167/184/204;
        // the gnome itself is owner 0 per the start template rows,
        // splt_urw_unq_start_tBB.tcl:239-240), is boxed (genericprod.tcl packtobox), delivered
        // and unboxed (prod_buildup_new, z_work_common.tcl:403-410), and flips built on the
        // completion edge (`set_buildupstate this [buildup_step > max]`, genericprod.tcl:812-813)
        // — only that edge may attach the flag; packing removes it (FUN_005edea0:92-104).
        [Test]
        public void ProducerChain_PlaceDeliverComplete_FlagAppears_PackRemoves()
        {
            var (interpreter, context) = BuildHost();
            string gnome = interpreter.Eval("new PlainBuilding");
            var producer = context.GetEntity(gnome)!;
            Assert.That(producer.Owner, Is.EqualTo(-1), "spawn default is ownerless (7cdd53a)");
            interpreter.Eval($"set_owner {gnome} 0");

            string item = interpreter.Eval("new FlaggedBuilding");
            var entity = context.GetEntity(item)!;
            Assert.That(entity.Owner, Is.EqualTo(-1), "the product spawns ownerless too");
            string producerOwner = interpreter.Eval($"get_owner {gnome}").Trim();
            interpreter.Eval($"set_owner {item} {producerOwner}");
            interpreter.Eval($"set_boxed {item} 1");
            Assert.That(entity.GameObject!.transform.Find(BuildingClanFlag.ChildName), Is.Null,
                "a boxed crate never carries the flag");

            interpreter.Eval($"set_boxed {item} 0");
            interpreter.Eval($"set_buildupstate {item} 0");
            Assert.That(entity.GameObject!.transform.Find(BuildingClanFlag.ChildName), Is.Null,
                "under construction: still no flag");

            interpreter.Eval($"set_buildupstate {item} 1");
            var flag = RequireFlag(entity);
            Assert.That(flag.OwnerVariant, Is.EqualTo(context.OwnerRace[0]));

            interpreter.Eval($"set_boxed {item} 1");
            Assert.That(entity.GameObject!.transform.Find(BuildingClanFlag.ChildName), Is.Null,
                "packing removes the flag");
        }

        // Ownerless build (e.g. a story-summoned building never assigned an owner): built but
        // owner −1 must never flag — the native producer gate (FUN_005edea0:92-104).
        [Test]
        public void OwnerlessBuilding_NeverFlags_EvenWhenBuilt()
        {
            var (interpreter, context) = BuildHost();
            string handle = interpreter.Eval("new FlaggedBuilding");
            var entity = context.GetEntity(handle)!;

            interpreter.Eval($"set_buildupstate {handle} 1");

            Assert.That(entity.Owner, Is.EqualTo(-1));
            Assert.That(entity.GameObject!.transform.Find(BuildingClanFlag.ChildName), Is.Null);
        }

        [Test]
        public void PresentationRefresh_AttachesFlagFromRestoredNativeState()
        {
            var (interpreter, context) = BuildHost();
            string handle = interpreter.Eval("new FlaggedBuilding");
            var entity = context.GetEntity(handle)!;
            context.OwnerRace[4] = 4;
            entity.Owner = 4;
            entity.BuildupComplete = true;

            interpreter.Eval($"set_visibility {handle} 1");

            var flag = RequireFlag(entity);
            Assert.That(flag.OwnerVariant, Is.EqualTo(4));
            Assert.That(flag.TextureName, Is.EqualTo("fahne_schwarz.tga"));
        }

        private (TclInterpreter interpreter, DigglesTclContext context) BuildHost()
        {
            var interpreter = new TclInterpreter();
            var context = DigglesTclEngine.Register(interpreter);
            var root = new GameObject("BuildingClanFlagTests");
            _roots.Add(root);
            context.SpawnParent = root.transform;
            context.EntitySpawner = new TestSpawner();
            interpreter.Eval(
                "def_class FlaggedBuilding none production 0 {} { class_flagoffset 2.5 4.0 }");
            interpreter.Eval("def_class PlainBuilding none production 0 {} {}");
            interpreter.Eval(@"def_class InitializedFlaggedBuilding none production 0 {} {
                class_flagoffset 1.0 2.0
                obj_init {
                    set_owner this 2
                    set_buildupstate this 1
                }
            }");
            return (interpreter, context);
        }

        private static BuildingClanFlag RequireFlag(DigglesTclEntity entity)
        {
            var flag = entity.GameObject!.GetComponentInChildren<BuildingClanFlag>(true);
            Assert.That(flag, Is.Not.Null);
            return flag!;
        }
    }
}
