#nullable enable

namespace Diggles.Runtime.Stats
{
    // Common base for a stat domain. Subsystems (attributes, skills) share the
    // definition registry and per-entity clamped/defaulted access; what differs is
    // which category they own and — in the future — their behaviour. The `Tick`
    // hook is the seam where a clean-C# behaviour (needs decay/death, skill growth)
    // will plug in. Today that behaviour is Tcl-driven, so Tick is a no-op: the Tcl
    // scripts call get/set/add_attrib, which route through these systems.
    public abstract class StatSystem
    {
        protected readonly StatRegistry Registry;
        protected StatSystem(StatRegistry registry) { Registry = registry; }

        public abstract bool Owns(StatCategory category);
        public bool Owns(string name) => Owns(Registry.CategoryOf(name));

        public double Get(StatValues vals, string name) => vals.Get(name, Registry);
        public double Set(StatValues vals, string name, double v) => vals.Set(name, v, Registry);
        public double Add(StatValues vals, string name, double delta) => vals.Add(name, delta, Registry);

        // Future C# behaviour seam (decay / regen / growth). No-op while Tcl drives it.
        public virtual void Tick(StatValues vals, double dt) { }
    }

    // Vitals + generic per-object data. Decay/regen/death currently live in the
    // class Tcl (z_events.tcl etc.); a future Needs behaviour overrides Tick.
    public sealed class AttributeSystem : StatSystem
    {
        public AttributeSystem(StatRegistry r) : base(r) { }
        public override bool Owns(StatCategory c) => c == StatCategory.Vital || c == StatCategory.Generic;
    }

    // Skills (exp_*). Diggles has no discrete level-ups — a skill rises gradually
    // while the gnome performs the matching work. Tcl drives that today via
    // add_attrib; `Train` names the operation so a future C# progression curve can
    // replace it without touching callers.
    public sealed class SkillSystem : StatSystem
    {
        public SkillSystem(StatRegistry r) : base(r) { }
        public override bool Owns(StatCategory c) => c == StatCategory.Skill;
        public double Train(StatValues vals, string name, double amount) => Add(vals, name, amount);
    }

    // Session facade: the shared registry + the two systems + def_attrib intake and
    // name→owning-system routing for the Tcl bridge. Lives on DigglesTclContext.
    public sealed class StatService
    {
        public StatRegistry Registry { get; } = new();
        public AttributeSystem Attributes { get; }
        public SkillSystem Skills { get; }

        public StatService()
        {
            Attributes = new AttributeSystem(Registry);
            Skills = new SkillSystem(Registry);
        }

        public StatSystem SystemFor(string name) => Skills.Owns(name) ? (StatSystem)Skills : Attributes;

        // def_attrib <name> <min> <max> <default> [label]
        public void Define(string name, double min, double max, double def, string? label)
            => Registry.Define(new StatDef(name, min, max, def, label));

        // Convenience: route a get/set/add by name to the owning system.
        public double Get(StatValues vals, string name) => SystemFor(name).Get(vals, name);
        public double Set(StatValues vals, string name, double v) => SystemFor(name).Set(vals, name, v);
        public double Add(StatValues vals, string name, double delta) => SystemFor(name).Add(vals, name, delta);

        // Future: tick every domain's C# behaviour. No-op today (Tcl drives it).
        public void Tick(StatValues vals, double dt) { Attributes.Tick(vals, dt); Skills.Tick(vals, dt); }
    }
}
