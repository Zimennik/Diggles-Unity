#nullable enable

namespace Diggles.Runtime
{
    using Diggles.Runtime.Tcl.Engine;
    using UnityEngine;

    // Runtime IDigglesEntitySpawner backed by DigglesModelLibrary (offset-binary→bbox
    // decode + lazy clip loading). Wired into a DigglesTclContext, `new ClassName...`
    // spawns a GameObject with a DigglesAnimator + DigglesAnimController configured from
    // the class's anim tables, so running Tcl (`action this anim...`) drives animation.
    //
    // Counterpart to the editor MarkerModelResolver spawn path, which keeps the legacy
    // decode for the campaign/world.
    public sealed class RuntimeLibrarySpawner : IDigglesEntitySpawner
    {
        readonly DigglesModelLibrary _lib;
        readonly Vector3 _modelEuler;
        readonly int _layer;
        readonly float _scale;

        //.3db models decode at their native bbox scale (~200 units tall); modelScale
        // brings them down. The default (DigglesModelLibrary.ModelToWorldScale) makes
        // library-spawned characters match the MarkerModelResolver spawn path exactly.
        public RuntimeLibrarySpawner(DigglesModelLibrary lib, Vector3 modelEuler, int layer = -1,
            float modelScale = DigglesModelLibrary.ModelToWorldScale)
        {
            _lib = lib;
            _modelEuler = modelEuler;
            _layer = layer;
            _scale = modelScale > 0f ? modelScale : DigglesModelLibrary.ModelToWorldScale;
        }

        public GameObject? Spawn(DigglesTclEntity ent, Transform parent)
        {
            string? obj = _lib.ResolveClass(ent.Class.Name)?.Obj;
            if (string.IsNullOrEmpty(obj) && _lib.GetObjDb(ent.Class.Name) != null)
                obj = ent.Class.Name;                 // class name is an object name
            if (string.IsNullOrEmpty(obj)) return null;

            var go = new GameObject($"{ent.Class.Name}#{ent.Handle}");
            if (parent != null) go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.rotation = Quaternion.Euler(_modelEuler);
            go.transform.localScale = Vector3.one * _scale;

            go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var animator = go.AddComponent<DigglesAnimator>();
            var ctrl = go.AddComponent<DigglesAnimController>();
            ctrl.Configure(_lib, animator, obj!);
            ctrl.SetTables(ent.Class.AnimMap, ent.Class.Animsets);

            if (_layer >= 0) SetLayerRecursive(go.transform, _layer);

            // Spawn pose: the class default anim (looping idle), else "standard".
            if (!string.IsNullOrEmpty(ent.Class.DefaultAnim))
                ctrl.PlayClip(ent.Class.DefaultAnim!, (DigglesAnimator.AnimMode)ent.Class.DefaultAnimMode);
            else
                ctrl.PlayClip("standard", DigglesAnimator.AnimMode.Loop);

            return go;
        }

        public void Destroy(GameObject go)
        {
            if (go != null) Object.Destroy(go);
        }

        public void ApplyPosition(GameObject go, Vector3 nativePos)
        {
            if (go != null) go.transform.localPosition = nativePos;
        }

        public void ApplyRotation(GameObject go, Vector3 nativeRotRadians)
        {
            // Orientation is owned by the model bake (_modelEuler); native yaw
            // could be folded in later. No-op for now.
        }

        public void ApplyAnim(GameObject go, string animName, int startFrame,
            DigglesAnimator.AnimMode mode)
        {
            if (go == null) return;
            var ctrl = go.GetComponent<DigglesAnimController>();
            if (ctrl != null) ctrl.PlayClip(animName, mode, startFrame);
        }

        static void SetLayerRecursive(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            for (int i = 0; i < t.childCount; i++) SetLayerRecursive(t.GetChild(i), layer);
        }
    }
}
