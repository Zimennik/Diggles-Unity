#nullable enable

namespace Diggles.Runtime
{
    using UnityEngine;

    // Slide-out-of-the-back-wall presentation for beautifier deco and cave-skin props
    // (clarification: +;
    // —; —).
    //
    // Ground truth on the NATIVE side (decompile):
    // • Spawn: the native POPS deco in (FUN_0058d370 spawn path has no ramp; the dummy classes
    // are single-anim def_class rows, animinit.tcl:59-256 — no emerge state). The smooth
    // emerge is a PORT PRESENTATION choice requested by the player.
    // • Removal: the native is NOT an in-place instant free — MapValidator's DUMMYDES phase
    // (FUN_005a46d0 label 0x10 → FUN_005eb7b0) finds objtype-dummy objects whose anim bbox
    // overlaps a carve dirty rect and calls FUN_005eae80: release render/light handles
    // (FUN_005ca8b0), then queue the object on the frame's deferred-destroy list guarded by
    // obj+0x404 — the object dies at the tick boundary, one piece at a time as the carve
    // front reaches it. ROUND-5 CORRECTION: the destroy DOES carry a VFX — FUN_005eae80's
    // first act is the MESH SHATTER (FUN_004845a0: current-frame + frustum gate + shatter
    // sound FUN_00453f00 → MeshDestructor FUN_004830a0: the mesh bursts into tessellated
    // textured shards). The round-3 "no VFX, debris-masked pop" claim here was WRONG — the
    // user's recollection was exact. The port's primary removal
    // presentation is DigglesTclEngine.ShatterEntity (set-43 fragment burst from the
    // current-frame vertices) + immediate delete; this component's BeginCrumble (dirt
    // burst + drop) remains only as the no-mesh FALLBACK, and the slide-back retreat stays
    // selectable via UseSlideRetreat.
    //
    // Axis: "out of the back wall" = along native z (the depth/elevation axis — the back wall of
    // a dug corridor is the remaining rock at low z; the front plane sits at z≈15..16). Native z
    // maps to WorldRoot-local Y (RuntimeWorldSpawner.NativeToLocal), so the slide is a pure
    // local-Y offset computed through the same mapping the spawn used.
    //
    // Play-mode only: the engine attaches this component only when a GameObject exists AND the
    // app is playing; headless/EditMode tests keep the instant path (deterministic asserts).
    public sealed class MapDecoSlide : MonoBehaviour
    {
        public const float SinkDepth = 2.5f;       // native units along z — buries boards & beams
        public const float EmergeSeconds = 1.0f;
        public const float RetreatSeconds = 0.6f;
        public const float CrumbleDrop = 0.9f;     // native units along +y = DOWN the ant farm
        public const float CrumbleSeconds = 0.28f;

        // Removal presentation switch (user recollection =, not a
        // slide back into the wall). Default = crumble: MapDecoRetire fires a brown-dirt burst
        // (particle set 20, FUN_004b2b50) and this component drops the piece with an ease-in
        // fall before the deferred delete. The native destroy has NO anim/VFX of its own to
        // copy (see the header), so the crumble is a cited approximation of the original's
        // carve-debris-masked pop; the slide-back retreat stays selectable here.
        public static bool UseSlideRetreat = false;

        Vector3 _rest;          // the piece's at-rest localPosition (spawn pose)
        Vector3 _delta;         // rest → target offset (sunk / dropped, local space)
        Vector3 _from;          // retreat/crumble start pose (captured at Begin*)
        float _t, _dur;
        int _mode;              // 0 idle, 1 emerge (sunk→rest), 2 retreat (from→sunk), 3 crumble
        bool _hasRest;
        System.Action? _onDone;

        static Vector3 SinkDelta(Vector3 nativePos) =>
            RuntimeWorldSpawner.NativeToLocal(nativePos - new Vector3(0f, 0f, SinkDepth))
            - RuntimeWorldSpawner.NativeToLocal(nativePos);

        // Native +y = down (game up = −tileY): the crumble fall vector in local space.
        static Vector3 DropDelta(Vector3 nativePos) =>
            RuntimeWorldSpawner.NativeToLocal(nativePos + new Vector3(0f, CrumbleDrop, 0f))
            - RuntimeWorldSpawner.NativeToLocal(nativePos);

        public static void BeginEmerge(GameObject go, Vector3 nativePos)
        {
            var s = go.GetComponent<MapDecoSlide>();
            if (s == null) s = go.AddComponent<MapDecoSlide>();
            s._rest = go.transform.localPosition;
            s._delta = SinkDelta(nativePos);
            s._hasRest = true;
            s._t = 0f; s._dur = EmergeSeconds; s._mode = 1;
            go.transform.localPosition = s._rest + s._delta;   // start buried
        }

        // The retreat runs from the CURRENT pose (an emerge may be in flight) to the sunk pose,
        // then fires onDone exactly once — the engine deletes the entity there (the deferred
        // destroy). If the GameObject dies early (scene teardown), OnDestroy still fires it.
        public static void BeginRetreat(GameObject go, Vector3 nativePos, System.Action onDone)
        {
            var s = go.GetComponent<MapDecoSlide>();
            if (s == null) s = go.AddComponent<MapDecoSlide>();
            if (!s._hasRest) { s._rest = go.transform.localPosition; s._hasRest = true; }
            s._delta = SinkDelta(nativePos);
            s._from = go.transform.localPosition;
            s._t = 0f; s._dur = RetreatSeconds; s._mode = 2;
            s._onDone = onDone;
        }

        // The crumble: a short accelerating drop from the current pose, then the deferred
        // delete (the dirt burst is fired by the engine at retire time).
        public static void BeginCrumble(GameObject go, Vector3 nativePos, System.Action onDone)
        {
            var s = go.GetComponent<MapDecoSlide>();
            if (s == null) s = go.AddComponent<MapDecoSlide>();
            s._from = go.transform.localPosition;
            s._delta = DropDelta(nativePos);
            s._t = 0f; s._dur = CrumbleSeconds; s._mode = 3;
            s._onDone = onDone;
        }

        void Update()
        {
            if (_mode == 0) return;
            _t += Time.deltaTime;
            float k = _dur <= 0f ? 1f : Mathf.Clamp01(_t / _dur);
            k = _mode == 3
                ? k * k                       // crumble: ease-in — an accelerating fall
                : k * k * (3f - 2f * k);      // emerge/retreat: smoothstep
            transform.localPosition = _mode switch
            {
                1 => Vector3.Lerp(_rest + _delta, _rest, k),
                2 => Vector3.Lerp(_from, _rest + _delta, k),
                _ => Vector3.Lerp(_from, _from + _delta, k),   // 3: fall from wherever it stood
            };
            if (_t < _dur) return;
            if (_mode >= 2) Finish();
            else _mode = 0;
        }

        void Finish()
        {
            _mode = 0;
            var cb = _onDone; _onDone = null;
            cb?.Invoke();
        }

        void OnDestroy()
        {
            // Never leak the entity delete if the GO dies before the retreat lands.
            if (_onDone == null) return;
            var cb = _onDone; _onDone = null;
            try { cb(); } catch (System.Exception) { /* teardown-tolerant */ }
        }
    }
}
