#nullable enable

namespace Diggles.Runtime
{
    using System;
    using System.Collections.Generic;
    using UnityEngine;

    // class / action name → GUI icon Sprite. Encodes the original's icon-by-class-name convention,
    // proven by both the decompile (ViWinProduction fill FUN_00520620 resolves the product icon from
    // the product class via FUN_00508830) and info_prods.tcl, which spells it out in Tcl:
    // product icon = "data/gui/icons/$class.tga" (e.g. class "Farm" → Farm.tga)
    // unaffordable = "data/gui/icons/Red$class.tga"
    // actions = fixed names (unpack, energyon, energyoff, repair, aufbau, packtobox, …)
    // So the mapping is the file set itself: keyed by file name = class/action name. Generated
    // edit-time by Diggles ▸ UI ▸ Build Icon Library (bulk-imports data/gui/icons/* into
    // Textures/UI/Icons and fills `entries`). `overrides` win over `entries` and survive a regen.
    // Lookups are exact — the engine is case-sensitive; a missing icon returns null.
    [CreateAssetMenu(menuName = "Diggles/Icon Library", fileName = "IconLibrary")]
    public sealed class IconLibrary : ScriptableObject
    {
        [Serializable]
        public struct Entry
        {
            public string key;
            public Sprite? sprite;
        }

        [Tooltip("Auto-filled from data/gui/icons/* (key = file name). Rewritten by the builder.")]
        [SerializeField] Entry[] entries = Array.Empty<Entry>();
        [Tooltip("Manual overrides — win over the auto entries; preserved across a regen.")]
        [SerializeField] Entry[] overrides = Array.Empty<Entry>();

        Dictionary<string, Sprite>? _map;

        Dictionary<string, Sprite> Map
        {
            get
            {
                if (_map != null) return _map;
                _map = new Dictionary<string, Sprite>(StringComparer.Ordinal);
                foreach (var e in entries) if (!string.IsNullOrEmpty(e.key) && e.sprite != null) _map[e.key] = e.sprite;
                foreach (var e in overrides) if (!string.IsNullOrEmpty(e.key) && e.sprite != null) _map[e.key] = e.sprite;
                return _map;
            }
        }

        void OnEnable() => _map = null;   // rebuild after a regen / domain reload

        // Exact lookup by class or action name, e.g. Get("Farm") / Get("unpack") / Get("energyon").
        public Sprite? Get(string key) => key != null && Map.TryGetValue(key, out var s) ? s : null;

        // Product icon: buildable → "<class>"; not buildable → "Red<class>" (info_prods.tcl:77-79),
        // falling back to the base icon when no Red variant exists (caller may red-tint instead).
        public Sprite? GetProduct(string className, bool buildable)
        {
            if (!buildable)
            {
                var red = Get("Red" + className);
                if (red != null) return red;
            }
            return Get(className);
        }

        public bool Has(string key) => key != null && Map.ContainsKey(key);

#if UNITY_EDITOR
        // Editor builder entry point — replaces the auto entries, leaves `overrides` intact.
        public void EditorSetEntries(List<Entry> auto)
        {
            entries = auto.ToArray();
            _map = null;
        }
#endif
    }
}
