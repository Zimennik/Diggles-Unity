#nullable enable

namespace Diggles.Runtime.UI
{
    using System;
    using System.Collections.Generic;

    // Stable handle-to-row diff: survivors retain their row instance while desired order may
    // change. The controller applies Handles to sibling indices after every sync.
    public sealed class DiggleRoster<T> where T : class
    {
        readonly Dictionary<int, T> rows = new();
        readonly List<int> order = new();
        readonly List<int> nextOrder = new();
        readonly HashSet<int> desiredSet = new();

        public IReadOnlyList<int> Handles => order;
        public int Count => order.Count;

        public bool TryGet(int handle, out T row) => rows.TryGetValue(handle, out row!);

        // Returns true when the roster mutated (a row was created, released, or the order
        // changed) — callers use it to force nested layout-group rebuilds only when needed.
        public bool Sync(
            IReadOnlyList<int> desiredHandles,
            Func<int, T> create,
            Action<T>? release = null)
        {
            if (desiredHandles == null) throw new ArgumentNullException(nameof(desiredHandles));
            if (create == null) throw new ArgumentNullException(nameof(create));

            bool changed = false;
            nextOrder.Clear();
            desiredSet.Clear();
            for (int i = 0; i < desiredHandles.Count; i++)
            {
                int handle = desiredHandles[i];
                if (handle <= 0 || !desiredSet.Add(handle)) continue;
                if (!rows.ContainsKey(handle)) { rows.Add(handle, create(handle)); changed = true; }
                nextOrder.Add(handle);
            }

            for (int i = order.Count - 1; i >= 0; i--)
            {
                int handle = order[i];
                if (desiredSet.Contains(handle) || !rows.TryGetValue(handle, out T? row)) continue;
                rows.Remove(handle);
                release?.Invoke(row);
                changed = true;
            }

            if (!changed && order.Count == nextOrder.Count)
            {
                for (int i = 0; i < order.Count; i++)
                    if (order[i] != nextOrder[i]) { changed = true; break; }
            }

            order.Clear();
            order.AddRange(nextOrder);
            return changed;
        }

        public void Clear(Action<T>? release = null)
        {
            if (release != null)
                for (int i = 0; i < order.Count; i++)
                    if (rows.TryGetValue(order[i], out T? row)) release(row);
            rows.Clear();
            order.Clear();
            nextOrder.Clear();
            desiredSet.Clear();
        }
    }
}
