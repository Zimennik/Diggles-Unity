#nullable enable

namespace Diggles.Runtime.UI
{
    using UnityEngine;

    internal static class DiggleUiBindings
    {
        public static Transform? FindDeep(Transform root, string objectName)
        {
            if (root.name == objectName) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform? found = FindDeep(root.GetChild(i), objectName);
                if (found != null) return found;
            }
            return null;
        }

        public static T? FindComponent<T>(Transform root, string objectName) where T : Component
        {
            Transform? child = FindDeep(root, objectName);
            return child != null ? child.GetComponent<T>() : null;
        }

        public static T? FindComponentBelow<T>(Transform root, string parentName, string objectName)
            where T : Component
        {
            Transform? parent = FindDeep(root, parentName);
            return parent != null ? FindComponent<T>(parent, objectName) : null;
        }

        public static void WarnMissingOnce(Object owner, ref bool warned, string message)
        {
            if (warned) return;
            warned = true;
            Debug.LogWarning(message, owner);
        }
    }
}
