#nullable enable

namespace Diggles.Runtime.UI
{
    using UnityEngine;
    using UnityEngine.EventSystems;
    using UnityEngine.UI;

    // One inventory grid slot — lives on a slot prefab authored in the Unity editor. Empty slots
    // render the frame only (icon hidden). Click routes the item handle to the controller (a
    // crate opens placement via eventgen; drag-and-drop not yet implemented).
    public sealed class InventorySlotView : MonoBehaviour, IPointerClickHandler
    {
        [Header("Wire what your prefab has (all optional)")]
        [SerializeField] Image? icon;

        InventorySlot? _slot;
        System.Action<int>? _onClick;

        public void Bind(InventorySlot? slot, Sprite? sprite, System.Action<int> onClick)
        {
            _slot = slot;
            _onClick = onClick;
            if (icon != null)
            {
                icon.sprite = sprite;
                icon.enabled = sprite != null;
                icon.preserveAspect = true;
            }
        }

        public void OnPointerClick(PointerEventData e)
        {
            if (_slot == null || _onClick == null) return;
            _onClick(_slot.ItemHandle);
        }
    }
}
