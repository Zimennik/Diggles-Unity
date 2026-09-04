#nullable enable

namespace Diggles.Runtime.Tcl.Engine
{
    using System;
    using System.Collections.Generic;

    // A Diggles class definition as registered via `def_class`.
    // Holds settings declared during the class body execution
    // (class_defaultanim, class_physic, etc.) plus the body itself for
    // potential per-instance init replay (Diggles' `obj_init` semantics).
    public sealed class DigglesClass
    {
        public string Name = "";
        public string Material = "none";       // material class arg (food, metal, stone, none)
        public string Kind = "";               // type/kind (gnome, monster, tool, material, info,...)
        public int Flag;
        public string[] Capabilities = Array.Empty<string>();
        public string Body = "";

        public string? DefaultAnim;            // "obj.anim" form (e.g. "mann.standard")
        public int DefaultAnimMode = 2;        // ANIM_LOOP (set_class_defaultanim 3rd arg)
        public string? DefaultMaterial;
        public string? DefaultTxtAnim;

        // Animation tables that the DigglesAnimController resolves logical states
        // into concrete clips against. Populated by the Tcl bridge:
        // set_class_anim <state> <obj>.<anim> → AnimMap[state]
        // set_class_animset <id> { {state <obj>.<anim>} } → Animsets[id][state]
        // Values are the raw "obj.anim" string (the controller splits on '.').
        public Dictionary<string, string> AnimMap = new();
        public Dictionary<int, Dictionary<string, string>> Animsets = new();
        public string? Physic;
        public string? Collision;
        public bool DisableScripting;
        public bool ViewInFog;
        // class+0xcc2 — the cave-skin flag, set only by `add_cave_skin_class` (FUN_00617570:67;
        // default 0 in the class ctor FUN_00601250:88). The map beautifier's cave-completion pass
        // (FUN_0058ba00) removes every live object of a flagged class inside a fully-dug cave rect
        // before spawning the fresh skins.
        public bool CaveSkin;

        // class_light {ox oy oz} range {r g b} [flag] — point light attached to each instance
        // (LightSrc markers, torches). null = no light. The spawner maps it to a UnityEngine.Light.
        public LightSpec? Light;

        // class_snaptowall — classdef+0x2c bit 0x8 (FUN_00614740:93-95) → obj flag 0x20000
        // (ctor FUN_005c7b70:80-82): the live wall z-settle (z = min fill over a ±¼-tile cross
        // + 0.01, re-probed every 16th tick — FUN_005cbf70:125-176). Misc_Light torches only.
        public bool SnapToWall;

        // class_objsnapclass <class> <range> — classdef+0xce4/+0xce8
        // (FUN_00617250). Objects of this class snap to the named target class within the
        // supplied native-unit range. The placement/link layer consumes this metadata.
        public string ObjectSnapClass = "";
        public float ObjectSnapRange;

        // class_fightdist <distance> — the preferred stand-off radius used by the native
        // fight place finder. GetAttackPos takes the larger radius of both participants.
        public float FightDistance = 1f;

        // Methods / handlers declared inside the body.
        // method NAME ARGS CODE → Methods["NAME"]
        // handle_event NAME CODE → EventHandlers["NAME"] (body is a script)
        // state NAME CODE → States["NAME"] (body is a script)
        // state_enter NAME CODE → StateEnter["NAME"]
        // state_leave NAME CODE → StateLeave["NAME"]
        public Dictionary<string, TclProc> Methods = new();
        // Procs defined while this class loaded (its class-def OR its obj_init `call …`).
        // In the original each object has its own interp, so `proc wait_time {}` in the
        // sequencer and `proc wait_time {seconds}` in a dwarf don't collide. Our interp shares
        // one global proc table, so the last-loaded clobbers the rest. We additionally record
        // procs per-class and resolve the active object's class first (global is the fallback),
        // restoring per-object proc isolation. See Docs/TCL_OBJECT_MODEL.md.
        public Dictionary<string, TclProc> Procs = new();
        public Dictionary<string, string> EventHandlers = new();
        public Dictionary<string, string> States = new();
        public Dictionary<string, string> StateEnter = new();
        public Dictionary<string, string> StateLeave = new();

        // Native state table order (class+0x88, stride 0x50): one descriptor per state name,
        // find-or-created by the first state/state_enter/state_leave that names it, in script
        // execution order. Save archives store the current/target state as the index into this
        // table (obj+0x200/+0x204, read back via FUN_00602170 name→index / FUN_0045f6d0
        // index→descriptor), so the port must reproduce the exact order to write/load.sav
        // state words. Names here are unique (find-or-create), unlike the three script dicts.
        public List<string> StateOrder = new();

        public void RegisterStateName(string name)
        {
            if (!StateOrder.Contains(name)) StateOrder.Add(name);
        }

        public int StateIndexOf(string? name)
            => string.IsNullOrEmpty(name) ? -1 : StateOrder.IndexOf(name!);

        public string? StateNameAt(int index)
            => index >= 0 && index < StateOrder.Count ? StateOrder[index] : null;

        // obj_init / obj_exit code blocks run on new / del.
        public string? InitCode;
        public string? ExitCode;

        // member-declared variable names (initialized on new).
        public HashSet<string> MemberVars = new();
        // Per-member default value captured from the class-def scope at `member` time. Most are ""
        // (no prior set), but e.g. `member ANIM_STILL ANIM_ONCE ANIM_LOOP` (after animclassinit set
        // them 0/1/2) carry those values — so an instance member doesn't shadow the genuine global
        // with "" (which made $ANIM_STILL read empty in obj_init → set_anim defaulted to Loop).
        public Dictionary<string, string> MemberDefaults = new();

        // inherit ‹classname›... — chain for method/event/state lookup.
        public List<string> Inherits = new();

        // class_particle emitter templates declared in the class body; replayed onto each instance at
        // spawn (CmdNew) so deco objects (waterfalls/crystals/lava/troll-smoke) auto-emit. Framework-
        // agnostic floats (the Tcl bridge builds Vector3s on replay).
        public List<ParticleEmitterDef> ParticleEmitters = new();
    }

    // One `class_particle <setId> {ox oy oz} {vx vy vz} <count> <rate>... <flag>` declaration.
    public sealed class ParticleEmitterDef
    {
        public int SetId;
        public float Ox, Oy, Oz;
        public float Vx, Vy, Vz;
        public int Count;
        public int Rate;
        public float Flag;
    }

    // Parsed `class_light` parameters (floats, framework-agnostic; the spawner maps to a
    // UnityEngine.Light). Offset is in native Diggles coords; colour components are 0..1.
    public sealed class LightSpec
    {
        public float Ox, Oy, Oz;   // offset (native)
        public float Range;        // radius (native/world units)
        public float R, G, B;      // colour 0..1
        public int Flag;           // trailing arg (e.g. -1)
    }
}
