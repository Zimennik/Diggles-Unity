#nullable enable

namespace Diggles.Runtime.Stats
{
    using System;
    using System.Collections.Generic;

    // Diggles defines every per-object numeric via `def_attrib <name> <min> <max>
    // <default> [label]`. They split into domains by name prefix:
    // atr_* → Vital (Hitpoints, Nutrition, Alertness, Mood …) — the living
    // condition that decays/regenerates and can kill the gnome.
    // exp_* → Skill (Holz, Stein, Kampf …) — grows gradually as the gnome works
    // (no discrete levels), carries a skill label.
    // else → Generic (weight, carrycap, Bp*, GnomeAge, counters/flags).
    public enum StatCategory { Generic, Vital, Skill }

    // One `def_attrib` definition: the range + spawn default + optional UI label.
    public readonly struct StatDef
    {
        public readonly string Name;
        public readonly double Min, Max, Default;
        public readonly string? Label;          // UI/skill label (Gesundheit, Holz…); null = unlabeled
        public readonly StatCategory Category;

        public StatDef(string name, double min, double max, double def, string? label)
        {
            Name = name; Min = min; Max = max; Default = def;
            Label = string.IsNullOrEmpty(label) ? null : label;
            Category = Classify(name);
        }

        public double Clamp(double v) => v < Min ? Min : (v > Max ? Max : v);

        public static StatCategory Classify(string name) =>
            name.StartsWith("atr_", StringComparison.Ordinal) ? StatCategory.Vital :
            name.StartsWith("exp_", StringComparison.Ordinal) ? StatCategory.Skill :
            StatCategory.Generic;
    }

    // Global definition table (one per Tcl session) — populated by `def_attrib`.
    public sealed class StatRegistry
    {
        readonly Dictionary<string, StatDef> _defs = new();
        readonly List<string> _order = new();

        public void Define(StatDef def)
        {
            // The native table (FUN_005da380) appends a new name / reuses the existing slot on
            // re-definition, so enumeration order = first-definition order. get_expattrib returns
            // names in that order and scripts copy skill values positionally against it
            // (z_methods.tcl:42-45, pzwerg.tcl:28) — keep it explicit.
            if (!_defs.ContainsKey(def.Name)) _order.Add(def.Name);
            _defs[def.Name] = def;
        }
        public bool TryGet(string name, out StatDef def) => _defs.TryGetValue(name, out def);
        public IReadOnlyDictionary<string, StatDef> All => _defs;
        public IReadOnlyList<string> DefinedInOrder => _order;

        // An undefined name has no spawn value (0) and is left unclamped — matches
        // the engine treating reads of undeclared attributes as 0.
        public double DefaultOf(string name) => _defs.TryGetValue(name, out var d) ? d.Default : 0.0;
        public double Clamp(string name, double v) => _defs.TryGetValue(name, out var d) ? d.Clamp(v) : v;
        public StatCategory CategoryOf(string name)
            => _defs.TryGetValue(name, out var d) ? d.Category : StatDef.Classify(name);
    }

    // Per-entity value store (the typed AttributeSet). Values absent here read as
    // their registered default (lazy) — a freshly spawned dwarf already "has"
    // Hitpoints 1.0 without eager seeding. Writes clamp to [min,max].
    public sealed class StatValues
    {
        readonly Dictionary<string, double> _v = new();

        public bool Has(string name) => _v.ContainsKey(name);

        public double Get(string name, StatRegistry reg)
            => _v.TryGetValue(name, out var x) ? x : reg.DefaultOf(name);

        public double Set(string name, double value, StatRegistry reg)
        {
            double c = reg.Clamp(name, value);
            _v[name] = c;
            return c;
        }

        public double Add(string name, double delta, StatRegistry reg)
            => Set(name, Get(name, reg) + delta, reg);

        public IReadOnlyDictionary<string, double> Raw => _v;
    }
}
