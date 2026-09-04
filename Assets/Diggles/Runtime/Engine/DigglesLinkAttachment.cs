#nullable enable

namespace Diggles.Runtime
{
    using UnityEngine;

    // Rides an object (hat / held item / weapon) on a parent's animation link point.
    // Attached to the child entity's root (outer) GameObject by the `link_obj` engine
    // command. Each frame it reads the parent's current link-point offset (model-local,
    // from DigglesAnimator) and places the child there. See Docs/HAT_LINK_SYSTEM.md.
    //
    // We re-parent this (the child outer) under the parent's inner "Model" transform —
    // which already carries the model's Q_x(-90) orientation + ModelToWorldScale — and
    // neutralize the child's own inner transform, so the child inherits the parent's
    // exact orientation/scale (no doubled rotation or scale) and del still cascades
    // (child inner stays under child outer stays under parent inner).
    public sealed class DigglesLinkAttachment : MonoBehaviour
    {
        DigglesAnimator? _parentAnim;
        int _linkId = DigglesLinkPoints.Head;

        // The pre-link hierarchy, captured on the first Attach so Detach can undo the
        // re-parenting. Without the restore a detached child GameObject stayed inside the
        // carrier's model subtree: frozen relative to the body (nothing drives it anymore),
        // and any later carrier-wide renderer sweep (a work-hat link_obj →
        // SyncRenderVisible(gnome)) re-enabled its renderers — a detached backpack would
        // reappear whenever the gnome changed hats.
        Transform? _origParent;
        Vector3 _origLocalPos;
        Quaternion _origLocalRot;
        Vector3 _origLocalScale = Vector3.one;
        Transform? _childInner;
        Vector3 _innerLocalPos;
        Quaternion _innerLocalRot;
        Vector3 _innerLocalScale = Vector3.one;
        bool _hasOrig;

        public void Attach(Transform parentInner, DigglesAnimator parentAnim, int linkId, Transform? childInner)
        {
            _parentAnim = parentAnim;
            _linkId = linkId;

            if (!_hasOrig)
            {
                _hasOrig = true;
                _origParent = transform.parent;
                _origLocalPos = transform.localPosition;
                _origLocalRot = transform.localRotation;
                _origLocalScale = transform.localScale;
                _childInner = childInner;
                if (childInner != null)
                {
                    _innerLocalPos = childInner.localPosition;
                    _innerLocalRot = childInner.localRotation;
                    _innerLocalScale = childInner.localScale;
                }
            }

            transform.SetParent(parentInner, worldPositionStays: false);
            transform.localRotation = Quaternion.identity;
            transform.localScale = Vector3.one;
            transform.localPosition = Vector3.zero;

            // The child's inner "Model" carries the spawner's Q_x(-90) + 0.005 scale;
            // neutralize it so it inherits the parent inner's transform instead.
            if (childInner != null)
            {
                childInner.localRotation = Quaternion.identity;
                childInner.localScale = Vector3.one;
                childInner.localPosition = Vector3.zero;
            }

            Place();
        }

        // Undo the re-parenting: child outer back under its original parent (the spawn root)
        // with its original local TRS, child inner gets the spawner chain back. Call before
        // destroying the component (CmdLinkObj unlink). A deleted original parent degrades
        // to scene root — the drop path re-places the object anyway (inv_rem ApplyPosition).
        public void Detach()
        {
            if (!_hasOrig) return;
            _hasOrig = false;
            if (_childInner != null)
            {
                _childInner.localPosition = _innerLocalPos;
                _childInner.localRotation = _innerLocalRot;
                _childInner.localScale = _innerLocalScale;
            }
            transform.SetParent(_origParent != null ? _origParent : null, worldPositionStays: false);
            transform.localPosition = _origLocalPos;
            transform.localRotation = _origLocalRot;
            transform.localScale = _origLocalScale;
        }

        void OnEnable() => DigglesFrameDriver.Register(this);    // central late tick
        void OnDisable() => DigglesFrameDriver.Unregister(this);

        // Called by DigglesFrameDriver.RunLatePhase (was a per-instance LateUpdate).
        internal void PlaceNow() => Place();

        void Place()
        {
            if (_parentAnim == null) return;
            // Orientation: the per-frame link rotation (head tilt/turn).
            _parentAnim.TryGetLinkRotation(_linkId, out var rot);   // = identity if unavailable
            transform.localRotation = rot;

            // Position: seat the object's origin at the raw link point, as the engine does. The hat
            // mesh is authored to extend forward and down over the skull from that point; seating on
            // the head-crown centroid (~21u ahead of link-4) would push the hat onto the face.
            if (_parentAnim.TryGetLinkOffset(_linkId, out var off))
                transform.localPosition = off;
        }
    }
}
