#nullable enable

namespace Diggles.Runtime
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using UnityEngine;

    public sealed class BuildingClanFlag : MonoBehaviour
    {
        public const string ChildName = "BuildingClanFlag";

        // CScapeIconMgr registers these single-player frames in owner-race order; slots 5..7
        // repeat green (FUN_004c2d40:340-477).
        private static readonly string[] s_singlePlayerTextureNames =
        {
            "fahne_blau.tga",
            "fahne_gruen.tga",
            "fahne_rot.tga",
            "fahne_weiss.tga",
            "fahne_schwarz.tga",
            "fahne_gruen.tga",
        };

        private static readonly Dictionary<string, Sprite> s_sprites =
            new(StringComparer.OrdinalIgnoreCase);

        private static Camera? s_camera;

        public int OwnerVariant { get; private set; } = -1;
        public Vector2 NativeOffset { get; private set; }
        public string TextureName { get; private set; } = "";

        internal static void Attach(
            GameObject owner,
            Vector2 nativeOffset,
            int ownerVariant,
            string dataRoot,
            bool visible)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            // Save-staging boundary: the candidate root is inactive until activation, and load
            // must not create presentation (only live producers may — the icon-lifecycle law).
            // The post-activation sweep in the load service re-runs the producer for restored
            // buildings, mirroring the native first CObj::Tick after gameload.
            if (!owner.activeInHierarchy) return;

            string textureName = TextureNameForOwnerVariant(ownerVariant);
            var childTransform = owner.transform.Find(ChildName);
            var child = childTransform != null
                ? childTransform.gameObject
                : new GameObject(ChildName);
            if (childTransform == null) child.transform.SetParent(owner.transform, false);

            // TryGetComponent, not `GetComponent ?? Add`: in the Editor a missing GetComponent
            // returns a fake-null stub that `??` (reference check) accepts, and the first member
            // access throws MissingComponentException (broke 37 headless tests).
            if (!child.TryGetComponent(out BuildingClanFlag flag))
                flag = child.AddComponent<BuildingClanFlag>();
            if (!child.TryGetComponent(out SpriteRenderer renderer))
                renderer = child.AddComponent<SpriteRenderer>();

            flag.OwnerVariant = ownerVariant;
            flag.NativeOffset = nativeOffset;
            flag.TextureName = textureName;
            renderer.sprite = LoadSprite(dataRoot, textureName);
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.enabled = visible;

            // Native anchor (CScapeIconMgr, FUN_004c3c90:46-59): icon = obj + {class+0xcec
            // offsetX on native x, DAT_00736f78 = 0.0 on native y, class+0xcf4 "height" on
            // native z}. The literal "1" of the old comment is the constant FUN_00613fc0:110-129
            // stores at class+0xcf0 — it is never read back for the position. The +height lands
            // on the native depth axis because the native draws scape icons as a projected
            // overlay: under the fixed camera tilt a deeper anchor projects higher on screen —
            // that is the flag's visual lift, and the overlay pass never depth-contests the
            // scene. The port renders a real depth-tested sprite, so the literal mapping buried
            // every positive-height flag inside the building/back wall (all real buildings,
            // heights 0.0–5.1, showed nothing) while the campfire's −1 "height" poked toward
            // the camera — the one phantom that was visible. Equivalent presentation in true 3D:
            // lift by height along the gameplay vertical (native −y) and bias the anchor 1 depth
            // unit toward the viewer so the owner's own mesh cannot occlude the quad (the depth
            // the phantom already demonstrably renders at).
            child.transform.localPosition =
                RuntimeWorldSpawner.NativeToLocal(
                    new Vector3(nativeOffset.x, -nativeOffset.y, -1f)) -
                RuntimeWorldSpawner.NativeToLocal(Vector3.zero);
            child.transform.localScale = Vector3.one;
            flag.FaceCamera();
        }

        internal static void Detach(GameObject owner)
        {
            if (owner == null) return;
            var child = owner.transform.Find(ChildName);
            if (child == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(child.gameObject);
            else UnityEngine.Object.DestroyImmediate(child.gameObject);
        }

        private static string TextureNameForOwnerVariant(int ownerVariant)
        {
            int index = ownerVariant is >= 0 and < 5 ? ownerVariant : 5;
            return s_singlePlayerTextureNames[index];
        }

        private void LateUpdate() => FaceCamera();

        private void FaceCamera()
        {
            // CScapeIconMgr draws the flag as a screen-facing quad, independent of model links.
            if (s_camera == null) s_camera = Camera.main;
            transform.rotation = s_camera != null
                ? s_camera.transform.rotation
                : Quaternion.identity;
        }

        private static Sprite? LoadSprite(string dataRoot, string textureName)
        {
            if (string.IsNullOrEmpty(dataRoot)) return null;
            try
            {
                string path = Path.GetFullPath(Path.Combine(
                    dataRoot,
                    "Texture",
                    "m064",
                    textureName));
                if (s_sprites.TryGetValue(path, out Sprite sprite)) return sprite;

                var result = TgaLoader.LoadFromFile(path);
                if (result == null) return null;
                result.Texture.wrapMode = TextureWrapMode.Clamp;
                sprite = Sprite.Create(
                    result.Texture,
                    new Rect(0f, 0f, result.Texture.width, result.Texture.height),
                    new Vector2(0.5f, 0.5f),
                    result.Texture.width);
                sprite.name = Path.GetFileNameWithoutExtension(textureName);
                s_sprites.Add(path, sprite);
                return sprite;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
