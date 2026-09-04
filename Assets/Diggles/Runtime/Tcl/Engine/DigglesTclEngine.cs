#nullable enable

namespace Diggles.Runtime.Tcl.Engine
{
    using System.Collections.Generic;
    using System.Globalization;
    using System.Runtime.CompilerServices;
    using Diggles.Runtime.Save;
    using Diggles.Runtime.Sim;
    using UnityEngine;

    // Registers Diggles-engine-aware commands on a TclInterpreter.
    // After calling `Register`, the interpreter can run Diggles scripts
    // that declare classes, spawn objects, set positions / anims, etc.
    public static class DigglesTclEngine
    {
        public static DigglesTclContext Register(TclInterpreter interp)
        {
            var ctx = new DigglesTclContext(interp);

            // Per-object proc isolation (the original gives each object its own interp). A proc
            // defined while a class loads — its class-def, or its obj_init `call …` — is recorded
            // on that class; the active object's class procs are resolved before the shared global
            // table. Without this, e.g. a dwarf's `proc wait_time {seconds}` clobbers the
            // sequencer's `proc wait_time {}`, breaking do_wait pacing (cutscene races, audio cuts).
            interp.OnProcRegistered = proc =>
            {
                var cls = ctx.CurrentClass ?? ctx.This?.Class;
                if (cls != null) cls.Procs[proc.Name] = proc;
            };
            interp.ResolveClassProc = name =>
            {
                var cls = ctx.This?.Class ?? ctx.CurrentClass;
                return cls != null ? ctx.FindProc(cls, name) : null;
            };

            FightSystem.RegisterCommands(ctx, interp);
            AiSystem.RegisterCommands(ctx, interp);
            EnergySystem.RegisterCommands(ctx, interp);
            FluidSystem.RegisterCommands(ctx, interp);
            ProductionSlotOverrides.RegisterCommands(ctx, interp);
            NewsTickerSystem.RegisterCommands(ctx, interp);
            DocumentWindowSystem.RegisterCommands(ctx, interp);

            // ----- class definition / lifecycle -----

            interp.RegisterCommand("def_class", (i, a) => CmdDefClass(ctx, a));
            interp.RegisterCommand("def_idiobjclass", (i, a) => CmdDefIdiObjClass(ctx, a));
            interp.RegisterCommand("class_defaultanim", (i, a) => CmdClassDefaultAnim(ctx, a));
            interp.RegisterCommand("class_defaultmaterial", (i, a) => CmdSetField(ctx, a, "DefaultMaterial"));
            interp.RegisterCommand("class_defaulttxtanim", (i, a) => CmdSetField(ctx, a, "DefaultTxtAnim"));
            interp.RegisterCommand("class_physic", (i, a) => CmdSetField(ctx, a, "Physic"));
            interp.RegisterCommand("class_collision", (i, a) => CmdSetField(ctx, a, "Collision"));
            interp.RegisterCommand("class_disablescripting", (i, a) => { RequireClass(ctx).DisableScripting = true; return ""; });
            // class_viewinfog <bool> — class default of the "stays drawn in gray fog" flag
            // (FUN_00613b20 → classdef+0x2c bit 0). The original parses the argument, so honor it.
            interp.RegisterCommand("class_viewinfog", (i, a) => { RequireClass(ctx).ViewInFog = a.Length < 2 || ParseTclBool(a[1]); return ""; });
            interp.RegisterCommand("set_class_anim", (i, a) => CmdSetClassAnim(ctx, a));
            interp.RegisterCommand("set_class_animset", (i, a) => CmdSetClassAnimset(ctx, a));
            interp.RegisterCommand("class_light", (i, a) => CmdClassLight(ctx, a));
            interp.RegisterCommand("class_objsnapclass", (i, a) => CmdClassObjSnapClass(ctx, a));
            interp.RegisterCommand("class_fightdist", (i, a) =>
            {
                if (a.Length > 1) RequireClass(ctx).FightDistance = ParseFloat(a[1]);
                return "";
            });
            interp.RegisterCommand("class_physcategory", (i, a) => "");
            // class_snaptowall <bool> — classdef+0x2c bit 0x8 (FUN_00614740:93-95). The object
            // constructor maps it to obj flag 0x20000 (FUN_005c7b70:80-82) = the live z-settle:
            // every 16th tick the object's z snaps to min(fill over a ±¼-tile cross)+0.01 — the
            // exposed wall face (FUN_005cbf70:125-176). Only Misc_Light sets it (emitter.tcl:10);
            // the port runs the settle in TunnelTorchTick. Docs/TUNNEL_TORCH_LIGHTING_RE.md §5a.
            interp.RegisterCommand("class_snaptowall", (i, a) =>
            { RequireClass(ctx).SnapToWall = a.Length < 2 || ParseTclBool(a[1]); return ""; });
            interp.RegisterCommand("class_flagoffset", (i, a) => CmdClassFlagOffset(ctx, a));
            interp.RegisterCommand("class_particle", (i, a) => CmdClassParticle(ctx, a));
            // add_cave_skin_class <set> <detail> <size> — CTclClassDefMethods::AddCaveSkinClass
            // (FUN_00617570): flags the defining class as a cave skin (class+0xcc2 = 1,:67) and
            // appends a {set, size, detail, class} rule to the beautifier's skin table (+0x28,
            //:146-168). Args per animinit.tcl:428 `add_cave_skin_class $iSet $iDetail $iSize`
            // (Urw/Met/Kris/Golden = 0..3; detail a/b/c = 2/1/0; size caves_1..4 = 0..3).
            interp.RegisterCommand("add_cave_skin_class", (i, a) =>
            {
                if (a.Length != 4) throw new TclException(
                    "wrong # args: should be \"add_cave_skin_class set detail size\"");
                var cls = RequireClass(ctx);                    // native: "no class defined." (:62-63)
                cls.CaveSkin = true;                            // class+0xcc2 (:67)
                ctx.CaveSkinRules.Add(new DigglesTclContext.CaveSkinRule
                {
                    Set = IntArg(a, 1, 0),                      // entry+0xc (:167)
                    Detail = IntArg(a, 2, 0),                   // entry+0x14 (:164)
                    Size = IntArg(a, 3, 0),                     // entry+0x10 (:168)
                    ClassName = cls.Name,                       // entry+0x18 = class id (:163)
                });
                return "";
            });

            // ----- class lifecycle: method / state / events / inherit -----

            interp.RegisterCommand("in_class_def", (i, a) => ctx.CurrentClass != null ? "1" : "0");

            interp.RegisterCommand("method", (i, a) => CmdMethod(ctx, a));
            interp.RegisterCommand("method_const", (i, a) => CmdMethod(ctx, a));
            interp.RegisterCommand("method_static", (i, a) => CmdMethod(ctx, a));

            interp.RegisterCommand("def_event", (_, a) =>
            {
                if (a.Length != 2) throw new TclException("wrong # args: should be \"def_event name\"");
                if (a[1].Length == 0) throw new TclException("def_event requires a non-empty name");
                ctx.RegisteredEventNames.Add(a[1]);
                return "";
            });
            interp.RegisterCommand("handle_event", (i, a) => CmdHandleEvent(ctx, a));
            interp.RegisterCommand("clone_event", (i, a) => "");    // payload-mutating clone TBD
            interp.RegisterCommand("set_event",   (i, a) => CmdSetEvent(ctx, a));
            interp.RegisterCommand("event_get",   (i, a) => CmdEventGet(ctx, a));
            // event_generator {objpar subpar pospar txtpar} {body} — registers the per-click
            // context-command decision script (Scripts/init/eventgen.tcl:11, loaded by
            // classinit.tcl:111). evtgen_attrib fills the pending event spec the UI fires on a
            // -command click. See CmdEventGenerator/EvtGenRun.
            interp.RegisterCommand("event_generator", (i, a) => CmdEventGenerator(ctx, a));
            interp.RegisterCommand("evtgen_attrib",   (i, a) => CmdEvtGenAttrib(ctx, a));
            interp.RegisterCommand("set_eventenabled", (i, a) => "");
            interp.RegisterCommand("get_eventenabled", (i, a) => "0");
            interp.RegisterCommand("timer_event", (i, a) => CmdTimerEvent(ctx, a));
            interp.RegisterCommand("timer_unset", (i, a) => CmdTimerUnset(ctx, a));
            // exec_deferred <script> — enqueue for the next shell tick, never the current Eval.
            // Trigger_Tournament uses this separation while replacing the Oberwelt script.
            interp.RegisterCommand("exec_deferred", (i, a) =>
            {
                if (a.Length != 2)
                    throw new TclException("wrong # args: should be \"exec_deferred script\"");
                ctx.DeferredScripts.Enqueue(a[1]);
                return "";
            });
            // gametime start|stop|reset|factor [F] — FUN_0061ece0 "TCL:CTclGlobalMethods::GameTime":
            // start → running byte +0xa098 = 1; stop → 0; reset → zero the accumulator +0xa088;
            // factor (no arg) → return the current factor (+0xa090); factor F → write it.
            // The factor scales the whole game-time flow (FUN_005653f0 accum += factor*dt paces
            // SrvDoThink) — DigglesGameSim.TimeFactor mirrors that. start_001's slow-mo
            // (`gametime factor 0.3` … `factor 1`), Trigger_StartScreen's defensive `factor 1`
            // and the drain line `+gametime factor 1` all hit this. No-arg form kept returning
            // GameTime for port-internal compatibility (the native form errors here).
            interp.RegisterCommand("gametime",    (i, a) =>
            {
                var sim = ctx.Sim;
                if (sim == null || a.Length < 2)
                    return (sim?.GameTime ?? 0.0).ToString("R", CultureInfo.InvariantCulture);
                switch (a[1])
                {
                    case "start": sim.ClockRunning = true;  return "";
                    case "stop":  sim.ClockRunning = false; return "";
                    case "reset": sim.GameTime = 0.0;       return "";
                    case "factor":
                        if (a.Length > 2)
                        {
                            sim.TimeFactor = double.TryParse(a[2], NumberStyles.Float,
                                CultureInfo.InvariantCulture, out var f) ? f : sim.TimeFactor;
                            return "";
                        }
                        return sim.TimeFactor.ToString("R", CultureInfo.InvariantCulture);
                    default:
                        return sim.GameTime.ToString("R", CultureInfo.InvariantCulture);
                }
            });
            // gethours — game time in clock hours: gettime / 150 (DAT_00737630 = 150.0f;
            // FUN_0068b790:110, FUN_0050ab10:669). One day = 12 h = 1800 s. The gnome init uses
            // [gethours]<2.0 / <1.0 as the "just-spawned campaign" window. gethours divides by
            // the same constant as the worktime gate, so work windows, hour-of-day triggers and
            // the dial hand advance together.
            interp.RegisterCommand("gethours",    (i, a) =>
                ((ctx.Sim?.GameTime ?? 0.0) / Diggles.Runtime.Sim.ProdDispatcher.WorktimeHourSeconds)
                    .ToString("R", CultureInfo.InvariantCulture));

            // obj_query <obj> <filters…> → Tcl list of matching handles, or "0" if none.
            // auto_choose_gender <obj> → "male"/"female" balancing the owner's dwarf population.
            // Together with set_objgender/-flagpos these drive faithful gender + clothing assignment.
            interp.RegisterCommand("obj_query", (i, a) => CmdObjQuery(ctx, a));
            interp.RegisterCommand("auto_choose_gender", (i, a) => CmdAutoChooseGender(ctx, a));

            interp.RegisterCommand("state",              (i, a) => CmdState(ctx, a));
            interp.RegisterCommand("state_enter",        (i, a) => CmdStateEnter(ctx, a));
            interp.RegisterCommand("state_leave",        (i, a) => CmdStateLeave(ctx, a));
            interp.RegisterCommand("state_trigger",      (i, a) => CmdStateTrigger(ctx, a, fresh: false));
            interp.RegisterCommand("state_triggerfresh", (i, a) => CmdStateTrigger(ctx, a, fresh: true));
            // state_enable / state_disable are absolute writes in the original, not a refcount:
            // StateEnable (FUN_0069f400:52) sets obj+0x208 = 1, StateDisable (FUN_0069f5a0:52)
            // sets it = 0, and HandleStates (FUN_005cac90:27) gates on `+0x208 > 0`. Scripts rely
            // on that — zwerg.tcl:308 does a bare `state_enable this` with no matching disable
            // before state_triggerfresh. Port mapping: enabled = StateEnabledCount >= 0 (sim
            // tests `< 0`), so enable writes 0 and disable writes -1.
            interp.RegisterCommand("state_enable",       (i, a) => { var e = ctx.ResolveEntity(a[1]); e.StateEnabledCount = 0;  e.StateWordsDirty = true; WorkLog(ctx, e, "state_enable"); return ""; });
            interp.RegisterCommand("state_disable",      (i, a) => { var e = ctx.ResolveEntity(a[1]); e.StateEnabledCount = -1; e.StateWordsDirty = true; WorkLog(ctx, e, "state_disable"); return ""; });

            // park_obj_actions { classlist } — a cutscene freezes the autonomous AI of all
            // objects of the given classes so their sparetime/idle/work doesn't fight the
            // sequence (the sim skips state-thinking for parked objects; direct eactions still
            // run). restore_obj_actions (shut_sequencer) un-freezes; remove_parked_obj clears
            // one object (add_actor un-parks each preset actor, then freezes it via state_disable).
            interp.RegisterCommand("park_obj_actions", (i, a) =>
            {
                if (a.Length < 2) return "";
                var classes = Diggles.Runtime.Tcl.TclList.Parse(a[1]);
                if (classes.Count == 0) return "";
                int parked = 0;
                foreach (var e in ctx.Entities.Values)
                {
                    if (e.Class == null) continue;
                    foreach (var c in classes)
                        if (ClassIsA(ctx, e.Class, c)) { e.Parked = true; parked++; break; }
                }
                if (Diggles.Runtime.CutsceneTrace.Enabled)
                    Diggles.Runtime.CutsceneTrace.Log($"park_obj_actions [{string.Join(",", classes)}] → parked {parked} of {ctx.Entities.Count}");
                return "";
            });
            interp.RegisterCommand("restore_obj_actions", (i, a) =>
            {
                foreach (var e in ctx.Entities.Values) e.Parked = false;
                return "";
            });
            // remove_parked_obj only drops the object's saved parked action so restore_obj_actions
            // won't restore it later — it does not un-freeze the state machine. add_actor calls it
            // on each preset cutscene actor, which must stay frozen (driven only by eactions) until
            // shut_sequencer's restore_obj_actions. We don't track saved actions, so this is a
            // no-op; it must not clear Parked, which would let the actor's AI fight the cutscene.
            interp.RegisterCommand("remove_parked_obj", (i, a) => "");

            // ----- engine action layer (CAction) + task list -----
            interp.RegisterCommand("action", (i, a) => CmdAction(ctx, a));
            interp.RegisterCommand("tasklist_add",      (i, a) => { if (a.Length > 2) { var e = ctx.ResolveEntity(a[1]); e.TaskList.Add(a[2]); e.TaskListDirty = true; } return ""; });
            interp.RegisterCommand("tasklist_addfront", (i, a) => { if (a.Length > 2) { var e = ctx.ResolveEntity(a[1]); e.TaskList.Insert(0, a[2]); e.TaskListDirty = true; } return ""; });
            interp.RegisterCommand("tasklist_get",      (i, a) => CmdTasklistGet(ctx, a));
            interp.RegisterCommand("tasklist_cnt",      (i, a) => ctx.ResolveEntity(a[1]).TaskList.Count.ToString(CultureInfo.InvariantCulture));
            interp.RegisterCommand("tasklist_clear",    (i, a) => { var e = ctx.ResolveEntity(a[1]); e.TaskList.Clear(); e.TaskListDirty = true; return ""; });
            // tasklist_rem <obj> <index> — removes by index, not by value (z_work_states.tcl:
            // `tasklist_rem this 0` after `tasklist_get this 0`; z_events.tcl uses `$i`).
            interp.RegisterCommand("tasklist_rem", (i, a) =>
            {
                if (a.Length > 2)
                {
                    var e = ctx.ResolveEntity(a[1]);
                    var list = e.TaskList;
                    e.TaskListDirty = true;
                    string popped = "?";
                    if (int.TryParse(a[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx))
                    { if (idx >= 0 && idx < list.Count) { popped = list[idx]; list.RemoveAt(idx); } }
                    else list.Remove(a[2]);   // tolerant fallback for any non-numeric caller
                    if (WorkDebug)   // guard: the string build must not run when the trace is off (hot path)
                        WorkLog(ctx, e, "POP \"" + (popped.Length > 40 ? popped.Substring(0, 40) + "…" : popped) + "\"");
                }
                return "";
            });
            interp.RegisterCommand("set_processactions",(i, a) => { ctx.ResolveEntity(a[1]).ProcessActions = a.Length < 3 || ParseTclBool(a[2]); return ""; });
            interp.RegisterCommand("get_processactions",(i, a) => ctx.ResolveEntity(a[1]).ProcessActions ? "1" : "0");
            // lnand <list1> <list2> → list2 minus list1 (elements of list2 not in list1) —
            // set difference. Used by seq_idle (avoid repeating the last idle) and the gnome
            // gender/clothing logic (strip the "0" empty-sentinel; anti-dup outfits).
            interp.RegisterCommand("lnand", (i, a) =>
            {
                if (a.Length < 3) return a.Length > 2 ? a[2] : "";
                var remove = new System.Collections.Generic.HashSet<string>(TclList.Parse(a[1]));
                var items = TclList.Parse(a[2]);
                items.RemoveAll(x => remove.Contains(x));
                return TclList.Format(items);
            });
            // land <list1> <list2> → list1 ∩ list2 (set intersection).
            interp.RegisterCommand("land", (i, a) =>
            {
                if (a.Length < 3) return "";
                var l1 = new System.Collections.Generic.HashSet<string>(TclList.Parse(a[1]));
                var items = TclList.Parse(a[2]);
                items.RemoveAll(x => !l1.Contains(x));
                return TclList.Format(items);
            });
            interp.RegisterCommand("state_reset",        (i, a) => { var e = ctx.ResolveEntity(a[1]); e.StateEnabledCount = 0; e.StateWordsDirty = true; WorkLog(ctx, e, "state_reset"); return ""; });
            interp.RegisterCommand("state_get",          (i, a) => ctx.ResolveEntity(a[1]).CurrentState ?? "");
            interp.RegisterCommand("state_getenablecnt", (i, a) => ctx.ResolveEntity(a[1]).StateEnabledCount.ToString());

            interp.RegisterCommand("obj_init", (i, a) => { RequireClass(ctx).InitCode = a.Length > 1 ? a[1] : ""; return ""; });
            interp.RegisterCommand("obj_exit", (i, a) => { RequireClass(ctx).ExitCode = a.Length > 1 ? a[1] : ""; return ""; });
            interp.RegisterCommand("member",   (i, a) =>
            {
                var c = RequireClass(ctx);
                for (int k = 1; k < a.Length; k++)
                {
                    c.MemberVars.Add(a[k]);
                    // Capture the var's class-def-scope value as the per-instance default (0/1/2 for
                    // the ANIM_* consts set just above by animclassinit; "" for the rest).
                    string def = "";
                    try { def = i.GetVar(a[k]) ?? ""; } catch { def = ""; }
                    c.MemberDefaults[a[k]] = def;
                }
                return "";
            });
            interp.RegisterCommand("inherit",  (i, a) => { var c = RequireClass(ctx); for (int k = 1; k < a.Length; k++) c.Inherits.Add(a[k]); return ""; });

            interp.RegisterCommand("check_method", (i, a) =>
            {
                if (a.Length < 3 || !ctx.Classes.TryGetValue(a[1], out var c)) return "0";
                return ctx.FindMethod(c, a[2]) != null ? "1" : "0";
            });
            interp.RegisterCommand("call_method",        (i, a) => CmdCallMethod(ctx, a, isStatic: false));
            interp.RegisterCommand("call_method_static", (i, a) => CmdCallMethod(ctx, a, isStatic: true));

            // ----- object instantiation -----

            interp.RegisterCommand("new", (i, a) => CmdNew(ctx, a));
            interp.RegisterCommand("qnew", (i, a) => CmdNew(ctx, a));  // same surface for now
            interp.RegisterCommand("del", (i, a) => CmdDel(ctx, a));
            interp.RegisterCommand("obj_clear", (i, a) => CmdObjClear(ctx));
            interp.RegisterCommand("destruct", (i, a) => CmdDestruct(ctx, a));
            interp.RegisterCommand("set_pf_influence", (i, a) => CmdSetPathInfluence(ctx, a));
            interp.RegisterCommand("create_doorlogic", (i, a) => CmdCreateDoorLogic(ctx, a));
            interp.RegisterCommand("set_doorproperties", (i, a) => CmdSetDoorProperties(ctx, a));
            interp.RegisterCommand("create_ladderlogic", (i, a) => CmdCreateLadderLogic(ctx, a));
            interp.RegisterCommand("set_ladderrange", (i, a) => CmdSetLadderRange(ctx, a));
            interp.RegisterCommand("create_elevatorlogic", (i, a) => CmdCreateElevatorLogic(ctx, a));
            interp.RegisterCommand("set_elevatorproperties", (i, a) => CmdSetElevatorProperties(ctx, a));
            interp.RegisterCommand("delete_transportlogic", (i, a) => CmdDeleteTransportLogic(ctx, a));
            interp.RegisterCommand("set_placesnapmode", (i, a) => CmdSetPlaceSnapMode(ctx, a));
            interp.RegisterCommand("get_placesnapmode", (i, a) => CmdGetPlaceSnapMode(ctx, a));
            interp.RegisterCommand("set_snaptowall", (i, a) => CmdSetSnapToWall(ctx, a));
            interp.RegisterCommand("get_snaptowall", (i, a) => CmdGetSnapToWall(ctx, a));
            interp.RegisterCommand("set_forceipol", (i, a) => CmdSetForceInterpolation(ctx, a));
            interp.RegisterCommand("get_forceipol", (i, a) => CmdGetForceInterpolation(ctx, a));

            // ----- position / movement -----

            interp.RegisterCommand("set_pos", (i, a) => CmdSetPos(ctx, a));
            interp.RegisterCommand("get_pos", (i, a) => CmdGetPos(ctx, a));
            interp.RegisterCommand("set_vel", (i, a) => CmdSetVel(ctx, a));
            interp.RegisterCommand("get_vel", (i, a) => CmdGetVel(ctx, a));

            // ----- animation -----

            interp.RegisterCommand("set_anim", (i, a) => CmdSetAnim(ctx, a));

            // ----- object queries / mutators -----

            // get_class_name / get_class_type / get_objclass / get_objname /
            // get_objgender / get_objtype — return string answers from the
            // entity's class. When no entity resolved (stub mode), return "".
            interp.RegisterCommand("get_class_name", (i, a) =>
            {
                if (a.Length < 2) return ctx.CurrentClass?.Name ?? "";
                var e = ctx.GetEntity(a[1]);
                return e?.Class?.Name ?? "";
            });
            interp.RegisterCommand("get_class_type", (i, a) =>
            {
                if (a.Length < 2) return ctx.CurrentClass?.Kind ?? "";
                // Every shipped caller passes a class name, never a handle: `get_class_type
                // $item` gates the boxed product path in every workshop's prod_item_actions
                // (feuerstelle.tcl:19/:37/:44 …), `get_class_type $classname`
                // (genericprod.tcl:100), `get_class_type [get_objclass $item]` (z_procs.tcl:332/
                //:606 — take_item's "buildings only as crates" test), std_ai.tcl:2333. Resolving
                // it as an entity handle returns "" → lsearch $BOXED_CLASSES "" = -1 → the
                // non-boxed branch. Class lookup first; the entity-handle form is kept for safety
                // (same shape as get_class_category).
                if (ctx.Classes.TryGetValue(a[1], out var cls)) return cls.Kind;
                var e = ctx.GetEntity(a[1]);
                return e?.Class?.Kind ?? "";
            });
            interp.RegisterCommand("get_class_category", (i, a) =>
            {
                if (a.Length < 2) return ctx.CurrentClass?.Material ?? "";
                // Every shipped caller passes a class name, not a handle: `get_class_category
                // $current_workclass` (z_events.tcl:1162 — the cook/work hat) and `[get_objclass
                // $item]` (z_work_common.tcl:543). Resolving it as an entity handle returns ""
                // for "Grillpilz" → prod_change_muetze "" → the default warning hat instead of
                // the "food" cook hat (def_class Grillpilz food …, rohstoffe.tcl:186). Class
                // lookup first; the entity-handle form is kept for callers passing a handle.
                if (ctx.Classes.TryGetValue(a[1], out var cls)) return cls.Material;
                var e = ctx.GetEntity(a[1]);
                return e?.Class?.Material ?? "";
            });
            interp.RegisterCommand("get_class_flags", (i, a) =>
            {
                if (a.Length < 2) return ctx.CurrentClass != null ? string.Join(" ", ctx.CurrentClass.Capabilities) : "";
                var e = ctx.GetEntity(a[1]);
                return e?.Class != null ? string.Join(" ", e.Class.Capabilities) : "";
            });
            interp.RegisterCommand("get_objclass", (i, a) =>
            {
                if (a.Length < 2) return "";
                var e = ResolveEntityOrNull(ctx, a[1]);
                return e?.Class?.Name ?? "";
            });
            interp.RegisterCommand("get_objname", (i, a) =>
            {
                if (a.Length < 2) return "";
                var e = ResolveEntityOrNull(ctx, a[1]);
                return e?.InstanceName ?? "";
            });
            // set_objname OBJ NAME | set_objname OBJ auto GENDER — writes the native display name
            // (obj+0xD8; the gnome-menu / info-panel name, serialized into saves). The `auto` form
            // draws a random name from Scripts/classes/zwerg/names.txt for the gender
            // (z_methods.tcl:255-259, pzwerg.tcl:16).
            interp.RegisterCommand("set_objname", (i, a) =>
            {
                if (a.Length < 3) return "";
                var e = ResolveEntityOrNull(ctx, a[1]);
                if (e == null) return "";
                string name = a[2];
                if (name == "auto")
                    name = PickAutoObjName(ctx, a.Length > 3 ? a[3] : e.Gender);
                e.InstanceName = name;
                return "";
            });
            // set_objinfo OBJ TEXTKEY — the native info string (obj+0x360; zwerg.tcl:154
            // `set_objinfo. EinZwerg`). Serialized in saves.
            interp.RegisterCommand("set_objinfo", (i, a) =>
            {
                if (a.Length < 3) return "";
                var e = ResolveEntityOrNull(ctx, a[1]);
                if (e != null) e.InfoString = a[2];
                return "";
            });
            interp.RegisterCommand("get_objinfo", (i, a) =>
            {
                if (a.Length < 2) return "";
                return ResolveEntityOrNull(ctx, a[1])?.InfoString ?? "";
            });
            // Gender flag (engine 0x3d0 male/female bits): set_objgender writes it,
            // get_objgender reads it, obj_query -flagpos male/female + auto_choose_gender use it.
            interp.RegisterCommand("set_objgender", (i, a) =>
            {
                var e = a.Length > 2 ? ResolveEntityOrNull(ctx, a[1]) : null;
                if (e != null)
                {
                    e.Gender = a[2] == "male" || a[2] == "female" ? a[2] : "neutral";
                    e.MarkNativeMutation(DigglesTclEntity.NativeMutationGender);
                }
                return "";
            });
            interp.RegisterCommand("get_objgender", (i, a) =>
                (a.Length > 1 ? ResolveEntityOrNull(ctx, a[1]) : null)?.Gender ?? "neutral");
            interp.RegisterCommand("get_objtype",   (i, a) =>
            {
                var e = a.Length > 1 ? ResolveEntityOrNull(ctx, a[1]) : null;
                return e?.Class?.Kind ?? "";
            });
            // get_objrace <obj> → the race of the object's owner (ENGINE_TCL_API.txt:520 "returns
            // race of an object"; there is no set_objrace in the API — race resolves through the
            // owner's set_ownerrace slot). z_methods.tcl:292-299 keys the whole clan look off
            // this: clanname {"" Voodoo_ Knocker_ Brain_ Vampir_}, the ${gender}${clan} clothing
            // switch (male1 → {55 66} …), Voodoo hair / Brain glasses props.
            // Tournament spectators spawn with `set_owner -1` (obw_wettkampf.tcl:1699) and must
            // fall through the male0..female4 cases into the Zuschauer default branch
            // (z_methods.tcl:349-372), so an invalid owner reads -1, never 0.
            interp.RegisterCommand("get_objrace",   (i, a) =>
            {
                var e = a.Length > 1 ? ResolveEntityOrNull(ctx, a[1]) : null;
                if (e == null) return "0";
                return (e.Owner >= 0 && e.Owner < ctx.OwnerRace.Length ? ctx.OwnerRace[e.Owner] : -1)
                    .ToString(CultureInfo.InvariantCulture);
            });
            // get_objownertype <obj> → 0=none 1=human 2=ai (ENGINE_TCL_API.txt:518). The dwarf
            // init derives is_human from `==2` (z_methods.tcl:200-204); AI gnomes then halve
            // their needs decay (z_events.tcl:1871-1875) and always wear the clan hat instead of
            // work-category hats (z_work_common.tcl:789-797 `![im_a_human]`).
            // Single-player: the one human slot is ctx.LocalPlayer; every other valid owner is an
            // AI player; owner -1 (tournament spectators / unowned) = 0 none.
            interp.RegisterCommand("get_objownertype", (i, a) =>
            {
                var e = a.Length > 1 ? ResolveEntityOrNull(ctx, a[1]) : null;
                if (e == null || e.Owner < 0 || e.Owner > 7) return "0";
                return e.Owner == ctx.LocalPlayer ? "1" : "2";
            });
            interp.RegisterCommand("get_owner",     (i, a) =>
                ((a.Length > 1 ? ResolveEntityOrNull(ctx, a[1]) : null)?.Owner ?? 0).ToString(CultureInfo.InvariantCulture));

            // set_alternateanimdb <obj> <true|false> — a female dwarf sets it (engine bit
            // 0x2000) so its model resolves from the alternate db. The dwarf's alternate is
            // `frau` (vs `mann`) — separate.3db, identical clip names — so we re-skin the
            // model object. get_alternateanimdb returns the flag (sequencer detects females).
            interp.RegisterCommand("set_alternateanimdb", (i, a) =>
            {
                var e = a.Length > 2 ? ResolveEntityOrNull(ctx, a[1]) : null;
                if (e == null) return "";
                bool alt = a[2] == "true" || a[2] == "1";
                e.AlternateAnimDb = alt;
                var inner = e.GameObject != null ? FindInnerModel(e.GameObject) : null;
                var ctrl = inner != null ? inner.GetComponent<Diggles.Runtime.DigglesAnimController>() : null;
                // Remap the dwarf's primary model (mann) → alternate (frau) for all clips
                // (bare + "mann."-prefixed), so prefixed anims don't revert to the male model.
                if (ctrl != null)
                {
                    bool changed = ctrl.UseAlternate != (alt && AlternateModelObj(ctrl.ObjName) != ctrl.ObjName);
                    ctrl.SetAlternate(alt && AlternateModelObj(ctrl.ObjName) != ctrl.ObjName,
                                      AlternateModelObj(ctrl.ObjName));
                    // Drop stale texture-variant overrides from the old db (e.g. ZMann_ face)
                    // so they don't paint onto the new (frau) mesh; they'll re-resolve fresh.
                    if (changed && inner!.GetComponent<Diggles.Runtime.DigglesTextureAnimator>() is { } tex)
                        tex.ClearChannels();
                }
                return "";
            });
            interp.RegisterCommand("get_alternateanimdb", (i, a) =>
                (a.Length > 1 ? ResolveEntityOrNull(ctx, a[1]) : null)?.AlternateAnimDb == true ? "1" : "0");
            interp.RegisterCommand("get_local_player", (i, a) => "0");
            interp.RegisterCommand("get_mapedit",   (i, a) => "0");
            interp.RegisterCommand("get_ref",       (i, a) =>
            {
                if (a.Length < 2) return "";
                var e = ResolveEntityOrNull(ctx, a[1]);
                return e != null ? e.Handle.ToString(CultureInfo.InvariantCulture) : "0";
            });

            // link_obj <child> <parent> <linkid> → attach child to a parent's animation
            // link point (head=4, back=5, hands 0/1/7, …); link_obj <child> → detach. Drives
            // hats, held items, weapons, the worn basket. See Docs/HAT_LINK_SYSTEM.md.
            interp.RegisterCommand("link_obj", (i, a) => CmdLinkObj(ctx, a));
            // get_linked_to <obj> — the handle this object is link-attached to (0 = none).
            interp.RegisterCommand("get_linked_to", (i, a) =>
            {
                var e = a.Length >= 2 ? ResolveEntityOrNull(ctx, a[1]) : null;
                return (e?.LinkedToHandle ?? 0).ToString(CultureInfo.InvariantCulture);
            });

            // def_texture <base> <var0.tga> … — register a material's texture-variant list.
            // Keyed by base material NAME → gender-aware (ZMann_/ZFrau_ separate) and general
            // (clothing/hair/hat color, elves, trolls). Read by set_textureanimation.
            interp.RegisterCommand("def_texture", (i, a) =>
            {
                if (a.Length >= 2 && ctx.ModelLibrary != null)
                {
                    var vars = new string[a.Length - 2];
                    System.Array.Copy(a, 2, vars, 0, vars.Length);
                    ctx.ModelLibrary.RegisterTextureVariants(a[1], vars);
                }
                return "";
            });

            // set_textureanimation <obj> <submesh> <framelist> [start=0] [animtype=2] — swap /
            // animate a submesh's texture through its def_texture variants (face/eyes/visemes/
            // blink on submeshes 3/4; clothing/hair/hat tint on 0/1/2). See Docs/FACE_ANIMATION.md.
            interp.RegisterCommand("set_textureanimation", (i, a) => CmdSetTextureAnimation(ctx, a));

            // Position component getters / setters
            interp.RegisterCommand("get_posx", (i, a) => a.Length > 1 && ResolveEntityOrNull(ctx, a[1]) is { } e1 ? e1.Position.x.ToString("R", CultureInfo.InvariantCulture) : "0");
            interp.RegisterCommand("get_posy", (i, a) => a.Length > 1 && ResolveEntityOrNull(ctx, a[1]) is { } e2 ? e2.Position.y.ToString("R", CultureInfo.InvariantCulture) : "0");
            interp.RegisterCommand("get_posz", (i, a) => a.Length > 1 && ResolveEntityOrNull(ctx, a[1]) is { } e3 ? e3.Position.z.ToString("R", CultureInfo.InvariantCulture) : "0");
            interp.RegisterCommand("set_posx", (i, a) => { if (a.Length >= 3 && ResolveEntityOrNull(ctx, a[1]) is { } e) { e.Position = new Vector3(ParseFloat(a[2]), e.Position.y, e.Position.z); e.MarkNativeMutation(DigglesTclEntity.NativeMutationTransform); } return ""; });
            interp.RegisterCommand("set_posy", (i, a) => { if (a.Length >= 3 && ResolveEntityOrNull(ctx, a[1]) is { } e) { e.Position = new Vector3(e.Position.x, ParseFloat(a[2]), e.Position.z); e.MarkNativeMutation(DigglesTclEntity.NativeMutationTransform); } return ""; });
            interp.RegisterCommand("set_posz", (i, a) => { if (a.Length >= 3 && ResolveEntityOrNull(ctx, a[1]) is { } e) { e.Position = new Vector3(e.Position.x, e.Position.y, ParseFloat(a[2])); e.MarkNativeMutation(DigglesTclEntity.NativeMutationTransform); } return ""; });

            // Rotation component getters / setters
            interp.RegisterCommand("get_rotx", (i, a) => a.Length > 1 && ResolveEntityOrNull(ctx, a[1]) is { } e ? e.Rotation.x.ToString("R", CultureInfo.InvariantCulture) : "0");
            // get_roty reads the LIVE yaw (NativeYawOf): movement executors rotate the transform
            // directly, so the stored field is stale after the first walk — and the whole dig/work
            // facing logic branches on `get_roty − digdir` (z_dignwalk.tcl:434,477).
            interp.RegisterCommand("get_roty", (i, a) => a.Length > 1 && ResolveEntityOrNull(ctx, a[1]) is { } e ? NativeYawOf(e).ToString("R", CultureInfo.InvariantCulture) : "0");
            interp.RegisterCommand("get_rotz", (i, a) => a.Length > 1 && ResolveEntityOrNull(ctx, a[1]) is { } e ? e.Rotation.z.ToString("R", CultureInfo.InvariantCulture) : "0");
            interp.RegisterCommand("set_rotx", (i, a) => { if (a.Length >= 3 && ResolveEntityOrNull(ctx, a[1]) is { } e) { e.Rotation = new Vector3(ParseFloat(a[2]), e.Rotation.y, e.Rotation.z); e.MarkNativeMutation(DigglesTclEntity.NativeMutationTransform); ReflectRotation(ctx, e); } return ""; });
            interp.RegisterCommand("set_roty", (i, a) => { if (a.Length >= 3 && ResolveEntityOrNull(ctx, a[1]) is { } e) { e.Rotation = new Vector3(e.Rotation.x, ParseFloat(a[2]), e.Rotation.z); e.MarkNativeMutation(DigglesTclEntity.NativeMutationTransform); ReflectRotation(ctx, e); } return ""; });
            interp.RegisterCommand("set_rotz", (i, a) => { if (a.Length >= 3 && ResolveEntityOrNull(ctx, a[1]) is { } e) { e.Rotation = new Vector3(e.Rotation.x, e.Rotation.y, ParseFloat(a[2])); e.MarkNativeMutation(DigglesTclEntity.NativeMutationTransform); ReflectRotation(ctx, e); } return ""; });
            interp.RegisterCommand("get_rot",  (i, a) => a.Length > 1 && ResolveEntityOrNull(ctx, a[1]) is { } e
                ? FormatVec3(new Vector3(e.Rotation.x, NativeYawOf(e), e.Rotation.z)) : "0 0 0");
            interp.RegisterCommand("set_rot",  (i, a) =>
            {
                if (a.Length < 3 || ResolveEntityOrNull(ctx, a[1]) is not { } e) return "";
                if (a.Length == 3) e.Rotation = ParseVec3(a[2]);
                else if (a.Length == 5) e.Rotation = new Vector3(ParseFloat(a[2]), ParseFloat(a[3]), ParseFloat(a[4]));
                e.MarkNativeMutation(DigglesTclEntity.NativeMutationTransform);
                ReflectRotation(ctx, e);
                return "";
            });

            // Per-entity bool/state property setters — stored on entity Vars as "1"/"0" so
            // scripts that later read them via $foo see a sensible value.
            void RegisterEntityProp(string cmd, string varName)
            {
                interp.RegisterCommand(cmd, (i, a) =>
                {
                    if (a.Length >= 3 && ResolveEntityOrNull(ctx, a[1]) is { } e)
                        e.VarsScope.Vars["_" + varName] = a[2];
                    return "";
                });
                interp.RegisterCommand("get_" + varName, (i, a) =>
                {
                    if (a.Length >= 2 && ResolveEntityOrNull(ctx, a[1]) is { } e
                        && e.VarsScope.Vars.TryGetValue("_" + varName, out var v)) return v;
                    return "0";
                });
            }
            // FUN_005cd750 (TCL SetCollision): the bool owns both object-flag 0x80000 and the
            // saved +0x2ac pick mode. Gnomes/babies use mode 2; ordinary classes use mode 1;
            // native searches the class string for the literal "baum" and leaves those at 0.
            interp.RegisterCommand("set_collision", (i, a) =>
            {
                if (a.Length >= 3 && ResolveEntityOrNull(ctx, a[1]) is { } e)
                {
                    bool enabled = ParseTclBool(a[2]);
                    e.VarsScope.Vars["_collision"] = enabled ? "1" : "0";
                    if (!enabled)
                        e.PickTargetMode = 0;
                    else if (e.NativeObjectType is 1 or 11)
                        e.PickTargetMode = 2;
                    else if (e.Class == null ||
                             !e.Class.Name.Contains("baum", System.StringComparison.Ordinal))
                        e.PickTargetMode = 1;
                }
                return "";
            });
            interp.RegisterCommand("get_collision", (i, a) =>
            {
                if (a.Length >= 2 && ResolveEntityOrNull(ctx, a[1]) is { } e &&
                    e.VarsScope.Vars.TryGetValue("_collision", out string value))
                {
                    return value;
                }
                return "0";
            });
            RegisterEntityProp("set_physic",         "physic");
            RegisterEntityProp("set_hoverable",      "hoverable");
            RegisterEntityProp("set_selectable",     "selectable");
            RegisterEntityProp("set_storable",       "storable");
            RegisterEntityProp("set_autolight",      "autolight");
            RegisterEntityProp("set_viewinfog",      "viewinfog");
            RegisterEntityProp("set_lock",           "lock");
            RegisterEntityProp("set_cloaked",        "cloaked");
            RegisterEntityProp("set_invulnerable",   "invulnerable");
            RegisterEntityProp("set_undeletable",    "undeletable");
            // set_sequenceactive <obj> <bool> — not a write-only prop var: it is the per-object
            // exemption bit from the global cutscene freeze (obj+0x3d0 bit 0x200000,
            // TCL:CTclObjMethods::SetSequenceActive FUN_006816a0:74-88 — an unparsable/absent
            // bool CLEARS the bit, matching the native's Tcl_GetBooleanFromObj-failure branch).
            // DigglesGameSim phases 2/3 gate event dispatch + state thinks on it while
            // ctx.SequenceActive (SrvDoThink FUN_005d38c0:138,185-187). See
            // DigglesTclEntity.SequenceActiveFlag for the full RE note.
            interp.RegisterCommand("set_sequenceactive", (i, a) =>
            {
                if (a.Length >= 2 && ResolveEntityOrNull(ctx, a[1]) is { } e)
                    e.SequenceActiveFlag = a.Length >= 3 && ParseTclBool(a[2]);
                return "";
            });
            interp.RegisterCommand("get_sequenceactive", (i, a) =>
                a.Length >= 2 && ResolveEntityOrNull(ctx, a[1]) is { } e && e.SequenceActiveFlag ? "1" : "0");
            RegisterEntityProp("set_objectcollision","objectcollision");
            RegisterEntityProp("set_boxed",          "boxed");
            // set_owner is deliberately not a RegisterEntityProp: the prop variant would register
            // a get_owner that shadows the real one (which reads the e.Owner field the work
            // dispatcher uses). The real set_owner (writes e.Owner) is registered below, before
            // RegisterStubs.
            // set_ownerrace <owner> <race> / get_ownerrace <owner> — the per-player race table
            // (ENGINE_TCL_API.txt:530/1305: the first arg is an owner index, not an object ref).
            // CampaignInit.tcl:15-19 / Skirmish.tcl:50-54 boot owners 0..4 → races 0..4;
            // std_ai.tcl:238/431/476 switches strategy on get_ownerrace. Out-of-range owner
            // reads -1 (invalid), never a valid clan.
            interp.RegisterCommand("set_ownerrace", (i, a) =>
            {
                int own = IntArg(a, 1, -1);
                if (own >= 0 && own < ctx.OwnerRace.Length)
                {
                    ctx.OwnerRace[own] = IntArg(a, 2, 0);
                    foreach (var entity in ctx.Entities.Values)
                        if (entity.Owner == own) RefreshBuildingClanFlag(entity);
                }
                return "";
            });
            interp.RegisterCommand("get_ownerrace", (i, a) =>
            {
                int own = IntArg(a, 1, -1);
                return (own >= 0 && own < ctx.OwnerRace.Length ? ctx.OwnerRace[own] : -1)
                    .ToString(CultureInfo.InvariantCulture);
            });

            // set_boxed override: besides the flag (which gates genericprod's init), a boxed
            // building must render as a closed crate, not its built model. The original draws a
            // generic crate; we swap the model to the 'kiste' object (PlayClip resolves the
            // obj prefix), and restore the class default anim when unpacked.
            interp.RegisterCommand("set_boxed", (i, a) =>
            {
                if (a.Length < 3 || !(ResolveEntityOrNull(ctx, a[1]) is { } e)) return "";
                e.VarsScope.Vars["_boxed"] = a[2];
                bool boxing = a[2] == "1" || a[2] == "true";
                if (boxing && string.Equals(
                        e.Class?.Kind,
                        "elevator",
                        System.StringComparison.OrdinalIgnoreCase))
                    e.NativeObjectType = NativeObjectTypeFromKind("elevator");
                if (e.TransportLogic?.Elevator is { } elevator)
                    elevator.CurrentPresentation = "";
                // FUN_0067dc80 side effects on the CProd record (decompile:139-163):
                // box (:157-161): flags = (flags & ~0x400000) | 0x4000 — boxing clears the
                // built bit (a produced crate must not read as a finished building)
                // and zeroes +0x3d / +0x2c / +0x29 → the pack request dies with it;
                // unbox (:144-148): flags &= ~0x4000, zeroes +0x3d / +0x2b / +0x28 → the
                // unpack order byte dies the moment the crate is opened for building
                // (prod_buildup_new's `set_boxed $item 0`, z_work_common.tcl:408), so a
                // re-clicked pending build order cannot dangle past placement and
                // dispatch a second builder.
                if (boxing)
                {
                    e.BuildupComplete = false;
                    e.PackRequested = false;
                    // Deferred move (rec+0x3c, the ALT-pack "Umzug" flow): the moment the moved
                    // building is boxed, the stored order converts into the unpack at OrderPos —
                    // the native prodplace idle tick's rec+0x3c consumption (FUN_005edea0:122-138);
                    // ProdDispatcher's kind-1 collector then sends a carrier to re-place it.
                    if (e.MoveAfterPack)
                    {
                        e.MoveAfterPack = false;
                        e.OrderAsBox = false;
                        e.UnpackRequested = true;
                    }
                }
                else
                {
                    e.UnpackRequested = false;
                }
                // Native set_boxed never repositions — after the flag/icon work it merely kicks
                // the point-physics job for the object (FUN_0067dc80:128-129 → FUN_005ebc90
                // physics-mgr singleton + FUN_005eb470 enqueue). FUN_005eb470 requires flag 0x10
                // (set_physic 1), not contained (+0x3d0 bit 2 clear) and no carrier/link
                // (+0x238==0, +0x278==-1) — and the job is pure gravity: the crate falls straight
                // down its own column (the intro chain urw_3.tcl:29 `sq_object summon
                // Feuerstelle DropPos` → `packtobox` relies on that drop). A snap-to-walkable
                // settle here would instead teleport a template crate off its authored spot
                // (urw_gng_004_f.tcl:24-29 authors the boxed Leiter at its Schatztonne) before
                // the barrel's +0.2 s evt_takeitems bbox query (takeitems.tcl:17, ±0.32/±0.64).
                if (boxing && e.ContainedIn < 0 && PhysicEnabled(e)
                    && ctx.Terrain != null && ctx.Terrain.Nav is { } bnav)
                {
                    e.Position = Diggles.Runtime.RuntimeWorldSpawner.GravitySettle(bnav, e.Position);
                    if (e.GameObject != null && ctx.EntitySpawner != null)
                        ctx.EntitySpawner.ApplyPosition(e.GameObject, e.Position);
                }
                if (ctx.SequencePresentationDrain)
                {
                    ctx.DeferSequencePresentation(e);
                    Diggles.Runtime.Diagnostics.HitchFamilyTimingDiag.Count(
                        "cutscene.presentation-suppressed");
                    RefreshTransportInfluence(ctx, e);
                    return "";
                }
                var ctrl = GetController(e);
                if (ctrl != null)
                {
                    // A boxed building renders as a closed crate = the 'kiste' object. PlayClip
                    // keys cross-object clips distinctly (kiste.standard ≠ the building's own
                    // standard), so it does not collide with the finished-building model.
                    if (boxing)
                    {
                        if (!ctrl.PlayClip("kiste.standard", DigglesAnimator.AnimMode.Loop))
                            Debug.LogWarning($"[set_boxed] could not load crate model 'kiste.standard' for {e.Class?.Name}#{e.Handle} — box not shown.");
                        else if (ctx.Terrain == null || ctx.Terrain.Nav == null)
                            SnapToGround(e.GameObject);   // no nav data — render-only rest
                        // Content icon — the native boxing side effect attaches the boxed class's
                        // object icon (FUN_0067dc80:150 → FUN_005dc780: free icon slot + class
                        // icon registry lookup + FUN_005dc850(slot, obj, type 5, idx)), so a
                        // kiste shows what is inside.
                        if (e.GameObject != null && e.Class != null)
                            Diggles.Runtime.BoxContentIcon.Attach(e.GameObject, e.Class.Name, ctx.DataRoot);
                    }
                    else
                    {
                        // unboxing clears the object icon slots (FUN_005dc6a0)
                        if (e.GameObject != null) Diggles.Runtime.BoxContentIcon.Detach(e.GameObject);
                        if (!string.IsNullOrEmpty(e.Class?.DefaultAnim))
                            ctrl.PlayClip(e.Class!.DefaultAnim!, (DigglesAnimator.AnimMode)e.Class.DefaultAnimMode);
                        else ctrl.PlayClip("standard", DigglesAnimator.AnimMode.Loop);
                    }
                }
                RefreshBuildingClanFlag(e);
                RefreshTransportInfluence(ctx, e);
                return "";
            });

            // get_boxed — the boxed flag readback (native TCL:CTclObjMethods::GetBoxed reads the
            // 0x4000 flag FUN_0067dc80 writes). Was UNREGISTERED: with the tolerant "0" answer
            // eventgen's `[get_boxed $subpar]` branches (eventgen.tcl:164/:379 beamto_world's
            // drop distance, z_procs.tcl:610 take_item's crate gate) all silently took the
            // NOT-boxed path — e.g. ALT+click on an own GROUND CRATE verb'd "assign"
            // (workprod_prefer,:21) instead of the native "pickup" (:187-189, carry the
            // crate). Reads the same "_boxed" var set_boxed maintains.
            interp.RegisterCommand("get_boxed", (i, a) =>
                a.Length > 1 && ResolveEntityOrNull(ctx, a[1]) is { } be
                && be.VarsScope.Vars.TryGetValue("_boxed", out var bxv)
                && (bxv == "1" || bxv == "true") ? "1" : "0");

            // set_visibility / get_visibility — a REAL render flag, not a write-only var.
            // Native: bit 0 of obj+0x3d0, set/cleared by TCL:CTclObjMethods::SetVisibility
            // (FUN_0067cca0:79-94, + dirty byte +0x3b4), read by GetVisibility (FUN_0067cef0);
            // DEFAULT = VISIBLE (CObj ctor flag word 0x01100101, FUN_005c83f0:224 — bit 0 set).
            // Visibility is INDEPENDENT of containment: native InvAdd clears the same bit
            // (FUN_0069bd30:132) but Tcl freely re-shows contained items — lager.tcl store_item
            //:734-737 (inv_add + set_visibility 1 + shelf set_pos) and the production
            // choreography (z_work_prod.tcl prod_move_item:605-617 etc.) display items that sit
            // in the workplace inventory; change_tool_finish_out (z_procs.tcl:1556-1557) shows
            // the held tool. The old port stored a var nobody read AND coupled hidden-ness to
            // GameObject.SetActive in inv_add → shelves rendered empty, workpieces invisible.
            // Port model: the "_visibility" var is the flag; ApplyRenderVisible drives renderers.
            interp.RegisterCommand("set_visibility", (i, a) =>
            {
                if (a.Length >= 3 && ResolveEntityOrNull(ctx, a[1]) is { } e)
                {
                    ApplyRenderVisible(e, ParseTclBool(a[2]));
                    RefreshTransportInfluence(ctx, e);
                }
                return "";
            });
            interp.RegisterCommand("get_visibility", (i, a) =>
            {
                if (a.Length >= 2 && ResolveEntityOrNull(ctx, a[1]) is { } e
                    && e.VarsScope.Vars.TryGetValue("_visibility", out var v)) return TruthyFlagVar(v) ? "1" : "0";
                return "1";   // engine default = visible (the old "0" default inverted reality)
            });

            // Stat/attribute system — `def_attrib name min max default [label]`
            // declares a stat (atr_ → vital, exp_ → skill, else generic);
            // get/set/add_attrib read/write the per-entity typed store, clamped to
            // [min,max] and defaulting to the declared spawn value when unset. All
            // route through ctx.Stats (StatService → AttributeSystem / SkillSystem);
            // values live on DigglesTclEntity.Stats, not the string var-dict.
            interp.RegisterCommand("def_attrib", (i, a) =>
            {
                if (a.Length >= 5)
                {
                    double.TryParse(a[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var lo);
                    double.TryParse(a[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var hi);
                    double.TryParse(a[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var def);
                    ctx.Stats.Define(a[1], lo, hi, def, a.Length >= 6 ? a[5] : null);
                }
                return "";
            });
            interp.RegisterCommand("set_attrib", (i, a) =>
            {
                if (a.Length >= 4 && ResolveEntityOrNull(ctx, a[1]) is { } e
                    && double.TryParse(a[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    ctx.Stats.Set(e.Stats, a[2], v);
                return "";
            });
            interp.RegisterCommand("get_attrib", (i, a) =>
            {
                if (a.Length >= 3 && ResolveEntityOrNull(ctx, a[1]) is { } e)
                    return ctx.Stats.Get(e.Stats, a[2]).ToString(CultureInfo.InvariantCulture);
                return "0";
            });
            interp.RegisterCommand("add_attrib", (i, a) =>
            {
                if (a.Length >= 4 && ResolveEntityOrNull(ctx, a[1]) is { } e
                    && double.TryParse(a[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var delta))
                {
                    // Invulnerability (FUN_00674580:117-146): a NEGATIVE atr_Hitpoints delta is
                    // DROPPED while the object's 0x10000000 flag is set (set_invulnerable —
                    // Unverwundbarkeitstrank, z_methods.tcl:551-580). All other deltas apply.
                    if (delta < 0 && a[2] == "atr_Hitpoints"
                        && e.VarsScope.Vars.TryGetValue("_invulnerable", out var inv) && inv == "1")
                        return ctx.Stats.Get(e.Stats, a[2]).ToString(CultureInfo.InvariantCulture);
                    return ctx.Stats.Add(e.Stats, a[2], delta).ToString(CultureInfo.InvariantCulture);
                }
                return "0";
            });
            // add_expattrib <obj> <attr> <delta> → new value. The EXP-skill add with the talent-cap
            // sweep (native CTclObjMethods::AddAttrib, FUN_00674950 — ENGINE_TCL_API.txt:60):
            // 1. value += delta (clamped to the def_attrib range, like add_attrib);
            // 2. ExpMax normalization (:228-271): collect every attrib whose name starts with
            // "exp_" (DAT_00721330) except the exact "exp_Kampf" aggregate; if their SUM exceeds
            // atr_ExpMax (and ExpMax ≥ 0, sum > 0) scale each positive one by ExpMax/sum — a
            // gnome's total learning is capped by its talent;
            // 3. fight aggregate (:320-357): exp_Kampf and atr_Kampf:= Σ exp_F_* (DAT_00721338);
            // 4. result = the (possibly rescaled) value of the named attrib (:357-373).
            // Was a silent "0" stub — dig_execute's tttgain_dig last entry (exp_Stein,
            // z_dignwalk.tcl:317-327) was dropped, so the dig skill never grew. Port extra: feed the
            // per-owner MAX-ever into OwnerAttribs so `gamestats attribmax $own exp_Stein`
            // (enable_digbrushes, z_dignwalk.tcl:188) sees progression.
            interp.RegisterCommand("add_expattrib", (i, a) =>
            {
                if (a.Length < 4 || !(ResolveEntityOrNull(ctx, a[1]) is { } e)
                    || !double.TryParse(a[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var delta))
                    return "0";
                string name = a[2];
                ctx.Stats.Add(e.Stats, name, delta);
                // 2. talent cap: Σ exp_* (except the exp_Kampf aggregate) ≤ atr_ExpMax.
                double expMax = ctx.Stats.Get(e.Stats, "atr_ExpMax");
                double sum = 0;
                var expNames = new System.Collections.Generic.List<string>();
                foreach (var kv in ctx.Stats.Registry.All)
                {
                    if (!kv.Key.StartsWith("exp_", System.StringComparison.Ordinal) || kv.Key == "exp_Kampf") continue;
                    expNames.Add(kv.Key);
                    sum += ctx.Stats.Get(e.Stats, kv.Key);
                }
                // (port hardening: skip the sweep when attribs.tcl hasn't defined atr_ExpMax —
                // an undefined cap reads 0 and would zero every skill in a bare harness)
                if (ctx.Stats.Registry.TryGet("atr_ExpMax", out _) && expMax >= 0 && sum > 0 && expMax < sum)
                {
                    double scale = expMax / sum;
                    foreach (var n in expNames)
                    {
                        double v = ctx.Stats.Get(e.Stats, n);
                        if (v > 0) ctx.Stats.Set(e.Stats, n, v * scale);
                    }
                }
                // 3. exp_Kampf / atr_Kampf mirror the summed exp_F_* fight skills.
                double fsum = 0; bool anyF = false;
                foreach (var kv in ctx.Stats.Registry.All)
                    if (kv.Key.StartsWith("exp_F_", System.StringComparison.Ordinal))
                    { fsum += ctx.Stats.Get(e.Stats, kv.Key); anyF = true; }
                if (anyF)
                {
                    ctx.Stats.Set(e.Stats, "exp_Kampf", fsum);
                    ctx.Stats.Set(e.Stats, "atr_Kampf", fsum);
                }
                double nv = ctx.Stats.Get(e.Stats, name);
                // 4. (port) per-owner max-ever for gamestats attribmax (dig brush unlocks).
                var bag = OwnerBag(e.Owner);
                if (!bag.TryGetValue(name, out var mx) || nv > mx) bag[name] = nv;
                return nv.ToString(CultureInfo.InvariantCulture);
            });
            // get_expattrib ?bool? → the LIST OF DEFINED EXP-ATTRIB NAMES, in def_attrib
            // registration order (native FUN_00644de0, CTclGlobalMethods::GetExpAttrib; the two
            // filter literals verified in Diggles.exe: DAT_00721330="exp_", DAT_00721338="exp_F_"):
            // no arg → every name with prefix "exp_" (:259-291, mode 2)
            // arg false/0 → prefix "exp_" MINUS prefix "exp_F_" (:122-214, mode 0 —
            // the production/general skills incl. the exp_Kampf aggregate)
            // arg true/1 → prefix "exp_F_" only (:216-257, mode 1 — fight)
            // Was a silent "0" bulk stub — and the scripts BRANCH on it everywhere: positional
            // skill copies on gender-swap/resurrection (z_methods.tcl:42,79), editor-placed gnome
            // init (pzwerg.tcl:28,67), inheritance (z_spare_reprod.tcl:512), death-cap save
            // (z_events.tcl:1975), baby growth (baby.tcl:153,460), Jungbrunnen (magie.tcl:184),
            // techtree explst (makettree.tcl:45) — a "0" here silently WIPED skills on every
            // life-stage transition.
            interp.RegisterCommand("get_expattrib", (i, a) =>
            {
                int mode = 2;                                    // no arg → all exp_*
                // (an EMPTY word counts as "no arg" — the callers that matter use the bare form
                // `[get_expattrib]` and must get the FULL list incl. exp_F_*, else fight skills
                // are dropped from every life-stage copy)
                if (a.Length >= 2 && !string.IsNullOrWhiteSpace(a[1]))
                {
                    // Tcl_GetBooleanFromObj semantics; a non-boolean arg reads FALSE
                    // (native:103-109: mode = parseOK && value — an error leaves 0).
                    var s = a[1].Trim().ToLowerInvariant();
                    bool truthy = s == "true" || s == "yes" || s == "on"
                        || (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var bv) && bv != 0);
                    mode = truthy ? 1 : 0;
                }
                var sb = new System.Text.StringBuilder();
                foreach (var n in ctx.Stats.Registry.DefinedInOrder)
                {
                    bool isExp = n.StartsWith("exp_", System.StringComparison.Ordinal);
                    bool isFight = n.StartsWith("exp_F_", System.StringComparison.Ordinal);
                    bool take = mode == 2 ? isExp : mode == 1 ? isFight : isExp && !isFight;
                    if (!take) continue;
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(n);
                }
                return sb.ToString();
            });
            // get_bestexp <obj> → the NAME of the object's highest-valued NON-FIGHT exp attrib
            // (same "exp_" minus "exp_F_" set as get_expattrib 0 — exp_Kampf aggregate included),
            // or "" when none is strictly > 0. Native FUN_0068bff0: collect (:82-178), strict-max
            // scan seeded at 0.0 with first-wins ties (:185-218 — `best < v`, index kept only on a
            // strict improvement), empty result when the max index stays -1 (:221,249). Drives the
            // spare-time favourite-places pick (z_spare_main.tcl:754-760 → stt_exp_* tables).
            interp.RegisterCommand("get_bestexp", (i, a) =>
            {
                if (a.Length < 2 || !(ResolveEntityOrNull(ctx, a[1]) is { } e)) return "";
                string best = ""; double bestV = 0.0;
                foreach (var n in ctx.Stats.Registry.DefinedInOrder)
                {
                    if (!n.StartsWith("exp_", System.StringComparison.Ordinal)
                        || n.StartsWith("exp_F_", System.StringComparison.Ordinal)) continue;
                    double v = ctx.Stats.Get(e.Stats, n);
                    if (v > bestV) { bestV = v; best = n; }
                }
                return best;
            });
            interp.RegisterCommand("get_attrib_names",  (i, a) => "");
            // Per-player resource / invention tallies (owner_attrib). owner-first arg order
            // (set_owner_attrib $owner Bp$item 1.0; add_owner_attrib [get_owner this] $class 1).
            // Bp<Class> = invented flag; <Class> = owned-item count. Backs production buildability,
            // the invent gate, and the HUD resource readout (were "0" stubs).
            System.Collections.Generic.Dictionary<string, double> OwnerBag(int owner)
            {
                if (!ctx.OwnerAttribs.TryGetValue(owner, out var bag))
                { bag = new System.Collections.Generic.Dictionary<string, double>(); ctx.OwnerAttribs[owner] = bag; }
                return bag;
            }
            // Unset names fall back to the def_attrib DEFAULT: the native resolves the name
            // through the attrib REGISTRY (FUN_0062c9b0 → FUN_005da2f0 name→index) and reads a
            // registry-shaped per-owner slot table, so a slot nobody ever wrote reads its
            // def_attrib spawn value. That's the whole preinvented mechanism — genericprod.tcl
            // re-defines `def_attrib Bp$itemtype 0 1 1` for prod_preinvented items at classinit
            // (:147-156) and NOTHING ever set_owner_attrib's them for the human player (only the
            // AI seeds explicitly, ai/std_ai.tcl:1270). Without this fallback the Bp gate fails
            // CLOSED for every item incl. day-1 Grillpilz → zero autonomous production in Play
            // (tests masked it by hand-seeding BpGrillpilz).
            interp.RegisterCommand("get_owner_attrib", (i, a) =>
            {
                if (a.Length >= 3 && int.TryParse(a[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ow))
                {
                    if (ctx.OwnerAttribs.TryGetValue(ow, out var bag) && bag.TryGetValue(a[2], out var v))
                        return v.ToString(CultureInfo.InvariantCulture);
                    return ctx.Stats.Registry.DefaultOf(a[2]).ToString(CultureInfo.InvariantCulture);
                }
                return "0";
            });
            interp.RegisterCommand("set_owner_attrib", (i, a) =>
            {
                if (a.Length >= 4 && int.TryParse(a[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ow)
                    && double.TryParse(a[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                { OwnerBag(ow)[a[2]] = v; return v.ToString(CultureInfo.InvariantCulture); }
                return "0";
            });
            interp.RegisterCommand("add_owner_attrib", (i, a) =>
            {
                if (a.Length >= 4 && int.TryParse(a[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ow)
                    && double.TryParse(a[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                {
                    var bag = OwnerBag(ow);
                    // read-modify-write over the registry-default slot (see get_owner_attrib)
                    if (!bag.TryGetValue(a[2], out var cur)) cur = ctx.Stats.Registry.DefaultOf(a[2]);
                    cur += d; bag[a[2]] = cur;
                    return cur.ToString(CultureInfo.InvariantCulture);
                }
                return "0";
            });
            interp.RegisterCommand("get_owner_attrib_list", (i, a) =>
            {
                if (a.Length >= 2 && int.TryParse(a[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ow)
                    && ctx.OwnerAttribs.TryGetValue(ow, out var bag) && bag.Count > 0)
                    return TclList.Format(new System.Collections.Generic.List<string>(bag.Keys));
                return "";
            });
            interp.RegisterCommand("reset_owner_attribs", (i, a) =>
            {
                if (a.Length >= 2 && int.TryParse(a[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ow))
                    ctx.OwnerAttribs.Remove(ow);
                return "";
            });
            interp.RegisterCommand("transfer_attribs", (i, a) =>
            {
                if (a.Length >= 3
                    && int.TryParse(a[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var from)
                    && int.TryParse(a[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var to)
                    && ctx.OwnerAttribs.TryGetValue(from, out var src))
                {
                    var dst = OwnerBag(to);
                    foreach (var kv in src) { dst.TryGetValue(kv.Key, out var c); dst[kv.Key] = c + kv.Value; }
                    ctx.OwnerAttribs.Remove(from);
                }
                return "";
            });

            // Worktime/sparetime clock. The native gnome gates both the idle state
            // (`if {[get_remaining_sparetime this]==0.0} {work_dispatch}` else set_idle_anim) AND the
            // dig/work loop (dig_continue runs only while remaining==0) on this. The day is DayHours
            // hour-units; each hour-unit = HourSec (= 150 s, RE-proven — clock-worktime-re.md) of
            // GameTime. A gnome works while the day phase is inside its window [WorkStart,
            // WorkStart+WorkLen); otherwise it is sparetime. get_remaining_sparetime returns 0.0
            // during worktime and the remaining CLOCK-HOURS until the next work start during
            // sparetime (GetRemainingSpareTime FUN_006955d0:58-59 reads the +0x35d window flag and
            // divides by DAT_00737630=150). Plan switching is PURE POLLING of this value
            // (zwerg.tcl:299-311, z_work_states.tcl:256, z_dignwalk.tcl:208) — no edge events exist.
            interp.RegisterCommand("get_remaining_sparetime", (i, a) =>
            {
                var e = a.Length >= 2 ? ResolveEntityOrNull(ctx, a[1]) : ctx.This;
                double t = ctx.Sim?.GameTime ?? 0.0;
                double phase = (t / Diggles.Runtime.Sim.ProdDispatcher.HourSec) % Diggles.Runtime.Sim.ProdDispatcher.DayHours;
                // Missing entity → no window (duration 0), matching the spawn default
                // (FUN_005c83f0:130-132) — never reads as "at work".
                double start = e?.WorkStart ?? 0f, len = e?.WorkLen ?? 0f;
                double rel = phase - start; if (rel < 0) rel += Diggles.Runtime.Sim.ProdDispatcher.DayHours;
                if (rel < len) return "0.0";                                                 // worktime
                // Sparetime → remaining CLOCK-HOURS until the next work start (the Tcl plans with
                // `hceil` over this and scales the activity list 1h..5h — z_spare_main.tcl:55-62;
                // an earlier revision returned SECONDS, which made every plan read as ≥5h).
                return (Diggles.Runtime.Sim.ProdDispatcher.DayHours - rel)
                    .ToString("0.0##", CultureInfo.InvariantCulture);
            });
            interp.RegisterCommand("set_remaining_sparetime", (i, a) => "");   // engine-derived from the clock
            // set_worktime <obj> START [LEN] — the gnome's work window in hour-units.
            interp.RegisterCommand("set_worktime", (i, a) =>
            {
                if (ResolveEntityOrNull(ctx, a.Length > 1 ? a[1] : "this") is { } e)
                {
                    if (a.Length > 2 && float.TryParse(a[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) e.WorkStart = s;
                    if (a.Length > 3 && float.TryParse(a[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var l)) e.WorkLen = l;
                }
                return "";
            });
            // get_worktime (TCL:CTclObjMethods::GetWorkTime, FUN_0068b790) — three return shapes:
            // • no subcommand → the `{start duration}` two-value list (the argc!=3 path,
            // FUN_0068b790:72-91; consumers magie.tcl:188, baby.tcl:157, z_events.tcl:1990);
            // • start|duration|end → HOUR-OF-DAY units (Trigger_Tutorial.tcl:705 does
            // `(int([get_worktime $g start])+6)%12`);
            // • nextstart|laststart|nextend|lastend → ABSOLUTE GETTIME SECONDS — the native
            // consumers compare them against [gettime]±100: std_ai.tcl:1443
            // `[get_worktime $gnome nextend]>$ctime-100`, Trigger_Tutorial.tcl:763-766
            // `[get_worktime $g nextend]<$ct+100`. (The old port returned absolute HOURS here
            // → off by ×HourSec; clock-worktime-re.md §1.5.3.)
            interp.RegisterCommand("get_worktime", (i, a) =>
            {
                if (ResolveEntityOrNull(ctx, a.Length > 1 ? a[1] : "this") is not { } e) return "0";
                if (a.Length <= 2)
                    return e.WorkStart.ToString("0.###", CultureInfo.InvariantCulture) + " "
                         + e.WorkLen.ToString("0.###", CultureInfo.InvariantCulture);
                string q = a[2];
                double day = Diggles.Runtime.Sim.ProdDispatcher.DayHours;
                double hourSec = Diggles.Runtime.Sim.ProdDispatcher.HourSec;
                double nowH = (ctx.Sim?.GameTime ?? 0.0) / hourSec;
                double phase = nowH % day;
                double Next(double edge) { double d = edge - phase; if (d <= 0) d += day; return nowH + d; }
                double v = q switch
                {
                    "duration" or "len" => e.WorkLen,
                    "end" => (e.WorkStart + e.WorkLen) % day,
                    "nextstart" => Next(e.WorkStart) * hourSec,
                    "laststart" => (Next(e.WorkStart) - day) * hourSec,
                    "nextend" => Next((e.WorkStart + e.WorkLen) % day) * hourSec,
                    "lastend" => (Next((e.WorkStart + e.WorkLen) % day) - day) * hourSec,
                    _ => e.WorkStart,
                };
                return v.ToString("0.###", CultureInfo.InvariantCulture);
            });
            // auto_choose_workingtime <obj> — the NATIVE algorithm (TCL:CTclObjMethods::
            // AutoChooseWorkingTime, FUN_00676b50; replaces the port's invented two-shift balancer):
            // • gnome/baby only (obj+0xe8 == 1|0xb,:92; else "not a gnome or baby");
            // • 12-bucket coverage histogram over every OTHER same-owner planner-tracked
            // object's window: for h in [int(start), int(start)+int(dur)): hist[h%12]++,
            // total += int(dur) (:127-156 — owner gate +0x36c, planner record +0x3e4≅gnome/baby);
            // • total == 0 → start=0, dur=6.0 (:164-167, 0x40c00000 = 6.0f);
            // • else hist ×999/total (:169-175), box-smooth 5-wide circular ×3 passes (:176-202),
            // argmin scanning from a RANDOM rotation with strict less-than (:203-265),
            // start = (argmin+10)%12 (= argmin − 2 h), dur = 6.0 (:266-267).
            // zwerg.tcl:147 calls it at spawn — the colony naturally staggers its shifts.
            interp.RegisterCommand("auto_choose_workingtime", (i, a) =>
            {
                if (ResolveEntityOrNull(ctx, a.Length > 1 ? a[1] : "this") is not { } e) return "";
                if (e.Class == null || (e.Class.Kind != "gnome" && e.Class.Kind != "baby")) return "";
                var hist = new int[12];
                int total = 0;
                foreach (var o in ctx.Entities.Values)
                {
                    if (o == e || o.Owner != e.Owner || o.Class == null) continue;
                    if (o.Class.Kind != "gnome" && o.Class.Kind != "baby") continue;   // ≅ +0x3e4 != 0
                    int s = (int)o.WorkStart, d = (int)o.WorkLen;
                    if (d <= 0) continue;
                    total += d;
                    for (int h = s; h < s + d; h++) hist[((h % 12) + 12) % 12]++;
                }
                if (total == 0) { e.WorkStart = 0f; e.WorkLen = 6f; return ""; }
                for (int k = 0; k < 12; k++) hist[k] = hist[k] * 999 / total;
                for (int pass = 0; pass < 3; pass++)
                {
                    var prev = (int[])hist.Clone();
                    for (int k = 0; k < 12; k++)
                        hist[k] = (prev[(k + 10) % 12] + prev[(k + 11) % 12] + prev[k]
                                 + prev[(k + 1) % 12] + prev[(k + 2) % 12]) / 5;
                }
                int rot = UnityEngine.Random.Range(0, 12);   // rand % 12 (:203-204)
                int best = rot;
                for (int k = 1; k < 12; k++)
                {
                    int cand = (rot + k) % 12;
                    if (hist[cand] < hist[best]) best = cand;   // strict < — first minimum in scan order
                }
                e.WorkStart = (best + 10) % 12;
                e.WorkLen = 6f;
                return "";
            });

            // ----- inventory / containment (inv_*) -----
            // Native ground truth (audit inventory-items): InvAdd = FUN_0069bd30 (clears the
            // +0x3d0 bit-0 visible flag at:132 + removes the object from the world/physics
            // grid — containment and RENDER visibility stay independent, so Tcl re-shows shelf
            // items / workpieces while contained); InvRem = FUN_0069c0f0 (drop pos DEFAULTS to
            // the holder's +0xc.. at:97-99, re-sets the visible bit:171-172); InvCheck =
            // FUN_005cf490. A contained entity is excluded from world obj_query unless the
            // query asks -flagpos contained/instore (see CmdObjQuery). Refs round-trip as
            // handle ints. KNOWN divergences kept for now: capacity is flat count<99 (native =
            // per-kind SIZE table + slot-use +0x220 + pannier overflow, FUN_005ce010 — not
            // modelled; set_inventoryslotuse still a stub) and inv_check ignores the item arg.
            // Registered BEFORE RegisterStubs so these win over the "0" stubs.
            interp.RegisterCommand("inv_add", (i, a) =>
            {
                if (a.Length < 3) return "-1";
                var self = ResolveEntityOrNull(ctx, a[1]);
                var item = ResolveEntityOrNull(ctx, a[2]);
                if (self == null || item == null) return "-1";
                int existing = self.Inventory.IndexOf(item.Handle);
                if (existing >= 0) return existing.ToString(CultureInfo.InvariantCulture);
                // Single-owner guard: two selected gnomes ordered onto one ground item would
                // otherwise duplicate it into both inventories. An item already contained in a
                // different holder must refuse — every legit transfer in the original scripts
                // inv_rem's first (z_work_common.tcl:51-52 store, z_work_prod.tcl:462-463
                // reorder, lager retrieve), and genericprod.tcl:505-509 explicitly gates
                // `is_contained || get_lock → return false` before its inv_add. The native
                // add chain never needs this test because its InvAdd ZEROES the item's world
                // pos (below), which makes take_item's arrival distance gate
                // (z_procs.tcl:648: dist > 3.0 → gnome_failed_work) kill the second pickup.
                if (item.ContainedIn >= 0 && item.ContainedIn != self.Handle) return "-1";
                // Gnome capacity = base 6 + worn-container bonuses (EffectiveInvCapacity — the
                // 7th pickup without a basket must refuse like the native size model
                // FUN_005ce010 does); non-gnomes keep their flat capacity.
                if (self.Inventory.Count >= EffectiveInvCapacity(ctx, self)) return "-1";
                self.Inventory.Add(item.Handle);
                self.MarkNativeMutation(DigglesTclEntity.NativeMutationInventory);
                item.ContainedIn = self.Handle;
                // Native InvAdd MOVES the item to the world origin: FUN_0069bd30:145-147 — if
                // the pos is nonzero it calls CObj::SetPosition(&{0,0,0}) (FUN_005cd8e0) and
                // pulls the object out of the world grid (FUN_005ebc90/FUN_005eb470). The
                // scripts PROVE they expect this: every shelf/workpiece flow saves the pos
                // before inv_add and re-sets it after (genericprod.tcl:506-508,
                // z_work_prod.tcl:461-464, lager.tcl store_item:734-737). This zero is the
                // native anti-dupe law for the SECOND gnome of a fanned-out evt_task_pickup:
                // his take_item finds the item at {0 0 0}, fails the ≤3.0 arrival gate
                // (z_procs.tcl:648-654) and ends via gnome_failed_work instead of beamto_inv.
                item.Position = Vector3.zero;
                if (item.GameObject != null && ctx.EntitySpawner != null)
                    ctx.EntitySpawner.ApplyPosition(item.GameObject, item.Position);
                // Native InvAdd (FUN_0069bd30:132) clears the +0x3d0 bit-0 VISIBLE flag and pulls
                // the object out of the world/physics grid — it does NOT kill the object. The old
                // SetActive(false) coupling meant a later `set_visibility 1` (lager shelf,
                // production workpiece) could never re-show a contained item.
                ApplyRenderVisible(item, false);
                SetCollidersEnabled(item, false);   // out of the click/physics world while packed
                // Worn-basket visual recompute (FUN_005cf940 runs after every native InvAdd).
                UpdateWornContainerVisual(ctx, self);
                return (self.Inventory.Count - 1).ToString(CultureInfo.InvariantCulture);
            });
            interp.RegisterCommand("inv_check", (i, a) =>
            {
                var self = a.Length >= 2 ? ResolveEntityOrNull(ctx, a[1]) : null;
                return self != null && self.Inventory.Count < EffectiveInvCapacity(ctx, self) ? "1" : "0";
            });
            string InvCount(string[] a)
            {
                var self = a.Length >= 2 ? ResolveEntityOrNull(ctx, a[1]) : null;
                return (self?.Inventory.Count ?? 0).ToString(CultureInfo.InvariantCulture);
            }
            interp.RegisterCommand("inv_cnt",     (i, a) => InvCount(a));
            interp.RegisterCommand("inv_cnt_raw", (i, a) => InvCount(a));
            interp.RegisterCommand("inv_getsize", (i, a) =>
            {
                // Native InvGetSize (FUN_005cde10): gnome = base 6 + worn-container bonuses;
                // others = their own capacity.
                var self = a.Length >= 2 ? ResolveEntityOrNull(ctx, a[1]) : null;
                return (self != null ? EffectiveInvCapacity(ctx, self) : 0)
                    .ToString(CultureInfo.InvariantCulture);
            });
            interp.RegisterCommand("inv_find", (i, a) =>
            {
                if (a.Length < 3) return "-1";
                var self = ResolveEntityOrNull(ctx, a[1]);
                if (self == null) return "-1";
                var want = new System.Collections.Generic.List<string> { a[2] };
                for (int k = 0; k < self.Inventory.Count; k++)
                    if (ctx.Entities.TryGetValue(self.Inventory[k], out var it) && EntityMatchesAnyClass(ctx, it, want))
                        return k.ToString(CultureInfo.InvariantCulture);
                return "-1";
            });
            string InvFindObj(string[] a)
            {
                if (a.Length < 3) return "-1";
                var self = ResolveEntityOrNull(ctx, a[1]);
                var item = ResolveEntityOrNull(ctx, a[2]);
                if (self == null || item == null) return "-1";
                return self.Inventory.IndexOf(item.Handle).ToString(CultureInfo.InvariantCulture);
            }
            interp.RegisterCommand("inv_find_obj", (i, a) => InvFindObj(a));
            interp.RegisterCommand("inv_find_raw", (i, a) => InvFindObj(a));
            string InvGet(string[] a)
            {
                if (a.Length < 3) return "0";
                var self = ResolveEntityOrNull(ctx, a[1]);
                if (self != null && int.TryParse(a[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx)
                    && idx >= 0 && idx < self.Inventory.Count)
                    return self.Inventory[idx].ToString(CultureInfo.InvariantCulture);
                return "0";
            }
            interp.RegisterCommand("inv_get",     (i, a) => InvGet(a));
            interp.RegisterCommand("inv_get_raw", (i, a) => InvGet(a));
            interp.RegisterCommand("inv_list", (i, a) =>
            {
                var self = a.Length >= 2 ? ResolveEntityOrNull(ctx, a[1]) : null;
                if (self == null || self.Inventory.Count == 0) return "";
                var refs = new System.Collections.Generic.List<string>(self.Inventory.Count);
                foreach (var h in self.Inventory) refs.Add(h.ToString(CultureInfo.InvariantCulture));
                return TclList.Format(refs);
            });
            interp.RegisterCommand("inv_rem", (i, a) =>
            {
                if (a.Length < 3) return "";
                var self = ResolveEntityOrNull(ctx, a[1]);
                if (self == null) return "";
                int idx = -1;
                var byRef = ResolveEntityOrNull(ctx, a[2]);                 // a[2] = index OR ref
                if (byRef != null && self.Inventory.Contains(byRef.Handle)) idx = self.Inventory.IndexOf(byRef.Handle);
                else if (int.TryParse(a[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
                         && n >= 0 && n < self.Inventory.Count) idx = n;
                if (idx < 0) return "";
                int h = self.Inventory[idx];
                self.Inventory.RemoveAt(idx);
                self.MarkNativeMutation(DigglesTclEntity.NativeMutationInventory);
                if (ctx.Entities.TryGetValue(h, out var item))
                {
                    item.ContainedIn = -1;
                    item.InStore = false;
                    // Native InvRem (FUN_0069c0f0): the drop position DEFAULTS to the HOLDER's
                    // (+0xc/+0x10/+0x14 captured at:97-99, always placed via FUN_005cb600 at
                    //:173) — Tcl callers that pass no pos (retrieve_item lager.tcl:796,
                    // bar.tcl `inv_rem this $obj; del $obj`) expect the item AT the holder, not
                    // wherever it was when packed. The release also re-sets the +0x3d0 bit-0
                    // visible flag (:171-172).
                    item.Position = a.Length >= 4 ? ParseVec3(a[3]) : self.Position;
                    if (item.GameObject != null && ctx.EntitySpawner != null)
                        ctx.EntitySpawner.ApplyPosition(item.GameObject, item.Position);
                    if (item.GameObject != null) item.GameObject.SetActive(true);   // legacy un-hide (pre-rework packs)
                    ApplyRenderVisible(item, true);
                    SetCollidersEnabled(item, true);
                    // A dropped worn basket detaches from the carrier's back (beamto_world's own
                    // `link_obj $item` also fires on the Tcl putdown path — this covers direct
                    // inv_rem callers like container-empty loops).
                    if (item.LinkedToHandle == self.Handle)
                        CmdLinkObj(ctx, new[] { "link_obj", h.ToString(CultureInfo.InvariantCulture) });
                }
                // Worn-basket visual recompute (the native remove path re-runs FUN_005cf940 too:
                // dropping below base capacity hides the still-carried basket again).
                UpdateWornContainerVisual(ctx, self);
                return h.ToString(CultureInfo.InvariantCulture);
            });
            interp.RegisterCommand("is_contained", (i, a) =>
            {
                var e = a.Length >= 2 ? ResolveEntityOrNull(ctx, a[1]) : null;
                return e != null && e.ContainedIn >= 0 ? "1" : "0";
            });
            interp.RegisterCommand("global_inv_rem", (i, a) =>
            {
                if (a.Length < 2) return "";
                foreach (var tok in TclList.Parse(a[1]))
                {
                    var item = ResolveEntityOrNull(ctx, tok);
                    if (item != null && item.ContainedIn >= 0 && ctx.Entities.TryGetValue(item.ContainedIn, out var owner))
                    {
                        owner.Inventory.Remove(item.Handle);
                        owner.MarkNativeMutation(DigglesTclEntity.NativeMutationInventory);
                        item.ContainedIn = -1;
                        item.InStore = false;
                        if (item.GameObject != null) item.GameObject.SetActive(true);
                        ApplyRenderVisible(item, true);
                        SetCollidersEnabled(item, true);
                        if (item.LinkedToHandle == owner.Handle)
                            CmdLinkObj(ctx, new[] { "link_obj", item.Handle.ToString(CultureInfo.InvariantCulture) });
                        UpdateWornContainerVisual(ctx, owner);   // FUN_005cf940 re-runs on removal too
                    }
                }
                return "";
            });
            interp.RegisterCommand("set_instore", (i, a) =>
            {
                if (a.Length >= 3 && ResolveEntityOrNull(ctx, a[1]) is { } e)
                    e.InStore = a[2] == "1" || a[2] == "true";
                return "";
            });
            interp.RegisterCommand("get_instore", (i, a) =>
            {
                var e = a.Length >= 2 ? ResolveEntityOrNull(ctx, a[1]) : null;
                return e != null && e.InStore ? "1" : "0";
            });
            // is_wearing_pannier <obj> — native handler FUN_0069e930 (registrar pair at
            // 0x671AD2/0x671AE6 with the.rdata string 0x732A2C) → FUN_005cf820: scan the
            // inventory for a type-9 CONTAINER (item+0xe8 == 9) and answer 1 ONLY when that
            // container's OWN inventory is NON-EMPTY (005cf820:53-55 `item+0x5c > 0`) — an
            // EMPTY worn basket does NOT trigger the pannier walk. Gates the pannier walk
            // animset (z_dignwalk.tcl:101-109: slow = ANIMSET_WALKWITHPANNIER, RUN killed);
            // the visible back-basket is those clips (mann.gehen_kiepe_*), no separate link.
            // Port: inventories are FLAT (no nested container storage — the native size model
            // FUN_005ce010:61-196 overflows into the pannier base-capacity-first), so "the
            // basket has content" ⇔ items beyond the Zwerg base capacity (InvGetSize
            // FUN_005cde10 base 6) — exactly the items the inventory panel's basket grid shows.
            interp.RegisterCommand("is_wearing_pannier", (i, a) =>
            {
                var e = a.Length >= 2 ? ResolveEntityOrNull(ctx, a[1]) : ctx.This;
                if (e == null) return "0";
                bool worn = false;
                foreach (var h in e.Inventory)
                    if (ctx.Entities.TryGetValue(h, out var it) && it.Class?.Kind == "transport") { worn = true; break; }
                return worn && e.Inventory.Count > ZwergBaseInvSlots ? "1" : "0";
            });

            // ----- selection -----
            // The player's selected object (one handle; 0 = none). ClickToMoveService writes it on
            // a unit click; the HUD reads it; Tcl set_selectedobject/get_selectedobject/is_selected
            // round-trip it. Real impls (were bulk-stubbed "0").
            interp.RegisterCommand("set_selectedobject", (i, a) =>
            {
                if (a.Length >= 2)
                {
                    var e = ResolveEntityOrNull(ctx, a[1]);
                    ctx.SelectedHandle = e?.Handle ?? (int.TryParse(a[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) ? h : 0);
                }
                else ctx.SelectedHandle = 0;
                return "";
            });
            // PRIMARY only — the native CTclGlobalMethods::GetSelectedObject (FUN_00647100)
            // returns selection[0] even when several units are selected.
            interp.RegisterCommand("get_selectedobject", (i, a) => ctx.SelectedHandle.ToString(CultureInfo.InvariantCulture));
            // LIST membership — the native CTclObjMethods::IsSelected (FUN_0069e750 →
            // FUN_005e35a0 scans the whole selection array), NOT primary equality.
            interp.RegisterCommand("is_selected", (i, a) =>
            {
                var e = a.Length >= 2 ? ResolveEntityOrNull(ctx, a[1]) : null;
                return e != null && ctx.Selection.Contains(e.Handle) ? "1" : "0";
            });
            // The native `selection` command family (FUN_0061e7a0): the raw list ops the doc
            // scripts drive (info_gnomes.tcl:26-34 `selection clear` + `selection include $gid`
            // + `selection check`). include enforces the caninclude law like the native.
            interp.RegisterCommand("selection", (i, a) =>
            {
                string sub = a.Length >= 2 ? a[1] : "";
                var e = a.Length >= 3 ? ResolveEntityOrNull(ctx, a[2]) : null;
                switch (sub)
                {
                    case "clear": ctx.Selection.Clear(); return "";
                    case "check": return e != null && ctx.Selection.Contains(e.Handle) ? "1" : "0";
                    case "caninclude": return e != null && ctx.Selection.CanInclude(e.Handle) ? "1" : "0";
                    case "include": if (e != null) ctx.Selection.Include(e.Handle); return "";
                    case "exclude": if (e != null) ctx.Selection.Exclude(e.Handle); return "";
                    default: return "";   // native errors "unknown sub-command"; tolerant here
                }
            });

            // ----- digging — drives the unmodified z_dignwalk.tcl loop -----
            // All operate in NATIVE coords {X=east, Y=depth, Z=height}; tile = (round X, round Y)
            // via the live DiggableTerrain (ctx.Terrain, read at call time). dig_apply lowers a tile's
            // solidity height; a fully-carved tile becomes air (block 59) and the terrain re-meshes.
            // Registered BEFORE RegisterStubs so they win over the "0" stubs.
            string VecIdx(string[] a, int idx)
            {
                if (a.Length < 2) return "0";
                var p = TclList.Parse(a[1]);
                return idx < p.Count ? p[idx] : "0";
            }
            interp.RegisterCommand("vector_unpackx", (i, a) => VecIdx(a, 0));
            interp.RegisterCommand("vector_unpacky", (i, a) => VecIdx(a, 1));
            interp.RegisterCommand("vector_unpackz", (i, a) => VecIdx(a, 2));
            interp.RegisterCommand("vector_pack",    (i, a) => a.Length >= 2 ? string.Join(" ", a, 1, a.Length - 1) : "0 0 0");
            interp.RegisterCommand("vector_packxyz", (i, a) => a.Length >= 2 ? string.Join(" ", a, 1, a.Length - 1) : "0 0 0");

            interp.RegisterCommand("get_hmap", (i, a) =>
            {
                // FUN_004093f0: PER-SUB-CELL height nibble — world float inputs round to sub-cells
                // (× 8.0 = DAT_0073752c). The old per-tile MAX-corner collapse made every cliff read
                // "15 everywhere" → dig_check's freeneighbours stayed 0 → the dwarf never dug (doc §6).
                var T = ctx.Terrain; if (T == null || a.Length < 3) return "15";
                var nav = T.Nav;
                if (nav != null)
                    return nav.GetHmapSub(
                        Mathf.RoundToInt(ParseFloat(a[1]) * 8f),
                        Mathf.RoundToInt(ParseFloat(a[2]) * 8f)).ToString(CultureInfo.InvariantCulture);
                return T.GetHmap(Mathf.RoundToInt(ParseFloat(a[1])), Mathf.RoundToInt(ParseFloat(a[2]))).ToString(CultureInfo.InvariantCulture);
            });
            interp.RegisterCommand("get_ftype", (i, a) =>
            {
                if (a.Length < 3) return "0";
                return ctx.Fluids.GetFluidType(NativeRound(ParseFloat(a[1])), NativeRound(ParseFloat(a[2])))
                    .ToString(CultureInfo.InvariantCulture);
            });
            // get_gnomeposition (FUN_0068d8a0:84-98): 0=on floor / 1=on wall. Probes the FLOOR bit
            // (+0x3c & 2) at the dwarf's sub-column in EXACTLY three rows — subY+1, subY, subY−1
            //: any floor surface within ±1 row of the body anchor ⇒ standing on
            // the ground (0); none ⇒ on a wall (1). Was a hardcoded "0" stub.
            interp.RegisterCommand("get_gnomeposition", (i, a) =>
            {
                var e = a.Length >= 2 ? ResolveEntityOrNull(ctx, a[1]) : ctx.This;
                var nav = ctx.Terrain?.Nav;
                if (e == null || nav == null) return "0";
                int gx = Mathf.RoundToInt(e.Position.x * 8f), gy = Mathf.RoundToInt(e.Position.y * 8f);
                // strict in-bounds ±1 guard (0068d8a0:67-70): out of the sub-bounds ⇒ wall (1).
                if (gx <= 0 || gy <= 0 || gx >= nav.SubW - 1 || gy >= nav.SubH - 1) return "1";
                if ((nav.SubAt(gx, gy + 1) & Diggles.Runtime.Navigation.NavWorld.SubFloor) != 0) return "0";
                if ((nav.SubAt(gx, gy)     & Diggles.Runtime.Navigation.NavWorld.SubFloor) != 0) return "0";
                if ((nav.SubAt(gx, gy - 1) & Diggles.Runtime.Navigation.NavWorld.SubFloor) != 0) return "0";
                return "1";
            });
            // get_walkresult <obj> → the entity's cached pathfind result (engine obj+0x248, doc §3.4):
            // 2=WS_NOTFINISHED (walking), 3=WS_NOPASSAGE, 4=WS_TARGETREACHED. Written by
            // WalkResultAction as a walk runs/ends. THE foundational unblock: the unchanged Tcl
            // `walkfail` (z_dignwalk.tcl) gives up via `4 != [get_walkresult this]` unless this
            // returns 4 once the walk arrives; busy-checks poll `== 2`. (Bulk-stubbed "0".)
            interp.RegisterCommand("get_walkresult", (i, a) =>
            {
                var e = a.Length >= 2 ? ResolveEntityOrNull(ctx, a[1]) : ctx.This;
                return (e?.WalkResult ?? 0).ToString(CultureInfo.InvariantCulture);
            });
            interp.RegisterCommand("get_ballisticresult", (i, a) =>
            {
                var e = a.Length >= 2 ? ResolveEntityOrNull(ctx, a[1]) : ctx.This;
                return (e?.BallisticResult ?? 0).ToString(CultureInfo.InvariantCulture);
            });
            // get_material $pos → INT material class 0..3 = tile+0x18 & 3 (FUN_00630bb0:93-94;
            // Tcl_NewIntObj at:96). 0/3 = undiggable rock/granite, 1 = hard earth (dig P×1),
            // 2 = soft earth (dig P×2) — the z_dignwalk.tcl:315 pace multiplier. Law + the L2M
            // load-time writer: DiggableTerrain.GetMaterial. The old float 0.6/0.85/1.0 was
            // invented and under-drove the dig pace.
            interp.RegisterCommand("get_material", (i, a) =>
            {
                var T = ctx.Terrain; if (T == null || a.Length < 2) return "1";
                var (tx, ty) = T.WorldToTile(ParseVec3(a[1]));
                return T.GetMaterial(tx, ty).ToString(CultureInfo.InvariantCulture);
            });
            interp.RegisterCommand("get_digedge", (i, a) =>
            {
                // FUN_00635180 → FUN_005981d0: BFS from the marker through this player's dig-mark
                // nibbles. Open (BDB record byte0 < 0x0c), explored frontier tiles are ranked by
                // squared tile distance to the DWARF (ref = round(dwarf pos + 0.5), strict <).
                // returned as {X, Y, 14.0}: the WRAPPER HARD-CODES Z = 14.0f (0x41600000, the floor
                // line) as the 3rd component; Tcl preserves it via `set originalz [lindex $edgepos 2]`
                // and re-applies it after vector_fixdig. Sentinel {-1,-1}+Z: valid iff Y > 0.
                // 1-tile world-edge inset reject (005981d0:84-87).
                var T = ctx.Terrain; if (T == null || a.Length < 3) return "-1 -1 14";
                var e = ResolveEntityOrNull(ctx, a[2]);
                var target = ParseVec3(a[1]);
                var (sx, sy) = T.WorldToTile(target);
                Vector3 reference = e?.Position ?? target;
                // The wrapper does not pass the raw fractional object coordinate. It packs
                // ref={ROUND(obj[+0xc]+0.5), ROUND(obj[+0x10]+0.5)} (FUN_00635180:153-158).
                int referenceTileX = Mathf.RoundToInt(reference.x + 0.5f);
                int referenceTileY = Mathf.RoundToInt(reference.y + 0.5f);
                if (!T.TryFindDigEdge(
                        sx,
                        sy,
                        e?.Owner ?? ctx.LocalPlayer,
                        referenceTileX,
                        referenceTileY,
                        out int edgeTileX,
                        out int edgeTileY,
                        ctx.FogOfWar))
                {
                    return "-1 -1 14";
                }
                return FormatVec3(new Vector3(edgeTileX, edgeTileY, 14f));
            });
            interp.RegisterCommand("dig_next", (i, a) =>
            {
                // FUN_00597b20: the n-th nearest dig-marked,
                // precondition-passing voxel around $pos, with the anchored weighted cost
                // dx² + (y−(posY−0.75))² + 0.25·((rim+4)−(posZ+6.0))² + 8.0 when the voxel's
                // dig-order id ≠ the gnome's current id (finish the vein first,:144-145); the
                // optional 5th arg ($matchId) makes a differing id a hard reject (:133).
                // Output Z = clamp(rim+4+2.0, 1.0, 14.0) (:249-253 — the +2.0 = DAT_00737330).
                //
                // Per-candidate gates (:127-130) — EXACTLY three:
                // 1. the owner nibble (tile+0x08 >> owner*4) & 0xB != 0 (:127);
                // 2. MATERIAL tile+0x18 & 3 ∈ {1,2} (:128-129) — the STATIC material band.
                // (The old port read this as a dynamic "exposure" byte and used the invented
                // TileExposed proxy — not applied: the native has NO exposure gate here. The
                // progressive front is enforced by the Tcl dig_check + try escalation
                // (z_dignwalk.tcl:163-181/:239) and dig_execute's range tether (:313).)
                // 3. node rim FUN_005968b0 != -1 (:130). (No "Diggable" height gate either —
                // completed tiles drop out via the carve's completion sweep of the nibble.)
                //
                // SLOT SEMANTICS (:85-114 init,:245-262 read): the native keeps a top-N array
                // (N = try+1) initialised to {-1,-1,-1}/1e7 and returns slot N-1 — FEWER THAN N
                // candidates ⇒ the SENTINEL, which ends the Tcl loop (unpacky>0 / try<9,
                // z_dignwalk.tcl:236/:287). The old port CLAMPED to the last candidate instead,
                // so dig_continue's escalation re-served the same dig_check-rejected voxel
                // forever — the gnome grinding in place, its dig order never completing.
                //
                // WINDOW (:58-77): trunc(pos), x ∈ [max(min+2, px−16), min(max−2, px+16)),
                // y ∈ [max(min+2, py−17), min(max−2, py+15)) — EXCLUSIVE upper bounds
                // (:120/:239). Scan y-outer/x-inner; ties keep scan order (:154-168 `<=`) — the
                // sort is made stable with the scan ordinal.
                var T = ctx.Terrain; if (T == null || a.Length < 3) return "-1 -1 -1";
                var gn = ResolveEntityOrNull(ctx, a[2]);
                int owner = gn?.Owner ?? ctx.LocalPlayer;
                int gnomeId = gn?.DigSetId ?? -1;
                int tryN = a.Length > 3 ? Mathf.Max(0, IntArg(a, 3, 0)) : 0;
                bool matchId = a.Length > 4 && ParseTclBool(a[4]);
                var pos = ParseVec3(a[1]);
                int px = (int)pos.x, py = (int)pos.y;                       // trunc (:58-61)
                int wx0 = Mathf.Max(2, px - 16), wx1 = Mathf.Min(T.Width - 2, px + 16);
                int wy0 = Mathf.Max(2, py - 17), wy1 = Mathf.Min(T.Height - 2, py + 15);
                var cand = new System.Collections.Generic.List<(float cost, int ord, int x, int y, int rim)>();
                for (int ty2 = wy0; ty2 < wy1; ty2++)
                    for (int tx2 = wx0; tx2 < wx1; tx2++)
                    {
                        if (!T.IsDigMarked(tx2, ty2, owner)) continue;      //:127
                        if (!T.TileMaterialDiggable(tx2, ty2)) continue;    //:128-129
                        int rim = T.RimAtNode(tx2, ty2);
                        if (rim < 0) continue;                              //:130
                        int id = T.DigMarkIdAt(tx2, ty2, owner);
                        if (matchId && id != gnomeId) continue;             //:133
                        float ddx = tx2 - pos.x;
                        float ddy = ty2 - (pos.y - 0.75f);
                        float ddz = (rim + 4f) - (pos.z + 6f);
                        float cost = ddx * ddx + ddy * ddy + ddz * ddz * 0.25f;
                        if (id != gnomeId) cost += 8f;                      //:144-145
                        cand.Add((cost, cand.Count, tx2, ty2, rim));
                    }
                if (cand.Count <= tryN) return "-1 -1 -1";                  // slot n-1 untouched (:249)
                cand.Sort((p, q) => p.cost != q.cost ? p.cost.CompareTo(q.cost) : p.ord.CompareTo(q.ord));
                var pick = cand[tryN];
                return FormatVec3(new Vector3(pick.x, pick.y, Mathf.Clamp(pick.rim + 6f, 1f, 14f)));
            });
            interp.RegisterCommand("dig_apply", (i, a) =>
            {
                // dig_apply $digpoint $gnome — ONE faithful carve step (FUN_006347a0 → FUN_00596b80)
                // at the digpoint's node, for the GNOME's owner layer: dig-nibble target decode, the
                // rim gate (clear-on-done), gate==rim corner transitions, the eight-owner completion
                // sweep (DiggableTerrain.CarveStep). On success the tile's dig-order id is
                // cached onto the gnome (+0x250) for dig_next's same-vein preference.
                // Node mapping (FUN_006347a0:95-98): node = (int)ROUND(coord + 0.5) on the x87 — the
                // classic round-half-UP idiom under the chop/trunc float→int the engine actually runs
                // (the nearest-even reading would collapse tourn_2100's 205/206 two-column shaft onto
                // ONE node line, which contradicts the shipped game). For the integral dig_next
                // outputs this equals trunc(x+0.5) = x; for fractional inputs it is floor(x+0.5),
                // NOT banker's RoundToInt — so compute it explicitly instead of WorldToTile.
                var T = ctx.Terrain; if (T == null || a.Length < 2) return "0";
                var gn = a.Length >= 3 ? ResolveEntityOrNull(ctx, a[2]) : ctx.This;
                var dp = ParseVec3(a[1]);
                int tx = Mathf.FloorToInt(dp.x + 0.5f), ty = Mathf.FloorToInt(dp.y + 0.5f);
                // CStoryMgr::DigNotify (FUN_005fd060) — fired from DigApply with the SAME rounded
                // node, BEFORE the carve (FUN_006347a0:103 vs the carve at:119): story callback,
                // story-map mark, growth-pressure accumulation + breakthrough latch.
                StoryDigNotify(ctx, T, tx, ty);
                int digOwner = gn?.Owner ?? ctx.LocalPlayer;
                // CMapBeautifier during-dig deco scan — FUN_006347a0:112-118 runs ScanAtPos
                // BEFORE the carve at the SAME rounded node with the digger's owner layer:
                // match the dummy-marked tunnel field against the lg template registry, register
                // a beam rect + queue the template's authored deco (beams/treasure/critters).
                // (The native local-view gate is always true in our SP port.)
                try { MapDecoOf(ctx)?.ScanAtPos(tx, ty, digOwner); }
                catch (System.Exception e) { Debug.LogError($"[map_deco] scan failed at ({tx},{ty}): {e.Message}"); }
                bool ok = T.CarveStep(tx, ty, digOwner, out int dugId);
                if (ok && gn != null) gn.DigSetId = dugId;
                // CMapBeautifier cave-skin completion pass — FUN_006347a0:127-133 runs it AFTER the
                // carve at the SAME rounded node with the DIGGER's owner (+0x36c). (The native
                // local-view gate `game+0x64 == view owner` is always true in our SP port.)
                try { CaveSkinDigCheck(ctx, T, tx, ty, digOwner); }
                catch (System.Exception e) { Debug.LogError($"[cave_skin] completion pass failed at ({tx},{ty}): {e.Message}"); }
                return ok ? "1" : "0";
            });
            interp.RegisterCommand("dig_mark", (i, a) =>
            {
                // FUN_006586c0 (Docs/re_parts/dig-natives.md §7): dig_mark $owner $x $y $mode [$cnt]
                // — mode 0 = paint nibble (cnt|8), cnt = the depth code {0=full,1,2,3=shallow};
                // mode 1 = paint-dummy (nibble 3, native standard-tunnel target 11);
                // mode 2 = CLEAR the owner's nibble.
                var T = ctx.Terrain; if (T == null || a.Length < 5) return "";
                int owner = IntArg(a, 1, ctx.LocalPlayer);
                int x = Mathf.RoundToInt(ParseFloat(a[2])), y = Mathf.RoundToInt(ParseFloat(a[3]));
                int mode = IntArg(a, 4, 0);
                int cnt = a.Length > 5 ? Mathf.Clamp(IntArg(a, 5, 0), 0, 3) : 0;
                if (owner >= 7) return "";                       // the `local_14 < 7` gate
                if (mode == 2) T.ClearDigMark(x, y, owner);
                else T.SetDigMark(x, y, owner, cnt, dummy: mode == 1);
                return "";
            });
            interp.RegisterCommand("is_dig_marked", (i, a) =>
            {
                var T = ctx.Terrain; if (T == null || a.Length < 5) return "0";
                int x1 = IntArg(a, 1, 0), y1 = IntArg(a, 2, 0), x2 = IntArg(a, 3, 0), y2 = IntArg(a, 4, 0);
                for (int yy = y1; yy < y2; yy++)
                    for (int xx = x1; xx < x2; xx++)
                        if (T.IsDigMarked(xx, yy, ctx.LocalPlayer)) return "1";
                return "0";
            });
            // cave_skin add <size> <x1> <y1> <x2> <y2> | cave_skin rem <x> <y> — the CMapBeautifier
            // cave-rect list (beautifier+0x30), Tcl handler FUN_0066a9a0 (registration string ref
            // 0x61dea2 → push 0x66a9a0). `add` (argc 7,:16): int size + 4 ROUNDed coords,
            // overlap-drop (FUN_0058eb30) then append {x1,y1,x2,y2,size}. `rem` (argc 4,:250-277):
            // drop the first rect containing (ROUND(x), ROUND(y)) (FUN_0058ec00). Same list the cave
            // STAMP commit appends to (FUN_00567cb0:204-214 = DiggableTerrain.CaveRecords), consumed
            // by the dig-completion pass (CaveSkinDigCheck = FUN_0058ba00). Callers: ai/std_ai.tcl
            //:3020/:2527, Trigger_Schwefel.tcl:798. Was a "0" stub.
            // (Native quirk, NOT reproduced: `add` stores size as FLOAT BITS into the int-compared
            // field (:220 vs FUN_0058e9c0 int match), so AI rects with size 1/2 never matched a skin
            // rule in the shipped game; we store the int — obvious intent, human path identical.)
            interp.RegisterCommand("cave_skin", (i, a) =>
            {
                var T = ctx.Terrain; if (T == null || a.Length < 2) return "";
                if (a[1] == "add" && a.Length == 7)
                {
                    int size = IntArg(a, 2, 0);
                    int x1 = Mathf.RoundToInt(ParseFloat(a[3])), y1 = Mathf.RoundToInt(ParseFloat(a[4]));
                    int x2 = Mathf.RoundToInt(ParseFloat(a[5])), y2 = Mathf.RoundToInt(ParseFloat(a[6]));
                    T.AddCaveRecord(x1, y1, x2, y2, size, markId: 0, owner: ctx.This?.Owner ?? ctx.LocalPlayer);
                }
                else if (a[1] == "rem" && a.Length == 4)
                {
                    T.RemoveCaveRecordAt(Mathf.RoundToInt(ParseFloat(a[2])), Mathf.RoundToInt(ParseFloat(a[3])));
                }
                return "";
            });
            interp.RegisterCommand("vector_fixdig", (i, a) =>
            {
                // FUN_00594e00 (Docs/re_parts/dig-natives.md §3): the dig standoff refiner.
                // (1) step OFF the wall toward the dwarf: f += (gnome−f)/d² when d² ≥ 1, else
                // f = gnome (NOT a unit-normalised step — the pull shrinks with distance);
                // (2) spiral-snap to the nearest WALKABLE sub-cell (+0x3c & 1);
                // (3) climb-band nudge (00594e00:299-331): probe ≤8 rows down (+y) for the FLOOR
                // bit and land on the FIRST floor row; when NO floor exists in the band,
                // REVERT to the wall-snapped row (decompile: the loop tail resets
                // `uVar9 = local_44` every miss — only the floor-hit `break` keeps the pushed
                // row `uVar9 = uVar12`). THIS is the wall-dig start law: a standoff on a bare
                // wall face STAYS ON THE WALL — the gnome climbs there and digs hanging
                // (kletterstand, z_procs.tcl:1707; dig_anim wall branch) until a floor line is
                // carved within 8 rows below, and only then it lands ON the new floor. The old
                // unconditional `fy += 8` parked the standoff a full tile down/inside the rock
                // ⇒ walkdist > nonewwalk every dig_continue ⇒ a rewalk churn (drift between
                // depth planes / climbing in place / hanging in air).
                // (4) Z (LAB_00595590, 00594e00:332-413): BOTH nibble reads use the SAME
                // (fx, fy−5) support cell (the decompile re-derives the identical tile+aux
                // address twice) — z = min(max(gnome.z, h(fx,fy−5)+1), 14.5) (DAT_00737564).
                // The old port re-sampled the BODY row (fx, fy) — the carve-active cell — so
                // the standoff depth jittered with every dig_apply swing.
                var T = ctx.Terrain; if (T == null || a.Length < 3) return a.Length > 1 ? a[1] : "0 0 0";
                var wall = ParseVec3(a[1]); var gnome = ParseVec3(a[2]);
                var nav = T.Nav;
                if (nav != null)
                {
                    float fx0 = wall.x, fy0 = wall.y;
                    float d2 = (fy0 - gnome.y) * (fy0 - gnome.y) + (fx0 - gnome.x) * (fx0 - gnome.x);
                    // Step (1) divides by the DISTANCE, not d² — FUN_0040b7e0 at 00594e4f is an
                    // xmm0 sqrt helper Ghidra drops silently (asm 00594ebf: divss by the sqrt
                    // result). The pull is exactly ONE world unit toward the dwarf; the d²≥1
                    // gate is equivalent to d≥1 (comiss at 00594e5f runs on the sqrt).
                    float d = Mathf.Sqrt(d2);
                    if (d >= 1f) { fx0 += (gnome.x - fx0) / d; fy0 += (gnome.y - fy0) / d; }
                    else { fx0 = gnome.x; fy0 = gnome.y; }
                    // Spiral CENTER row = ROUND(fy*8) + 8 — one full tile BELOW the pulled point
                    // (00594e00:55-57: uVar9 = ROUND(fy·8)+8 feeds BOTH the pre-test and the spiral;
                    // the return uses the found row verbatim, never subtracting the 8). The digger
                    // stands at the foot of the face it digs: without this bias a vertical-seam
                    // digpoint snaps to the walkable shaft above the seam (nearer by pure distance)
                    // and the dig job rewalks into a no-path churn (example: seam x=181/y=122..125 —
                    // top d²=377 vs foot d²=178 with the bias, foot wins; without it, 137 vs 450,
                    // top wins). Radius 0x32=50 (00594e00:69), not 24.
                    int px = Mathf.RoundToInt(fx0 * 8f), py = Mathf.RoundToInt(fy0 * 8f) + 8;
                    if (TrySnapToSub(nav, px, py, 50,
                            Diggles.Runtime.Navigation.NavWorld.SubWalk, out int fx, out int fy))
                    {
                        for (int k = 0; k < 8; k++)                          // rows fy.. fy+7
                            if ((nav.SubAt(fx, fy + k) & Diggles.Runtime.Navigation.NavWorld.SubFloor) != 0)
                            { fy += k; break; }                              // no floor ⇒ fy unchanged (revert)
                        int h5 = nav.GetHmapSub(fx, fy - 5);                 // the support sample 5 rows up
                        float z = gnome.z;
                        if (z <= h5 + 1) z = h5 + 1;                         // same (fx, fy−5) cell re-read
                        return FormatVec3(new Vector3(fx * 0.125f, fy * 0.125f, Mathf.Min(z, 14.5f)));
                    }
                }
                var (dx, dy) = T.WorldToTile(wall);
                var (sx, sy) = T.NearestStandableNeighbour(dx, dy, gnome);
                return FormatVec3(new Vector3(sx, sy, T.HmapToZ(sx, sy)));
            });
            interp.RegisterCommand("dig_resetid", (i, a) =>
            {
                if (a.Length > 1 && ResolveEntityOrNull(ctx, a[1]) is { } e) e.DigSetId = -1;
                return "";
            });
            // get_angle_num4 $pos $cpos → quantized direction 0..3 of d=(cpos-pos) by atan2(d.y,d.x) on the
            // NATIVE X/Y (horizontal) plane: 1=+X(east) 2=+Y(north/depth) 3=-X(west) 0=-Y(south). Instruction-
            // exact RE of FUN_00646b10 (constants 3π/4, 10π, -π/2). It was MISSING entirely (neither real nor
            // stubbed) → dig_check (z_dignwalk:170) + dig_anim (:518) threw "unknown command" → the REAL gnome
            // dig loop crashed (the Ctrl+click test bypasses it). Cross-checked vs dig_check's xl/yl tables.
            interp.RegisterCommand("get_angle_num4", (i, a) =>
            {
                if (a.Length < 3) return "0";
                var p = ParseVec3(a[1]); var c = ParseVec3(a[2]);
                float ang = Mathf.Atan2(c.y - p.y, c.x - p.x);                  // d = arg2 - arg1
                int eax = (int)((ang - 2.3561945f + 31.415928f) / -1.5707964f); // trunc toward zero (cvttss2si)
                return ((63 - eax) & 3).ToString(CultureInfo.InvariantCulture);
            });
            // check_ghost_coll <mode> <subject> <item> — TCL:CTclObjMethods::CheckGhostColl
            // (FUN_0067b090): queries the item's STANDING ORDER GHOST (the placement singleton's
            // +0xf1 registry — the loop at:131-140 iterates DAT_0076a338+0xf1/+0xf5). THE
            // native body law for gnomes-vs-buildings: the engine never blocks walking through
            // bodies (set_collision/rec+0xb8 gates PLACEMENT only — FUN_005e7000's sole caller
            // region is the placement tick FUN_0046b450; Calc1 gates on tile+0x06 alone, which
            // only doors/story barriers patch via set_pf_influence INT_MAX);
            // instead the Tcl chain shoos gnomes out reactively through this query:
            // bbox → "{min} {max}" native corners of the ghost body — walk_out_of steps a
            // gnome out of a body it stands in (z_dignwalk.tcl:1049-1074, baby.tcl:301);
            // objects → boolean body-overlap vs collidable objects — prod_buildup_check's
            // authoritative pre-build re-validation (z_work_common.tcl:345-371);
            // gnomes → the gnome/baby refs standing INSIDE the ghost body (subject excluded)
            // — send_gnomes_outofplacement / prod_buildup_waitforfree
            // (z_work_common.tcl:374-401) walk_outofplacement's each of them.
            // Was a bulk "0" stub — no gnome was ever shooed out of a placement footprint (the
            // stand-inside-the-new-building case). No standing ghost for the item → "0" (the
            // native no-record path), matching the Tcl `!= 0` guards.
            interp.RegisterCommand("check_ghost_coll", (i, a) =>
            {
                if (a.Length < 4 || ctx.Placement is not { } pcGhost) return "0";
                var item = ResolveEntityOrNull(ctx, a[3]);
                if (item == null) return "0";
                switch (a[1])
                {
                    case "bbox":
                        if (!pcGhost.TryGetOrderGhostBody(item.Handle, out var bMin, out var bMax))
                            return "0";
                        return "{" + FormatVec3(bMin) + "} {" + FormatVec3(bMax) + "}";
                    case "objects":
                        return pcGhost.OrderGhostCollidesObjects(item.Handle) ? "1" : "0";
                    case "gnomes":
                    {
                        if (!pcGhost.TryGetOrderGhostBody(item.Handle, out var gMin, out var gMax))
                            return "0";
                        int subject = ResolveEntityOrNull(ctx, a[2])?.Handle ?? -1;
                        var inside = new System.Collections.Generic.List<string>();
                        foreach (var e in ctx.Entities.Values)
                        {
                            if (e.Handle == subject || e.ContainedIn >= 0) continue;
                            var kind = e.Class?.Kind;
                            if (kind != "gnome" && kind != "baby") continue;
                            var p = e.Position;
                            // inclusive bounds — the Tcl side of the same test is vector_inbox
                            // (>= / <=), and a gnome STANDS exactly at the body's base row
                            if (p.x >= gMin.x && p.x <= gMax.x && p.y >= gMin.y && p.y <= gMax.y
                                && p.z >= gMin.z && p.z <= gMax.z)
                                inside.Add(e.Handle.ToString(CultureInfo.InvariantCulture));
                        }
                        return inside.Count == 0 ? "0" : TclList.Format(inside);
                    }
                    default: return "0";
                }
            });
            interp.RegisterCommand("inc_playerdigmarkid", (i, a) => { ctx.Terrain?.IncPlayerDigMarkId(); return ""; });

            // obj_valid <ref> → 1 if the handle is a live entity, else 0. Was a bulk "0" stub, which
            // broke EVERY `if {[obj_valid $x]} {...}` guard — notably del_current_muetze (z_work_common.tcl:729):
            // the whole unlink+del block was skipped, so the old hat was never removed → two hats stacked /
            // hat not removed on takeoff. Real impl fixes the hat lifecycle (and all other obj_valid guards).
            interp.RegisterCommand("obj_valid", (i, a) =>
                a.Length >= 2 && ResolveEntityOrNull(ctx, a[1]) != null ? "1" : "0");

            // ----- production (M7) — de-stub the leaves + a correct-by-construction cook -----
            // genericprod.tcl is loaded but was inert because these returned "0". Per-building schedule
            // (ProdSlots) + buildup flag are real per-entity state; the spatial leaves return the shapes
            // the Tcl expects (esp. get_place MUST be "-1 0 0" not "0", else fallbacks misfire).
            interp.RegisterCommand("get_prod_slot_cnt", (i, a) =>
                a.Length >= 3 && ResolveEntityOrNull(ctx, a[1]) is { } b && b.ProdSlots.TryGetValue(a[2], out var n)
                    ? n.ToString(CultureInfo.InvariantCulture) : "0");
            interp.RegisterCommand("set_prod_slot_cnt", (i, a) =>
            {
                if (a.Length >= 4 && ResolveEntityOrNull(ctx, a[1]) is { } b
                    && double.TryParse(a[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                    b.ProdSlots[a[2]] = n;
                return "";
            });
            // get_prod_slot_list <b> — the building's PRODUCT CATALOG. The class carries it as the
            // prod_items method (= tttitems_<Class> from techtreetunes.tcl, baked in at class load
            // by genericprod.tcl:103). Was a "0" bulk stub → the production panel showed no
            // products at all.
            interp.RegisterCommand("get_prod_slot_list", (i, a) =>
            {
                if (a.Length < 2 || ResolveEntityOrNull(ctx, a[1]) is not { } b || b.Class == null) return "";
                try { return interp.Eval($"call_method_static {b.Class.Name} prod_items"); }
                catch { return ""; }
            });
            // get_prod_slot_invented <b> <item> — native tests the owner's attribute page at the
            // exact registry index retained by this CProd slot; -1 means always known. The name
            // currently occupying that index is only a bridge into the already-restored raw page.
            interp.RegisterCommand("get_prod_slot_invented", (i, a) =>
            {
                if (a.Length < 3 || ResolveEntityOrNull(ctx, a[1]) is not { } b) return "0";
                DigglesTclEntity.NativeProductionSlotState? nativeSlot =
                    b.NativeProductionSlots?.Find(slot => slot.ClassName == a[2]);
                if (nativeSlot != null)
                {
                    if (nativeSlot.RegistryValue2 == -1) return "1";
                    foreach (KeyValuePair<string, int> entry in
                        OriginalSaveNativeAttributeSlots.SlotByName)
                    {
                        if (entry.Value != nativeSlot.RegistryValue2) continue;
                        if (ctx.OwnerAttribs.TryGetValue(
                                b.Owner,
                                out Dictionary<string, double>? ownerValues) &&
                            ownerValues.TryGetValue(entry.Key, out double nativeValue))
                        {
                            return nativeValue != 0 ? "1" : "0";
                        }
                        break;
                    }
                }
                try
                {
                    var v = interp.Eval($"get_owner_attrib {b.Owner} Bp{a[2]}").Trim();
                    return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d != 0
                        ? "1" : "0";
                }
                catch { return "0"; }
            });
            // get_prod_slot_inventable <b> <item> — "may this item be invented here" (the book's
            // gate, info_prods.tcl:39/129). The native predicate is the tech/era registry; the
            // port has no era system, so the pragmatic truth is "the item belongs to this class's
            // catalog" (prod_items = tttitems_<Class>) — was a bulk "0" stub, which read every
            // uninvented order as non-inventable.
            interp.RegisterCommand("get_prod_slot_inventable", (i, a) =>
            {
                if (a.Length < 3 || ResolveEntityOrNull(ctx, a[1]) is not { } b || b.Class == null) return "0";
                try
                {
                    foreach (var it in TclList.Parse(interp.Eval($"call_method_static {b.Class.Name} prod_items")))
                        if (it == a[2]) return "1";
                }
                catch { /* no catalog → not inventable */ }
                return "0";
            });
            // get_prod_slot_buildable <b> <item> — "ingredients available" (drives the Red<icon>
            // variant): every recipe material (prod_item_materials) is either already in the
            // place inventory or has an eligible source in the world (ProdDispatcher.SlotBuildable
            // — the port reading of the native per-slot material byte, FUN_00434e60 slot+0x18).
            interp.RegisterCommand("get_prod_slot_buildable", (i, a) =>
            {
                if (a.Length < 3 || ResolveEntityOrNull(ctx, a[1]) is not { } b) return "0";
                var disp = ctx.ProdDispatcher;
                return disp == null || disp.SlotBuildable(b, a[2]) ? "1" : "0";
            });
            // info_end_prod <prodplace> — fired by the building's finish_itemprod method at the END
            // of one produced item (genericprod.tcl:376, reached via the work chain's terminal
            // prod_finishitem → z_work_common.tcl:210). The native (FUN_0069e210:57-104) DECREMENTS
            // the prodplace's open-job/slot demand for the item it just finished (the planner record
            // +0x3e0 slot whose +0x48 == the active itemtype; a count slot (==10 ⇒ infinite, marker
            // DAT_00737034) is left alone, a finite slot is reduced). It was a "0" bulk stub, so the
            // Grillpilz slot NEVER fell — prod_getitemtask kept returning a fresh task list
            // (genericprod.tcl:723 gates on slot>0) and the cook looped forever. The
            // produced item is the building's current_itemtype, set by get_itemtasklist
            // (genericprod.tcl:352-356) in the building's own object scope.
            interp.RegisterCommand("info_end_prod", (i, a) =>
            {
                if (a.Length < 2 || ResolveEntityOrNull(ctx, a[1]) is not { } b) return "";
                if (b.VarsScope.Vars.TryGetValue("current_itemtype", out var item)
                    && !string.IsNullOrEmpty(item)
                    && b.ProdSlots.TryGetValue(item, out var n) && n > 0 && n < 10)   // 10 = infinite (kept)
                    b.ProdSlots[item] = n - 1;
                // The produced item is done — the progress surface resets (the native clears the
                // icon progress with the job; genericprod.tcl:373-380 finish_itemprod is the only
                // caller and stop_production follows immediately).
                b.CurrentProdItem = "";
                b.ProdProgressCur = 0;
                b.ProdProgressMax = 0;
                return "";
            });
            // info_start_prod <place> <itemtype> — genericprod.tcl fires it when a produce/invent
            // task list is handed to the worker (prod_getitemtask:714/:733), always right before
            // the first info_progress_prod. The port stores the produced item on the entity so the
            // UI can attach the progress bar to the matching product cell ("Bp" invention orders
            // surface as their product class).
            interp.RegisterCommand("info_start_prod", (i, a) =>
            {
                if (a.Length < 3 || ResolveEntityOrNull(ctx, a[1]) is not { } b) return "";
                var item = a[2];
                if (item.StartsWith("Bp", System.StringComparison.Ordinal)) item = item.Substring(2);
                b.CurrentProdItem = item;
                b.ProdProgressCur = 0;
                b.ProdProgressMax = 0;
                return "";
            });
            // info_progress_prod <place> <cur> <max> — the per-building production/buildup progress
            // (the native routes it to the object's icon progress word; ViWinProduction draws the
            // pie). Callers: prod_progress (genericprod.tcl:362, one bump per completed work-loop
            // step), prod_progressjump (:366-369, rescales BOTH numbers), prod_getitemtask
            // (:717/:734, the initial 0-or-1/N), set_buildupstep/inc_buildupstep (:219/:343 —
            // construction progress, no info_start_prod around it). Stored RAW; ProdProgressPct
            // derives 0..100.
            interp.RegisterCommand("info_progress_prod", (i, a) =>
            {
                if (a.Length < 4 || ResolveEntityOrNull(ctx, a[1]) is not { } b) return "";
                b.ProdProgressCur = Mathf.RoundToInt(ParseFloat(a[2]));
                b.ProdProgressMax = Mathf.RoundToInt(ParseFloat(a[3]));
                return "";
            });
            // get/set_buildupstate — the native "built" flag, obj+0x3d0 bit 0x400000.
            // GetBuildUpState (FUN_0067e410:57) returns (flags & 0x400000) != 0; SetBuildUpState
            // (FUN_0067e1e0:74-86): FALSE/unparsable clears the bit (:76), TRUE writes
            // flags = flags & 0xffffbfff | 0x400000 (:85) — i.e. setting "built" ALSO CLEARS THE
            // BOXED BIT (0x4000). Spawn default = UNBUILT (CObj ctor word 0x01100101, bit 22 clear,
            // FUN_005c83f0:224); genericprod object-init flips real buildings to built
            // (genericprod.tcl:451) and the construction chain drives it from then on
            // (set_buildupanim, genericprod.tcl:812-813).
            interp.RegisterCommand("get_buildupstate", (i, a) =>
                a.Length >= 2 && ResolveEntityOrNull(ctx, a[1]) is { } b ? (b.BuildupComplete ? "1" : "0") : "0");
            interp.RegisterCommand("set_buildupstate", (i, a) =>
            {
                if (a.Length < 3 || ResolveEntityOrNull(ctx, a[1]) is not { } b) return "";
                bool built = ParseTclBool(a[2]);
                b.BuildupComplete = built;
                if (built)
                {
                    b.VarsScope.Vars["_boxed"] = "0";   // the `& 0xffffbfff` term (FUN_0067e1e0:85)
                    b.BuildupRequested = false;          // a finished building has no pending request
                    // NO claim release here: the native SetBuildUpState (FUN_0067e1e0:74-86)
                    // writes ONLY the object flag word — the planner claim rec+0x24 is released
                    // exclusively by the release paths (FUN_0043a0c0 via gnome-idle/failed-work).
                    // A former `CurrentWorkerHandle = -1` "construction claim released" glue fired
                    // from set_buildupanim's per-step `set_buildupstate this [buildup_step > max]`
                    // (genericprod.tcl:812-813) — prod_builddown_rep's first set_buildupstep write
                    // sits above max, so every pack order dropped its worker claim ~0.3 s after
                    // dispatch while the pack byte deliberately survives → the collector
                    // re-dispatched a fresh packer every sweep (repro: PackFanoutReproTests).
                }
                RefreshBuildingClanFlag(b);
                RefreshTransportInfluence(ctx, b);
                return "";
            });
            // set/get_prod_buildup — the planner's per-prodplace "construction requested" byte
            // (planner record obj+0x3e0, byte +0x2a): SetProdBuildUp (FUN_006937e0:86) writes it,
            // GetProdBuildUp (FUN_00693610:59) reads it. The planner's job collector turns it into
            // a kind-7 buildup job gated only on "not contained" (FUN_005f0850:22-54), and the
            // dispatch emitter clears it — one-shot (FUN_005f3a50:134-138). Was a bulk "0" stub.
            interp.RegisterCommand("set_prod_buildup", (i, a) =>
            {
                if (a.Length >= 3 && ResolveEntityOrNull(ctx, a[1]) is { } b)
                    b.BuildupRequested = ParseTclBool(a[2]);
                return "";
            });
            interp.RegisterCommand("get_prod_buildup", (i, a) =>
                a.Length >= 2 && ResolveEntityOrNull(ctx, a[1]) is { } b ? (b.BuildupRequested ? "1" : "0") : "0");
            // get_linkpos <obj> <linkid> — the link/dummy point's offset from the object
            // origin in NATIVE units (TCL:CTclObjMethods::GetLinkPos, FUN_00686d10).
            // Ground truth: linkid ∈ [0,31] (:99-102 raises "Invalid link-id" outside);
            // model/anim record missing → "0 0 0" (:114-119); else the model-space link
            // point at the CURRENT anim frame (FUN_00408920/FUN_00408a10 on model+0x5c+id*4)
            // is rotated by the object's rotation (obj+0x40/44/48,:131-154) and scaled to
            // native units — x,y × DAT_00736fcc, z doubled (:152-153) because the native
            // height unit is half the horizontal one. The RELATIVE offset is returned
            // (scripts always `vector_add [get_pos this] [get_linkpos this N]`,
            // bordell.tcl:89, tempel.tcl:58, Lorelei.tcl:79). The port resolves it through
            // the live transform chain (inner Model = Q_x(-90)·ModelToWorldScale; the
            // WorldRoot Local→Native map carries the ×2 height), which reproduces exactly
            // that mapping. Was a "0 0 0" stub → every work/guest walk derived from a
            // dummy targeted the building ORIGIN.
            interp.RegisterCommand("get_linkpos", (i, a) =>
            {
                if (a.Length < 2 || ResolveEntityOrNull(ctx, a[1]) is not { } e) return "0 0 0";
                int linkId = 0;   // missing/unparsable → 0 (FUN_00686d10:91-97)
                if (a.Length > 2) int.TryParse(a[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out linkId);
                if (linkId < 0 || linkId > 31)
                {
                    if (s_loggedBodyErrors.Add("get_linkpos badid"))
                        Debug.LogWarning($"[get_linkpos] Invalid link-id : {linkId} (further repeats suppressed)");
                    return "0 0 0";   // native raises a Tcl error; tolerance convention = sentinel
                }
                return TryGetLinkOffsetNative(ctx, e, linkId, applyRotation: true, out var off)
                    ? FormatVec3(off)
                    : "0 0 0";        // no model/link data (incl. headless) = the native's -1-model path
            });
            // get_linkrot <obj> <linkId> — the link record's intrinsic Euler (radians, native model
            // space; index 1 = yaw). Seated/at-link poses do `set_roty this [lindex [get_linkrot
            // $item $d] 1] + 3.1415` (z_work_common.tcl:500,524,574) to sit FACING the seat. Was a
            // "0 0 0" stub although the decoder existed all along (DigglesLinkPoints.TryGetLocalEuler
            // — the hat-tilt path): every seated/working-at-link pose got yaw π regardless of the
            // link's authored orientation (facing away from the seat).
            interp.RegisterCommand("get_linkrot", (i, a) =>
            {
                if (a.Length < 3 || ResolveEntityOrNull(ctx, a[1]) is not { } e || e.GameObject == null)
                    return "0 0 0";
                if (!int.TryParse(a[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int linkId))
                    return "0 0 0";
                var inner = FindInnerModel(e.GameObject);
                var anim = inner != null ? inner.GetComponent<Diggles.Runtime.DigglesAnimator>() : null;
                return anim != null && anim.TryGetLinkEuler(linkId, out var eul) ? FormatVec3(eul) : "0 0 0";
            });
            // set_posbottom's free-item law reads the current display-mesh selector at obj+0x39c/
            // +0x3a0 and subtracts mesh bbox.maxY * 0.005 from the requested bottom Y before the
            // position write (FUN_005cb600:17-41). Those selectors are replaced with the active
            // animation/mesh at FUN_005cdae0:68-93. Keep the conversion on the inventory-item
            // route admitted by eventgen.tcl:245-274/:34; boxed structures stay unchanged.
            //
            // get_posbottom is the symmetric twin: FUN_006842c0 (profiler tag says
            // "TCL:CTclObjMethods::GetPos" — a copy-paste; the REAL get_pos is the other
            // GetPos-tagged handler FUN_00684120, which returns obj+0xc raw) reads the SAME
            // selector pair (+0x39c/+0x3a0, boxed override DAT_00772400+0xa3d4/+0xa3dc under
            // flag 0x4000,:62-67) and returns y + bank[+0x54]·DAT_00736fcc (:81-87) — the
            // exact term the set subtracts. GetItemMeshBottomOffset is the shared source for
            // both directions, so set→get round-trips (Skirmish.tcl:42→:46 zwerg→fs handoff;
            // genericprod.tcl:671 / z_dignwalk.tcl:744 read heights natively shifted by it).
            interp.RegisterCommand("get_posbottom", (i, a) =>
            {
                if (a.Length < 2 || ResolveEntityOrNull(ctx, a[1]) is not { } e) return "0 0 0";
                Vector3 bottom = e.Position;
                bottom.y += GetItemMeshBottomOffset(ctx, e);
                return FormatVec3(bottom);
            });
            interp.RegisterCommand("set_posbottom", (i, a) =>
            {
                if (a.Length >= 3 && ResolveEntityOrNull(ctx, a[1]) is { } e)
                {
                    Vector3 bottomPosition = ParseVec3(a[2]);
                    bottomPosition.y -= GetItemMeshBottomOffset(ctx, e);
                    e.Position = bottomPosition;
                    if (e.GameObject != null && ctx.EntitySpawner != null) ctx.EntitySpawner.ApplyPosition(e.GameObject, e.Position);
                }
                return "";
            });
            // get_negbbox / get_posbbox <obj> — the model bounding-box corner OFFSETS from the
            // object's anchor, native frame (neg = min corner, pos = max corner; negbbox
            // components ≤ 0). Consumer: from_wall (z_procs.tcl:424-438, muetzen.tcl:121-132)
            // nudges a wall-dropped item out of the wall by `z + negbbox.z − 1.0` against
            // get_hmap. Was UNREGISTERED (unknown → "0" → lindex "" → the clearance term
            // silently dropped, items could clip into the wall — audit inventory-items #get_negbbox).
            // With a live model: world AABB of the renderers mapped through the spawn frame back
            // to native axes, relative to e.Position. Headless / no model → "0 0 0" (safe: the
            // lindex yields a number and from_wall degrades to a 1.0 clearance).
            string BboxCorner(string[] a, bool positive)
            {
                var e = a.Length >= 2 ? ResolveEntityOrNull(ctx, a[1]) : null;
                var go = e?.GameObject;
                if (e == null || go == null || ctx.SpawnParent == null) return "0 0 0";
                var rends = go.GetComponentsInChildren<UnityEngine.Renderer>(true);
                if (rends.Length == 0) return "0 0 0";
                bool any = false; Vector3 mn = default, mx = default;
                foreach (var r in rends)
                {
                    var b = r.bounds;
                    for (int c = 0; c < 8; c++)   // both AABB corners per axis — the axis map permutes/flips
                    {
                        var w = new Vector3((c & 1) == 0 ? b.min.x : b.max.x,
                                            (c & 2) == 0 ? b.min.y : b.max.y,
                                            (c & 4) == 0 ? b.min.z : b.max.z);
                        var nat = Diggles.Runtime.RuntimeWorldSpawner.LocalToNative(
                                      ctx.SpawnParent.InverseTransformPoint(w)) - e.Position;
                        if (!any) { mn = mx = nat; any = true; }
                        else { mn = Vector3.Min(mn, nat); mx = Vector3.Max(mx, nat); }
                    }
                }
                return FormatVec3(positive ? mx : mn);
            }
            interp.RegisterCommand("get_negbbox", (i, a) => BboxCorner(a, positive: false));
            interp.RegisterCommand("get_posbbox", (i, a) => BboxCorner(a, positive: true));
            // identity passthrough: a "0" return broke walk_near_item, which wraps every walkpos in vector_fix.
            // vector_fix — FUN_00595770 (Docs/re_parts/dig-natives.md §4): the plain landscape snap
            // every walk target goes through (walk_pos / walk_pos_dig / walk_run / walk_near_item).
            // OOB margin (48 sub-cells) → PASS THROUGH; already-walkable → keep the caller's z;
            // else spiral to the nearest walkable sub-cell, z = clamp(max(pos.z, support(y−1)+1), 14.5).
            // (Was an identity stub → un-snapped, off-mesh walk targets — gap G4.)
            interp.RegisterCommand("vector_fix", (i, a) =>
            {
                if (a.Length < 2) return "0 0 0";
                var T = ctx.Terrain; var nav = T?.Nav;
                if (nav == null) return a[1];
                var pos = ParseVec3(a[1]);
                int vx = Mathf.RoundToInt(pos.x * 8f), vy = Mathf.RoundToInt(pos.y * 8f);
                if (vx < 48 || vy < 48 || vx >= nav.SubW - 48 || vy >= nav.SubH - 48) return a[1];
                if (vx <= 9 || vy <= 9) return "0 0 0";
                if ((nav.SubAt(vx, vy) & Diggles.Runtime.Navigation.NavWorld.SubWalk) != 0)
                {
                    // Port deviation (items/rings sinking into the ground): the native
                    // walkable branch passes z through untouched (FUN_00595770:270-275) because
                    // native movers never hand it a below-relief depth. Our movement stack can
                    // (climb/dig drift, WIP on the nav track), and putdown-in-place seats items
                    // at gnome z ± abst (beamto_world z_procs.tcl:379-385 — 0.8 for a boxed
                    // pannier) through this branch — a minimum-depth gnome buried the drop
                    // inside the relief. Apply the spiral branch's own seat law (:256-262,
                    // z = min(max(z, hmap(y−1)+1), 14.5)) here too: it only fires on depths
                    // that are illegal to stand at anyway.
                    float zc = Mathf.Min(Mathf.Max(pos.z, nav.GetHmapSub(vx, vy - 1) + 1f), 14.5f);
                    return FormatVec3(new Vector3(vx * 0.125f, vy * 0.125f, zc));
                }
                if (TrySnapToSub(nav, vx, vy, 24, Diggles.Runtime.Navigation.NavWorld.SubWalk, out int fx, out int fy))
                {
                    float z = Mathf.Min(Mathf.Max(pos.z, nav.GetHmapSub(fx, fy - 1) + 1f), 14.5f);
                    return FormatVec3(new Vector3(fx * 0.125f, fy * 0.125f, z));
                }
                return a[1];
            });
            // get_place — the REAL PlaceFinder (P6; was a "-1 0 0" stub = sparetime-idle.md gap 1:
            // walk_random always bailed, so idle gnomes never wandered or vacated locked spots).
            // Native = TCL:CTclGlobalMethods::GetPlace (FUN_00656560) → CPlaceFinder_FindPlace.
            // Option arities are FIXED and MUST be walked exactly (the old per-pair walker derailed
            // on -rect/-checkfree and silently dropped every later option — sleep/talk/cartwheel
            // placements parsed garbage):
            // -center {x y z} / -nearpos {x y z} — one list arg (:135-198,:833-874);
            // -rect x0 z0 x1 z1 — FOUR scalars, a centre-relative band on the
            // GROUND plane: x0/x1 bound the east axis and
            // z0/z1 bound the DEPTH axis (CPlaceFinder_
            // FindPlace 005a9700:165-170 — the rect branch
            // seeds the depth scan band as
            // (p7+center.z)·8/2.. (p9+center.z)·8/2 and
            // the x args feed FUN_0041f7a0's column seed;
            // the tile ROW is nav-implied, not bounded).
            // The old port read the 2nd pair as the tile-Y
            // row band — prod_bringtoprod's delivery rect
            // (-10 1 10 3, z_work_common.tcl:71) then
            // excluded every floor row → "itempos not
            // found" → EVERY material delivery failed;
            // -circle R -random N -mindist M -walldist W -rimdist D -placelock T -materials B
            // -except <obj> -placelockidexcept <id> -enableplacelockcheck B — one arg each
            // (:584-1071);
            // -checkfree — NO arg; switches the RESULT to a BOOLEAN
            // "free place found" (:1072-1112, emit:1135-1140 —
            // sptfun_check_wheeling does `if {[get_place
            // -checkfree …]}`, z_spare_fun.tcl:789).
            // Consumers of the rect/except forms: sparetime_slp_start ground-sleep
            // (z_spare_procs.tcl:519,529), sparetime_find_talker approach (z_spare_fun.tcl:396),
            // sptfun_check_writing (z_spare_fun.tcl:771), walk_down_from_wall (z_dignwalk.tcl:869),
            // beam_back (z_procs.tcl:1098). Failure = the "-1 0 0" sentinel so
            // prod_createproduct/walk_near_item keep their fallbacks.
            interp.RegisterCommand("get_place", (i, a) =>
            {
                var nav = ctx.Terrain?.Nav;
                Vector3 center = default, nearpos = default;
                bool hasCenter = false, hasNear = false, checkFree = false, hasRect = false, hasClip = false;
                float circle = 2f, mindist = 0f, walldist = 0f, randomW = 0f, placelockDur = 0f;
                float rx0 = 0f, ry0 = 0f, rx1 = 0f, ry1 = 0f;
                float cx0 = 0f, cz0 = 0f, cx1 = 0f, cz1 = 0f;
                int lockExcept = -1, bodyExcept = -1;
                bool materialsVeto = true;   // -materials default (the native flag inits true)
                for (int k = 1; k < a.Length; )
                    switch (a[k])
                    {
                        case "-center":  if (k + 1 < a.Length) { center = ParseVec3(a[k + 1]); hasCenter = true; } k += 2; break;
                        case "-nearpos": if (k + 1 < a.Length) { nearpos = ParseVec3(a[k + 1]); hasNear = true; } k += 2; break;
                        case "-rect":
                            if (k + 4 < a.Length)
                            {
                                rx0 = ParseFloat(a[k + 1]); ry0 = ParseFloat(a[k + 2]);
                                rx1 = ParseFloat(a[k + 3]); ry1 = ParseFloat(a[k + 4]);
                                hasRect = true;
                            }
                            k += 5; break;
                        // -clip x0 z0 x1 z1 — FOUR scalars like -rect: the centre-relative
                        // EXCLUSION rect (east band × DEPTH band). walk_out_of searches the
                        // -rect ring AROUND a ghost body and clips the body's own rect OUT
                        // (z_dignwalk.tcl:1055-1063 — xn/xp = bbox x ±1, zn/zp = bbox z ±2).
                        // The old walker took it as a ONE-value option → derailed on the 3
                        // trailing scalars and silently dropped -placelock (the option-arity
                        // plague, same class as the -rect/-checkfree derail this port fixed).
                        case "-clip":
                            if (k + 4 < a.Length)
                            {
                                cx0 = ParseFloat(a[k + 1]); cz0 = ParseFloat(a[k + 2]);
                                cx1 = ParseFloat(a[k + 3]); cz1 = ParseFloat(a[k + 4]);
                                hasClip = true;
                            }
                            k += 5; break;
                        case "-circle":  if (k + 1 < a.Length) circle = ParseFloat(a[k + 1]); k += 2; break;
                        case "-mindist": if (k + 1 < a.Length) mindist = ParseFloat(a[k + 1]); k += 2; break;
                        // -placelock T — auto-lock the RESULT for T seconds (finder +0x2c;
                        // FindPlace locks the winner via LockPlace(pos, id −1, radius 0.8) —
                        // 005a9700:327 → FUN_005aa900 — which is a no-op unless T > 0, the
                        // DAT_00736f78 guard; default 0). Callers: walk_out_of / baby ghost
                        // avoidance `-placelock 5` (z_dignwalk.tcl:1064, baby.tcl:318).
                        case "-placelock": if (k + 1 < a.Length) placelockDur = ParseFloat(a[k + 1]); k += 2; break;
                        case "-walldist": if (k + 1 < a.Length) walldist = ParseFloat(a[k + 1]); k += 2; break;
                        case "-placelockidexcept": if (k + 1 < a.Length) lockExcept = IntArg(a, k + 1, -1); k += 2; break;
                        case "-checkfree": checkFree = true; k += 1; break;   // FLAG — no value (:1072-1112)
                        // -except <obj>: the body-block exemption — the native CPlaceFinder rejects
                        // candidates occupied by an object BODY, and -except names the one body to
                        // ignore (FUN_00656560 option walker; sparetime_slp_start passes `-except
                        // this`, z_spare_procs.tcl:529, talk-approach passes the partner,
                        // z_spare_fun.tcl:396). Parsed for the body veto below.
                        case "-except":
                            if (k + 1 < a.Length && ResolveEntityOrNull(ctx, a[k + 1]) is { } exEnt)
                                bodyExcept = exEnt.Handle;
                            k += 2; break;
                        // -random N: the N-weighted jitter term of the native cost (FUN_005a9270's
                        // param[0x13] randomness) — walk_random's scatter. Without it the scan is exact.
                        case "-random": if (k + 1 < a.Length) randomW = ParseFloat(a[k + 1]); k += 2; break;
                        // -materials B (default TRUE): whether cells occupied by LYING ITEMS veto a
                        // candidate. Native: FindPlace stamps object occupancy into the scan grid
                        // via FUN_0041cac0(grid, 0, materialsFlag) (005a9700:226 — the flag is the
                        // 3rd arg, param+0x14 from the option walker) and the candidate test
                        // rejects `(cell & 0xff000000) != 0` (:264). Every producer/deliverer
                        // first tries WITH the veto and falls back `-materials false`
                        // (prod_createproduct_rndrot z_work_prod.tcl:256-259, prod_laydown_
                        // infrontof_farm:283-286) — without honouring it the finished product
                        // beamed down onto the delivered-ingredient pile in the same front band.
                        case "-materials": if (k + 1 < a.Length) materialsVeto = ParseTclBool(a[k + 1]); k += 2; break;
                        // one-arg options the port has no state to honour (parsed so the walk stays
                        // aligned): -rimdist, -enableplacelockcheck (native default ON —
                        // FUN_005a9190 sets finder+0x58 = 1; the port lock check is always on).
                        default:
                            if (a[k].Length > 0 && a[k][0] == '-' && k + 1 < a.Length) k += 2; else k += 1;
                            break;
                    }
                if (nav == null || !hasCenter) return checkFree ? "0" : "-1 0 0";
                double now = ctx.Sim?.GameTime ?? 0.0;
                int cx = Mathf.RoundToInt(center.x * 8f), cy = Mathf.RoundToInt(center.y * 8f);
                int wallSubs = Mathf.CeilToInt(walldist * 8f);
                int selfExcept = ctx.This?.Handle ?? 0;

                // -materials veto pre-pass: the lying (uncontained, world-visible) item/material
                // positions near the centre — the port stand-in for the FUN_0041cac0 occupancy
                // stamp (a small per-item footprint instead of the native per-cell grid bits).
                List<Vector3>? lyingItems = null;
                if (materialsVeto)
                {
                    foreach (var it in ctx.Entities.Values)
                    {
                        if (it.ContainedIn >= 0 || it.Class == null) continue;
                        if (it.Class.Kind != "material" && it.Class.Kind != "item") continue;
                        float dx0 = it.Position.x - center.x, dy0 = it.Position.y - center.y;
                        if (dx0 * dx0 + dy0 * dy0 > 400f) continue;   // outside any plausible scan
                        (lyingItems ??= new List<Vector3>()).Add(it.Position);
                    }
                }

                // ---- the native CPlaceFinder law (FUN_005a9700 + scorer FUN_005a9270) ----
                // IDEAL POINT (005a9700:188-207): -nearpos is re-projected onto the MINDIST ring
                // around the centre along the depth-weighted (dz×0.5, DAT_0073708c) direction to
                // the asker — "stand on MY side of the item, at exactly the standoff". No nearpos
                // → ideal = the centre itself. COST (FUN_005a9270:46-66): ‖cand−ideal‖ +
                // 2·‖cand−centre‖, both depth-weighted; candidates outside [mindist, circle]
                // rejected (:29-30). The native scan is a DETERMINISTIC full sweep — minimum cost
                // wins; -random N adds an N-weighted jitter to the cost (walk_random's scatter).
                // The previous port picked 32 RANDOM annulus points and returned the first/nearest
                // valid — pickup stands scattered 0.6→2.6, so play_anim_pickup's 2.0 gate
                // (z_procs.tcl:572) failed at random (no bend anim / "picked up from afar") and
                // check_switcher_dist's 1.0 gate silently killed the Schatztonne open_box chain
                // (z_procs.tcl:161-166,956).
                Vector3 ideal = center;
                if (hasNear)
                {
                    float ix = nearpos.x - center.x, iy = nearpos.y - center.y;
                    float iz = (nearpos.z - center.z) * 0.5f;
                    float il = Mathf.Sqrt(ix * ix + iy * iy + iz * iz);
                    float ring = Mathf.Max(mindist, 0.0001f);
                    if (il > 0.0001f)
                        ideal = new Vector3(center.x + ix / il * ring, center.y + iy / il * ring,
                                            center.z + iz / il * ring * 2f);   // un-weight depth back
                }
                int colHalf = hasRect
                    ? Mathf.CeilToInt(Mathf.Max(Mathf.Abs(rx0), Mathf.Abs(rx1)) * 8f)
                    : Mathf.CeilToInt((circle + 0.0626f) * 8f);
                int colStride = Mathf.Max(1, (colHalf * 2 + 1) / 192);   // cap ~192 columns (wide rects)
                // The tile ROW is never rect-bounded (see the -rect note above) — both forms walk
                // the same ±1.5-tile floor window around the centre row.
                const int rowSpan = 12;

                // Depth scan law, split by call shape (port deviation from the native):
                // • -rect (an explicit scripted depth band — the product/crate/farm drops):
                // depth is a free scan coordinate — FindPlace sweeps (x-column, z) pairs over
                // center.z+ry0.. center.z+ry1 (005a9700:163-164) in 0.5 steps (:249-313,
                // local_44 += 4 → ×0.125), range ≤ the native 64-cell grid (the 0x40 checks
                //:253/:272 = 32 units). This is what lands crates 1..3 in front of the
                // building at dx≈0 (band [cz+min(1,11−cz), 13−cz], z_work_prod.tcl:217) and
                // packs drops into the 2-D ground plane.
                // • -circle (wander/approach shapes — walk_random z_dignwalk.tcl:725,
                // walk_near_item:764, act_when_idle side-steps): the native also scans
                // center.z ∓ 2R (:149-152), but gates each (cell,z) against flood-grid
                // stamps (FUN_0041f7a0 build + FUN_0041bff0) whose z-legality is not yet
                // RE'd. Scanning the whole standing pocket here made side-step/approach
                // winners scatter in depth — gnomes spread invisibly along z and stacked
                // on screen, and walk_near_item ring spots landed |dz|≈2·radius from the
                // item so the 1.0 interaction gate (check_switcher_dist, z_procs.tcl:161-166)
                // aborted barrel opens. Until the grid stamps are RE'd, circle shapes pin one
                // z per (column,row): the analytic optimum of the z cost
                // (1·|z−ideal.z| + 2·|z−center.z|) clamped into the standing pocket keeps
                // approach/side-step depths sane.
                float bandLo, bandHi;
                if (hasRect)
                {
                    bandLo = center.z + ry0;
                    bandHi = Mathf.Min(center.z + ry1, bandLo + 32f);   // the 64-cell grid range cap
                }
                else
                {
                    bandLo = center.z - 2f * circle;
                    bandHi = center.z + 2f * circle;
                }
                float zPin = (ideal.z + 2f * center.z) / 3f;   // separable z-cost optimum (circle pin)

                List<Vector3>? bodyBlockers = CollectBodyBlockers(
                    ctx,
                    center,
                    colHalf * 0.125f + rowSpan * 0.125f + Mathf.Abs(bandHi - bandLo),
                    selfExcept,
                    bodyExcept);

                Vector3 best = default; float bestCost = float.MaxValue; bool found = false;
                for (int ox2 = -colHalf; ox2 <= colHalf; ox2 += colStride)
                {
                    int sx = cx + ox2;
                    float px = sx * 0.125f;
                    if (hasRect && (px - center.x < rx0 - 0.0626f || px - center.x > rx1 + 0.0626f)) continue;
                    for (int dy = 0; dy <= rowSpan; dy++)
                        for (int s = -1; s <= 1; s += 2)
                        {
                            int sy = cy + (dy == 0 && s > 0 ? 0 : dy * s);
                            if (dy == 0 && s > 0) continue;   // sy == cy handled once (s = -1 leg)
                            if (!nav.SubIsFloor(sx, sy)) continue;
                            float py = sy * 0.125f;
                            // -walldist: no climb-wall cell within W along the line.
                            if (wallSubs > 0)
                            {
                                bool nearWall = false;
                                for (int ox = -wallSubs; ox <= wallSubs && !nearWall; ox++)
                                    for (int oy = -2; oy <= 2; oy++)
                                        if (nav.SubIsClimbWall(sx + ox, sy + oy)) { nearWall = true; break; }
                                if (nearWall) continue;
                            }
                            // Candidate depth legality = the STANDING pocket of this column: from
                            // one nibble in FRONT of the back-wall relief (z ≥ hmap(sx,sy−1)+1 —
                            // the vector_fix standing law, FUN_00595770:256-262) to the front-glass
                            // cap 14.5 (DAT_00737564), intersected with the scan band. The old
                            // min(hA,hO)+0.5 strip floor admitted seats half a nibble UNDER the
                            // standing line — a low-seated workplace centre dragged product drops
                            // into the ground relief (buried/invisible items).
                            int hA = nav.GetHmapSub(sx, sy - 1);
                            float zCellLo = Mathf.Max(hA + 1f, bandLo);
                            float zCellHi = Mathf.Min(14.5f, bandHi);
                            if (zCellLo > zCellHi) continue;   // band misses the standable pocket
                            // circle shapes: pin the cell's z to the clamped cost optimum
                            // (the port deviation note above) — one candidate per cell.
                            float zStep = 0.5f;
                            if (!hasRect)
                            {
                                zCellLo = zCellHi = Mathf.Clamp(zPin, zCellLo, zCellHi);
                                zStep = 1f;   // any positive step — the loop runs once
                            }
                            // one jitter draw per (column,row) cell, shared by its z candidates —
                            // the native draws per scored candidate (FUN_005a9270:34/57); a
                            // per-cell draw keeps Random-stream use bounded. ×3.0 = DAT_00737428
                            // (the native jitter scale an earlier revision dropped).
                            float jitter = randomW > 0f ? randomW * Random.value * 3f : 0f;
                            for (float z = zCellLo; z <= zCellHi + 0.0001f; z += zStep)
                            {
                                // -clip: the centre-relative EXCLUSION rect (east × depth) — the
                                // ghost-body cutout of walk_out_of (the search ring is -rect, the
                                // body itself is clipped out). Candidates inside it never win.
                                if (hasClip && px - center.x >= cx0 && px - center.x <= cx1
                                            && z - center.z >= cz0 && z - center.z <= cz1) continue;
                                // The [mindist, circle] band on the depth-weighted distance to centre
                                // (FUN_005a9270:29-30): the MINDIST floor is UNCONDITIONAL
                                // (`mindist<=0 || mindist² <= dc²` — no rect gate), only the CIRCLE
                                // cap is bypassed by the rect flag (param_1[5],:30). The old port
                                // skipped BOTH under -rect, so the product-drop call
                                // (`-rect -10 z0 10 z1 -mindist 1.5`, prod_createproduct_box*
                                // z_work_prod.tcl:842/:855) could pick the workplace's own cell —
                                // dc≈0 minimizes the 2·dc cost term — and the finished Zelt crate
                                // beamed down on top of the campfire.
                                float dcx = px - center.x, dcy = py - center.y, dcz = (z - center.z) * 0.5f;
                                float dc = Mathf.Sqrt(dcx * dcx + dcy * dcy + dcz * dcz);
                                if (dc < mindist) continue;
                                if (!hasRect && dc > circle + 0.0626f) continue;
                                var cand = new Vector3(px, py, z);
                                // Except the ASKING entity's own locks (a gnome's previously locked spot
                                // must not veto its own get_place) AND the explicit -placelockidexcept id
                                // — else every place-based sparetime occupation (sit/talk/eat/sleep
                                // spots) fails and wander wins every think.
                                if (ctx.PlaceLocks.Check(cand, 0.7f, selfExcept, lockExcept, now)) continue;
                                // -materials: a candidate on a lying item's cells is occupied — the
                                // native stamps the item BODY into the (x,z) scan grid (cell ≈ 0.31
                                // units, 64 cells / 20-unit rect — FUN_0041cac0 via 005a9700:226,
                                // reject:264), so the footprint is 1-2 grid cells, NOT a column:
                                // drops slot in front/behind an item at the same east-column. The
                                // old ±0.4/±0.9 box killed the whole column per item and smeared
                                // piles into a 1-D east line.
                                if (lyingItems != null)
                                {
                                    bool onItem = false;
                                    for (int li = 0; li < lyingItems.Count && !onItem; li++)
                                    {
                                        var ip = lyingItems[li];
                                        if (Mathf.Abs(px - ip.x) < 0.3f && Mathf.Abs(py - ip.y) < 0.3f
                                            && Mathf.Abs(z - ip.z) < 0.4f) onItem = true;
                                    }
                                    if (onItem) continue;
                                }
                                // BODY-BLOCK: the native CPlaceFinder rejects candidates occupied by an
                                // object body (that is what -except exists to exempt — FUN_00656560).
                                // Port veto = the act_when_idle avoid box {±0.6 ±0.3 ±1.2}
                                // (z_procs.tcl:1301) around every OTHER live gnome/baby: a wander/side-
                                // step target (walk_random → get_place, z_dignwalk.tcl:724-729) must
                                // never land where arrival instantly re-triggers the crowd check —
                                // without this the 5-gnome camp ping-pongs side-steps forever (each
                                // arrival re-seeds a neighbour's act_when_idle → walk_random → …).
                                if (BodyBlocked(bodyBlockers, cand)) continue;
                                if (checkFree) return "1";            // boolean form (:1135-1140)
                                float dix = px - ideal.x, diy = py - ideal.y, diz = (z - ideal.z) * 0.5f;
                                float cost = Mathf.Sqrt(dix * dix + diy * diy + diz * diz) + 2f * dc + jitter;
                                if (cost < bestCost) { bestCost = cost; best = cand; found = true; }
                            }
                        }
                }
                if (checkFree) return "0";
                // -placelock T > 0: lock the winner so the next finder avoids it for T seconds
                // (FindPlace 005a9700:327 → FUN_005aa900, duration-gated on DAT_00736f78 = 0).
                if (found && placelockDur > 0f)
                    ctx.PlaceLocks.Set(best, placelockDur, s_placeLockSeq--, now);
                return found ? FormatVec3(best) : "-1 0 0";
            });
            interp.RegisterCommand("prod_assignworker",    (i, a) => "1");
            interp.RegisterCommand("prod_valid",           (i, a) => "1");
            // set_prodautoschedule <obj> <bool> — the native auto-schedule flag (obj+0x3d0 bit
            // 0x800000, INVERTED: bit set = manual). FUN_00680e20 (its trace string is a
            // copy-pasted "SetSelectable" — the REAL set_selectable is FUN_00680a20 on bit 0x200,
            // matching obj_query's selectable mapping): arg true → CLEAR the bit (auto ON);
            // false OR missing/unparsable → SET the bit (auto OFF) (:73-87). Default ON — the
            // CObj ctor flag word 0x01100101 has the bit clear (FUN_005c83f0:224). Scripts:
            // grabstein.tcl/Lorenbahn.tcl/Trigger_Urwald.tcl `set_prodautoschedule X 0`.
            interp.RegisterCommand("set_prodautoschedule", (i, a) =>
            {
                if (a.Length >= 2 && ResolveEntityOrNull(ctx, a[1]) is { } e)
                    e.ProdAutoSchedule = a.Length >= 3 && ParseTclBool(a[2]);
                return "";
            });
            // get_prodautoschedule — FUN_00681050:57 `(flags & 0x800000) == 0`; consumers
            // zwerg.tcl:32 (idle walk_down_from_wall gate), std_ai.tcl (skips manual buildings).
            interp.RegisterCommand("get_prodautoschedule", (i, a) =>
                a.Length >= 2 && ResolveEntityOrNull(ctx, a[1]) is { } e && e.ProdAutoSchedule ? "1" : "0");

            // talkissue <gnome> <subcmd> … — the per-gnome weighted topic ledger (native store at
            // obj+0x3f4; TCL:CTclGlobalMethods::Talkissue, FUN_006aa650). Was a bulk "0" stub —
            // which pinned spt_talk_desire at 0 ([talkissue this sum] is its ONLY source,
            // z_spare_talk.tcl:42) and so suppressed the ENTIRE gnome-talk mechanic (initiation
            // needs desire>5.0, z_spare_fun.tcl:149; accept needs ≥3.0, z_spare_main.tcl:157).
            // Subcommands (argc per the native dispatcher; unknown → "invalid talkissue command",
            // ported tolerantly as "0"):
            // add <name> <v> — value = clamp(old+v, 0, 50) (DAT_007375cc=50.0); a NEGATIVE add
            // landing below 3.0 (DAT_00737428) DELETES the entry (FUN_005ff2c0:34-68);
            // set <name> <v> — clamp(v, 0, 50) stored (FUN_005ff160);
            // get <name> — value, 0.0 when missing;
            // sum — Σ values (the spt_talk_desire source);
            // list — the issue NAMES (FUN_005ff510);
            // reduce <f> — value ×= f, but ONLY for names whose first char > '9'
            // (FUN_005ff660:27-31 — numeric-prefixed story issues are immune);
            // delete <name> — remove the entry.
            // Native gates on obj+0xe8==1 ("object is not a gnome"); the port keeps its tolerance
            // convention (any entity gets a ledger — only gnome scripts call it).
            interp.RegisterCommand("talkissue", (i, a) =>
            {
                if (a.Length < 3 || ResolveEntityOrNull(ctx, a[1]) is not { } e) return "0";
                var led = e.TalkIssues ??= new System.Collections.Generic.Dictionary<string, double>();
                string F(double d) => d.ToString("0.0###", CultureInfo.InvariantCulture);
                switch (a[2])
                {
                    case "add":
                    {
                        if (a.Length < 5) return "0";
                        double v = ParseFloat(a[4]);
                        led.TryGetValue(a[3], out var old);
                        double nv = System.Math.Min(System.Math.Max(old + v, 0.0), 50.0);
                        if (v < 0.0 && nv < 3.0) led.Remove(a[3]);   // FUN_005ff2c0:51-67
                        else led[a[3]] = nv;
                        return "";
                    }
                    case "set":
                        if (a.Length < 5) return "0";
                        led[a[3]] = System.Math.Min(System.Math.Max((double)ParseFloat(a[4]), 0.0), 50.0);
                        return "";
                    case "get":
                        return a.Length >= 4 && led.TryGetValue(a[3], out var g) ? F(g) : "0.0";
                    case "sum":
                    {
                        double s = 0.0;
                        foreach (var kv in led) s += kv.Value;
                        return F(s);
                    }
                    case "list":
                        return string.Join(" ", led.Keys);
                    case "reduce":
                    {
                        if (a.Length < 4) return "0";
                        double f = ParseFloat(a[3]);
                        var keys = new System.Collections.Generic.List<string>(led.Keys);
                        foreach (var k in keys)
                            if (k.Length > 0 && k[0] > '9') led[k] *= f;   // FUN_005ff660:27
                        return "";
                    }
                    case "delete":
                        if (a.Length >= 4) led.Remove(a[3]);
                        return "";
                    default:
                        return "0";   // native: Tcl error "invalid talkissue command"
                }
            });

            // (The invented prod_make/prod_cook cook shortcuts were removed: the built-Feuerstelle
            // cook click now drives the real production chain via evt_autoprod_workat — see
            // ClickToMoveService cook region. prod_make/prod_cook never existed in the original;
            // the faithful output is prod_createproduct_rndrot inside Feuerstelle.prod_item_actions,
            // gated + terminated by the slot count.)

            // Getobjpos/Getobjref are NOT stubbed — the real sequencer.tcl procs run (DispatchCommand
            // checks C# commands BEFORE procs, so a stub here would silently shadow them). They do
            // obj_query this "-pos {parse_pos $who} -class $cla -owner $owner -range $rng -limit 1 -cloaked 1"
            // → get_pos, and obj_query now supports -pos/-class/-owner/-range/-limit (-cloaked benign).
            // The "0 0 0" stub anchored urw_6a's cave-painting montage (Getobjpos Info_Pos_Spinne) at
            // the world origin → the camera flew far upper-left. (Same for parse_pos earlier.)

            // Vector ops — useful elsewhere; defined locally on TclInterpreter.
            interp.RegisterCommand("vector_add", (i, a) => VectorOp(a, (x, y) => x + y));
            interp.RegisterCommand("vector_sub", (i, a) => VectorOp(a, (x, y) => x - y));
            interp.RegisterCommand("vector_mul", (i, a) => VectorScalar(a, (x, s) => x * s));
            // vector_dist = HORIZONTAL (ground-plane) distance — native X,Y are horizontal, Z is height.
            // Distinct from vector_dist3d (full 3D); the engine has BOTH for a reason. The dig loop's range
            // gate (dig_execute: `vector_dist $digpoint $thispos < 3.0`) must ignore the height difference,
            // else a dig point a few solidity-units "below" the dwarf reads as out of range and never digs.
            interp.RegisterCommand("vector_dist", (i, a) =>
            {
                if (a.Length < 3) return "0";
                var u = ParseVec3(a[1]); var v = ParseVec3(a[2]);
                float dx = u.x - v.x, dy = u.y - v.y;
                return Mathf.Sqrt(dx * dx + dy * dy).ToString("R", CultureInfo.InvariantCulture);
            });
            interp.RegisterCommand("vector_dist3d", (i, a) =>
            {
                if (a.Length < 3) return "0";
                var u = ParseVec3(a[1]); var v = ParseVec3(a[2]);
                var d = new Vector3(u.x - v.x, u.y - v.y, u.z - v.z);
                return Mathf.Sqrt(d.x * d.x + d.y * d.y + d.z * d.z).ToString("R", CultureInfo.InvariantCulture);
            });
            // get_anglexz <from> <to> — Y-rotation (radians, [0,2π)) to face `to` from `from`.
            // As the NAME says, the angle is taken in the native X-Z plane (entity Position vectors
            // use X-Z as the ground here; that's a different space from the voxel map where z is the
            // height nibble). Empirically this matches the original — using (x,y) regressed every
            // actor's facing. Convention matches set_roty (front=0, left=π/2, back=π, right=3π/2).
            interp.RegisterCommand("get_anglexz", (i, a) =>
            {
                if (a.Length < 3) return "0";
                var p1 = ParseVec3(a[1]); var p2 = ParseVec3(a[2]);
                // NOTE the negated X: our render has native+X on screen-RIGHT, but the original's
                // angle convention (used by the named dirs front/back/LEFT/right = 0/π/π·½/π·1½) puts
                // +X on screen-LEFT. Negating dx here (and matching the wx sign in
                // FaceRotationForNativeAngle) aligns our handedness with the original so `rotate left`
                // faces screen-left. This is INVARIANT for position-derived facing (the two negations
                // cancel — a gnome still faces the actual target), it only fixes the NAMED directions.
                double ang = Mathf.Atan2(p1.x - p2.x, p2.z - p1.z);
                if (ang < 0) ang += 2.0 * System.Math.PI;
                return ang.ToString("R", CultureInfo.InvariantCulture);
            });
            interp.RegisterCommand("vector_random", (i, a) =>
            {
                if (a.Length < 4) return "0 0 0";
                float rx = Random.Range(-ParseFloat(a[1]), ParseFloat(a[1]));
                float ry = Random.Range(-ParseFloat(a[2]), ParseFloat(a[2]));
                float rz = Random.Range(-ParseFloat(a[3]), ParseFloat(a[3]));
                return FormatVec3(new Vector3(rx, ry, rz));
            });

            // ----- dbg_*: cutscene diagnostics (write a dumpable file via DigglesDebugLog) -----
            // Used by Scripts/sequences/Test/dbg.tcl so a single log dump shows what's correct vs
            // wrong. dbg_log appends a labelled line; dbg_actor dumps an actor's native+world pos,
            // its world-forward, and — when a target native pos is given — the SIGNED ANGLE ERROR
            // between where it FACES and the direction TO the target (ANGERR≈0 ⇒ facing is right;
            // a non-zero ANGERR is exactly the native→world mapping error to fix). PlayCutscene
            // opens the file automatically; dbg_begin lets youopen it manually.
            interp.RegisterCommand("dbg_begin", (i, a) =>
            {
                string path = a.Length > 1 ? a[1] : "diggles_cutscene.log";
                Diggles.Runtime.Diagnostics.DigglesDebugLog.Begin(path);
                Debug.Log($"[dbg] logging to: {System.IO.Path.GetFullPath(path)}");
                return "";
            });
            interp.RegisterCommand("dbg_log", (i, a) =>
            {
                Diggles.Runtime.Diagnostics.DigglesDebugLog.Log(a.Length > 1 ? string.Join(" ", a, 1, a.Length - 1) : "");
                Diggles.Runtime.Diagnostics.DigglesDebugLog.Flush();
                return "";
            });
            interp.RegisterCommand("dbg_actor", (i, a) =>
            {
                var e = a.Length > 1 ? ResolveEntityOrNull(ctx, a[1]) : null;
                if (e == null || e.GameObject == null)
                {
                    Diggles.Runtime.Diagnostics.DigglesDebugLog.Log($"[dbg_actor] '{(a.Length > 1 ? a[1] : "?")}' not found/unspawned");
                    Diggles.Runtime.Diagnostics.DigglesDebugLog.Flush();
                    return "";
                }
                var t = e.GameObject.transform;
                // VISUAL facing, not transform.forward: Diggles meshes are Y-down, so transform.forward
                // points vertically and its XZ projection is ~0 (useless). The model's authored "nose"
                // is DigglesObject.SpawnFaceWorld; the current rotation delta from SpawnRotation turns it.
                var dobj = e.GameObject.GetComponent<Diggles.Runtime.DigglesObject>();
                Vector3 face;
                if (dobj != null && dobj.SpawnFaceWorld.sqrMagnitude > 1e-6f)
                    face = (t.rotation * Quaternion.Inverse(dobj.SpawnRotation)) * dobj.SpawnFaceWorld;
                else
                    face = t.forward;
                face.y = 0f; if (face.sqrMagnitude > 1e-6f) face.Normalize();
                string line = $"[dbg_actor] h={e.Handle} cls={e.Class?.Name} natpos=({FormatVec3(e.Position)}) " +
                              $"worldpos=({FormatVec3(t.position)}) roty={t.eulerAngles.y:F1} faceXZ=({FormatVec3(face)}) " +
                              $"rawfwdXZ=({t.forward.x:F2} {t.forward.z:F2})";
                if (a.Length >= 5)
                {
                    Vector3 tgtNat = new Vector3(ParseFloat(a[2]), ParseFloat(a[3]), ParseFloat(a[4]));
                    // Map the target's native coords to world through the SAME parent (WorldRoot) the
                    // actor lives under, so dir-to-target is in the actor's own world frame.
                    Vector3 tgtWorld = t.parent != null
                        ? t.parent.TransformPoint(Diggles.Runtime.RuntimeWorldSpawner.NativeToLocal(tgtNat))
                        : Diggles.Runtime.RuntimeWorldSpawner.NativeToLocal(tgtNat);
                    Vector3 dir = tgtWorld - t.position; dir.y = 0f;
                    float natAng = Mathf.Atan2(e.Position.x - tgtNat.x, tgtNat.z - e.Position.z) * Mathf.Rad2Deg;
                    if (dir.sqrMagnitude > 1e-6f && face.sqrMagnitude > 1e-6f)
                    {
                        dir.Normalize();
                        float err = Vector3.SignedAngle(face, dir, Vector3.up);
                        line += $" | tgtNat=({FormatVec3(tgtNat)}) tgtWorld=({FormatVec3(tgtWorld)}) " +
                                $"dirXZ=({FormatVec3(dir)}) get_anglexz={natAng:F1} ANGERR={err:F1}";
                    }
                }
                Diggles.Runtime.Diagnostics.DigglesDebugLog.Log(line);
                Diggles.Runtime.Diagnostics.DigglesDebugLog.Flush();
                return "";
            });

            // ----- misc -----

            // Scripts call `log` as their trace mechanism — hundreds of times per second
            // once the real AI/state machines run (TIMING EVENT, Gnome dies, …). Routing
            // every one to Unity's (slow) Debug.Log freezes the editor. Off by default;
            // flip VerboseScriptLog to surface them when debugging a specific script.
            interp.RegisterCommand("log", (i, a) =>
            {
                if (!VerboseScriptLog && !Diggles.Runtime.Diagnostics.DigglesDebugLog.Enabled) return "";
                var msg = a.Length > 1 ? string.Join(" ", a, 1, a.Length - 1) : "";
                // When the file logger is on, route the (high-volume) tcl trace THERE (buffered, fast) and
                // NOT to Debug.Log — flooding the Unity console at this volume freezes the editor, which
                // stalls the sim so the gnome appears not to move. Capture the SELECTED gnome (the one being
                // debugged) in addition to explicitly-tracked handles.
                if (Diggles.Runtime.Diagnostics.DigglesDebugLog.Enabled)
                {
                    int h = ctx.This?.Handle ?? 0;
                    if (h == 0 || h == ctx.SelectedHandle || Diggles.Runtime.Diagnostics.DigglesDebugLog.IsTracked(h))
                        Diggles.Runtime.Diagnostics.DigglesDebugLog.Log($"[tcl #{h}] {msg}");
                }
                else if (VerboseScriptLog) Debug.Log($"[tcl] {msg}");
                return "";
            });

            interp.RegisterCommand("random", (i, a) =>
            {
                // random — float in [0, 1)
                // random max — float in [0, max)
                // random min max — float in [min, max) (raupe.tcl:76 `random 2.0 4.0`)
                if (a.Length >= 3)
                {
                    float lo = ParseFloat(a[1]), hi = ParseFloat(a[2]);
                    return (lo + Random.value * (hi - lo)).ToString("R", CultureInfo.InvariantCulture);
                }
                float max = a.Length > 1 ? ParseFloat(a[1]) : 1f;
                return (Random.value * max).ToString("R", CultureInfo.InvariantCulture);
            });

            interp.RegisterCommand("irandom", (i, a) =>
            {
                // irandom max — integer in [0, max)
                // irandom min max — integer in [min, max) (ENGINE_TCL_API.txt:758-760)
                // The 2-arg form was silently collapsing to its LOWER bound (a[2] ignored) — e.g.
                // rohstoffe.tcl `stein_0[irandom 1 4]` always picked stein_00, the huge embedded
                // BROCKEN rock instead of the loose stein_01..03 reward (the "10× stones" bug).
                // Exclusive upper bound per the data: stein objects are _00.._03, no _04.
                if (a.Length < 2) throw new TclException("wrong # args: should be \"irandom ?min? max\"");
                if (a.Length >= 3)
                {
                    int lo = int.Parse(a[1], CultureInfo.InvariantCulture);
                    int hi = int.Parse(a[2], CultureInfo.InvariantCulture);
                    return (hi <= lo ? lo : Random.Range(lo, hi)).ToString(CultureInfo.InvariantCulture);
                }
                int max = int.Parse(a[1], CultureInfo.InvariantCulture);
                return Random.Range(0, max).ToString(CultureInfo.InvariantCulture);
            });

            // Diggles float-math helpers (were stubbed → "0", which silently broke things —
            // notably `fincr sum $freq` in random_fanim_sequence: the blink probability
            // accumulator stayed 0, so the blink never rolled; also the mood/attrib decay math).
            // fincr varName ?increment=1? — float counterpart of `incr`.
            interp.RegisterCommand("fincr", (i, a) =>
            {
                if (a.Length < 2) throw new TclException("wrong # args: should be \"fincr varName ?increment?\"");
                float v = ParseFloat(i.GetVar(a[1]) ?? "0") + (a.Length > 2 ? ParseFloat(a[2]) : 1f);
                var s = v.ToString("R", CultureInfo.InvariantCulture);
                i.SetVar(a[1], s);
                return s;
            });
            interp.RegisterCommand("hmin", (i, a) =>
                (a.Length > 2 ? Mathf.Min(ParseFloat(a[1]), ParseFloat(a[2])) : ParseFloat(a.Length > 1 ? a[1] : "0"))
                    .ToString("R", CultureInfo.InvariantCulture));
            interp.RegisterCommand("hmax", (i, a) =>
                (a.Length > 2 ? Mathf.Max(ParseFloat(a[1]), ParseFloat(a[2])) : ParseFloat(a.Length > 1 ? a[1] : "0"))
                    .ToString("R", CultureInfo.InvariantCulture));
            interp.RegisterCommand("hfloor", (i, a) => Mathf.Floor(ParseFloat(a.Length > 1 ? a[1] : "0")).ToString("R", CultureInfo.InvariantCulture));
            // hceil returns a Tcl DOUBLE — real Tcl renders 6.0 as "6.0", and the sparetime
            // planner switches on that EXACT string: z_spare_main.tcl:55-62
            // `switch [hceil [get_remaining_sparetime this]] { 0.0 {…} 1.0 {…} … }`. The old
            // "R" format emitted "6", so every 1-4 h plan fell to the 5-activity default and the
            // gnome over-planned its short sparetime.
            interp.RegisterCommand("hceil",  (i, a) => Mathf.Ceil(ParseFloat(a.Length > 1 ? a[1] : "0")).ToString("0.0###", CultureInfo.InvariantCulture));
            interp.RegisterCommand("hf2i",   (i, a) => ((long)ParseFloat(a.Length > 1 ? a[1] : "0")).ToString(CultureInfo.InvariantCulture));
            interp.RegisterCommand("hi2f",   (i, a) => ParseFloat(a.Length > 1 ? a[1] : "0").ToString("R", CultureInfo.InvariantCulture));

            // call SCRIPT_PATH — read file (relative to DataRoot) and eval.
            // callnc — same; Diggles' "no-cache" hint is meaningless to us.
            interp.RegisterCommand("call",      (i, a) => CmdCall(ctx, a));
            interp.RegisterCommand("callnc",    (i, a) => CmdCall(ctx, a));
            interp.RegisterCommand("source",    (i, a) => CmdCall(ctx, a));
            interp.RegisterCommand("load_file", (i, a) => CmdLoadFile(ctx, a));
            // Filesystem load mechanics (override the core glob stub + the cd stub),
            // resolving against DataRoot + ctx.Cwd. classinit.tcl drives the real
            // class load with `cd <dir>; lsort [glob *.tcl]; cd..`.
            interp.RegisterCommand("glob",      (i, a) => CmdGlob(ctx, a));
            interp.RegisterCommand("cd",        (i, a) => CmdCd(ctx, a));
            interp.RegisterCommand("pwd",       (i, a) => CmdPwd(ctx, a));
            interp.RegisterCommand("file",      (i, a) => CmdFile(ctx, a));
            // Cross-object variable access (the ONLY sanctioned channel for reaching
            // another object's per-instance vars): ref_get/ref_set <objref> <member>.
            interp.RegisterCommand("ref_get",   (i, a) => CmdRefGet(ctx, a));
            interp.RegisterCommand("ref_set",   (i, a) => CmdRefSet(ctx, a));
            interp.RegisterCommand("db_load",   (i, a) => "");
            interp.RegisterCommand("minimalrun",(i, a) => "0");
            // has_cmap <obj.anim> [frame] — TRUE iff the anim frame's MESH carries a collision
            // map (native FUN_00659280: anim → frame mesh → mesh+0x00 cmap index != -1; port:
            // ThreeDbMesh.Field0 != 0xFFFF). THE collision default: animinit.tcl keys every
            // dummy class's class_collision on it (:249-251,:327 `if {[has_cmap
            // $anim.standard]} {class_collision 1}`) — as a "0" stub NO mushroom/tree was ever
            // collidable, so placement overlap and walk body-blocking ignored them (the
            // walk-through-mushrooms case). Headless (no model library) keeps the old "0" —
            // collision then falls to the kind heuristics.
            interp.RegisterCommand("has_cmap",  (i, a) =>
            {
                if (a.Length < 2 || ctx.ModelLibrary == null) return "0";
                int dot = a[1].IndexOf('.');
                if (dot <= 0) return "0";
                int frame = a.Length >= 3 ? IntArg(a, 2, 0) : 0;
                return ctx.ModelLibrary.HasCMap(a[1].Substring(0, dot), a[1].Substring(dot + 1), frame)
                    ? "1" : "0";
            });
            // Real game clock (was a "0" stub). Scripts use gettime for `-attime` math
            // (e.g. disable_auto_fanim → evt_fanim_enable re-enable). Must match the timer
            // base, which is ctx.Sim.GameTime — same source as `gametime`.
            interp.RegisterCommand("gettime",   (i, a) =>
                (ctx.Sim?.GameTime ?? 0.0).ToString("R", CultureInfo.InvariantCulture));
            interp.RegisterCommand("regexp",    (i, a) => "0");
            interp.RegisterCommand("regsub",    (i, a) => "0");
            // NOTE: open / close / gets / eof are intentionally NOT registered natively.
            // `misc/file_wrapper.tcl` implements them in pure Tcl (over load_file + split +
            // a seek index) and the sequencer relies on that. A native command would shadow
            // the Tcl proc (dispatch checks native before procs), so the sequencer could never
            // read its script. See Docs/CUTSCENE_SYSTEM.md §1.
            interp.RegisterCommand("seek",      (i, a) => "");
            interp.RegisterCommand("smalltalk", (i, a) => "");

            // ----- map / world construction (Phase 5) -----

            // `map create W H` is a subcommand-style call; the engine has
            // other `map` subcommands (size, ground,...) which we leave
            // as no-ops for now.
            interp.RegisterCommand("map", (i, a) => CmdMap(ctx, a));
            interp.RegisterCommand("sm_create_map", (i, a) => CmdSmCreateMap(ctx, a));
            interp.RegisterCommand("map_template", (i, a) => CmdMapTemplate(ctx, a));
            interp.RegisterCommand("map_setlayer2", (i, a) => CmdMapSetLayer2(ctx, a));
            interp.RegisterCommand("split_template", (i, a) => CmdSplitTemplate(ctx, a));

            // ----- cinematic camera (sequencer) -----
            // The sequencer's sq_camera proc resolves positions to WORLD coords (parse_pos →
            // get_pos), then calls these native leaves. CutsceneCameraState holds the view +
            // interpolates on Sim.Step; a Play component poses the real Camera from it.
            ctx.Camera = new CutsceneCameraState();
            ctx.Camera.SetPositionResolver(h =>
                ctx.Entities.TryGetValue(h, out var e) ? e.Position : (Vector3?)null);

            // Native CTclGlobalMethods::SetView reads x/y/zoom and writes camera target fields
            // +0x134/+0x138/+0x13c (FUN_006284e0:52, 75-173), then reads dx/dy and the optional
            // move/freeze/speed/set arguments in that order (FUN_006284e0:175-369).
            interp.RegisterCommand("set_view", (i, a) =>
            {
                var cam = ctx.Camera!;
                float x = a.Length > 1 ? ParseFloat(a[1]) : cam.Focus.x;
                float y = a.Length > 2 ? ParseFloat(a[2]) : cam.Focus.y;
                float zoom = a.Length > 3 ? ParseFloat(a[3]) : cam.Zoom;
                float dx = a.Length > 4 ? ParseFloat(a[4]) : 0f;
                float dy = a.Length > 5 ? ParseFloat(a[5]) : 0f;
                bool move = a.Length > 6 && ParseTclBool(a[6]);
                bool freeze = a.Length > 7 && ParseTclBool(a[7]);
                float speed = a.Length > 8 ? ParseFloat(a[8]) : 1f;
                bool camset = a.Length > 9 && ParseTclBool(a[9]);
                // Native SetView has only the x/y/zoom target fields above; its movement setup copies
                // those 2-D targets into the camera move state (FUN_006284e0:380-393). Focus.z is a
                // port-only set_viewpos dimension, so explicit set_view returns it to the gameplay plane.
                float fz = a.Length > 1 ? 0f : cam.Focus.z;
                cam.SetView(new Vector3(x, y, fz), zoom, dx, dy, move, freeze, speed, camset);
                return "";
            });
            // set_viewpos x y z zoom dx dy [move freeze speed set] — absolute 3D focus point.
            interp.RegisterCommand("set_viewpos", (i, a) =>
            {
                var cam = ctx.Camera!;
                float x = a.Length > 1 ? ParseFloat(a[1]) : cam.Focus.x;
                float y = a.Length > 2 ? ParseFloat(a[2]) : cam.Focus.y;
                float z = a.Length > 3 ? ParseFloat(a[3]) : cam.Focus.z;
                float zoom = a.Length > 4 ? ParseFloat(a[4]) : cam.Zoom;
                float dx = a.Length > 5 ? ParseFloat(a[5]) : 0f;
                float dy = a.Length > 6 ? ParseFloat(a[6]) : 0f;
                bool move = a.Length > 7 && ParseTclBool(a[7]);
                bool freeze = a.Length > 8 && ParseTclBool(a[8]);
                float speed = a.Length > 9 ? ParseFloat(a[9]) : 1f;
                bool camset = a.Length > 10 && ParseTclBool(a[10]);
                cam.SetView(new Vector3(x, y, z), zoom, dx, dy, move, freeze, speed, camset);
                return "";
            });
            // get_view [pred] → "x y zoom dx dy move" (consumed by set_view on restore).
            interp.RegisterCommand("get_view", (i, a) =>
            {
                var c = ctx.Camera!;
                bool predicted = a.Length > 1 && ParseTclBool(a[1]);
                return c.ViewString(predicted);
            });
            // set_camerafollow <objref|-1> [zoom] — track an object, or stop following (-1).
            interp.RegisterCommand("set_camerafollow", (i, a) =>
            {
                var cam = ctx.Camera!;
                if (a.Length < 2) { cam.StopFollow(); return ""; }
                if (int.TryParse(a[1], out int h) && h >= 0)
                    cam.Follow(h, a.Length > 2 ? ParseFloat(a[2]) : 1f);
                else cam.StopFollow();
                return "";
            });
            interp.RegisterCommand("get_camerafollowdist", (i, a) => Ftos(ctx.Camera!.FollowDist()));
            interp.RegisterCommand("get_cameramoving",     (i, a) => ctx.Camera!.Moving ? "1" : "0");
            interp.RegisterCommand("viewlock", (i, a) =>
            { ctx.Camera!.ViewLocked = a.Length > 1 && ParseTclBool(a[1]); return ""; });
            // camera_set <startspeed> <waypoints> — install the easing profile for the next move.
            // waypoints = a Tcl list of {s a b} (s=segment token, a=progress fraction, b=velocity).
            interp.RegisterCommand("camera_set", (i, a) =>
            {
                if (a.Length < 3) return "";
                float startSpeed = ParseFloat(a[1]);
                var wpStrs = TclList.Parse(a[2]);
                var pts = new System.Collections.Generic.List<(float, float)>(wpStrs.Count);
                foreach (var w in wpStrs)
                {
                    var f = TclList.Parse(w);          // {s a b}
                    if (f.Count >= 3) pts.Add((ParseFloat(f[1]), ParseFloat(f[2])));
                }
                if (pts.Count > 0) ctx.Camera!.SetCamset(startSpeed, pts.ToArray());
                return "";
            });

            // ----- dialogue / subtitles (sequencer) -----
            ctx.Dialogue = new DialogueState();
            // set_speechout <maxtime> <resstr> [force] — resstr = flat list {col text col text …}
            // (4 actor slots); col = "r g b". Shows the subtitle bar for maxtime seconds.
            interp.RegisterCommand("set_speechout", (i, a) =>
            {
                var d = ctx.Dialogue!;
                float maxTime = a.Length > 1 ? ParseFloat(a[1]) : 0f;
                var slots = new System.Collections.Generic.List<(Color32, string)>(4);
                if (a.Length > 2)
                {
                    var items = TclList.Parse(a[2]);
                    for (int k = 0; k + 1 < items.Count && slots.Count < 4; k += 2)
                        slots.Add((ParseColor(items[k]), items[k + 1]));
                }
                bool force = a.Length > 3 && ParseTclBool(a[3]);
                d.SetSpeechOut(maxTime, slots, force);
                ctx.Audio?.BindVoiceDialogue(d, maxTime);

                // Mouth lip-sync: the native engine drove the talking mouth from the voice playback
                // (text → per-frame visemes on submesh 3, for the line duration). No Tcl drives it, so
                // we reproduce it here. set_speechout is called from inside do_text, so the speaking
                // actor (`who`), the actor handle table (`actors`) and the `offtext` flag are still in
                // the current scope. offtext=="Off" = OFF-SCREEN narrator (Märchenerzähler) — the line
                // is NOT spoken by the on-screen actor, so do NOT animate that actor's mouth.
                // Skip drain: a drained do_text never STARTS its voice sample (and set_sequence 0
                // closes the stream, FUN_00649080:100-104), so the native talk tick never installs a
                // mouth anim for it (FUN_004d34d0 keys the viseme install to the playing sample) —
                // don't leave a post-skip mouth flapping over the restored gameplay.
                if (maxTime > 0f && ctx.SequenceActive && !ctx.SequencePresentationDrain
                    && i.GetVar("offtext") != "Off")
                {
                    string spoken = "";
                    foreach (var s in slots) if (!string.IsNullOrWhiteSpace(s.Item2)) spoken += (spoken.Length > 0 ? " " : "") + s.Item2;
                    var whoStr = i.GetVar("who");
                    var actorsStr = i.GetVar("actors");
                    if (spoken.Length > 0 && !string.IsNullOrEmpty(whoStr) && !string.IsNullOrEmpty(actorsStr)
                        && int.TryParse(whoStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int who))
                    {
                        var actorList = TclList.Parse(actorsStr!);
                        if (who >= 0 && who < actorList.Count)
                        {
                            var speaker = ResolveEntityOrNull(ctx, actorList[who]);
                            // revertToBaseOnComplete: end of the line = mouth back to the BASE
                            // face texture. Native chain: the finished type-1 flipbook freezes
                            // (CObj_Tick FUN_005cc600 cursor wrap → type 0) and the face-mood
                            // layer immediately repaints the resting face (z_faceanim.tcl
                            // set_fanim; cutscene actors get it via free_face_anim,
                            // sequencer.tcl:543); an absent/cleared channel renders variant 0 =
                            // base (FUN_005f5ef0 cursor-past-count branch). The port's frozen
                            // cutscene actors have no auto-mood repaint, so the talk channel
                            // closes itself — otherwise the mouth froze open on its first
                            // viseme after every line.
                            if (speaker != null)
                                ApplyFaceTextureAnim(ctx, speaker, 3,
                                    BuildVisemeFrames(spoken, maxTime), 0, 1,
                                    revertToBaseOnComplete: true);
                        }
                    }
                }
                return "";
            });
            // speechicon <obj> add <text> <time> … | clear | lock <a> <b>
            interp.RegisterCommand("speechicon", (i, a) =>
            {
                var d = ctx.Dialogue!;
                if (a.Length < 3) return "";
                if (!int.TryParse(a[1], out int h)) return "";
                switch (a[2])
                {
                    case "add":
                        d.SpeechIconAdd(h, a.Length > 3 ? a[3] : "", a.Length > 4 ? ParseFloat(a[4]) : 2f);
                        break;
                    case "clear": d.SpeechIconClear(h); break;
                    case "lock":  d.SpeechIconLock(h, true); break;
                }
                return "";
            });
            // calc_text_time <txt> — reading-time estimate (seconds) from text length.
            interp.RegisterCommand("calc_text_time", (i, a) =>
                Ftos(a.Length > 1 ? Mathf.Max(0.5f, a[1].Length * CutsceneTextSecondsPerChar) : 0.5f));
            // Text locale for the original data — drives the `..._[locale]` text-file names the
            // story/sequencer scripts open. From diggles_config.json (default "en"). Must match the
            // data set: the Russian release ships only "_ru" text files (no "_en") — with "en" every
            // dialogue/subtitle lookup would open a non-existent file and come back empty.
            interp.RegisterCommand("locale", (i, a) => Diggles.Runtime.DigglesText.Locale);
            // option get|set <name> [value] — game options (TextSpeed, …).
            interp.RegisterCommand("option", (i, a) =>
            {
                if (a.Length >= 3 && a[1] == "get")
                    return ctx.Options.TryGetValue(a[2], out var v) ? v : (a[2] == "TextSpeed" ? "5" : "");
                if (a.Length >= 4 && a[1] == "set") { ctx.Options[a[2]] = a[3]; return a[3]; }
                return "";
            });

            // ----- audio (sound / adaptive_sound / seq_audiostream) -----
            ctx.Audio = new AudioState();
            // sound bind|anim|animnoseq|play|playglobal|volume|animsound …
            // Native dispatcher: CTclGlobalMethods::Sound FUN_00643a60 (verbs matched with
            // stricmp). Data-used verbs (grep over Scripts/**): anim ×2171, bind ×1015,
            // animnoseq ×735, play ×147, playglobal ×3, animsound ×2.
            interp.RegisterCommand("sound", (i, a) =>
            {
                var au = ctx.Audio!;
                if (a.Length < 2) return "";
                string verb = a[1].ToLowerInvariant();
                switch (verb)
                {
                    case "bind":
                        // sound bind <name> <file> — native fopen-checks the file and errors
                        // "can't find sample file." (FUN_00643a60:250-272; z_soundinit wraps
                        // optional binds in catch{} relying on exactly this). Validation is
                        // skipped for tolerant test hosts (no DataRoot / TolerateMissingFiles).
                        if (a.Length > 3)
                        {
                            if (!ctx.TolerateMissingFiles && !string.IsNullOrEmpty(ctx.DataRoot)
                                && ResolveDataFile(ctx, a[3]) == null)
                                throw new TclException("can't find sample file.");
                            au.Bind(a[2], a[3]);
                        }
                        return "";
                    case "anim": case "animnoseq":
                    {
                        // sound anim|animnoseq <db.anim> <frame> <snd> [mat] [room] [vol] [freq]
                        // [volvar] [freqvar] (FUN_00643a60:284-486). The material (any/sand/soil/
                        // stone/wood/metal/water/lava) and room (any/tunnel/small/medium/big) args
                        // are validated then DISCARDED — FUN_004cf770 receives neither (dead args
                        // in the shipped exe). The native also errors "can't find anim." against
                        // the loaded anim DBs; the port loads anim DBs lazily, so that check is
                        // deliberately skipped (registrations for unknown anims simply never fire).
                        if (a.Length < 5) return "";
                        string snd = a[4];
                        // z_soundinit's snd_animlist over a fully-commented brace block (z_:2954)
                        // degenerates to empty/garbage tokens (Tcl `//` lines are DATA inside
                        // braces) — an empty sample name registers nothing, silently.
                        if (snd.Length == 0) return "";
                        if (!ctx.TolerateMissingFiles && au.ResolveSound(snd) == null)
                            throw new TclException("unknown sound");   // FUN_00643a60:351-358
                        if (a.Length > 5 && !IsSoundMaterial(a[5])) throw new TclException("invalid material");
                        if (a.Length > 6 && !IsSoundRoom(a[6])) throw new TclException("invalid room");
                        au.RegisterAnimSound(a[2], (int)ParseFloat(a[3]), snd,
                            a.Length > 7 ? ParseFloat(a[7]) : 1f,        // vol (default DAT_00737138=1.0)
                            a.Length > 8 ? ParseFloat(a[8]) : 1f,        // freq (default 1.0)
                            a.Length > 9 ? ParseFloat(a[9]) : 0f,        // volvar
                            a.Length > 10 ? ParseFloat(a[10]) : 0f,      // freqvar
                            verb == "animnoseq");
                        return "";
                    }
                    case "play":
                    {
                        // sound play <name> <vol> [{x y z}] — one-shot at a native ground position
                        // (default = the camera focus, FUN_00643a60:190-193, i.e. effectively
                        // unattenuated). Unknown names are NOT rejected here (native errors
                        // "unknown sound"): cutscene hosts play file-named samples before
                        // soundinit ran, and the presenter resolves bind-or-filename.
                        string snd = a.Length > 2 ? a[2] : "";
                        float vol = a.Length > 3 ? ParseFloat(a[3]) : 1f;
                        bool pos = a.Length > 4;
                        au.PlayOneShot(snd, vol, pos, pos ? ParseVec3(a[4]) : Vector3.zero);
                        return "";
                    }
                    case "playglobal":
                        // non-positional, full volume everywhere (FUN_004d1bb0 — no falloff/pan)
                        au.PlayOneShot(a.Length > 2 ? a[2] : "", a.Length > 3 ? ParseFloat(a[3]) : 1f, false, Vector3.zero);
                        return "";
                    case "volume":
                        if (a.Length > 2) au.MasterVolume = ParseFloat(a[2]);
                        return Ftos(au.MasterVolume);
                    case "animsound":
                        // global anim-SFX toggle (byte at global+0x95d8, FUN_00643a60:507-517;
                        // always returns the current flag)
                        if (a.Length > 2) au.AnimSoundEnabled = ParseTclBool(a[2]);
                        return au.AnimSoundEnabled ? "1" : "0";
                }
                return "";
            });
            // adaptive_sound <subcmd> … — streaming theme music. Native dispatcher
            // FUN_00642d70 (verbs: basevol volfact start stop markerenable markerdisable
            // addtheme setfightstream setintrostream addstandardstream changemode changetheme
            // changethemenow primary marker markerobj delmarker delmarkerobj markerfind fight
            // autofight autofightpos normal).
            interp.RegisterCommand("adaptive_sound", (i, a) =>
            {
                var au = ctx.Audio!;
                if (a.Length < 2) return "";
                switch (a[1].ToLowerInvariant())
                {
                    case "setintrostream":   if (a.Length > 3) au.SetIntroStream(a[2], a[3]); break;
                    case "setfightstream":   if (a.Length > 3) au.SetFightStream(a[2], a[3]); break;
                    case "addstandardstream":if (a.Length > 3) au.AddStandardStream(a[2], a[3]); break;
                    // marker <theme> <pos> [radius] (FUN_00642d70:283-299; radius default 50.0
                    // per the markerobj stamp at:521)
                    case "marker":
                        if (a.Length > 3) au.AddMarker(a[2], ParseVec3(a[3]), a.Length > 4 ? ParseFloat(a[4]) : 50f);
                        break;
                    // Object markers retain the stable native object registry ID. Their position is
                    // updated by the adaptive-sound manager during normal runtime processing.
                    case "markerobj":
                        if (a.Length > 3 && ResolveEntityOrNull(ctx, a[3]) is { } markerObject)
                            au.AddObjectMarker(a[2], markerObject.Handle);
                        break;
                    case "delmarker": if (a.Length > 2) au.DelMarker(ParseVec3(a[2])); break;   // FUN_00642d70:303-323
                    case "delmarkerobj":
                        if (a.Length > 2 && ResolveEntityOrNull(ctx, a[2]) is { } deletedMarkerObject)
                            au.DelObjectMarkers(deletedMarkerObject.Handle);
                        break;
                    case "changetheme": case "changethemenow": if (a.Length > 2) au.ChangeTheme(a[2]); break;
                    // primary <theme> — the no-marker fallback THEME (FUN_00642d70:272-281), not a mode.
                    case "primary":
                        if (a.Length > 2)
                        {
                            au.PrimaryTheme = a[2];
                            if (au.CurrentTheme == null) au.ChangeTheme(a[2]);
                        }
                        break;
                    // autofight <theme> — combat switch target (FUN_00642d70:419-426)
                    case "autofight": if (a.Length > 2) au.AutoFightTheme = a[2]; break;
                    case "fight": au.Mode = "fight"; break;
                    case "normal": au.Mode = "normal"; break;
                    case "changemode": if (a.Length > 2) au.Mode = a[2]; break;
                    case "start": au.MusicPlaying = true; break;
                    case "stop":  au.MusicPlaying = false; break;
                    case "markerenable":  au.MarkersEnabled = true; break;
                    case "markerdisable": au.MarkersEnabled = false; break;
                    // volfact/basevol take an INT PERCENT 0..100 (FUN_00642d70:116-152 reads the
                    // arg with the int getter FUN_006baee0; scripts pass `volfact 0|20|…|100`).
                    case "basevol": case "volfact":
                        if (a.Length > 2) au.MusicBaseVolume = Mathf.Clamp01(ParseFloat(a[2]) / 100f);
                        return Ftos(au.MusicBaseVolume * 100f);
                }
                return "";
            });
            // seq_audiostream open <file> | play [start stop] | stop | close — cutscene voice.
            interp.RegisterCommand("seq_audiostream", (i, a) =>
            {
                var au = ctx.Audio!;
                if (a.Length < 2) return "";
                switch (a[1])
                {
                    case "open":  return (a.Length > 2 && au.OpenVoice(a[2])) ? "1" : "0";
                    case "play":
                        au.PlayVoice(a.Length > 2 ? ParseFloat(a[2]) : 0f, a.Length > 3 ? ParseFloat(a[3]) : 0f);
                        return "";
                    case "stop":  au.StopVoice();  return "";
                    case "close": au.CloseVoice(); return "";
                }
                return "";
            });

            // ----- particles / FX (sequencer + gameplay) -----
            ctx.Particles = new ParticleSystemState();
            // Resolve a particle-command object arg: a numeric handle, the literal "this"/$this (current
            // object), or an object name. The Tcl scripts pass the bare keyword `this` (NOT $this) to
            // change_/set_/blow_/free_particlesource — so plain int.TryParse alone yields -1, collapsing
            // every object's source onto key (-1, slot) where they overwrite each other (the elf's setId-33
            // source got clobbered). Route through the real entity resolver instead.
            int Handle(string s)
            {
                if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)) return h;
                var ent = ResolveEntityOrNull(ctx, s);
                return ent != null ? ent.Handle : -1;
            }
            int IntArg(string[] a, int idx, int def) =>
                idx < a.Length && int.TryParse(a[idx], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : def;
            // change_particlesource <obj> <srcidx> <type> <relpos> <dir> <max> <rate> <visfog>
            // [link] [maxrng] [objverts] [submesh] — allocate/configure a persistent source.
            interp.RegisterCommand("change_particlesource", (i, a) =>
            {
                if (a.Length < 8) return "";
                int objH = Handle(a[1]);
                int setId = IntArg(a, 3, 0);
                if (setId == 33)
                    UnityEngine.Debug.Log($"[Particles] change_particlesource setId=33 (elf magic) obj='{a[1]}'→{objH} slot={IntArg(a, 2, 0)} relpos={a[4]}");
                // a[8] = IS_VIS_IN_FOG (native emitter byte+0x64), NOT the active flag —
                // activation is set_particlesource's job. A 0-flagged emitter skips SPAWNING
                // while its tiles are under fog (< 0x81 on the local lane — the per-type tick
                // prologue FUN_004a1c50:48-77 and its twins); 1 = emits regardless. Stored on
                // the source; ParticlePresenter applies the gate at emission time.
                ctx.Particles!.Configure(objH, IntArg(a, 2, 0), setId,
                    ParseVec3(a[4]), ParseVec3(a[5]), IntArg(a, 6, 0), IntArg(a, 7, 1),
                    ParseTclBool(a[8]), IntArg(a, 9, -1), IntArg(a, 12, -1));
                return "";
            });
            // set_particlesource <obj> <srcidx> <activity>
            interp.RegisterCommand("set_particlesource", (i, a) =>
            {
                if (a.Length >= 4) ctx.Particles!.SetActive(Handle(a[1]), IntArg(a, 2, 0), ParseTclBool(a[3]));
                return "";
            });
            // blow_particlesource <obj> <srcidx> <dir> — one-shot burst.
            interp.RegisterCommand("blow_particlesource", (i, a) =>
            {
                if (a.Length >= 4) ctx.Particles!.Blow(Handle(a[1]), IntArg(a, 2, 0), ParseVec3(a[3]));
                return "";
            });
            interp.RegisterCommand("free_particlesource", (i, a) =>
            {
                if (a.Length >= 3) ctx.Particles!.Free(Handle(a[1]), IntArg(a, 2, 0));
                return "";
            });
            // create_particlesource <type> <pos> <dir> <num> <time> — temporary world source.
            interp.RegisterCommand("create_particlesource", (i, a) =>
            {
                if (a.Length >= 6)
                    ctx.Particles!.CreateTemp(IntArg(a, 1, 0), ParseVec3(a[2]), ParseVec3(a[3]),
                        IntArg(a, 4, 1), ParseFloat(a[5]));
                return "";
            });
            // obj_to_particle <obj> <type> — emit from the object's mesh (record as a temp burst).
            interp.RegisterCommand("obj_to_particle", (i, a) =>
            {
                if (a.Length >= 3)
                {
                    var e = ResolveEntityOrNull(ctx, a[1]);
                    ctx.Particles!.CreateTemp(IntArg(a, 2, 0), e?.Position ?? Vector3.zero, Vector3.zero, 0, 2f);
                }
                return "";
            });
            interp.RegisterCommand("particle_processing", (i, a) =>
            { ctx.Particles!.ProcessingEnabled = !(a.Length > 1) || ParseTclBool(a[1]); return ""; });

            // ----- screen FX (fade / shake / lightning / laserbeam / light) -----
            ctx.ScreenFx = new ScreenFxState();
            // screenvibe att sus dec amx frx amy fry — camera shake envelope (sq_screenvibe
            // resolves named presets in Tcl, then calls this).
            interp.RegisterCommand("screenvibe", (i, a) =>
            {
                if (a.Length >= 8)
                    ctx.ScreenFx!.StartShake(ParseFloat(a[1]), ParseFloat(a[2]), ParseFloat(a[3]),
                        ParseFloat(a[4]), ParseFloat(a[5]), ParseFloat(a[6]), ParseFloat(a[7]));
                return "";
            });
            // start_fade <dur> <0=to black | 1=from black>
            interp.RegisterCommand("start_fade", (i, a) =>
            {
                if (a.Length >= 2) ctx.ScreenFx!.StartFade(ParseFloat(a[1]), a.Length > 2 && ParseTclBool(a[2]));
                return "";
            });
            interp.RegisterCommand("cancel_fade", (i, a) => { ctx.ScreenFx!.CancelFade(); return ""; });
            interp.RegisterCommand("lightning", (i, a) =>
            { if (a.Length > 1) ctx.ScreenFx!.Lightning(ParseVec3(a[1])); return ""; });
            // laserbeam <from> <to> <color> [intensity …] — render a beam.
            interp.RegisterCommand("laserbeam", (i, a) =>
            {
                if (a.Length >= 4)
                    ctx.ScreenFx!.AddBeam(ParseVec3(a[1]), ParseVec3(a[2]), ParseColor(a[3]),
                        a.Length > 7 ? ParseFloat(a[7]) : 1f);
                return "";
            });
            interp.RegisterCommand("delete_laserbeams", (i, a) => { ctx.ScreenFx!.Beams.Clear(); return ""; });
            // change_light <id> <intensity> <r> <g> <b> [range]
            // Two native signatures share the name: the sequencer's SCREEN light
            // (`change_light INDEX intensity r g b`) and the gameplay OBJECT light
            // (`change_light OBJ {ox oy oz} range {r g b}` — FUN_00673F40 writes CObj+0x280
            // offset, +0x290 range, +0x298..+0x2A0 color; serialized as N22/N23). Route by
            // whether arg 1 resolves to an entity.
            interp.RegisterCommand("change_light", (i, a) =>
            {
                if (a.Length >= 3 && ResolveEntityOrNull(ctx, a[1]) is { } lightEntity)
                {
                    lightEntity.LightOffset = ParseVec3(a[2]);
                    if (a.Length >= 4) lightEntity.LightRange = ParseFloat(a[3]);
                    if (a.Length >= 5)
                    {
                        var color = ParseVec3(a[4]);
                        lightEntity.LightColor = color;
                    }
                    return "";
                }
                if (a.Length >= 5)
                    ctx.ScreenFx!.SetLight(IntArg(a, 1, 0), ParseFloat(a[2]),
                        new Color32((byte)IntArg(a, 3, 255), (byte)IntArg(a, 4, 255), (byte)IntArg(a, 5, 255), 255));
                return "";
            });

            // ----- actor / anim glue (do_action, sq_actor, do_elf timing) -----
            // db_animlength <obj.anim> — clip frame count (timing: offset = len/10). Real via the
            // model library; 0 when no library is wired (headless degrades to no delay).
            interp.RegisterCommand("db_animlength", (i, a) =>
            {
                if (a.Length < 2 || ctx.ModelLibrary == null) return "0";
                int dot = a[1].IndexOf('.');
                if (dot <= 0) return "0";
                return ctx.ModelLibrary.GetClipFrameCount(a[1].Substring(0, dot), a[1].Substring(dot + 1))
                    .ToString(CultureInfo.InvariantCulture);
            });
            // set_classaniminfo <class> <anim> <info…> / get_classaniminfo <class> <anim> — the
            // per-clip face payload (submesh + viseme framelist) the sequencer applies via
            // start_fanim during a talk anim. Empty when none registered (do_action skips face).
            interp.RegisterCommand("set_classaniminfo", (i, a) =>
            {
                if (a.Length < 3) return "";
                ctx.ClassAnimInfo[a[1] + "\n" + a[2]] = string.Join(" ", a, 3, a.Length - 3);
                return "";
            });
            interp.RegisterCommand("get_classaniminfo", (i, a) =>
                a.Length >= 3 && ctx.ClassAnimInfo.TryGetValue(a[1] + "\n" + a[2], out var v) ? v : "");

            // ----- the Elf guide (do_elf / `elf <verb>`) -----
            // Faithful CElf command surface (dispatcher FUN_00649640) over the rewritten ElfState
            // core (modes 0-5 + FlyToward + anim registry; see elf-re-spec.md). Headless-safe obj
            // hooks wired here so lookat/finishcode work without a presenter.
            ctx.Elf = new ElfState();
            ctx.Elf.SetEvalCallback(code =>
            {
                try { interp.Eval(code); }
                catch (System.Exception e) { Debug.LogWarning($"elf finishcode failed: {e.Message}"); }
            });
            ctx.Elf.ResolveObjPos = id => ctx.Entities.TryGetValue(id, out var oe) ? oe.Position : (Vector3?)null;
            ctx.Elf.IsObjAlive = id => ctx.Entities.ContainsKey(id);
            interp.RegisterCommand("elf", (i, a) =>
            {
                var elf = ctx.Elf!;
                if (a.Length < 2) return "";
                switch (a[1])
                {
                    case "move":
                        if (a.Length > 2) elf.CmdMove(ParseVec3(a[2])); break;
                    case "crash":      // mode 4: spawn 15 above, fall, abstuerzen_fall→aufprall
                        if (a.Length > 2) elf.CmdCrash(ParseVec3(a[2])); break;
                    case "movescreen":
                        // With a vector: mode 1, screen-anchored (z<0 ⇒ x,y are 800×600-reference,
                        // negatives are edge-relative — handled in ElfState). No arg: fly to the
                        // current view focus at native z=16 (mode 2).
                        if (a.Length > 2) elf.CmdMoveScreen(ParseVec3(a[2]));
                        else elf.CmdMoveScreenNoArg();
                        break;
                    case "path":       // elf path {p1} {p2} <speed> — mode 5, quantized velocity (FUN_004288c0)
                        if (a.Length > 4) elf.CmdPath(ParseVec3(a[2]), ParseVec3(a[3]), ParseFloat(a[4]));
                        break;
                    case "beam": case "set_pos":   // no native "beam" — do_elf maps it to set_pos
                        if (a.Length > 2) elf.CmdSetPos(ParseVec3(a[2])); break;
                    case "get_pos":
                        return FormatVec3(elf.CmdGetPos());
                    case "anim": case "action":    // unknown names clamp to standard (FUN_00649640)
                        if (a.Length > 2) elf.CmdAction(a[2]); break;
                    case "standard": elf.CmdAction("standard"); break;
                    case "sleep":    elf.CmdAction("sleep");    break;
                    case "idle":     elf.CmdAction("idle");     break;
                    case "stop":     elf.CmdStop();             break;
                    case "hide":     elf.CmdHide();             break;   // mode 3: fly out, vanish OFF-SCREEN
                    case "lookat":   elf.CmdLookat(a.Length > 2 && int.TryParse(a[2], out var h) ? h : -1); break;
                    case "followview":   // mode 1 with the given anchor — the vector is REQUIRED natively
                        if (a.Length > 2) elf.CmdFollowView(ParseVec3(a[2]));
                        else throw new TclException("wrong # args: should be \"elf followview {x y z}\"");
                        break;
                    case "unfollowview": elf.CmdUnfollowView(); break;   // mode 0: stop + hold position
                    case "finishcode":
                        // a[2] = the watched object (native checks liveness before firing). Resolve
                        // like Obj::GetObjSafe would ("this"/handle); unresolvable → -2 = no-check.
                        if (a.Length > 3)
                        {
                            var fobj = ResolveEntityOrNull(ctx, a[2]);
                            elf.CmdFinishcode(fobj?.Handle ?? (int.TryParse(a[2], out var fo) ? fo : -2), a[3]);
                        }
                        break;
                    case "texvar":
                        if (a.Length > 2 && int.TryParse(a[2], out var tv)) { elf.CmdTexVar(tv); return ""; }
                        return elf.CmdTexVar(null).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    case "lockview":   elf.CmdLockView(true);  break;
                    case "unlockview": elf.CmdLockView(false); break;
                    case "drawoverui":
                        if (a.Length > 2) { elf.CmdDrawOverUi(ParseTclBool(a[2])); return ""; }
                        return elf.CmdDrawOverUi(null) ? "1" : "0";
                    case "say":       // debug-only text window natively; route to the dialogue overlay
                        if (a.Length > 2 && ctx.Dialogue != null)
                        {
                            elf.MarkVisible();
                            var saySlots = new System.Collections.Generic.List<(Color32, string)>(4);
                            foreach (var line in a[2].Split('\n'))
                                if (saySlots.Count < 4) saySlots.Add((new Color32(255, 203, 247, 255), line));
                            ctx.Dialogue.SetSpeechOut(3f, saySlots, false);
                        }
                        break;
                }
                return "";
            });

            // ----- StoryManager events + trigger lifecycle (enough to run sequences) -----
            // sm_set_event <name> [val] / sm_get_event <name> — story flags read by triggers/AI.
            interp.RegisterCommand("sm_set_event", (i, a) =>
            { if (a.Length >= 2) ctx.StoryEvents[a[1]] = a.Length > 2 ? a[2] : "1"; return ""; });
            interp.RegisterCommand("sm_get_event", (i, a) =>
                a.Length >= 2 && ctx.StoryEvents.TryGetValue(a[1], out var v) ? v : "0");
            // sm_send_message <obj> <message> — "TCL:CTclObjMethods::SMSendMessage" (FUN_006a9f40):
            // resolves the sender object and hands (senderId, message) to
            // CStoryMgr::HandleTriggerMessage (FUN_005f8080 singleton + FUN_005f8cf0). That routine:
            // 1. EVENT AUTO-SET (FUN_005f8cf0 first loop, event stride 0x10): every registered
            // event whose flag byte (+0xc) is still 0 AND whose Trigger class (+0x4) equals
            // the SENDER's class name AND whose keyword (+0x8) equals the message gets its
            // flag set to 1 (the AchievementManager::CheckTriggerName / FUN_005f8840 calls
            // there are the Steam-achievement side — not ported). The keyword DEFAULTS to
            // "activate": SMAddEvent (FUN_0065a820) builds the literal "activate" for every
            // 3-arg registration and only overwrites it when argc == 4. So
            // `sm_add_event Torwaechterin_befreit Trigger_urw_unq_troll_005_Befreien`
            // (CampaignInit.tcl:593) latches when that trigger's sequence ACTIVATES
            // (sequencer.tcl:391 `sm_send_message this "activate"`) — the gate the
            // Metalltor cutscene condition reads (Trigger_Urwald.tcl:693).
            // 2. MUSIC THEMES (second loop): sm_music_theme records — dead in shipped data
            // (every sm_music_theme in CampaignInit.tcl:43-68 is commented out). Not ported.
            // 3. STORY CALLBACK (tail, +0x74 proc / +0x78 obj from sm_register): formats
            // "%s %s %s %i" = "<proc> <senderClass> <message> <senderId>" (FUN_006b9040) and
            // evals it in the registered object's interp (FUN_00555a00) → story.tcl:398
            // `proc callback {cb_trg cb_msg cb_tid}` → newslist → quest news + QL_* events.
            // Sender lifecycle messages come from sequencer.tcl: "create" (:348), "activate"
            // (:391), "shut" (:2002 delete_connected /:2169 destroy); story scripts add custom
            // ones ("RdL" Trigger_Urwald.tcl:1071, "alleweg" Lavawelt.tcl). Was a no-op — the
            // WHOLE campaign event/questlog chain (Torwaechterin_befreit → Urw_040 gate scene,
            // Ring_Der_Natur_erhalten, quest news) was dead.
            interp.RegisterCommand("sm_send_message", (i, a) =>
            {
                if (a.Length < 3) throw new TclException("wrong # args: should be \"sm_send_message obj message\"");
                var sender = ResolveEntityOrNull(ctx, a[1]);
                if (sender == null) throw new TclException("invalid object");   // Obj_GetObjSafe == 0 → TCL_ERROR (FUN_006a9f40:52-54)
                if (sender.Handle <= 0) return "";                              // native routes only when obj id > 0 (:86)
                string senderClass = sender.Class?.Name ?? "";
                string msg = a[2];

                // 1. event auto-set — compare class + keyword EXACTLY (FUN_006b97f0 string equal).
                foreach (var name in ctx.StoryEventOrder)
                {
                    if (!ctx.StoryEventTriggers.TryGetValue(name, out var meta)) continue;
                    if (string.IsNullOrEmpty(meta.Trigger)) continue;           // no trigger metadata → manual event
                    if (ctx.StoryEvents.TryGetValue(name, out var flag)
                        && flag != "0" && flag.Length != 0) continue;           // +0xc already set → skipped (:59)
                    string keyword = meta.Extra.Length == 0 ? "activate" : meta.Extra;   // FUN_0065a820 default
                    if (string.Equals(meta.Trigger, senderClass, System.StringComparison.Ordinal)
                        && string.Equals(keyword, msg, System.StringComparison.Ordinal))
                        ctx.StoryEvents[name] = "1";
                }

                // 3. registered story callback(s) — same invocation shape as StoryDigNotify (BA).
                if (ctx.StoryCallbacks.Count > 0)
                    foreach (var kv in new List<KeyValuePair<int, string>>(ctx.StoryCallbacks))
                        if (ctx.Entities.TryGetValue(kv.Key, out var smEnt))
                            RunInEntityContext(ctx, smEnt,
                                kv.Value + " " + senderClass + " " + msg + " " +
                                sender.Handle.ToString(CultureInfo.InvariantCulture), "sm_callback");   // "%s %s %s %i" — no quoting, like the native sprintf
                return "";
            });

            // sm_dig_message <bool> (BC) — FUN_0065c500 "SMDigMessage": parse the arg as a Tcl boolean
            // and store it on the StoryMgr singleton +0xa0 (:70-84), returning the boolean. This flag
            // GATES the DigNotify story callback (BA). Ctor default 0 (FUN_005fa7c0:33) and dead in
            // shipped data — the only live call is `sm_dig_message 0` (story.tcl:792); the `1` calls
            // are commented (:524/539/555). Wrong-argc = native TCL_ERROR (:17).
            interp.RegisterCommand("sm_dig_message", (i, a) =>
            {
                if (a.Length < 2) throw new TclException("wrong # args: should be \"sm_dig_message bool\"");
                ctx.StoryDigMessage = ParseTclBool(a[1]);
                return ctx.StoryDigMessage ? "1" : "0";
            });

            // sm_add_event <name> ?trigger? ?keyword? (BB) — FUN_0065a820 "SMAddEvent": append the
            // event NAME to the StoryMgr event array (FUN_005f8670; the flag byte +0xc seeds 0) and
            // store the optional Trigger_* class + keyword (:122-162) as metadata; return the new
            // event index (:203). Generator-relevance: NONE — sm_get_event/sm_set_event are
            // create-on-write here, so the zone/growth gate (check_uebergang → sm_get_event
            // Uebergang_*) already works WITHOUT registration; this only makes events quest-log-visible
            // and holds trigger metadata. DEFERRED (flagged): the trigger-FIRING side —
            // AchievementManager::CheckTriggerName + FUN_005f8840 on set-true, and spawning the
            // Trigger_* story objects — is story/quest, read by no growth decision. Redefinition is a
            // native TCL_ERROR (FUN_005f8670:28-31); the port is idempotent (the ~55 registrations run
            // once per CampaignInit, many call sites already in catch{}), returning the existing index.
            interp.RegisterCommand("sm_add_event", (i, a) =>
            {
                if (a.Length < 2) throw new TclException("wrong # args: should be \"sm_add_event name ?trigger? ?arg?\"");
                string name = a[1];
                int idx = ctx.StoryEventOrder.IndexOf(name);
                if (idx >= 0)
                    // Redefinition: the NATIVE errors out ("Story Event redefinition",
                    // FUN_0065a820:118-125 via FUN_005f8670 == -1) BEFORE the metadata store — an
                    // existing event's trigger/keyword is NEVER touched. Scripts re-register
                    // defensively inside catch{} (e.g. Trigger_Urwald.tcl:678
                    // `catch { sm_add_event Torwaechterin_befreit }` in the Metalltor obj_init);
                    // the port's earlier metadata OVERWRITE here clobbered CampaignInit.tcl:593's
                    // trigger binding and killed the event auto-set. Idempotent-return, no store.
                    return idx.ToString(CultureInfo.InvariantCulture);
                idx = ctx.StoryEventOrder.Count;
                ctx.StoryEventOrder.Add(name);
                if (!ctx.StoryEvents.ContainsKey(name)) ctx.StoryEvents[name] = "0";   // +0xc seeded 0
                ctx.StoryEventTriggers[name] = (a.Length > 2 ? a[2] : "", a.Length > 3 ? a[3] : "");
                return idx.ToString(CultureInfo.InvariantCulture);
            });
            // set_sequence 0|1 — enter/leave cutscene mode (gates the Play presenter's camera/UI).
            // Faithful FUN_00649080 "TCL:CTclGlobalMethods::SetSequence": acts on VALUE CHANGE
            // only — writes +0x8c/+0x598, ALWAYS clears the break flag +0x8d, and on the 1→0
            // transition stops+closes the cutscene voice stream (SoloudAudioPlayer::Stop/Close,
            //:100-104) so a skipped cutscene's voice doesn't keep talking over the menu.
            interp.RegisterCommand("set_sequence", (i, a) =>
            {
                bool v = a.Length > 1 && ParseTclBool(a[1]);
                if (v != ctx.SequenceActive)
                {
                    if (v) CompleteSequencePresentationDrain(ctx);
                    // Diagnostic: a raised flag with no visible cutscene swallows every click
                    // order ([ClickToMove] EATER). Keep the transition attributed — who flipped
                    // it, from which object scope, at what game time.
                    UnityEngine.Debug.Log(
                        $"[set_sequence] {(v ? 1 : 0)} by " +
                        $"{(ctx.This != null ? $"{ctx.This.Class?.Name}#{ctx.This.Handle}" : "<root>")} " +
                        $"at gametime={ctx.Sim?.GameTime ?? 0.0:F2}");
                    ctx.SequenceActive = v;
                    ctx.SequenceBreaked = false;   // FUN_00649080:90 — every transition zeroes +0x8d
                    if (!v && ctx.SequencePresentationDrain)
                    {
                        Diggles.Runtime.Diagnostics.HitchFamilyTimingDiag.Record(
                            "cutscene.skip-drain",
                            ctx.SequencePresentationDrainStarted,
                            ctx.SequencePresentationDeferredOps);
                        // The synchronous parse drain is over. Presentation performed by the
                        // remaining shut_sequencer cleanup is final state the player WILL see
                        // (face/action restoration), so it must not be suppressed.
                        ctx.SequencePresentationDrain = false;
                    }
                    if (!v && ctx.Audio != null) { ctx.Audio.StopVoice(); ctx.Audio.CloseVoice(); }
                    // Entering a cutscene: forget any prior view so the camera holds the gameplay
                    // position until this cutscene's first explicit sq_camera (see HasView).
                    if (v && ctx.Camera != null) ctx.Camera.HasView = false;
                }
                if (Diggles.Runtime.CutsceneTrace.Enabled)
                    Diggles.Runtime.CutsceneTrace.Log($"set_sequence {(a.Length > 1 ? a[1] : "?")} at gametime={ctx.Sim?.GameTime ?? 0.0:F2}");
                return "";
            });
            // get_sequencebreaked — FUN_00649330 "TCL:CTclGlobalMethods::Get" returns the byte at
            // DAT_00772400+0x8d (set by ESC in sequence mode, FUN_0052c510:353-355). Polled ONLY by
            // the sequencer's eval_sequence state (sequencer.tcl:229): a true flag triggers the
            // synchronous skip DRAIN (`set is_breaked 1; while {[parse_next 1]} {}; shut_sequencer`).
            // Was a "0" stub — the entire Tcl break/drain path was dead in the port.
            interp.RegisterCommand("get_sequencebreaked", (i, a) =>
            {
                if (ctx.SequenceActive && ctx.SequenceBreaked && !ctx.SequencePresentationDrain)
                {
                    ctx.SequencePresentationDrain = true;
                    ctx.SequencePresentationDrainStarted =
                        Diggles.Runtime.Diagnostics.HitchFamilyTimingDiag.Start();
                    ctx.SequencePresentationDeferredOps = 0;
                    ResetDrainDeferredPayloads(ctx);   // no stale anim/flipbook records from a prior drain
                }
                return ctx.SequenceBreaked ? "1" : "0";
            });
            // ----- the NATIVE TRIGGER ENGINE (wiggles/Wiggles/Objects/Trigger.cpp) -----
            // `trigger <verb> <obj> …` = FUN_0060e920: per-object trigger records (timer/
            // proximity/callback) that arm the whole Trigger_* story-quest chain
            // (Trigger_Urwald.tcl:105/925-940, Trigger_Tutorial.tcl:1028…). Scanned once per
            // 10 Hz logic tick by DigglesGameSim.Step (native: FUN_0060df40 ← CGameThread::
            // SrvDoThink 006b4a30:391). Was a one-line no-op — no story trigger ever armed.
            ctx.TriggerService = new TriggerService(ctx);
            interp.RegisterCommand("trigger", (i, a) => ctx.TriggerService!.Command(a));

            // ----- gap-fill: de-stub RETURN-CRITICAL engine commands (full Tcl-usage audit) -----
            // A workflow classified all 234 used-but-unimplemented commands by their REAL Tcl usage; these
            // are the ones whose "0"/no-op is BRANCHED on (if/while/expr/set) or a critical state side
            // effect — silently breaking the unchanged Tcl. Benign side-effects (log/render/sound/loading)
            // and out-of-scope systems (combat fight_*, multiplayer net, level-gen lg_*, energy/water,
            // doors/elevators) stay "0". Registered before RegisterStubs so these win.

            // list ops (siblings of land/lnand): lor = ordered union; lcount = occurrences of a[2] in a[1].
            interp.RegisterCommand("lor", (i, a) =>
            {
                var seen = new System.Collections.Generic.HashSet<string>();
                var outl = new System.Collections.Generic.List<string>();
                for (int k = 1; k < a.Length; k++) foreach (var e in TclList.Parse(a[k])) if (seen.Add(e)) outl.Add(e);
                return TclList.Format(outl);
            });
            interp.RegisterCommand("lcount", (i, a) =>
            {
                if (a.Length < 3) return "0";
                int n = 0; foreach (var e in TclList.Parse(a[1])) if (e == a[2]) n++;
                return n.ToString(CultureInfo.InvariantCulture);
            });

            // pure vector primitives (native X,Y horizontal, Z height; vector_angle == get_anglexz formula).
            interp.RegisterCommand("vector_abs", (i, a) =>
            {
                if (a.Length < 2) return "0";
                var v = ParseVec3(a[1]);
                return Mathf.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z).ToString("R", CultureInfo.InvariantCulture);
            });
            // vector_angle p1 p2 — NOT the get_anglexz formula: the native (FUN_00639e80
            // "TCL:CTclGlobalMethods::VectorAngle" → FUN_00410940) measures the DELTA's angle as
            // ±acos over the (x, z) ground plane — a quarter turn off get_anglexz, range (−π, π].
            // Three independent script families prove the −π/2 law against our get_anglexz:
            // rotate_towards (z_dignwalk.tcl:971-992): VA(item,this) − 1.57 must face the item ⇒
            // G − 1.57 − 1.57 = G(this,item) ✓; troll/spider/dragon turn-to (t_procs.tcl:138,
            // spinne.tcl:394): rotate [1.57 + VA(this,target)] = G(this,target) ✓;
            // and the ANCHOR — harvest's dummy pick (z_procs.tcl:794-807): the explicit east/west
            // shortcuts (dx>5 ⇒ dummy 4, dx<−5 ⇒ dummy 0) reproduce through the
            // `for {tryangle −2.749} … 0.7853` binning ONLY with VA = G − π/2 in (−π, π]
            // (east ⇒ ±π ⇒ 0/8 decrements ⇒ 4; west ⇒ 0 ⇒ 4 decrements ⇒ 0).
            // The old version returned G itself wrapped to [0, 2π) — every rotate_towards was a
            // quarter turn off (chop/barrel/pickup facing) and the binning start at −2.749 was
            // unreachable.
            interp.RegisterCommand("vector_angle", (i, a) =>
            {
                if (a.Length < 3) return "0";
                var p1 = ParseVec3(a[1]); var p2 = ParseVec3(a[2]);
                double ang = Mathf.Atan2(p1.x - p2.x, p2.z - p1.z) - System.Math.PI / 2.0;
                if (ang <= -System.Math.PI) ang += 2.0 * System.Math.PI;   // → (−π, π], the native ±acos range
                return ang.ToString("R", CultureInfo.InvariantCulture);
            });
            interp.RegisterCommand("vector_setz", (i, a) => { var v = ParseVec3(a[1]); v.z = ParseFloat(a[2]); return FormatVec3(v); });
            interp.RegisterCommand("vector_inbox", (i, a) =>
            {
                if (a.Length < 4) return "0";
                var p = ParseVec3(a[1]); var mn = ParseVec3(a[2]); var mx = ParseVec3(a[3]);
                return (p.x >= mn.x && p.x <= mx.x && p.y >= mn.y && p.y <= mx.y && p.z >= mn.z && p.z <= mx.z) ? "1" : "0";
            });
            interp.RegisterCommand("vector_equal", (i, a) =>
            {
                if (a.Length < 3) return "0";
                var u = ParseVec3(a[1]); var v = ParseVec3(a[2]);
                return (Mathf.Abs(u.x - v.x) < 1e-3f && Mathf.Abs(u.y - v.y) < 1e-3f && Mathf.Abs(u.z - v.z) < 1e-3f) ? "1" : "0";
            });
            interp.RegisterCommand("vector_normalize", (i, a) =>
            {
                if (a.Length != 2)
                    throw new TclException("wrong # args: should be \"vector_normalize vector\"");
                var v = ParseVec3(a[1]);
                return FormatVec3(v.sqrMagnitude > 1e-12f ? v.normalized : Vector3.zero);
            });
            interp.RegisterCommand("vector_roty", (i, a) =>
            {
                if (a.Length != 3)
                    throw new TclException("wrong # args: should be \"vector_roty vector yrot\"");
                var v = ParseVec3(a[1]);
                float angle = ParseFloat(a[2]);
                float sin = Mathf.Sin(angle);
                float cos = Mathf.Cos(angle);
                return FormatVec3(new Vector3(
                    v.x * cos - v.z * sin,
                    v.y,
                    v.x * sin + v.z * cos));
            });
            // get_vectorxz <roty> <len> — the facing DIRECTION of a native yaw, i.e. the inverse of
            // get_anglexz: G(a,b) = atan2(a.x−b.x, b.z−a.z) ⇒ facing dir (b−a) = (−sin θ, 0, cos θ).
            // The +sin version pointed mirror-X — switch/lever stands (switch_item, z_procs.tcl:132)
            // landed on the wrong side of the object.
            interp.RegisterCommand("get_vectorxz", (i, a) =>
            {
                if (a.Length < 3) return "0 0 0";
                float ang = ParseFloat(a[1]), len = ParseFloat(a[2]);
                return FormatVec3(new Vector3(-len * Mathf.Sin(ang), 0f, len * Mathf.Cos(ang)));
            });
            interp.RegisterCommand("dist_between", (i, a) =>
            {
                var e1 = ResolveEntityOrNull(ctx, a.Length > 1 ? a[1] : "");
                var e2 = ResolveEntityOrNull(ctx, a.Length > 2 ? a[2] : "");
                if (e1 == null || e2 == null) return "99999";   // invalid → "far" so `<N` guards don't skip the approach
                var d = e1.Position - e2.Position;
                return Mathf.Sqrt(d.x * d.x + d.y * d.y + d.z * d.z).ToString("R", CultureInfo.InvariantCulture);
            });

            // ownership: write the e.Owner FIELD that get_owner + the work dispatcher read (a[2]=player id).
            interp.RegisterCommand("set_owner", (i, a) =>
            {
                if (a.Length > 2 && ResolveEntityOrNull(ctx, a[1]) is { } e)
                {
                    e.Owner = IntArg(a, 2, e.Owner);
                    RefreshBuildingClanFlag(e);
                }
                return "";
            });
            // get_diplomacy <o1> <o2> — native FUN_00669000: unparsable args read as 0 (:62/:88);
            // either side -1 (the world) → "enemy" (:93-105 — the world string is the same word
            // FUN_005e2330's relation-4 mask branch matches: wild critters are hostile to all);
            // out-of-range >7 → "invalid owner" error (port: ""); else table code → string
            // {0 "own" (:130-135), 1 "ally", 2 "friend", 3 "neutral", 4 "enemy" (:138-149)}.
            // The old port shortcut (same owner → "ally", else "neutral") ignored the table —
            // set_diplomacy writes vanished and traps/AI read a frozen world.
            interp.RegisterCommand("get_diplomacy", (i, a) =>
            {
                int o1 = IntArg(a, 1, 0), o2 = IntArg(a, 2, 0);
                if (o1 == -1 || o2 == -1) return "enemy";
                if (o1 < 0 || o1 > 7 || o2 < 0 || o2 > 7) return "";
                return ctx.Diplomacy[o1, o2] switch
                {
                    0 => "own", 1 => "ally", 2 => "friend", 3 => "neutral", 4 => "enemy",
                    _ => "invalid",
                };
            });
            // set_diplomacy <o1> <o2> <word> — native FUN_00669360: rejects the world
            // ("can't change world diplomacy state",:95-96) and self-pairs ("can't change own
            // diplomacy state !",:103-105); owners must be 0..7 ("invalid owner"); word →
            // code own=0/ally=1/friend=2/neutral=3/enemy=4, own(0) and unknown(5) rejected
            // ("invalid diplomacy state !",:219-233); the write is ONE-DIRECTIONAL
            // dip[o1][o2] = code (:220-225) — scripts set both directions themselves
            // (CampaignInit.tcl:4-13, conquer_callback z_work_common.tcl:616-617).
            interp.RegisterCommand("set_diplomacy", (i, a) =>
            {
                if (a.Length < 4) return "";
                int o1 = IntArg(a, 1, -1), o2 = IntArg(a, 2, -1);
                if (o1 == -1 || o2 == -1 || o1 == o2) return "";
                if (o1 < 0 || o1 > 7 || o2 < 0 || o2 > 7) return "";
                int code = a[3] switch { "ally" => 1, "friend" => 2, "neutral" => 3, "enemy" => 4, _ => 5 };
                if (code == 5) return "";
                ctx.Diplomacy[o1, o2] = code;
                return "";
            });
            // get_player_color <owner> → the player's colour index; hatcolor = this+1 (z_methods.tcl:297
            // hatcolor → set_textureanimation on the muetzen.3db colour frames), clothing reads the same palette.
            // This +1 path is reached ONLY in the menu/skirmish — in a CAMPAIGN the unchanged init forces
            // hatcolor 0 via is_storymgr (z_methods.tcl:194), so campaign hats use variant 0 (yellow work hats +
            // red clan hat). Player 0 → colour 0; others distinct for multiplayer. (Native
            // CTclGlobalMethods::GetPlayerColor reads the per-game player setup we don't fully run; this
            // reproduces it.)
            interp.RegisterCommand("get_player_color", (i, a) =>
            {
                int o = IntArg(a, 1, 0);
                int[] pal = { 0, 1, 2, 3, 4, 5, 6, 7 };   // [0]=player0 → hatcolor 1 (clan hat); others distinct
                return (o >= 0 && o < pal.Length ? pal[o] : 0).ToString(CultureInfo.InvariantCulture);
            });

            // get_max_fow <obj> [owner] — REAL (FUN_0067beb0 → CObj::GetMaxFow FUN_005cb3a0):
            // with an owner returns that lane's byte; without = max over players 0..7. Byte
            // semantics: 0 black, 1 gray floor, ≥0x80 bright half, 0xFF currently lit. Ambient
            // AI gates on it: raupe.tcl:68 / t_procs.tcl:1156 / w_procs.tcl:137 / aggr_events.tcl:76
            // roam only when ≥10 (don't wander in the unexplored black); Info_Pos_TrollSpeier
            // won't spawn in plain sight when >100 (infoobjs.tcl:100). Falls back to the old "50"
            // compromise while no fog state exists (menu backdrop / bare tests).
            interp.RegisterCommand("get_max_fow", (i, a) =>
            {
                var e = ResolveEntityOrNull(ctx, a.Length > 1 ? a[1] : "");
                if (ctx.FogOfWar == null) return "50";
                if (e == null) return GetMaxFowAllPlayers(ctx, null).ToString(CultureInfo.InvariantCulture);
                if (a.Length > 2 && int.TryParse(a[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var owner))
                    return GetMaxFow(ctx, e, owner).ToString(CultureInfo.InvariantCulture);
                return GetMaxFowAllPlayers(ctx, e).ToString(CultureInfo.InvariantCulture);
            });

            // set_fogofwar <obj> <sizex> <sizey> [owner] — REAL (FUN_0068a980): stores the reveal
            // half-extents ×8 (fine units) on the object; exactly -1 = disabled; NEGATIVE values
            // select RemoveFogBoxNoCollision in UpdateFoW (FogRemover `-50 -50` = a permanent
            // 50×50-tile window through solid rock). Gnomes pass 14 8; buildings tttfow_x/y
            // (genericprod), cleared to -1 -1 on pack-to-box. The optional 4th arg sets the owner
            // slot (native obj+0x36c).
            interp.RegisterCommand("set_fogofwar", (i, a) =>
            {
                var e = ResolveEntityOrNull(ctx, a.Length > 1 ? a[1] : "");
                if (e == null || a.Length < 4) return "";
                double sx = ArgNum(a, 2, -1), sy = ArgNum(a, 3, -1);
                e.FowSizeX = sx == -1 ? -1 : (int)(sx * 8);
                e.FowSizeY = sy == -1 ? -1 : (int)(sy * 8);
                if (a.Length > 4 && int.TryParse(a[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ow))
                    e.Owner = ow;
                return "";
            });

            // remove_black_fog <player> <l> <t> <r> <b> — REAL (FUN_0066aec0): tile rect, pure
            // BLACK→GRAY flip (0→1 only; no fade list, no reveal). Story reveals + the
            // Info_Fog_Aufdecker `grau 1` mode use it.
            interp.RegisterCommand("remove_black_fog", (i, a) =>
            {
                if (ctx.FogOfWar == null || a.Length < 6) return "";
                ctx.FogOfWar.RemoveBlackFog(ArgInt(a, 1, 0), ArgInt(a, 2, 0), ArgInt(a, 3, 0),
                                            ArgInt(a, 4, 0), ArgInt(a, 5, 0));
                return "";
            });

            // set_fow_begin <row> [player] — REAL (FUN_0064b380 → FUN_00580610): the sky line.
            // Rows above are permanently revealed and never re-fog. The native handler oddly
            // requires player < 7 (0064b380:118) — kept faithful.
            interp.RegisterCommand("set_fow_begin", (i, a) =>
            {
                if (ctx.FogOfWar == null || a.Length < 2) return "";
                int player = a.Length > 2 ? ArgInt(a, 2, 0) : 0;
                if (player < 7) ctx.FogOfWar.SetFowBegin(ArgInt(a, 1, 0), player);
                return "";
            });

            // show_fogofwar <bool> — render-only toggle. NOTE: in the shipped exe the flag
            // (game+0x59c, FUN_0062af80) is WRITE-ONLY/dead; the port honors it so cutscenes can
            // reveal the scene. The fog LOGIC keeps running either way (faithful).
            interp.RegisterCommand("show_fogofwar", (i, a) =>
            {
                if (ctx.FogOfWar != null && a.Length > 1) ctx.FogOfWar.ShowFog = ParseTclBool(a[1]);
                return "";
            });

            // set_light_begin <row> — REAL (FUN_0064b620 "SetLightBegin" → FUN_00590620 writes
            // DAT_007683d8): the SKY line of the LIGHTING system ("Der Himmel fängt in dieser
            // Höhe an", single_Oberwelt.tcl:6; campaigns use 33). Above it terrain gets full
            // daylight (baselight 20) and NO auto-torches spawn; below is the dark underground.
            // Runs during boot eval before the world systems exist → lands on the context and
            // seeds TunnelTorchSystem/BaseLightMap at creation (Docs/TUNNEL_TORCH_LIGHTING_RE.md §6).
            interp.RegisterCommand("set_light_begin", (i, a) =>
            {
                if (a.Length < 2) return "";
                int row = ArgInt(a, 1, -10000);
                ctx.LightBeginRow = row;
                ctx.GroundBeginRow = row;
                if (ctx.TunnelTorches != null) ctx.TunnelTorches.LightBegin = row;
                if (ctx.BaseLight != null && ctx.BaseLight.LightBegin != row)
                {
                    ctx.BaseLight.LightBegin = row;
                    ctx.BaseLight.RecomputeAll();   // native dirty-rects the sky band (FUN_00590620:21-27)
                }
                return "";
            });

            interp.RegisterCommand("set_ground_begin", (i, a) =>
            {
                if (a.Length > 1) ctx.GroundBeginRow = ArgInt(a, 1, -10000);
                return "";
            });

            // get_lmap_red/green/blue <x> <y> — REAL and deliberately trivial: the native
            // handlers (FUN_0062fe40/00630190/006304e0 "GetLMapRed/Green/Blue") read NO map —
            // they return the constant 8 when (x,y) lies inside the light rect, else 0.
            // get_lmap_light does NOT exist in the exe (string absent) — its "0" bulk stub
            // stands. No data script branches on these (only a debug log in utility.tcl:27).
            string LmapProbe(string[] a)
            {
                if (a.Length < 3 || ctx.Terrain == null) return "0";
                double x = ArgNum(a, 1, -1), y = ArgNum(a, 2, -1);
                return x >= 0 && x < ctx.Terrain.Width && y >= 0 && y < ctx.Terrain.Height
                    ? "8" : "0";
            }
            interp.RegisterCommand("get_lmap_red",   (i, a) => LmapProbe(a));
            interp.RegisterCommand("get_lmap_green", (i, a) => LmapProbe(a));
            interp.RegisterCommand("get_lmap_blue",  (i, a) => LmapProbe(a));

            // is_storymgr — NATIVE; true in a campaign (StoryMgr) world, false at the menu backdrop + skirmish.
            // Was an unknown-command → "0", so the campaign's `if {[is_storymgr]} {set hatcolor 0}`
            // (z_methods.tcl:194) never ran → hatcolor stayed -1 → the +1 override gave 1 = the RED multiplayer
            // hat atlas for the stone/wood WORK hats (they should be YELLOW = variant 0). With this true in the
            // campaign, the unchanged init sets hatcolor 0 → yellow work hats + red clan hat, faithfully.
            // Driven by DigglesWorldRuntime (ctx.IsStoryMgr = !_atMenu). NOTE: `im_in_campaign` is NOT
            // registered here — it is a Tcl PROC (z_procs.tcl:22) returning the per-object `is_campaign` that
            // init sets from is_storymgr; shadowing it with a C# command would bypass that per-object value.
            interp.RegisterCommand("is_storymgr", (i, a) => ctx.IsStoryMgr ? "1" : "0");

            // set_prod_enabled / get_prod_enabled — the place's job-emission gate. Native:
            // SetProdEnabled (TCL:CTclObjMethods::SetProdEnabled, FUN_006950a0:35-87) writes a
            // bool at prodstruct(obj+0x3e0)+0x64 for object types {5,6,7,10}; GetProdEnabled
            // (FUN_00694e80) reads it; the WorkScheduler job collector FUN_005f0850:21,128 emits
            // a place's per-slot work jobs ONLY while the byte is set. The Lager toggles it from
            // its storable-item scans (lager.tcl:145/154, 254/257, 326/329) — "there is something
            // to store"; fallen.tcl traps + tueren.tcl doors branch on the getter. Default
            // ENABLED (the gate is opt-out). Were bulk "0" stubs — the Lager could never signal,
            // and every get_prod_enabled branch was dead. NOTE: ProdDispatcher does not gate on
            // this yet — this de-stubs the Tcl-visible state faithfully.
            interp.RegisterCommand("set_prod_enabled", (i, a) =>
            {
                if (a.Length >= 3 && ResolveEntityOrNull(ctx, a[1]) is { } b)
                    b.VarsScope.Vars["_prod_enabled"] = ParseTclBool(a[2]) ? "1" : "0";
                return "";
            });
            interp.RegisterCommand("get_prod_enabled", (i, a) =>
                a.Length >= 2 && ResolveEntityOrNull(ctx, a[1]) is { } b
                    && b.VarsScope.Vars.TryGetValue("_prod_enabled", out var pe) ? pe : "1");

            // lmsg <key> [a1..a9] — native TCL:CTclGlobalMethods::lmsg = localized text lookup
            // in data/messages.txt (## "Key" → per-locale lines). Resolves through the shared
            // GameLocalization table (bound by DigglesEngine.Build); extra args substitute the
            // native %1..%9 placeholders. Unbound table / unknown key → the KEY passes through
            // (visible + greppable), matching the table's own en fallback law.
            interp.RegisterCommand("lmsg", (i, a) =>
            {
                if (a.Length < 2) return "";
                var localization = Diggles.Runtime.Localization.GameLocalization.Current;
                if (localization == null) return a[1];
                if (a.Length == 2) return localization.Get(a[1]);
                var args = new object[a.Length - 2];
                System.Array.Copy(a, 2, args, 0, args.Length);
                return localization.Format(a[1], args);
            });

            // per-entity production state flags (set + matching getter; getters were stuck at "0").
            // set/get_prod_materialneed — the recipe-gate byte rec+0x41 (SetProdMaterialNeed
            // FUN_00696920:73-82 writes it; the recipe check FUN_00434c20:22 and the demand scan
            // FUN_00434e60:301 gate on it). Default ON — the record inits need-materials and only
            // self-supplying places (farm.tcl:118/:157) turn it off. ProdDispatcher reads the var
            // with the same default.
            interp.RegisterCommand("set_prod_materialneed", (i, a) => { if (a.Length >= 3 && ResolveEntityOrNull(ctx, a[1]) is { } b) b.VarsScope.Vars["_materialneed"] = ParseTclBool(a[2]) ? "1" : "0"; return ""; });
            interp.RegisterCommand("get_prod_materialneed", (i, a) => a.Length >= 2 && ResolveEntityOrNull(ctx, a[1]) is { } b && b.VarsScope.Vars.TryGetValue("_materialneed", out var v) ? v : "1");
            // set/get_prod_schedule — the production-section enable byte rec+0x43
            // (FUN_00696670:73-82 writes it; the SP collector FUN_00436750:70 and the demand
            // scan's invention emission FUN_00434e60:608 run ONLY while it is set). Default ON;
            // the FARM drives it 0/1 every event0 tick (farm.tcl:158/:166 — 1 = "send me a
            // gnome", e.g. the initial breeding visit), dojo lessons arm it (dojo.tcl:94/:110).
            // Was a "0" bulk stub — the farm could neither read back its own writes nor gate
            // the planner (idle-gap/extra-fetch cluster).
            interp.RegisterCommand("set_prod_schedule", (i, a) => { if (a.Length >= 3 && ResolveEntityOrNull(ctx, a[1]) is { } b) b.VarsScope.Vars["_prod_schedule"] = ParseTclBool(a[2]) ? "1" : "0"; return ""; });
            interp.RegisterCommand("get_prod_schedule", (i, a) => a.Length >= 2 && ResolveEntityOrNull(ctx, a[1]) is { } b && b.VarsScope.Vars.TryGetValue("_prod_schedule", out var v) ? v : "1");
            // set/get_prod_switchmode — the "orders are an ON/OFF TOGGLE" mode (farm.tcl:119
            // sets it; prod_inventattribute zeroes a freshly-invented slot only in switchmode,
            // z_work_common.tcl:28-30; std_ai branches on it, std_ai.tcl:2197). The production
            // window renders switchmode slots as the green-lamp toggle instead of a counted
            // order. Was a "0" bulk stub.
            interp.RegisterCommand("set_prod_switchmode", (i, a) =>
            {
                if (a.Length >= 3 && ResolveEntityOrNull(ctx, a[1]) is { } b)
                    b.ProdSwitchMode = ParseTclBool(a[2]);
                return "";
            });
            interp.RegisterCommand("get_prod_switchmode", (i, a) =>
                a.Length >= 2 && ResolveEntityOrNull(ctx, a[1]) is { ProdSwitchMode: true }
                    ? "1"
                    : "0");
            // set/get_prod_exclusivemode — "one active slot at a time" (farm.tcl:120); the
            // window's toggle click turns the other slots off when set. Was a "0" bulk stub.
            interp.RegisterCommand("set_prod_exclusivemode", (i, a) =>
            {
                if (a.Length >= 3 && ResolveEntityOrNull(ctx, a[1]) is { } b)
                    b.ProdExclusiveMode = ParseTclBool(a[2]);
                return "";
            });
            interp.RegisterCommand("get_prod_exclusivemode", (i, a) =>
                a.Length >= 2 && ResolveEntityOrNull(ctx, a[1]) is { ProdExclusiveMode: true }
                    ? "1"
                    : "0");
            // set/get_prod_pack — the planner's "deconstruction requested" byte (+0x29 on the
            // obj+0x3e0 planner record): SetProdPack FUN_006933b0:86 writes, GetProdPack
            // FUN_006931e0:59 reads. Backed by the typed PackRequested field (sibling of
            // BuildupRequested/+0x2a): the dispatcher collects it as the kind-0 pack job
            // (FUN_005f0850:56-89) and prod_builddown gates on it mid-chain (z_work_common.tcl:444).
            interp.RegisterCommand("set_prod_pack", (i, a) => { if (a.Length >= 3 && ResolveEntityOrNull(ctx, a[1]) is { } e) e.PackRequested = IntArg(a, 2, 0) != 0; return ""; });
            interp.RegisterCommand("get_prod_pack", (i, a) => a.Length >= 2 && ResolveEntityOrNull(ctx, a[1]) is { } e ? (e.PackRequested ? "1" : "0") : "0");
            // set/get_prod_ownerstrength — the conquer/repair rope-pull float at prod record
            // rec+0x68: SetProdOwnerStrength (FUN_00693c20:87) writes it RAW (no clamp;
            // unparsable → 0.0,:75-85 — the Tcl caps at 1.0 itself, z_work_common.tcl:645),
            // GetProdOwnerStrength (FUN_00693a40) reads it, record ctor DEFAULT = 1.0f
            // (FUN_005c9fa0:219). BRANCHED: prod_autorepair `< 0.95` → conquer else prod_repair
            // (z_work_common.tcl:313-317); conquer_callback pulls ±offset per pullrope anim and
            // completes the takeover at ≤0.01 (:613-657). The old "0" stubs turned every
            // dispatcher repair into an INSTANT conquer (strength read 0 → change_owner at
            // once) — the voodoo-camp-takeover bug's second leg.
            interp.RegisterCommand("set_prod_ownerstrength", (i, a) => { if (a.Length >= 3 && ResolveEntityOrNull(ctx, a[1]) is { } e) e.ProdOwnerStrength = ParseFloat(a[2]); return ""; });
            interp.RegisterCommand("get_prod_ownerstrength", (i, a) => a.Length >= 2 && ResolveEntityOrNull(ctx, a[1]) is { } e ? Ftos(e.ProdOwnerStrength) : "1");
            // set/get_prod_unpack — the planner's "unpack requested" byte (+0x28): SetProdUnpack
            // FUN_00692f80:86 writes a BOOL (Tcl_GetBooleanFromObj — unparsable/absent → false),
            // GetProdUnpack FUN_00692db0:59 reads it. TYPED (was a "_prod_unpack" string var no
            // collector ever read — buildup-carrier-re.md §9.2): the dispatcher turns it into the
            // kind-1 unpack job at the entity's OrderPos (FUN_005f0850:90-126), and stop_prod's
            // cancel `set_prod_unpack $item 0` (z_procs.tcl:1240) really clears the pending order.
            interp.RegisterCommand("set_prod_unpack", (i, a) => { if (a.Length >= 3 && ResolveEntityOrNull(ctx, a[1]) is { } e) e.UnpackRequested = ParseTclBool(a[2]); return ""; });
            interp.RegisterCommand("get_prod_unpack", (i, a) => a.Length >= 2 && ResolveEntityOrNull(ctx, a[1]) is { } e ? (e.UnpackRequested ? "1" : "0") : "0");

            // gnome↔workplace association the production work dispatch records on every work-loop entry.
            interp.RegisterCommand("prod_gnome_last_workplace", (i, a) => { if (a.Length >= 3 && ResolveEntityOrNull(ctx, a[1]) is { } g) g.VarsScope.Vars["_last_workplace"] = a[2]; return ""; });
            interp.RegisterCommand("prod_gnome_get_last_workplace", (i, a) => a.Length >= 2 && ResolveEntityOrNull(ctx, a[1]) is { } g && g.VarsScope.Vars.TryGetValue("_last_workplace", out var v) ? v : "0");
            // Native planner sub+0x40 toggle (FUN_006a81b0:55-75): a missing/invalid target clears,
            // the same live handle toggles off, and a different live handle replaces it. The setter
            // performs no building-kind or ownership validation; dispatcher consumption validates
            // liveness and same-player ownership without destroying save-retained dangling handles.
            interp.RegisterCommand("prod_gnome_preferred_workplace", (i, a) =>
            {
                if (a.Length < 2 || ResolveEntityOrNull(ctx, a[1]) is not { } g) return "";
                if (g.NativeObjectType != 1) throw new TclException("no gnome");
                int targetHandle = a.Length == 3 && ResolveEntityOrNull(ctx, a[2]) is { } workplace
                    ? workplace.Handle
                    : -1;
                g.PreferredWorkplaceHandle = g.PreferredWorkplaceHandle == targetHandle
                    ? -1
                    : targetHandle;
                return "";
            });
            interp.RegisterCommand("prod_gnome_get_preferred_workplace", (i, a) =>
            {
                if (a.Length < 2 || ResolveEntityOrNull(ctx, a[1]) is not { } g) return "0";
                if (g.NativeObjectType != 1) throw new TclException("no gnome");
                return g.PreferredWorkplaceHandle > 0 &&
                    ctx.Entities.ContainsKey(g.PreferredWorkplaceHandle)
                    ? g.PreferredWorkplaceHandle.ToString(CultureInfo.InvariantCulture)
                    : "0";
            });

            // get_expattrib / get_bestexp — registered ONCE, decompile-verified, in the attrib
            // block up top (near add_expattrib). An earlier approximation here (bare call →
            // NON-fight names only; get_bestexp scanning exp_F_* too) silently OVERWROTE the
            // faithful version — native FUN_00644de0's bare form returns ALL exp_* (fight incl.),
            // which every life-stage skill copy (z_methods.tcl:42/79, pzwerg.tcl:28, baby.tcl)
            // iterates positionally, and FUN_0068bff0's best-exp scan EXCLUDES the exp_F_* set.

            // tasklist_find: index of a queued task, -1 if absent (the "0" stub read as "already queued").
            interp.RegisterCommand("tasklist_find", (i, a) =>
            {
                var e = a.Length > 1 ? ResolveEntityOrNull(ctx, a[1]) : null;
                if (e == null) return "-1";
                string needle = a.Length > 2 ? a[2] : "";
                return e.TaskList.FindIndex(t => t == needle).ToString(CultureInfo.InvariantCulture);
            });
            // Production task aggregates are registered with the rest of CAIManager's native
            // world-query surface by AiSystem.
            // get_place_long <center> <range> [fixwalkable] [walkable] — "find a passable point
            // (no collision-map-check!)" (ENGINE_TCL_API): the terrain-only companion of get_place —
            // no object/placelock tests, just the nearest standable FLOOR sub-cell (walk|floor,
            // value 3 — the bare walk bit would also match CLIMB wall cells) within `range` of the
            // centre. Callers: walk_down_from_wall (z_dignwalk.tcl:855-877, expanding-range 5..50
            // ground scan for a climber) and sparetime_climb_somewhere (z_spare_procs.tcl:968-992);
            // both gate on `lindex 0 > 0/1`, so failure keeps the get_place "-1 0 0" sentinel.
            // Was that sentinel UNCONDITIONALLY — a climbing gnome could never find ground and hung
            // on the wall through its whole sparetime shift.
            interp.RegisterCommand("get_place_long", (i, a) =>
            {
                var nav = ctx.Terrain?.Nav;
                if (nav == null || a.Length < 3) return "-1 0 0";
                var center = ParseVec3(a[1]);
                int cx = Mathf.RoundToInt(center.x * 8f), cy = Mathf.RoundToInt(center.y * 8f);
                int maxR = Mathf.Clamp(Mathf.CeilToInt(ParseFloat(a[2]) * 8f), 1, 512);
                const byte floorBits = Diggles.Runtime.Navigation.NavWorld.SubWalk
                                     | Diggles.Runtime.Navigation.NavWorld.SubFloor;
                if (!TrySnapToSub(nav, cx, cy, maxR, floorBits, out int fx, out int fy))
                    return "-1 0 0";
                // Stand depth of the found line — the vector_fix law (support(y−1)+1, capped).
                float z = Mathf.Min(nav.GetHmapSub(fx, fy - 1) + 1f, 14.5f);
                return FormatVec3(new Vector3(fx * 0.125f, fy * 0.125f, z));
            });

            // colony aggregates (per-owner counts) the sparetime / breeding / AI loops branch on.
            interp.RegisterCommand("gamestats", (i, a) =>
            {
                string sub = a.Length > 1 ? a[1] : ""; int own = IntArg(a, 2, 0);
                int Cnt(System.Func<DigglesTclEntity, bool> p) { int n = 0; foreach (var e in ctx.Entities.Values) if (e.Owner == own && p(e)) n++; return n; }
                switch (sub)
                {
                    case "numgnomes":   return Cnt(e => e.Class?.Name == "Zwerg").ToString();
                    case "numfemale":   return Cnt(e => e.Class?.Name == "Zwerg" && e.Gender == "female").ToString();
                    case "nummale":     return Cnt(e => e.Class?.Name == "Zwerg" && e.Gender == "male").ToString();
                    case "numbabies":   return Cnt(e => e.Class?.Name == "Baby").ToString();
                    case "numpregnant": return Cnt(e => e.VarsScope.Vars.TryGetValue("_partner_preg", out var p) && p == "1").ToString();
                    case "numitems":    { string cls = a.Length > 3 ? a[3] : ""; return Cnt(e => e.Class?.Name == cls).ToString(); }
                    case "numbuiltprodclasses":
                    {
                        var classes = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
                        foreach (var e in ctx.Entities.Values)
                            if (e.Owner == own && e.BuildupComplete && e.Class != null &&
                                (e.Class.Kind == "production" || e.Class.Kind == "store" ||
                                 e.Class.Kind == "energy" || e.Class.Kind == "protection" ||
                                 e.Class.Kind == "elevator"))
                                classes.Add(e.Class.Name);
                        return classes.Count.ToString(CultureInfo.InvariantCulture);
                    }
                    case "attribsum": case "attribmax": case "attribavg":
                        return (ctx.OwnerAttribs.TryGetValue(own, out var d) && d.TryGetValue(a.Length > 3 ? a[3] : "", out var x) ? x : 0.0)
                            .ToString("R", CultureInfo.InvariantCulture);
                    default: return "0";
                }
            });
            interp.RegisterCommand("gamesave", (i, a) =>
            {
                if (a.Length != 2)
                    throw new TclException("wrong # args: should be \"gamesave name\"");
                if (ctx.SaveGames == null)
                    throw new TclException("gamesave is unavailable before a gameplay world is loaded");
                return ctx.SaveGames.Save(a[1]);
            });
            interp.RegisterCommand("gameload", (i, a) =>
            {
                if (a.Length != 2)
                    throw new TclException("wrong # args: should be \"gameload name\"");
                if (ctx.SaveGames == null)
                    throw new TclException("gameload is unavailable before a gameplay world is loaded");
                return ctx.SaveGames.Load(a[1]);
            });

            // partner_info: the gnome family/relationship store (breeding) — get* are branched, set* mutate.
            interp.RegisterCommand("partner_info", (i, a) =>
            {
                string sub = a.Length > 1 ? a[1] : "";
                var e = a.Length > 2 ? ResolveEntityOrNull(ctx, a[2]) : null;
                if (e == null) return "";
                string Get(string k) => e.VarsScope.Vars.TryGetValue("_partner_" + k, out var v) ? v : "";
                void Put(string k, string v) => e.VarsScope.Vars["_partner_" + k] = v;
                void PutLink(string role, string value)
                {
                    Put(role, value);
                    int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id);
                    switch (role)
                    {
                        case "partner":
                            e.FamilyPartnerId = id;
                            e.FamilyPartnerState = 1;
                            e.FamilyPartnerReferenceObjectId = -1;
                            break;
                        case "father":
                            e.FamilyFatherId = id;
                            e.FamilyFatherState = 1;
                            e.FamilyFatherReferenceObjectId = -1;
                            break;
                        case "mother":
                            e.FamilyMotherId = id;
                            e.FamilyMotherState = 1;
                            e.FamilyMotherReferenceObjectId = -1;
                            break;
                    }
                    e.HasFamilyState = true;
                }
                switch (sub)
                {
                    case "setpartner":   PutLink("partner", a[3]); return "";
                    case "getpartner":   return Get("partner");
                    case "setfather":    PutLink("father", a[3]); return "";
                    case "getfather":    return Get("father");
                    case "setmother":    PutLink("mother", a[3]); return "";
                    case "getmother":    return Get("mother");
                    case "setpregnancy": Put("preg", a[3]); e.FamilyPregnancyFlag = ParseTclBool(a[3]); e.HasFamilyState = true; return "";
                    case "getpregnancy": { var p = Get("preg"); return p == "" ? "0" : p; }
                    case "addchild":     { var l = TclList.Parse(Get("children")); l.Add(a[3]); Put("children", TclList.Format(l)); e.FamilyChildrenNames = l; e.HasFamilyState = true; return ""; }
                    case "getchildren":  return Get("children");
                    case "transfer":
                    {
                        var dst = ResolveEntityOrNull(ctx, a[3]);
                        if (dst != null)
                        {
                            foreach (var kv in new List<KeyValuePair<string, string>>(e.VarsScope.Vars))
                                if (kv.Key.StartsWith("_partner_", System.StringComparison.Ordinal)) dst.VarsScope.Vars[kv.Key] = kv.Value;
                            dst.HasFamilyState = e.HasFamilyState;
                            dst.FamilyPartnerId = e.FamilyPartnerId;
                            dst.FamilyPartnerState = e.FamilyPartnerState;
                            dst.FamilyPartnerReferenceObjectId = e.FamilyPartnerReferenceObjectId;
                            dst.FamilyMotherId = e.FamilyMotherId;
                            dst.FamilyMotherState = e.FamilyMotherState;
                            dst.FamilyMotherReferenceObjectId = e.FamilyMotherReferenceObjectId;
                            dst.FamilyFatherId = e.FamilyFatherId;
                            dst.FamilyFatherState = e.FamilyFatherState;
                            dst.FamilyFatherReferenceObjectId = e.FamilyFatherReferenceObjectId;
                            dst.FamilyChildrenNames = new List<string>(e.FamilyChildrenNames);
                            dst.FamilyPregnancyFlag = e.FamilyPregnancyFlag;
                        }
                        return "";
                    }
                    default: return "";
                }
            });

            // sparetime: leisure-place registry (announce/disannounce register a need slot; query* return the
            // nearby announced places of a kind the gnome then walks to). Native = FUN_006aadd0.
            interp.RegisterCommand("sparetime", (i, a) =>
            {
                var self = a.Length > 1 ? ResolveEntityOrNull(ctx, a[1]) : null;
                string sub = a.Length > 2 ? a[2] : "";
                if (self == null) return "";
                switch (sub)
                {
                    case "announce":
                    {
                        if (!SparetimeCategoryCodec.TryParse(
                                a.Length > 3 ? a[3] : "",
                                out SparetimeCategory category))
                            return "0";
                        self.SpareKind = SparetimeCategoryCodec.GetName(category);
                        int index = ctx.SpareRegistry.FindIndex(entry => entry.ObjectId == self.Handle);
                        var registration = new SparetimeRegistration(category, self.Handle);
                        if (index < 0) ctx.SpareRegistry.Add(registration);
                        else ctx.SpareRegistry[index] = registration;
                        return "";
                    }
                    case "disannounce":
                    {
                        int index = ctx.SpareRegistry.FindIndex(entry => entry.ObjectId == self.Handle);
                        if (index >= 0) ctx.SpareRegistry.RemoveAt(index);
                        self.SpareKind = "";
                        return "";
                    }
                    // queryrect <kind> x0 y0 x1 y1 — the native ADDS the asking object's own
                    // (x, y) to all four bounds and returns the announced places of <kind>
                    // inside that ABSOLUTE rect (FUN_006aadd0:295-318 → FUN_005f4d00). The Tcl
                    // always passes a gnome-centred rect: {-max -half max half} with
                    // max = stt_maxsearch_range = 160 (z_spare_fun.tcl:81,435,517,708;
                    // z_spare_procs.tcl:133,405,633). The old 50-radius proxy silently CUT the
                    // search field to a third, hiding legitimate eat/sleep/fun places.
                    //
                    // OWNER + FLAG GATE (both query walkers, FUN_005f4d00:75-76 /
                    // FUN_005f4b20:75-76): a registered place is returned ONLY when
                    // place+0x36c == asker+0x36c — SAME OWNER, and
                    // (flags & 0x1404002) == 0x1400000 — built(0x400000) + active(0x1000000)
                    // set, boxed(0x4000) + contained(0x2) clear
                    // (plus the not-underwater test, FUN_0057a940, backed by FluidSystem).
                    // Without the owner term the DISCOVERED VOODOO CAMP (template objects
                    // `set_owner 1`, templates/tcl/urw_unq_vodo_*.tcl) entered the player
                    // gnomes' eat/sleep/home searches — they ate at the herbalists' fire and
                    // slept in their tents. The active bit is the CObj ctor default (word
                    // 0x01100101) and set_activegameplay is unimplemented → always passes here.
                    case "queryrect":
                    {
                        if (!SparetimeCategoryCodec.TryParse(
                                a.Length > 3 ? a[3] : "",
                                out SparetimeCategory category))
                            return "";
                        float x0 = ParseFloat(a.Length > 4 ? a[4] : "-160"), y0 = ParseFloat(a.Length > 5 ? a[5] : "-80");
                        float x1 = ParseFloat(a.Length > 6 ? a[6] : "160"),  y1 = ParseFloat(a.Length > 7 ? a[7] : "80");
                        var sb = new System.Text.StringBuilder();
                        foreach (SparetimeRegistration registration in ctx.SpareRegistry)
                        {
                            if (registration.Category != category ||
                                !ctx.Entities.TryGetValue(registration.ObjectId, out var e))
                                continue;
                            if (!SparePlaceEligible(ctx, e, self)) continue;   // FUN_005f4d00:75-76
                            var d = e.Position - self.Position;
                            if (d.x >= x0 && d.x <= x1 && d.y >= y0 && d.y <= y1)
                            { if (sb.Length > 0) sb.Append(' '); sb.Append(registration.ObjectId.ToString(CultureInfo.InvariantCulture)); }
                        }
                        return sb.ToString();
                    }
                    case "queryrange":
                    {
                        if (!SparetimeCategoryCodec.TryParse(
                                a.Length > 3 ? a[3] : "",
                                out SparetimeCategory category))
                            return "";
                        float range = ParseFloat(a.Length > 4 ? a[4] : "0");
                        var sb = new System.Text.StringBuilder();
                        foreach (SparetimeRegistration registration in ctx.SpareRegistry)
                        {
                            if (registration.Category != category ||
                                !ctx.Entities.TryGetValue(registration.ObjectId, out var e))
                                continue;
                            if (!SparePlaceEligible(ctx, e, self)) continue;   // FUN_005f4b20:75-76
                            var d = e.Position - self.Position;
                            // native circle test: dx² + dy² ≤ r² (FUN_005f4b20:78-80)
                            if (d.x * d.x + d.y * d.y <= range * range) { if (sb.Length > 0) sb.Append(' '); sb.Append(registration.ObjectId.ToString(CultureInfo.InvariantCulture)); }
                        }
                        return sb.ToString();
                    }
                    default: return "0";
                }
            });

            // prod_guest — the native guest-seat table (TCL:CTclObjMethods::ProdGuest,
            // FUN_006a8920): the building's prod record (+0x3e0) holds SIXTEEN 12-byte slots at
            // +0x6c = {linkId, guestPtr, order}. A slot EXISTS only once `addlink` has claimed it
            // (link == -1 marks unused;:134-190 scans for the first -1 and stores the link id) —
            // the link is the MODEL DUMMY the seated guest walks to. The Tcl contract this feeds:
            // feuerstelle.tcl:297-306 registers 3 sit links {5 6 7} THEN 5 wait links {0..4}
            // (so seat index 0 → link 5!); sparetime_check_in (z_spare_procs.tcl:882-917) does
            // guestfree→guestset→resetorder; the eat/sleep/home loops then walk_dummy to
            // `prod_guest getlink <place> <seat>` (z_spare_procs.tcl:270,462; z_spare_fun.tcl:192).
            // The old port ignored addlink, fabricated FOUR seats and returned the SEAT INDEX from
            // getlink — every "sit at the fire/bed" walk targeted the wrong dummy (link 0-2 = the
            // WAIT spots / the building origin), so the sit-down occupations never composed.
            // Subcommands (offsets: +0 link, +4 guest, +8 order):
            // addlink <b> <link> — claim the next free slot for this link (:134-190);
            // setlink <b> <seat> <link> — overwrite a slot's link (:198-241);
            // getlink <b> <seat> — the slot's LINK id, -1 OOB/unused (:243-263);
            // guestfree <b> — first slot with a link and NO guest, else -1; dead
            // guest pointers are GC'd to empty on the way (:357-410);
            // guestget <b> <seat> — the seated guest's ref, 0 = empty (:330-356);
            // guestset <b> <seat> <ref> — ref 0/invalid clears guest+order, else seats it (:594-612);
            // guestremove <b> <ref> — clear whichever slot holds ref → its index, 1 if absent (:271-310);
            // addorder / remorder — order+1 / max(0, order−1) — REMORDER DECREMENTS, it
            // does not zero (:566-593 /:436-455);
            // resetorder <b> <seat> — order = 0 (:457-475);
            // getorder <b> <seat> — the order count (:531-549);
            // nextorder <b> — the SEATED slot with the highest non-zero order, else -1 (:476-530).
            interp.RegisterCommand("prod_guest", (i, a) =>
            {
                string sub = a.Length > 1 ? a[1] : "";
                var b = a.Length > 2 ? ResolveEntityOrNull(ctx, a[2]) : null;
                if (b == null) return "0";
                if (b.Guests == null || b.Guests.Length < 16) b.Guests = new int[16];
                if (b.GuestOrders == null || b.GuestOrders.Length < 16) b.GuestOrders = new double[16];
                var links = GuestLinks(b);
                int Seat() => IntArg(a, 3, 0);
                switch (sub)
                {
                    case "addlink":
                        for (int s = 0; s < 16; s++) if (links[s] == -1) { links[s] = IntArg(a, 3, 0); break; }
                        return "";
                    case "setlink":   { int s = Seat(); if (s >= 0 && s < 16) links[s] = IntArg(a, 4, -1); return ""; }
                    case "getlink":   { int s = Seat(); return (s >= 0 && s < 16) ? links[s].ToString(CultureInfo.InvariantCulture) : "-1"; }
                    case "guestfree":
                        for (int s = 0; s < 16; s++)
                        {
                            if (links[s] == -1) continue;
                            // native GC: a guest object that no longer exists frees its seat (:381-405)
                            if (b.Guests[s] != 0 && !ctx.Entities.ContainsKey(b.Guests[s]))
                            { b.Guests[s] = 0; b.GuestOrders[s] = 0; }
                            if (b.Guests[s] == 0) return s.ToString(CultureInfo.InvariantCulture);
                        }
                        return "-1";
                    case "guestget":    { int s = Seat(); return (s >= 0 && s < 16) ? b.Guests[s].ToString(CultureInfo.InvariantCulture) : "0"; }
                    case "guestset":
                    {
                        int s = Seat();
                        if (s >= 0 && s < 16)
                        {
                            int r = IntArg(a, 4, 0);
                            if (r == 0) { b.Guests[s] = 0; b.GuestOrders[s] = 0; }   // clear seats AND order (:601-607)
                            else b.Guests[s] = r;
                        }
                        return "";
                    }
                    case "guestremove":
                    {
                        int r = IntArg(a, 3, 0);
                        for (int s = 0; s < 16; s++)
                            if (b.Guests[s] == r && r != 0)
                            { b.Guests[s] = 0; b.GuestOrders[s] = 0; return s.ToString(CultureInfo.InvariantCulture); }
                        return "1";   // native local_1c = 1 when no slot held the ref (:300-309)
                    }
                    case "addorder":    { int s = Seat(); if (s >= 0 && s < 16) b.GuestOrders[s] += 1; return ""; }
                    case "remorder":    { int s = Seat(); if (s >= 0 && s < 16) b.GuestOrders[s] = System.Math.Max(0.0, b.GuestOrders[s] - 1); return ""; }
                    case "resetorder":  { int s = Seat(); if (s >= 0 && s < 16) b.GuestOrders[s] = 0; return ""; }
                    case "getorder":    { int s = Seat(); return (s >= 0 && s < 16) ? ((int)b.GuestOrders[s]).ToString(CultureInfo.InvariantCulture) : "0"; }
                    case "nextorder":   { int best = -1; double bv = 0; for (int s = 0; s < 16; s++) if (b.Guests[s] != 0 && b.GuestOrders[s] > bv) { bv = b.GuestOrders[s]; best = s; } return best.ToString(CultureInfo.InvariantCulture); }
                    default: return "0";
                }
            });

            // StoryMgr exploration/occupancy grid + per-object callback registry. sm_map_get OOB
            // reads 1 = GROWN (FUN_005fa5d0:52-57) so the free-rect expansion stops at the borders.
            // sm_map_set <x> <y> <w> <h> — a RECT mark of 1s (FUN_00660110:179 → FUN_005fa510),
            // NOT (x,y,value): the old value-signature happened to coincide for story.tcl's only
            // shape `sm_map_set $orgx $orgy 1 1`.
            interp.RegisterCommand("sm_map_set", (i, a) =>
            { StoryMarkRect(ctx, IntArg(a, 1, 0), IntArg(a, 2, 0), IntArg(a, 3, 1), IntArg(a, 4, 1)); return ""; });
            interp.RegisterCommand("sm_map_get", (i, a) => StoryMapGet(ctx, IntArg(a, 1, 0), IntArg(a, 2, 0)).ToString(CultureInfo.InvariantCulture));
            interp.RegisterCommand("sm_register", (i, a) => { if (a.Length > 1 && ResolveEntityOrNull(ctx, a[1]) is { } e) ctx.StoryCallbacks[e.Handle] = a.Length > 2 ? a[2] : "callback"; return ""; });

            // ----- level generator — the lg_* world-growth engine the StoryMgr drives -----
            // The native lg_* placer + the sm_* zone layer, reimplemented in C# (LevelGenerator.cs). The
            // UNCHANGED Tcl (story.tcl, lginit.tcl, lgtools.tcl) configures + drives it; output = a
            // {tcl x y} placement list that flows into the already-working map_template stamping pipeline.
            // Tier (B) minimal grower (Docs/LEVEL_GENERATOR.md): seed + opposite-edge greedy fill.
            ctx.LevelGen = new Diggles.Runtime.World.LevelGenerator { DataRoot = ctx.DataRoot };
            var lg = ctx.LevelGen;
            int? LgOptInt(string[] a, string opt) { for (int k = 1; k + 1 < a.Length; k++) if (a[k] == opt && int.TryParse(a[k + 1], out var n)) return n; return null; }
            interp.RegisterCommand("lg_addfilterclass", (i, a) => { for (int k = 1; k < a.Length; k++) foreach (var c in TclList.Parse(a[k])) lg.AddFilterClass(c); return ""; });
            interp.RegisterCommand("lg_set_templategroup", (i, a) => { if (a.Length > 2) lg.SetTemplateGroup(a[1], TclList.Parse(a[2])); return ""; });
            interp.RegisterCommand("lg_set_templategroupprops", (i, a) => { if (a.Length > 1) lg.SetTemplateGroupProps(a[1], LgOptInt(a, "-levelcount")); return ""; });
            interp.RegisterCommand("lg_set_templateprops", (i, a) => { if (a.Length > 1) lg.SetTemplateProps(a[1], LgOptInt(a, "-levelcount"), LgOptInt(a, "-tunnel"), LgOptInt(a, "-gamecount")); return ""; });
            interp.RegisterCommand("lg_set_templategroupvalue", (i, a) => { if (a.Length > 2) lg.SetTemplateGroupValue(a[1], ParseFloat(a[2])); return ""; });
            interp.RegisterCommand("lg_set_templatevalue", (i, a) => { if (a.Length > 2) lg.SetTemplateValue(a[1], ParseFloat(a[2])); return ""; });
            interp.RegisterCommand("lg_get_temp_size", (i, a) => { if (a.Length < 2) return "0 0"; lg.DataRoot = ctx.DataRoot; var (w, h) = lg.GetTempSize(a[1]); return w + " " + h; });
            interp.RegisterCommand("lg_tp_clear", (i, a) => { lg.PoolClear(); return ""; });
            interp.RegisterCommand("lg_tp_addtemplatesets", (i, a) => { if (a.Length > 1) lg.PoolAddTemplateSets(a[1]); return ""; });
            interp.RegisterCommand("lg_tp_setfilter", (i, a) => { if (a.Length > 1) lg.PoolSetFilter(a[1].Trim()); return ""; });
            interp.RegisterCommand("lg_tp_objfilter", (i, a) => { if (a.Length > 1) lg.PoolObjFilter(TclList.Parse(a[1])); return ""; });
            interp.RegisterCommand("lg_set_objcount", (i, a) => { if (a.Length > 4) lg.SetObjCount(a[1], IntArg(a, 2, 0), IntArg(a, 3, 0), IntArg(a, 4, 1)); return ""; });
            interp.RegisterCommand("lg_set_templateratio", (i, a) => { lg.SetTemplateRatio(a.Length > 1 ? ParseFloat(a[1]) : 0f, a.Length > 2 ? ParseFloat(a[2]) : 0.1f, a.Length > 3 ? ParseFloat(a[3]) : 1f); return ""; });
            interp.RegisterCommand("lg_set_leveltype", (i, a) => { if (a.Length > 1) lg.SetLevelType(a[1]); return ""; });
            interp.RegisterCommand("lg_set_area", (i, a) => { if (a.Length > 4) lg.SetArea(IntArg(a, 1, 0), IntArg(a, 2, 0), IntArg(a, 3, 0), IntArg(a, 4, 0)); return ""; });
            interp.RegisterCommand("lg_add_preset", (i, a) =>
            {
                if (a.Length != 4)
                    throw new TclException("wrong # args: should be \"lg_add_preset template x y\"");
                lg.DataRoot = ctx.DataRoot;
                WireLgLiveMap(ctx, lg);
                lg.AddPreset(a[1], IntArg(a, 2, 0), IntArg(a, 3, 0));
                return "";
            });
            interp.RegisterCommand("lg_mark_area", (i, a) => "");
            interp.RegisterCommand("lg_set_starttemplatepro", (i, a) => { lg.DataRoot = ctx.DataRoot; return CmdLgSetStartTemplatePro(ctx); });
            interp.RegisterCommand("lg_start", (i, a) =>
            {
                lg.DataRoot = ctx.DataRoot;
                // ONE shared random stream (native: the game-wide MT19937, FUN_0049c670) —
                // per-run private fixed seeds regenerated the same caves every session.
                lg.Rng = ctx.StoryRng;
                WireLgLiveMap(ctx, lg);
                bool okStart = lg.Start();
                // A failed generation is otherwise silent — story.tcl's own `log` line (:213) is
                // gated behind the verbose trace, and the failcount retry just waits for the next
                // breakthrough. One console line per failed attempt names the reason (rect,
                // anchor, solver LastFailReason).
                if (!okStart)
                    Debug.Log($"[worldgen] lg_start FAILED: {lg.LastFailReason} " +
                              $"(rect {ctx.StoryRectX},{ctx.StoryRectY} {ctx.StoryRectW}x{ctx.StoryRectH} cells, " +
                              $"latch dir={ctx.StoryLatchDir} at ({ctx.StoryLatchX},{ctx.StoryLatchY}), utemp='{ctx.StoryUtemp}')");
                else
                    Debug.Log($"[worldgen] lg_start OK: {lg.LastLevel.Count} pieces, conns={lg.ConnectionCount} " +
                              $"(rect {ctx.StoryRectW}x{ctx.StoryRectH} cells @({ctx.StoryRectX},{ctx.StoryRectY}), dir={ctx.StoryLatchDir}, utemp='{ctx.StoryUtemp}')");
                return okStart ? "1" : "0";
            });
            interp.RegisterCommand("lg_get_level", (i, a) =>
            {
                long __perf = Diggles.Runtime.Diagnostics.PerfTrace.Now();
                string res = lg.GetLevel();
                Diggles.Runtime.Diagnostics.PerfTrace.LogIfSlow("worldgen.lg_get_level", __perf, 40);
                {   // [worldgen] does the EMITTED level actually contain a story cave? (the payoff check)
                    string sc = "none"; int np = 0;
                    foreach (var tr in TclList.Parse(res))
                    {
                        np++; var p = TclList.Parse(tr);
                        if (sc == "none" && p.Count > 0 && (p[0].Contains("unq_") || p[0].Contains("vodo") ||
                            p[0].Contains("einsiedler") || p[0].Contains("feen") || p[0].Contains("troll") || p[0].Contains("raetsel")))
                            sc = p[0];
                    }
                    Debug.Log($"[worldgen] LEVEL emitted: {np} pieces, storyCave={sc}");
                }
                // FUN_00652d10:276-279 — lg_get_level MARKS each emitted placement's story-cell
                // rect grown (FUN_005fa510). This is the anti-duplicate law: the next generation's
                // free rect can no longer overlap this level (the missing mark stacked duplicate
                // caves on every re-scan).
                bool anyPlacement = false;
                foreach (var (_, px, py, pw, ph) in lg.LastLevelRects())
                {
                    anyPlacement = true;
                    StoryMarkRect(ctx, StoryCell(px), StoryCell(py),
                                  (pw >> 2) + ((pw & 3) != 0 ? 1 : 0),
                                  (ph >> 2) + ((ph & 3) != 0 ? 1 : 0));
                }
                lg.ClearObjCounts();   // constraints are per-generation (define_obj_counts)
                // THE GROWTH RE-ARM (FUN_00652d10:73 local_30 = FUN_005f8080 — the STORYMGR
                // singleton, NOT the generator — and:280 `*(local_30+0x98) = 0` inside the
                // per-placement emit loop): a generation that EMITS placements zeroes the
                // StoryMgr's dig-pressure counter, so the next breakthrough needs a fresh
                // sm_set_digcount worth of digging. A FAILED generation (no placements) skips
                // the loop body ⇒ the weight survives and the very next dig re-latches — the
                // story.tcl failcount retry path. The old port never reset the weight (the
                // ":280 = objcount clear" misread): after the FIRST breakthrough every dig
                // stroke latched a new breakthrough ⇒ a new cave per swing, generations
                // marching away from the dwarf, and the multi-generation stamp bursts.
                if (anyPlacement)
                {
                    ctx.StoryDigWeight = 0;
                    // FUN_00652d10:84-93 — bump +0x50 for each emitted object-bearing placement, so
                    // an object-cave variant ships at most once per session (cross-generation variety).
                    lg.CommitSessionEmission();
                }
                return res;
            });
            interp.RegisterCommand("lg_sort_level", (i, a) =>
            {
                int sx = IntArg(a, 1, 0), sy = IntArg(a, 2, 0);
                var items = new System.Collections.Generic.List<(string, int, int)>();
                if (a.Length > 3) foreach (var tr in TclList.Parse(a[3])) { var t = TclList.Parse(tr); if (t.Count >= 3 && int.TryParse(t[1], out var x) && int.TryParse(t[2], out var y)) items.Add((t[0], x, y)); }
                return lg.SortLevel(sx, sy, items);
            });

            // `info` override: adds the `vars` subcommand the base TclBuiltins.Info lacks.
            // story.tcl's define_obj_counts gates its min-generation rules on
            // `[info vars mingen_$item] != ""` (story.tcl:77) and is called from generate_next
            // BETWEEN lg_set_starttemplatepro and lg_start (story.tcl:197) — the builtin's
            // `bad option "vars"` throw silently killed the whole growth generation there
            // (rect+utemp populated, zero templates ever stamped). Real Tcl `info vars pattern`
            // lists visible variables matching the glob; the game's only use is a literal name,
            // so name-exists ⇒ the name, else "" (glob patterns: no enumeration API — return "",
            // safe for the != "" gates). Other subcommands mirror TclBuiltins.Info exactly.
            interp.RegisterCommand("info", (i, a) =>
            {
                if (a.Length < 2) throw new TclException("wrong # args for info");
                switch (a[1])
                {
                    case "exist":
                    case "exists":
                        if (a.Length != 3) throw new TclException("wrong # args for info exists");
                        return i.VarExists(a[2]) ? "1" : "0";
                    case "vars":
                        if (a.Length < 3) return "";
                        return a[2].IndexOfAny(new[] { '*', '?', '[' }) < 0 && i.VarExists(a[2])
                            ? a[2] : "";
                    case "commands":
                    case "procs":
                        return "";
                    default:
                        throw new TclException($"bad option \"{a[1]}\" for info");
                }
            });

            // ----- sm_* StoryMgr layer — the real CStoryMgr growth chain -----
            // worldgen-wiring-re.md §2: DigNotify (in dig_apply) accumulates growth pressure and
            // latches breakthroughs; sm_scan consumes the latch; sm_get_level_info grows the free
            // rect + picks the unique temp; lg_set_starttemplatepro anchors the generation at the
            // dig. The UNCHANGED story.tcl drives all of it from its idle state.

            // sm_set_digcount <n> — the growth threshold (+0x9c, FUN_0065c750:233). Previously
            // misrouted into a conflated "StoryResolution" (the audit's stage-0 fix).
            interp.RegisterCommand("sm_set_digcount", (i, a) => { if (a.Length > 1) ctx.StoryDigThreshold = IntArg(a, 1, 5); return ""; });
            // sm_get_resolution — the CONSTANT 4 (FUN_0065ffb0:301 Tcl_NewIntObj(4)); the /4 is
            // hardwired all over the native StoryMgr. The old conflated 32 made every story.tcl
            // cell↔world conversion 8× off.
            interp.RegisterCommand("sm_get_resolution", (i, a) => "4");
            // sm_add_zone <name> — append a zone band (upper bound −1 = +inf until sm_set_zone).
            // Re-registering a name updates in place (a New-Game re-runs CampaignInit).
            interp.RegisterCommand("sm_add_zone", (i, a) =>
            {
                if (a.Length < 2) return "";
                foreach (var z in ctx.StoryZones) if (z.Name == a[1]) return "";
                ctx.StoryZones.Add(new DigglesTclContext.StoryZone { Name = a[1] });
                return "";
            });
            // sm_set_zone <idx> <y> — set zone idx's UPPER world-Y bound (story.tcl:240-244 shifts
            // them as the uebergang templates stamp). sm_force_zone <idx> — lookup floor (+0x84).
            interp.RegisterCommand("sm_set_zone", (i, a) =>
            {
                int zi = IntArg(a, 1, -1);
                if (zi >= 0 && zi < ctx.StoryZones.Count) ctx.StoryZones[zi].Bound = IntArg(a, 2, -1);
                return "";
            });
            interp.RegisterCommand("sm_force_zone", (i, a) => { ctx.StoryZoneFloor = IntArg(a, 1, 0); return ""; });
            // sm_get_zone <y> — band lookup (FUN_005fcdf0): zone i spans (bound[i−1], bound[i]],
            // bound −1 = +inf, clamped up by the force floor.
            interp.RegisterCommand("sm_get_zone", (i, a) =>
            {
                int y = a.Length > 1 ? Mathf.RoundToInt(ParseFloat(a[1])) : 0;
                int zi = StoryZoneIndex(ctx, y);
                return zi >= 0 && zi < ctx.StoryZones.Count ? ctx.StoryZones[zi].Name : "Urwald";
            });
            // sm_add_temp <name> [lo hi] [flags…] — unique-template registry (FUN_0065cba0).
            interp.RegisterCommand("sm_add_temp", (i, a) => CmdSmAddTemp(ctx, a));
            // sm_def_temp_group <name> <spec> — stone-drawing group tables (stored; sm_draw_stone
            // -utmat consumes them natively).
            interp.RegisterCommand("sm_def_temp_group", (i, a) => { if (a.Length > 2) ctx.StoryTempGroups[a[1]] = a[2]; return ""; });
            // sm_draw_stone — FUN_0065e090 (TCL:CTclGlobalMethods::SMDrawStone).
            interp.RegisterCommand("sm_draw_stone", (i, a) => CmdSmDrawStone(ctx, a));
            // sm_set_temp {{name x y} …} — record placements + mark matching registry temps used
            // (FUN_00660a80 → FUN_005fa320: pos + used flag; feeds -exclusive/-mindist + sm_get_temppos).
            interp.RegisterCommand("sm_set_temp", (i, a) =>
            {
                for (int k = 1; k < a.Length; k++)
                    foreach (var entry in TclList.Parse(a[k]))
                    {
                        var t = TclList.Parse(entry);
                        if (t.Count < 3 || !int.TryParse(t[1], out var px) || !int.TryParse(t[2], out var py)) continue;
                        string nm = StoryTempName(t[0]);
                        ctx.StoryPlacedTemps.Add((nm, px, py));
                        foreach (var rec in ctx.StoryTemps)
                        {
                            if (rec.Name == nm) { rec.Placed = true; rec.PlacedX = px; rec.PlacedY = py; continue; }
                            // COMPOSITE completion (FUN_005fa320's recursion into the registry
                            // record's group children): a unique registered as an
                            // sm_def_temp_group is "used" once ALL its sub-templates have
                            // placed — the level list carries only sub-template names, so
                            // without this the composite was re-picked on every later scan.
                            if (rec.Placed || !ctx.StoryTempGroups.TryGetValue(rec.Name, out var spec)) continue;
                            int total = 0; bool isChild = false;
                            foreach (var (tcl, _, _) in ParseTempGroupEntries(spec))
                            {
                                total++;
                                if (StoryTempName(tcl) == nm) isChild = true;
                            }
                            if (!isChild) continue;
                            rec.SeenChildren.Add(nm);
                            if (rec.SeenChildren.Count >= total)
                            { rec.Placed = true; rec.PlacedX = px; rec.PlacedY = py; }
                        }
                    }
                return "";
            });
            interp.RegisterCommand("sm_get_temppos", (i, a) =>
            {
                if (a.Length > 1)
                {
                    string nm = StoryTempName(a[1]);
                    foreach (var (name, px, py) in ctx.StoryPlacedTemps)
                        if (name == nm) return px + " " + py;
                }
                return "-1 -1";
            });
            // sm_mark_temparea <x> <y> <template> — story-map rect mark sized from the template
            // (FUN_00653350: name+".tcl" → size lookup FUN_00583bd0 → ceil to cells → FUN_005fa510).
            // THE boot-occupancy writer: Campaign.tcl first_level marks every hand stamp (:184) so
            // the free-rect expansion can never grow over the start caves.
            interp.RegisterCommand("sm_mark_temparea", (i, a) =>
            {
                if (a.Length < 4 || ctx.LevelGen == null) return "";
                int x = Mathf.RoundToInt(ParseFloat(a[1])), y = Mathf.RoundToInt(ParseFloat(a[2]));
                var (w, h) = StoryTempSize(ctx, a[3]);   // group-aware (composite extent)
                if (w <= 0 || h <= 0) { w = 16; h = 16; }   // unknown meta → conservative 4×4 cells
                int cw = (w >> 2) + ((w & 3) != 0 ? 1 : 0);
                int ch = (h >> 2) + ((h & 3) != 0 ? 1 : 0);
                int cx = StoryCell(x), cy = StoryCell(y);
                for (int iy = 0; iy < ch; iy++)
                    for (int ix = 0; ix < cw; ix++)
                        ctx.StoryMap[(cx + ix, cy + iy)] = 1;
                return "";
            });
            // sm_get_level_info <cx> <cy> — free-rect expansion + unique-temp pick (FUN_00661340 →
            // FUN_005fac90). SIDE EFFECTS (rect + utemp into ctx) feed lg_set_starttemplatepro.
            interp.RegisterCommand("sm_get_level_info", (i, a) => CmdSmGetLevelInfo(ctx, a));
            // sm_scan — the latch consumer (FUN_006616d0): no latch / zero coords / already consumed
            // → Tcl int 0 (NEVER "" — story.tcl:1123 gates on `$area_list != 0` and the expr
            // string-compare of "" vs 0 is TRUE, the audit's falsy hazard). Fresh latch → the
            // sign-rounded cell pair, consume-once (+0x94:= 1).
            interp.RegisterCommand("sm_scan", (i, a) =>
            {
                if (ctx.StoryLatchDir == 0 || ctx.StoryLatchX == 0 || ctx.StoryLatchY == 0
                    || ctx.StoryLatchConsumed) return "0";
                ctx.StoryLatchConsumed = true;
                return StoryCell(ctx.StoryLatchX) + " " + StoryCell(ctx.StoryLatchY);
            });

            // Native horizon/fog region registry (FUN_00642110/FUN_00642800/FUN_00642a10).
            // HorizonSet replaces the first record within one native unit of the new position,
            // clamps rounded RGB components to bytes, then appends the new ordered record.
            interp.RegisterCommand("horizon_set", (i, a) =>
            {
                if (a.Length != 7) return "0";
                Vector3 position = ParseVec3(a[1]);
                RemoveHorizonRegionAt(ctx, position);
                int red = Mathf.Clamp(NativeRound(ParseFloat(a[2]) * 255f), 0, 255);
                int green = Mathf.Clamp(NativeRound(ParseFloat(a[3]) * 255f), 0, 255);
                int blue = Mathf.Clamp(NativeRound(ParseFloat(a[4]) * 255f), 0, 255);
                ctx.HorizonRegions.Add(new HorizonRegionState(
                    position,
                    red,
                    green,
                    blue,
                    ParseFloat(a[5]),
                    ParseFloat(a[6])));
                return "";
            });
            interp.RegisterCommand("horizon_rem", (i, a) =>
            {
                if (a.Length == 2) RemoveHorizonRegionAt(ctx, ParseVec3(a[1]));
                return "";
            });
            interp.RegisterCommand("horizon_clear", (i, a) =>
            {
                ctx.HorizonRegions.Clear();
                return "";
            });

            // ----- autonomous work dispatch — native CProdPlanner port -----
            // The dispatcher service + the gnome_idle / placelock primitives the UNCHANGED Tcl work
            // states call. Registered before RegisterStubs so they win over the "0" stubs.
            ctx.ProdDispatcher = new Diggles.Runtime.Sim.ProdDispatcher(ctx);

            // ----- building placement mode (the native ghost-placement singleton DAT_0076a338) -----
            // EnterPlacement/Confirm/Cancel = FUN_00530e80/FUN_00531310; driven by the click layer
            // (ClickToMoveService) and the authored building-window UI. Engine-owned so the
            // hide_obj_ghost native below (and headless tests) reach it without a scene.
            ctx.Placement = new Diggles.Runtime.PlacementController(ctx);

            // hide_obj_ghost <obj> — remove the persistent ORDER GHOST standing at the confirmed
            // placement spot (TCL:CTclGlobalMethods::HideObjGhost FUN_0069b440:58 → list remove
            // FUN_0046a960; the list lives on the placement singleton +0xf1, added at confirm by
            // FUN_0046a880, FUN_00531310:225). The construction chain clears it when the real
            // building appears (prod_buildup z_work_common.tcl:416, unpackfrombox
            // genericprod.tcl:204), and the order-cancel path does too (prod_buildup_check failure,
            // z_work_common.tcl:364). Was a "0" stub — order ghosts would have leaked forever.
            interp.RegisterCommand("hide_obj_ghost", (i, a) =>
            {
                if (a.Length >= 2 && ResolveEntityOrNull(ctx, a[1]) is { } e)
                    ctx.Placement?.HideOrderGhost(e.Handle);
                return "";
            });

            // get_place_info — "placing green asbox" (TCL:CTclGlobalMethods::GetPlaceInfo
            // FUN_0064c1e0: a 3-bool list from DAT_00772400+0xa3e0 placing / +0xa3e2 green /
            // +0xa3e1 asbox — written by the placement enter FUN_00530e80:89 and the per-frame
            // tick epilogue FUN_0046b450:2586-2587). The tutorial gates on "1 0 0" (ghost on the
            // mouse, no valid spot yet), "1 1 0" (green building spot) and "1 1 1" (box-only
            // spot), Trigger_Tutorial.tcl:1320-1322. Was a "0" stub — the tutorial's placement
            // beats could never advance.
            interp.RegisterCommand("get_place_info", (i, a) =>
            {
                var p = ctx.Placement;
                if (p == null || !p.Active) return "0 0 0";
                return $"1 {(p.Valid ? 1 : 0)} {(p.AsBox ? 1 : 0)}";
            });

            // gnome_idle <obj> 1|0 → register/unregister availability (CProdPlanner::GnomeIdle).
            interp.RegisterCommand("gnome_idle", (i, a) =>
            {
                var e = ResolveEntityOrNull(ctx, a.Length > 1 ? a[1] : "this");
                bool on = a.Length > 2 && ParseTclBool(a[2]);
                if (on) ctx.ProdDispatcher?.OnGnomeIdle(e); else ctx.ProdDispatcher?.OnGnomeBusy(e);
                return "";
            });
            // gnome_failed_work / gnome_stopped_work <obj> → release reservation + cooldown so the job re-opens.
            interp.RegisterCommand("gnome_failed_work", (i, a) =>
            { ctx.ProdDispatcher?.ReleaseGnome(ResolveEntityOrNull(ctx, a.Length > 1 ? a[1] : "this")); return ""; });
            interp.RegisterCommand("gnome_stopped_work", (i, a) =>
            { ctx.ProdDispatcher?.ReleaseGnome(ResolveEntityOrNull(ctx, a.Length > 1 ? a[1] : "this")); return ""; });
            // gnome_announce_dig <obj> {pos} — the digging gnome CLOSES its tile to further
            // dispatch. Native (FUN_0067a450:100-117): writes the per-player announce record
            // {round(x), round(y), strength 0x32 = 50}; the planner's dig-job builder decays it
            // 1.0/s and forces the tile's job need to 0 while it is positive (FUN_00435f50:53-54,
            //:142-147) — so a tile someone is walking to / digging at never re-dispatches
            // (dig_starttask announces at take-up, z_dignwalk.tcl:143, and the dig loop re-stamps
            // every cycle,:236/:313). NOTE this does NOT touch the dig MARK nibble — the old
            // port's mark re-stamp resurrected marks the carve's completion sweep had cleared
            // (the dig never finished); the mark lifecycle stays owned by dig_mark + CarveStep.
            interp.RegisterCommand("gnome_announce_dig", (i, a) =>
            {
                if (a.Length >= 3 && ctx.ProdDispatcher != null)
                {
                    var p = ParseVec3(a[2]);
                    ctx.ProdDispatcher.AnnounceDig(Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.y));
                }
                return "";
            });
            // prod_gnome_state <obj> <state> [...] (FUN_006a7230) — the engine's per-gnome work
            // disposition the Tcl writes every tick ("idle"/"sparetime"/"dig"/"walk"/…). Stored on
            // the entity; "idle" additionally marks the gnome available (the native byte=1 →
            // CProdPlanner pool, same as gnome_idle). Was a bulk "0" stub (sparetime-idle.md gap 2).
            interp.RegisterCommand("prod_gnome_state", (i, a) =>
            {
                var e = a.Length > 1 ? ResolveEntityOrNull(ctx, a[1]) : ctx.This;
                if (e == null || a.Length < 3) return "";
                e.ProdState = a[2];
                // FUN_006a7230 availability law: ONLY "idle" sets the planner record's
                // availability byte rec+4 = 1 (:109); EVERY other disposition clears it — the
                // inline cases (guard:152, fight:175, putdown:262, pickup:304, explore:390)
                // write 0 directly, the routed ones zero it in their record fills ("unknown"
                // FUN_005ebdf0:10, sparetime FUN_005ebe50, harvest FUN_005ec2e0:17 + code 0xd
                //:34, walk/build/invent/pack/unpack/transport/dig FUN_005ebeb0..005ec3a0). This
                // is the MANUAL work chains' only planner notification (evt_task_harvest_proc
                // z_events.tcl:935, mine:977, dig:1054, …) — without the busy mark the sweep
                // stole a manually-ordered gnome whose pool entry was still live. Busy = pool
                // removal only (OnGnomeBusy) — plans are untouched, matching the
                // native record fill which never tears down CProdPlanner plans.
                if (a[2] == "idle") ctx.ProdDispatcher?.OnGnomeIdle(e);
                else ctx.ProdDispatcher?.OnGnomeBusy(e);
                // "unpack <item> <pos>" (evt_task_buildup_proc, z_events.tcl:891): native
                // prod_gnome_state only fills the gnome's HUD/occupation record (state code 0xb +
                // target + pos, FUN_006a7230 → FUN_005ec140) — the REAL claim (rec+0x24) is written
                // at DISPATCH by FUN_005ede50:17, and the carrier path never claims at all. Kept as
                // documented port glue (buildup-carrier-re.md §9.4): it stamps CurrentWorkerHandle
                // for the evt_task carrier path so diagnostics/tests can see who is building;
                // since the auto-arm fan-out is gone, nothing COLLECTS on this claim anymore.
                if (a[2] == "unpack" && a.Length > 3 && ResolveEntityOrNull(ctx, a[3]) is { } wp)
                    wp.CurrentWorkerHandle = e.Handle;
                return "";
            });
            // prod_gnomeidle <obj> [bool] — the second idle announce; same pool as gnome_idle.
            interp.RegisterCommand("prod_gnomeidle", (i, a) =>
            {
                var e = ResolveEntityOrNull(ctx, a.Length > 1 ? a[1] : "this");
                bool on = a.Length < 3 || ParseTclBool(a[2]);
                if (on) ctx.ProdDispatcher?.OnGnomeIdle(e); else ctx.ProdDispatcher?.OnGnomeBusy(e);
                return "";
            });

            // placelock_set {pos} dur [id] / placelock_check {pos} radius [exceptId] / placelock_rem id —
            // the native PlaceFinder reservation list so two gnomes don't target the same spot.
            interp.RegisterCommand("placelock_set", (i, a) =>
            {
                if (a.Length >= 3)
                    ctx.PlaceLocks.Set(ParseVec3(a[1]), ParseFloat(a[2]), a.Length > 3 ? IntArg(a, 3, 0) : 0,
                        ctx.Sim?.GameTime ?? 0.0);
                return "";
            });
            interp.RegisterCommand("placelock_check", (i, a) =>
            {
                if (a.Length < 3) return "0";
                float r = ParseFloat(a[2]);
                int except = a.Length > 3 ? IntArg(a, 3, 0) : -1;
                return ctx.PlaceLocks.Check(ParseVec3(a[1]), r, except, ctx.Sim?.GameTime ?? 0.0) ? "1" : "0";
            });
            interp.RegisterCommand("placelock_rem", (i, a) => { if (a.Length > 1) ctx.PlaceLocks.Rem(IntArg(a, 1, 0)); return ""; });

            // ----- bulk stubs (deferred to the end so they don't overwrite
            // any explicit registrations above) -----
            RegisterStubs(interp, GetBulkStubNames());
            RegisterPlusVariants(interp);
            RegisterMinusVariants(interp);

            return ctx;
        }

        // Per-building seat→model-link table for prod_guest (the native 16-slot prod record at
        // building+0x3e0+0x6c, FUN_006a8920 — see the prod_guest registration). Stored on
        // DigglesTclEntity.GuestLinks so save restore and live commands share one table.
        // -1 = slot not claimed by addlink yet (the native's unused marker).
        // get_place -placelock auto-lock ids: the native LockPlace appends a NEW record when the
        // id is −1 (FUN_005aa900 skips the upsert search for −1); the port's PlaceLockStore.Set
        // replaces by id, so each auto-lock takes a fresh negative id from this sequence.
        static int s_placeLockSeq = -100;

        static int[] GuestLinks(DigglesTclEntity b)
        {
            // The seat->link table lives ON the entity so the save restore path
            // (OriginalSaveRuntimeSessionStager) and live prod_guest share one source of truth;
            // the former ConditionalWeakTable side store made restored links invisible here.
            var links = b.GuestLinks;
            if (links == null || links.Length < DigglesTclEntity.GuestSeatCount)
            {
                links = new int[DigglesTclEntity.GuestSeatCount];
                for (int s = 0; s < links.Length; s++) links[s] = -1;
                b.GuestLinks = links;
            }
            return links;
        }

        // ===== CStoryMgr world growth (worldgen-wiring-re.md) =====

        // Static int-arg helper (Register's IntArg is a local function, unreachable from here).
        static int StoryInt(string[] a, int idx, int def) =>
            idx < a.Length && int.TryParse(a[idx], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : def;

        // Sign-rounded tile→story-cell division by 4: (v + ((v>>31)&3)) >> 2 (the idiom used at
        // FUN_005fd060:71-72, FUN_006616d0:110/120 and every other cell conversion).
        static int StoryCell(int v) => (v + ((v >> 31) & 3)) >> 2;

        // FUN_005fa9a0 — the FREE-RECT FINDER (extracted so both sm_get_level_info and the
        // FUN_005fc0a0 re-placement share ONE faithful copy). Grows the seed (w,h) to the target
        // inside the raw free rect [minX,maxX]×[minY,maxY] (native +0x58/+0x5c/+0x60/+0x64), then
        // POSITIONS the rect to CONTAIN the point (px,py) — the scan cell for the initial fit, the
        // orientation X-hint + cursor Y for the FUN_005fc0a0 re-placement. In/out: px/py = the
        // containment point → the placed origin; w/h = seed → grown. Steps cite FUN_005fa9a0 lines.
        // • targetW = min(rawW, max(16, w+8)) (:25-35), targetH = min(rawH, max(8, h)) (:36-45).
        // • grow w to min(targetW,24) then a rand remainder (:52-60); grow h to min(targetH,8) + rand (:64-74).
        // • feasible band to contain (px,py) within the raw rect: xMin=max(minX,px-w+1),
        // xMax=min(px,maxX-w) (:83-92); yMin/yMax the same in Y (:93-102).
        // • mode 1 = case 1 fit: centered WITHOUT a utemp, RANDOM with one (+0x54!=-1 diverts to
        // case 0,:110/:136-165). mode 4 = case 4: clamp the given (px,py) hint into the band
        // (:145-160) — used by FUN_005fc0a0 for up/down. Returns false on a degenerate raw rect
        // (:46-51). The rand style (System.Random.Next over the band, clamped non-inverted first)
        // is the port's existing one — it matches the native's collapsed/inverted-by-1 bands (a
        // dig-side edge) exactly and its draw COUNT preserves the shared story RNG stream.
        static bool FreeRectFit(int minX, int minY, int maxX, int maxY,
            ref int px, ref int py, ref int w, ref int h, int mode, bool hasUtemp, System.Random rng)
        {
            int rw = maxX - minX, rh = maxY - minY;
            if (rw <= 0 || rh <= 0) return false;                                     //:46-51 degenerate
            int targetW = System.Math.Min(rw, System.Math.Max(16, w + 8));            //:25-35
            int targetH = System.Math.Min(rh, System.Math.Max(8, h));                 //:36-45
            while (w < targetW && w < 24) w++;                                         // grow, cap 0x18 (:52-57)
            if (w < targetW) w += rng.Next(targetW - w);                              // rand remainder (:58-60)
            while (h < targetH && h < 8) h++;                                          // grow, cap 8 (:64-69)
            if (h < targetH) h += rng.Next(targetH - h);                              // (:70-74)
            int xMin = System.Math.Max(minX, px - w + 1), xMax = System.Math.Min(px, maxX - w);   //:83-92
            int yMin = System.Math.Max(minY, py - h + 1), yMax = System.Math.Min(py, maxY - h);   //:93-102
            if (xMax < xMin) xMax = xMin;
            if (yMax < yMin) yMax = yMin;
            if (mode == 4)                                                             // case 4: clamp hint (:145-160)
            {
                px = px < xMin ? xMin : (px > xMax ? xMax : px);
                py = py < yMin ? yMin : (py > yMax ? yMax : py);
            }
            else if (hasUtemp)                                                         // mode1 + utemp → case 0 random (:110)
            {
                px = xMin + rng.Next(xMax - xMin + 1);
                py = yMin + rng.Next(yMax - yMin + 1);
            }
            else                                                                       // case 1 centered (:136-165)
            {
                px = (xMax - xMin) / 2 + xMin;
                py = (yMax - yMin) / 2 + yMin;
            }
            return true;
        }

        // sm_def_temp_group spec → the composite's {tcl, dx, dy} entries. Row shape:
        // `{<dx> <dy> <subTemplate>}` (CampaignInit.tcl:100-140); non-numeric rows (the `mat`
        // stone-paint tables) are skipped.
        static List<(string tcl, int dx, int dy)> ParseTempGroupEntries(string spec)
        {
            var outList = new List<(string, int, int)>();
            foreach (var row in TclList.Parse(spec))
            {
                var f = TclList.Parse(row);
                if (f.Count < 3) continue;
                if (!int.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int dx)
                    || !int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int dy))
                    continue;
                outList.Add((f[2] + ".tcl", dx, dy));
            }
            return outList;
        }

        // sm_def_temp_group `mat` rows (FUN_0065b180:216-361, CTclGlobalMethods::SMDefTempGroup): a row
        // whose FIRST token is `mat` (:216 strcmp vs "mat") carries one or more 5-tuples {type x y w h}
        // (composite-LOCAL tiles; CampaignInit.tcl e.g. `{mat {0 28 24 80 8} {0 28 32 8 28} …}`). The
        // native stores each as a group rect {x, y, x+w, y+h, type} — CORNERS, extent→corner done at
        // parse (:339-340 uVar11=x+w/uVar10=y+h;:354-361 offsets +0x00..+0x10). Only TYPE-0 rects are
        // later forwarded to the CLevelGenerator +0x17c mark-area / keep-clear list (FUN_00650f90:424 —
        // the type field, rect+0x10, gated == 0) and stamped into the placement grid (FUN_00589790:156).
        // Returns composite-local corner rects [x0,y0,x1,y1,type]; the caller offsets by the composite
        // grid origin and keeps type==0. (This is an OCCUPANCY reservation, NOT terrain.)
        public static List<(int x0, int y0, int x1, int y1, int type)> ParseTempGroupMatRects(string spec)
        {
            var outList = new List<(int, int, int, int, int)>();
            foreach (var row in TclList.Parse(spec))
            {
                var f = TclList.Parse(row);
                if (f.Count < 2 || f[0].Trim() != "mat") continue;                 //:216 first token == "mat"
                for (int i = 1; i < f.Count; i++)                                  //:239 from sub-index 1
                {
                    var t = TclList.Parse(f[i]);
                    if (t.Count < 5) continue;
                    if (!int.TryParse(t[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ty)) continue;
                    if (!int.TryParse(t[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)) continue;
                    if (!int.TryParse(t[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)) continue;
                    if (!int.TryParse(t[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int w)) continue;
                    if (!int.TryParse(t[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h)) continue;
                    outList.Add((x, y, x + w, y + h, ty));                         // {x, y, x+w, y+h, type} (:354-361)
                }
            }
            return outList;
        }

        // Extent of a unique temp — the composite's bounding box when the name is an
        // sm_def_temp_group (the native size table FUN_00583bd0 covers groups), else the
        // single template's PMP dims.
        static (int w, int h) StoryTempSize(DigglesTclContext ctx, string name)
        {
            if (ctx.LevelGen == null) return (0, 0);
            ctx.LevelGen.DataRoot = ctx.DataRoot;
            if (ctx.StoryTempGroups.TryGetValue(StoryTempName(name), out var spec))
            {
                int w = 0, h = 0;
                foreach (var (tcl, dx, dy) in ParseTempGroupEntries(spec))
                {
                    var (tw, th) = ctx.LevelGen.GetTempSize(tcl);
                    w = System.Math.Max(w, dx + tw);
                    h = System.Math.Max(h, dy + th);
                }
                return (w, h);
            }
            return ctx.LevelGen.GetTempSize(StoryTempName(name) + ".tcl");
        }

        // FUN_005f7660 — the utemp's CONNECT-DOOR absolute X within the composite, in TILES; drives the
        // FUN_005fc0a0 up/down orientation (FUN_005fc010) + X hint. A composite (sm_def_temp_group) has
        // record +0x3c = the GROUP index, set AT REGISTRATION (FUN_00650f90:364-365 uses +0x3c==-1 as the
        // single-template test, so at THIS call site — before lg_start — a composite's +0x3c is already a
        // valid group idx, NOT a placed-instance ref), so FUN_005f7660:42-88 resolves the connect
        // sub-piece and returns x = subpieces[a].dx + door[b].x_of_ref (a = -digconnect arg0 / the
        // native group+0x24 default 0; b = arg1). ⚠ FLAG — native DEFAULT reproduced, not invented: the
        // intra-piece door pixel (door[b].x of the fixed &DAT_0072555c reference template,
        // FUN_005f7660:90-114) is UNMODELED — we return the connect sub-piece's ORIGIN dx, aligning that
        // sub-piece's origin (≤ one piece-width off the exact door pixel) at the cursor. A single template
        // (+0x3c==-1 → FUN_005f7660:42 skipped, no sub-offset) returns 0 → doorX<5 → utDir 3 →
        // left-edge≈cursor (identical to the prior utDir-3-only approximation for singles).
        static int StoryUtempConnectDoorX(DigglesTclContext ctx, string name)
        {
            if (!ctx.StoryTempGroups.TryGetValue(StoryTempName(name), out var spec)) return 0;
            var subs = ParseTempGroupEntries(spec);
            if (subs.Count == 0) return 0;
            int a = 0;                                    // native group+0x24 default (no -digconnect)
            var rec = ctx.StoryTemps.Find(t => t.Name == name);
            if (rec != null && rec.DigConnect.Length > 0)
            {
                var tok = rec.DigConnect.Split(' ');
                if (tok.Length > 0 && int.TryParse(tok[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var av))
                    a = av;
            }
            if (a < 0 || a >= subs.Count) a = 0;
            return subs[a].dx;
        }

        // -mindist gate (FUN_005fac90:145+:187-191): the native distance term is degenerate, so in
        // practice the candidate is eligible only once its reference temp is already placed.
        static bool RefPlaced(DigglesTclContext ctx, string refName)
        {
            foreach (var t in ctx.StoryTemps)
                if (t.Name == refName && t.Placed) return true;
            return false;
        }

        // sm_draw_stone — FUN_0065e090 (TCL:CTclGlobalMethods::SMDrawStone; branch strings
        // -border@0x731060 / -funnel@0x731068 / -exlimit@0x731088 / -utmat@0x731094).
        // IMPLEMENTED: `-border` (:930-941) — THE WORLD-BORDER DIG STOP. Three probabilistic
        // stone-class-0 band paints via FUN_005fc9d0 over the top/left/right map edges:
        // top (0,0)–(W,32) falloff flag 1, strength 2.5
        // left (0,0)–(32,H+4) falloff flag 4, strength 1.0
        // right (W−32,0)–(W,H+4) falloff flag 8, strength 1.0
        // (W = SM+0x7c<<2 = 512, H+4 = SM+0x80*4+4 = 644 on the campaign map; NO bottom band —
        // the bottom is covered by the ±1-tile marker bounds only, FUN_00566870:19-22.) With
        // strength 1.0 over a 32-tile band the outer ~16 tiles are solid class 0 and the paint
        // tapers quadratically to 0 at ~29 tiles in — a teleported gnome digging toward the
        // border stops at the ragged band edge, never reaching the void. Class 0 is enforced at
        // marker validity (FUN_00566870:26-28), both painters (FUN_005672d0:41-46 /
        // FUN_005671a0:33-38) and the carve precondition (FUN_005976b0:36-38) — all already
        // wired through DiggableTerrain.TileMaterialDiggable; the native's fourth gate (the
        // fog-reveal marker erase, CFogOfWar::NewVisibleBlock FUN_0057dfc0:44-48) is subsumed
        // by the no-fog port enforcing class everywhere. Each band also MARKS its story-map
        // rect grown (FUN_005fc9d0:139-141 → FUN_005fa510) so growth never generates into the
        // border. Virgin rock elsewhere stays class 2 = diggable (FUN_00598f10:154) — verified
        // native behavior, only the band stops border digging.
        // Campaign's -matlist branch is also implemented (FUN_0065e090:190-206): its list begins
        // with literal `mat`, followed by {type x y w h} records. Each record is translated by the
        // supplied world offset and widened +2 on its far edge before FUN_005fc9d0. The other four
        // gameplay branches are -border (:930-941), -funnel (:213-237), -exlimit (:273-395), and
        // -utmat (:432-496). Each drives FUN_005fc9d0 (the port's faithful DrawStoneIntoWorld) — which
        // repaints ONLY class-1/2 tiles (class 0=stone and 3 survive, so an already-carved cave is
        // preserved by the class check, making FUN_005fc9d0's inner-rect args:63-64 vestigial for
        // these calls) and marks each painted rect grown (:139-141). Only -rect (LevelEditor paint)
        // remains a no-op.
        static string CmdSmDrawStone(DigglesTclContext ctx, string[] a)
        {
            if (a.Length < 2) return "";
            if (ctx.MapBuilder is not Diggles.Runtime.World.RuntimeMapBuilder rmb) return "";
            int W = ctx.StoryMapW * 4, H = ctx.StoryMapH * 4;
            if (W <= 0 || H <= 0) return "";
            // FUN_005fc9d0: paint stone class `cls` over [x0,x1)×[y0,y1) with the `flags` falloff +
            // `strength`, then mark the rect grown (:139-141; painter clamps first,:28-38).
            void Band(int x0, int y0, int x1, int y1, uint flags, float strength, int cls = 0)
            {
                rmb.DrawStone(x0, y0, x1, y1, flags, cls, strength, ctx.StoryRng);
                int cx0 = System.Math.Max(0, x0), cy0 = System.Math.Max(0, y0);
                int cx1 = System.Math.Min(W, x1), cy1 = System.Math.Min(H, y1);
                StoryMarkRect(ctx, cx0 >> 2, cy0 >> 2, (cx1 - cx0) >> 2, (cy1 - cy0) >> 2);
            }
            switch (a[1])
            {
                case "-matlist": // Campaign.tcl: sm_draw_stone -matlist 16 16 $nexttemp
                {
                    // FUN_0065e090:190-206: argv = option, offsetX, offsetY, mat-list;
                    // a valid list starts with `mat`, then carries {type x y w h} records.
                    if (a.Length < 5 ||
                        !int.TryParse(a[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int offsetX) ||
                        !int.TryParse(a[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int offsetY))
                    {
                        return "";
                    }

                    var records = TclList.Parse(a[4]);
                    if (records.Count == 0 || !string.Equals(records[0], "mat", System.StringComparison.Ordinal))
                        return "";

                    for (int index = 1; index < records.Count; index++)
                    {
                        var record = TclList.Parse(records[index]);
                        if (record.Count < 5 ||
                            !int.TryParse(record[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int type) ||
                            !int.TryParse(record[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) ||
                            !int.TryParse(record[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y) ||
                            !int.TryParse(record[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int width) ||
                            !int.TryParse(record[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int height))
                        {
                            continue;
                        }

                        Band(offsetX + x, offsetY + y,
                            offsetX + x + width + 2, offsetY + y + height + 2,
                            0, 2.5f, cls: type);
                    }
                    return "";
                }

                case "-border":   // THE WORLD-BORDER DIG STOP (:926-943)
                    Band(0, 0, W, 32, 1, 2.5f);           // top (:930-933)
                    Band(0, 0, 32, H + 4, 4, 1.0f);       // left (:934-937)
                    Band(W - 32, 0, W, H + 4, 8, 1.0f);   // right (:938-941)
                    // additionally mark the TOP 9 CELL ROWS grown full-width (:943) — no surface growth.
                    StoryMarkRect(ctx, 0, 0, ctx.StoryMapW, 9);
                    return "";

                case "-funnel":   // the surface START funnel (:213-237): two shrinking stone triangles
                {
                    // local_20 = y (16, +4/row); local_14 = band width (Wcells*2−20, −8/row, while >0).
                    // Per row [y, y+6]: RIGHT band [W−bw, W] falloff 8, LEFT band [0, bw] falloff 4,
                    // both class 0 strength 1.0 (:226-234).
                    int y = 16, bw = ctx.StoryMapW * 2 - 20;
                    while (bw > 0)
                    {
                        Band(W - bw, y, W, y + 6, 8, 1.0f);   // right (:228)
                        Band(0, y, bw, y + 6, 4, 1.0f);       // left (:231)
                        y += 4; bw -= 8;
                    }
                    return "";
                }

                case "-exlimit":  // post-growth UNDIGGABLE stone RIM around the just-grown cave (:273-395)
                {
                    // The native reads the SELECTED unique-temp record (SM+0x54) placed rect
                    // (temp+0x48/+0x4c origin + temp+0x30/+0x34 size) and paints a FULL-WIDTH band
                    // [0,W] × [caveY0, caveY1] falloff 3, class 0, strength 2.0 (:391-395). The
                    // class-1/2-only repaint leaves the carved cave (class 0/3) intact, so the band
                    // becomes an undiggable rim at the cave's depth. Port: the utemp's placed WORLD rect
                    // stored by lg_set_starttemplatepro (StoryUtempWorld*). No utemp placed → no-op.
                    if (!ctx.StoryUtempPlaced) return "";
                    // DISABLED: the full-width band below — faithful to the native
                    // FUN_005fc9d0(0, caveY0, mapW*4, caveY1, falloff 3, class 0, strength 2.0)
                    // call (byte-verified) — blocks progress past the story cave in practice. The
                    // original leaves a small diggable pocket (roughly 1-2 blocks near one corner)
                    // from the Y-falloff edge plus the carved cave/corridor surviving the
                    // class-1/2-only repaint; that exact pocket is a position-dependent interaction
                    // not yet reproduced headless. Un-blocking beats a faithful-but-blocking rim;
                    // re-enable and tune the pocket against the original later. (-border world
                    // edges + -utmat material are unaffected.)
                    // if (ctx.StoryUtempPlaced)
                    // Band(0, ctx.StoryUtempWorldY, W, ctx.StoryUtempWorldY + ctx.StoryUtempExtH, 3, 2.0f);
                    return "";
                }

                case "-utmat":    // the unique cave's per-rect SCAPE MATERIAL (:432-496)
                {
                    // Paint each of the composite's `mat` rects (group+0x0c list) at the utemp's placed
                    // WORLD origin with matClass = the rect's TYPE (rect+0x10), falloff 0, strength 2.5,
                    // x1/y1 +2 (:492). The type IS the scape/stone class per rect (0 = the zone's default
                    // ground, non-0 = lava/sulfur/etc.). ⚠ APPROXIMATION (flagged): the native resolves
                    // the class through an SM+0x10 material table (:468-473) that is un-RE'd — the port
                    // uses the raw type index, exact for the common type-0 case. No utemp placed / no
                    // group spec → no-op.
                    if (!ctx.StoryUtempPlaced) return "";
                    if (!ctx.StoryTempGroups.TryGetValue(ctx.StoryUtempPlacedName, out var matSpec)) return "";
                    foreach (var (mx0, my0, mx1, my1, mtype) in ParseTempGroupMatRects(matSpec))
                        Band(ctx.StoryUtempWorldX + mx0, ctx.StoryUtempWorldY + my0,
                             ctx.StoryUtempWorldX + mx1 + 2, ctx.StoryUtempWorldY + my1 + 2,
                             0, 2.5f, cls: mtype);         // (:492) falloff 0, class = type, strength 2.5
                    return "";
                }

                default: return "";   // -rect (LevelEditor) unhandled
            }
        }

        // FUN_005fa510 — story-map RECT mark (cells:= 1) with the native's whole-rect bounds
        // gate: if ANY part of the rect is outside the sized grid, NOTHING is marked (:18-19).
        // Unsized grid (tests/menu) → mark unconditionally into the dictionary.
        static void StoryMarkRect(DigglesTclContext ctx, int cx, int cy, int cw, int ch)
        {
            if (ctx.StoryMapW > 0
                && (cx < 0 || cy < 0 || cx + cw > ctx.StoryMapW || cy + ch > ctx.StoryMapH)) return;
            for (int iy = 0; iy < ch; iy++)
                for (int ix = 0; ix < cw; ix++)
                    ctx.StoryMap[(cx + ix, cy + iy)] = 1;
        }

        // Story-map read with the native OOB contract: outside the sized grid counts as GROWN (=1,
        // FUN_005fa5d0:52-57 returns 1 out of bounds) so the free-rect expansion and the DigNotify
        // neighbour probes stop at the map border. Unsized grid (tests/menu) → dictionary only.
        static int StoryMapGet(DigglesTclContext ctx, int cx, int cy)
        {
            if (ctx.StoryMapW > 0 && (cx < 0 || cy < 0 || cx >= ctx.StoryMapW || cy >= ctx.StoryMapH))
                return 1;
            return ctx.StoryMap.TryGetValue((cx, cy), out var v) ? v : 0;
        }

        // Zone band lookup (FUN_005fcdf0): zone i spans (bound[i−1], bound[i]] with bound −1 = +inf;
        // result clamped up by the sm_force_zone floor (+0x84).
        static int StoryZoneIndex(DigglesTclContext ctx, int y)
        {
            if (ctx.StoryZones.Count == 0) return -1;
            int zi = 0;
            for (int k = 0; k < ctx.StoryZones.Count; k++)
            {
                int lo = k > 0 ? ctx.StoryZones[k - 1].Bound : 0;
                int hi = ctx.StoryZones[k].Bound;
                if (hi == -1) hi = int.MaxValue;
                if (lo <= y && y <= hi) { zi = k; break; }
                zi = k;
            }
            return zi < ctx.StoryZoneFloor ? ctx.StoryZoneFloor : zi;
        }

        // The zone-relative window base for the utemp pick: zone i's LOWER bound = zone[i−1].Bound
        // (0 for the first zone / unset bounds). NOTE: the decompile of FUN_005fac90:122-127 reads
        // `*(Get(zi)+4)` which is literally the zone's UPPER bound entry — but only the LOWER bound
        // is consistent with both FUN_005fcdf0's own band reads (lower = entry[i−1].+4) and the
        // CampaignInit windows (swf_unq_lore 0-32 must ramp from the METALL zone's start, not from
        // an unset −1) — treated as a decompiler index artefact.
        static int StoryZoneBase(DigglesTclContext ctx, int y)
        {
            int zi = StoryZoneIndex(ctx, y);
            return zi > 0 && zi - 1 < ctx.StoryZones.Count
                ? System.Math.Max(0, ctx.StoryZones[zi - 1].Bound) : 0;
        }

        // Registry temp names are stored WITHOUT the.tcl suffix (sm_add_temp urw_der_einsiedler vs
        // the lg level entries urw_xxx.tcl) — normalize both directions here.
        static string StoryTempName(string s)
        {
            s = s.Trim().Trim('{', '}', '"');
            return s.EndsWith(".tcl", System.StringComparison.OrdinalIgnoreCase)
                ? s.Substring(0, s.Length - 4) : s;
        }

        // The DigNotify nibble gate (FUN_005fd060:75-77): the owner's tile+0x08 nibble must be set
        // with bit3 CLEAR — i.e. a mode-1 "dummy"/standard-tunnel mark (3, FUN_005672d0:61). Mode-0
        // depth orders write cnt|8 (FUN_005671a0:50, bit3 SET) and virgin rock is 0 — both FAIL.
        static bool StoryDigNibbleGate(Diggles.Runtime.World.DiggableTerrain T, int tx, int ty, int player)
        {
            int n = T.RawDigNibble(tx, ty, player);
            return (n & 8) == 0 && (n & 0xF) != 0;
        }

        // sm_add_temp <name> [lo hi] [-exclusive N] [-zoneborder Z] [-stoneidx I] [-mindist T V]
        // [-middist V] [-nonrelevant P] [-digconnect A B] [-connect L] [-checkalign N]
        // [-events …] — FUN_0065cba0. Window lo/hi = zone-relative Y-DEPTH tile bands
        // (correction #6: NOT "digcount windows"). Re-registering a name replaces the record.
        static string CmdSmAddTemp(DigglesTclContext ctx, string[] a)
        {
            if (a.Length < 2) return "";
            // rec+0x50:= zoneCount−1 (FUN_005f91e0:94) — the record belongs to the most recently
            // added zone (CampaignInit.tcl interleaves sm_add_zone Urwald/Metall/… with its temps).
            var rec = new DigglesTclContext.StoryTemp
            { Name = StoryTempName(a[1]), Zone = ctx.StoryZones.Count - 1 };
            int k = 2;
            if (k < a.Length && int.TryParse(a[k], NumberStyles.Integer, CultureInfo.InvariantCulture, out var lo))
            { rec.Lo = lo; k++; }
            if (k < a.Length && int.TryParse(a[k], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hi))
            { rec.Hi = hi; k++; }
            for (; k < a.Length; k++)
            {
                switch (a[k])
                {
                    case "-exclusive":   if (++k < a.Length) rec.Exclusive = ParseTclBool(a[k]); break;
                    case "-zoneborder":  if (++k < a.Length) rec.ZoneBorder = a[k]; break;
                    case "-stoneidx":    if (++k < a.Length) rec.StoneIdx = StoryInt(a, k, -1); break;
                    case "-mindist":
                        if (++k < a.Length) rec.MinDistRef = StoryTempName(a[k]);
                        if (++k < a.Length) rec.MinDistVal = ParseFloat(a[k]);
                        break;
                    case "-middist":     if (++k < a.Length) rec.MidDist = ParseFloat(a[k]); break;
                    case "-nonrelevant": if (++k < a.Length) rec.NonRelevant = ParseFloat(a[k]); break;
                    case "-digconnect":
                        if (++k < a.Length) rec.DigConnect = a[k];
                        if (k + 1 < a.Length && !a[k + 1].StartsWith("-")) rec.DigConnect += " " + a[++k];
                        break;
                    case "-connect":     if (++k < a.Length) rec.Connect = a[k]; break;
                    case "-checkalign": if (++k < a.Length) rec.CheckAlign = StoryInt(a, k, -1); break;
                    default: break;   // unknown flag word — skip (native errors; tolerant here)
                }
            }
            ctx.StoryTemps.RemoveAll(t => t.Name == rec.Name);
            ctx.StoryTemps.Add(rec);
            return "";
        }

        // sm_get_level_info <cx> <cy> — FUN_00661340 → FUN_005fac90. THREE stages, all feeding the
        // shared StoryMgr state lg_set_starttemplatepro re-reads:
        // 1. RAW maximal free-rect expansion from the scan cell by 4-direction strip probes over
        // un-grown story cells (:74-113, probe FUN_005fa8e0: in bounds && != 1; raw bounds go
        // to +0x58..+0x64 — internal to this command).
        // 2. unique-temp pick over the sm_add_temp registry (:121-330 + selection:634-820).
        // 3. the RESULT rect via the FUN_005fa9a0 placement fit — NOT the raw expansion: the
        // returned w/h are clamped to min(rawW, max(16, seedW+8)) × min(rawH, max(8, seedH))
        // cells and positioned to CONTAIN the scan cell (randomized inside the feasible band
        // with a utemp, CENTERED without one —:103-165). On a young story map the raw rect
        // spans half the world; the fit is what keeps growth LOCAL to the dig breakthrough.
        // Result stored at +0x40/+0x44/+0x38/+0x3c (:833-836); returns "%i %i %i %i %s"
        // (00661340:136).
        static string CmdSmGetLevelInfo(DigglesTclContext ctx, string[] a)
        {
            int cx = StoryInt(a, 1, 0), cy = StoryInt(a, 2, 0);

            // --- 1) RAW free-rect expansion (FUN_005fac90:74-113; probe order left, up, down, right) ---
            int rx = cx, ry = cy, rw = 1, rh = 1;
            bool ok0 = StoryMapGet(ctx, cx, cy) != 1;
            bool gl = ok0, gu = ok0, gd = ok0, gr = ok0;
            bool StripFree(int sx, int sy, int sw, int sh)
            {
                for (int iy = 0; iy < sh; iy++)
                    for (int ix = 0; ix < sw; ix++)
                        if (StoryMapGet(ctx, sx + ix, sy + iy) == 1) return false;
                return true;
            }
            int guard = 0;
            while ((gl || gu || gd || gr) && guard++ < 4096)
            {
                if (gl) { if (StripFree(rx - 1, ry, 1, rh)) { rx--; rw++; } else gl = false; }
                if (gu) { if (StripFree(rx, ry - 1, rw, 1)) { ry--; rh++; } else gu = false; }
                if (gd) { if (StripFree(rx, ry + rh, rw, 1)) rh++; else gd = false; }
                if (gr) { if (StripFree(rx + rw, ry, 1, rh)) rw++; else gr = false; }
            }

            // --- 2) unique-temp pick (FUN_005fac90:121-330 candidates +:634-820 selection) ---
            ctx.StoryUtemp = "";
            int wy = cy * 4;
            int zi = StoryZoneIndex(ctx, wy);
            int zoneBase = StoryZoneBase(ctx, wy);
            int seedW = 1, seedH = 1;            // the FUN_005fa9a0 seeds (1×1 when no utemp)
            if (ok0)
                foreach (var rec in ctx.StoryTemps)
                {
                    if (rec.Placed) continue;                       // used flag +0x14 (:138)
                    // REGISTRATION-ZONE filter (:254): rec+0x50 (the zone current at sm_add_temp
                    // time) must equal the scan cell's zone — keeps swf_/kris_/lava_ uniques out
                    // of Urwald growth (a former bug picked swf_troll_wasser_raetzel at wy 36).
                    if (rec.Zone != zi) continue;
                    // Window normalization: sm_add_temp stores lo=min(lo,hi+1), hi=max(lo,hi+1)
                    // (FUN_0065cba0:238-249). Y-DEPTH BAND [lo, hi] in the wy=cy·4 frame (units match
                    // the native, which also compares cy·4 — FUN_005fac90:121). A temp is eligible
                    // ONLY WITHIN its band: the low gate skips shallow-of-lo, the HIGH gate skips
                    // deep-of-hi. Without the high gate the ramp saturates ≥1 past the band and the
                    // FIRST-registered temp (urw_der_einsiedler, band 56-60) is picked at EVERY depth,
                    // shadowing voodoo/troll/feen/uebergang — its huge composite then rarely fits, the
                    // utemp placement fails, and the generation falls back to plain 3-cave growth: the
                    // "story caves never generate + always the same size" bug. The
                    // band gate lets the depth-appropriate temps compete (probabilistically, below).
                    int lo = System.Math.Min(rec.Lo, rec.Hi + 1), hi = System.Math.Max(rec.Lo, rec.Hi + 1);
                    if (wy < lo + zoneBase) continue;
                    // NOTE (WORLDGEN_ORIGINAL_RE §B2): the "HIGH cutoff"
                    // (wy > hi+zoneBase ⇒ skip) is not applied — the native FUN_005fac90 has NO such gate;
                    // `max`/hi feeds ONLY the ramp weight below, so a past-band unique stays eligible at
                    // weight ≥ 1 (certain-IF-IT-FITS). What actually stops a narrow-band unique from
                    // monopolizing selection is the FREE-RECT FIT test added below. Removing the cutoff
                    // WITHOUT the fit test regresses (einsiedler everywhere) — they are ONE atomic change.
                    // -mindist = "the ref temp must already be placed" (the native distance term is
                    // degenerate,:187-191). -middist = INDEPENDENT centre-column EXCLUSION (:203-215/:220
                    // — a scan near the map centre ⇒ SKIP; the OPPOSITE of the old "rescue near centre").
                    if (rec.MinDistVal > 0f && rec.MinDistRef.Length > 0 && !RefPlaced(ctx, rec.MinDistRef))
                        continue;
                    if (rec.MidDist > 0f && System.Math.Abs(cx - ctx.StoryMapW / 2) * 4 < rec.MidDist)
                        continue;
                    // Selection probability (:284-313 weight;:675 keep-if weight ≥ rand): -nonrelevant
                    // value IS the pass probability; else the window ramp (wy−lo−zoneBase+1)/(hi−lo) —
                    // 0→1 across the band, ≥ 1 past it.
                    float weight = rec.NonRelevant > 0f
                        ? rec.NonRelevant
                        : (wy - lo - zoneBase + 1) / (float)System.Math.Max(1, hi - lo);
                    if (weight < ctx.StoryRng.NextDouble()) continue;        // dice FIRST (:641-642,:675)
                    // FREE-RECT FIT + FALL-THROUGH (FUN_005fa9a0:76/:80): a utemp that does NOT fit the
                    // raw free rect (rw×rh story cells) is REJECTED and selection CONTINUES to the next
                    // candidate — falling through to −1 = a NORMAL cave. The port previously omitted this,
                    // converting native-NORMAL generations into STORY generations whenever a story cave
                    // won the dice but wouldn't fit ("story caves spawn when they shouldn't" — §B1).
                    // seeds = temp cells + 1 (:685-693); composites size by their sm_def_temp_group extent.
                    {
                        var (tw, th) = StoryTempSize(ctx, rec.Name);
                        int sW = tw > 0 ? StoryCell(tw + 8) + 1 : 1;
                        int sH = th > 0 ? StoryCell(th + 8) + 1 : 1;
                        if (sW > rw || sH > rh) continue;                    // FIT + FALL-THROUGH
                        ctx.StoryUtemp = rec.Name; seedW = sW; seedH = sH;
                    }
                    break;
                }

            // --- 3) RESULT rect = the FUN_005fa9a0 fit inside the raw rect (shared FreeRectFit) ---
            // PERSIST the raw free rect + scan cursor (native: +0x58/+0x5c/+0x60/+0x64 & +0x48/+0x4c
            // on the StoryMgr, FUN_005fac90:114-117/:628-629) so lg_set_starttemplatepro's
            // FUN_005fc0a0 re-placement can re-fit around the SAME cursor later.
            ctx.StoryRawRectX = rx; ctx.StoryRawRectY = ry; ctx.StoryRawRectW = rw; ctx.StoryRawRectH = rh;
            ctx.StoryScanX = cx; ctx.StoryScanY = cy;
            int x = cx, y = cy, w = seedW, h = seedH;
            // mode 1: the native sm_get_level_info fit always passes 1, and +0x54 is STILL −1 here
            // (FUN_005fac90:630 clears it; the matched utemp index is only written at:824, AFTER the
            // fit) so FUN_005fa9a0 takes case 1 = CENTERED. The port keys hasUtemp on the picked utemp
            // (utemp → random) to preserve the working initial fit + its RNG draws; the re-placement
            // below overrides the position for utemp generations regardless.
            if (ok0)
                FreeRectFit(rx, ry, rx + rw, ry + rh, ref x, ref y, ref w, ref h,
                            mode: 1, hasUtemp: ctx.StoryUtemp.Length > 0, ctx.StoryRng);
            // Grown/OOB scan cell: FUN_005fa9a0:21-23 rejects up front, leaving the 1×1 seed at the
            // scan cell — story.tcl:155's "too small" gate (w·4 < 16) then aborts the generation.
            ctx.StoryRectX = x; ctx.StoryRectY = y; ctx.StoryRectW = w; ctx.StoryRectH = h;
            return x + " " + y + " " + w + " " + h + " "
                 + (ctx.StoryUtemp.Length == 0 ? "{}" : ctx.StoryUtemp);
        }

        // Live-map wiring for the forced-start laws (FUN_005848b0 / FUN_0058a510): the ≥8-tile
        // map inset gate + the per-tile "already templated" probe (native flag +0x13 bit0 ≈ our
        // HasTpl). Shared by lg_start and the lg_set_starttemplatepro walk.
        static void WireLgLiveMap(DigglesTclContext ctx, Diggles.Runtime.World.LevelGenerator lg)
        {
            if (ctx.MapBuilder is not Diggles.Runtime.World.RuntimeMapBuilder rmb) return;

            // `map create` establishes the native DAT_0077241c rect before any template has
            // been emitted, so its dimensions must already gate FUN_0058a510 presets even
            // though RuntimeMapBuilder.WorldResult (and HasTpl) is materialized only later by
            // EmitTerrain. At that fresh-boot point FUN_0058a510:42-61's templated-bit probe is
            // known clean; once arrays exist, use the real HasTpl mark. lg_start's ForcePlace
            // pass remains the ordered overlap/map re-probe in both cases.
            lg.MapW = rmb.Width;
            lg.MapH = rmb.Height;
            if (rmb.WorldResult is { } wr)
            {
                lg.TileBlocked = (x, y) => x >= 0 && y >= 0 && x < wr.Width && y < wr.Height
                                           && wr.HasTpl.Length > y * wr.Width + x && wr.HasTpl[y * wr.Width + x];
            }
            else lg.TileBlocked = null;
        }

        // lg_set_starttemplatepro — FUN_00650f90 (byte-verified full RE), the
        // dig→generation ANCHOR + START-PIECE law. Chain:
        // 1. Latch gate (+0x88 && +0x8c && +0x90,:177-178) + cell snap &~3 (:179-180).
        // 2. THE PRE-STEP (:182 = FUN_005f7a90 with this=&latchDir, byte-verified `lea
        // ecx,[edi+0x88]` at 0x6511eb): the walk offset starts at ONE ±4 step ALONG the dig
        // direction — iteration k lands the start piece's top-left at dugCell + 2 CELLS +
        // k cells along the dig dir. The start template therefore NEVER overlaps the
        // player's tunnel: one full cell of virgin rock (the latch cell) separates the
        // breakthrough from the stamp, and the dwarf DIGS THROUGH that cell and the
        // piece's closed wall into its authored bore (which sits on the same 4k+1/4k+2
        // marquee lattice — the junction mates by lattice alignment, not door-to-door).
        // The old port anchored AT the latch cell — one cell too close — which shoved the
        // piece onto the live tunnel and (with the then carve-wins blit) produced the
        // half-buried mating corridor.
        // 3. THE WALK (≤4 iterations,:184-239): per iteration — the CONSTANT latch-cell-
        // centre dig-nibble gate (:185-187; a miss fails all 4 identically → "nodigmark");
        // a FRESH random dig-direction start piece (:188 = FUN_005f7980 → FUN_00585d80,
        // see LevelGenerator.PickStartPiece); the pose probe FUN_0058a510 (4-align, map
        // inset ≥8, no-HasTpl footprint — LevelGenerator.ProbeForcedPose). Failure steps
        // a further ±4 (:221-238) and retries: only the shrinking HasTpl overlap or the
        // map inset can turn a later iteration green.
        // 4. On success the forced record {piece, areaRelX, areaRelY} is registered
        // (FUN_0058a510 appends to the +0x194 list; lg_start = FUN_005896a0 places the
        // records VERBATIM and fails the whole generation if any won't sit — the story
        // failcount retry channel). NEAR-BORDER SWAP (:261-273): when the anchor X lands
        // <5 tiles from the area's left edge or ≥ areaW−4, the record's template is
        // swapped in place for the MODE-0 pick (single onward door — matters for down
        // digs, where mode 1 = the L+R through-corridor that could never dock its
        // side door against the border).
        // ⚠ PORT APPROXIMATIONS (flagged, cited):
        // • the STORY RECT is RE-PLACED here via FUN_005fc0a0(dir, |walk|+4) around the story
        // cursor before SetArea (PORTED below — the rect is sized to fit utemp+dig-distance in
        // the dig direction and re-positioned so the scan cell sits at the dig-side edge);
        // • a resolvable UTEMP natively keeps the walk piece as record 0 and positions
        // the composite AWAY from the breakthrough (:281-503, ≤4 random attempts with
        // the dig-side band excluded) with start↔utemp corridor CONNECTIONS routed by
        // the un-ported FUN_00587480 — the port registers the composite at the walked
        // pose INSTEAD of the walk piece (preserves reachability until the router
        // lands; the previously verified einsiedler behavior).
        // Returns "%i %i %i %i %s" (:513, fmt @0x730808) = rect cells + utemp; walk failure
        // writes "nodigmark" into the name slot (:245-249, str @0x7307fc).
        static string CmdLgSetStartTemplatePro(DigglesTclContext ctx)
        {
            var lg = ctx.LevelGen;
            string rect = ctx.StoryRectX + " " + ctx.StoryRectY + " " + ctx.StoryRectW + " " + ctx.StoryRectH;
            if (lg == null) return rect + " nodigmark";
            lg.Rng = ctx.StoryRng;
            WireLgLiveMap(ctx, lg);
            lg.ClearForcedRecords();
            lg.ClearConnections();   // the +0xa4 connect list is rebuilt per lg_set_starttemplatepro
            lg.ClearReservedRects(); // the +0x17c mark-area (courtyard) list, likewise rebuilt (B8)
            ctx.StoryUtempPlaced = false;   // (B9) re-armed only if a composite lands this generation

            // dig dir → PORT door-side label (0=left 1=top 2=right 3=bottom; native labels are
            // 3/0/1/2): FUN_005f7980:15-42 — up→top, +x→right, −x→left; down passes the mode
            // through (mode 1 → the {left,right} through-corridor signature, mode 0 → bottom).
            int side; bool downThrough;
            switch (ctx.StoryLatchDir)
            {
                case 1: side = 1; downThrough = false; break;
                case 2: side = 3; downThrough = true; break;
                case 4: side = 0; downThrough = false; break;
                case 8: side = 2; downThrough = false; break;
                default: side = -1; downThrough = false; break;
            }
            int stepX = 0, stepY = 0;
            switch (ctx.StoryLatchDir)
            { case 1: stepY = -4; break; case 2: stepY = 4; break; case 4: stepX = -4; break; case 8: stepX = 4; break; }

            bool anchored = false; int ax = 0, ay = 0; string piece = "";
            if (side >= 0 && ctx.StoryLatchX != 0 && ctx.StoryLatchY != 0)
            {
                int lx = ctx.StoryLatchX & ~3, ly = ctx.StoryLatchY & ~3;   //:179-180
                var T = ctx.Terrain;
                int wx = lx + stepX, wy = ly + stepY;                        // the PRE-STEP (:182)
                for (int k = 0; k < 4 && !anchored; k++)
                {
                    // the CONSTANT latch-cell-centre nibble gate (:185-187); headless tolerance:
                    // no terrain wired (pure-lg tests) → the gate passes.
                    if (T != null && !StoryDigNibbleGate(T, lx + 2, ly + 2, ctx.LocalPlayer)) break;
                    var cand = lg.PickStartPiece(side, downThrough);         //:188 (fresh pick)
                    if (cand == null) break;      // empty pool → "none" → unresolvable natively
                    if (lg.ProbeForcedPose(cand, wx, wy))                    // FUN_0058a510
                    { anchored = true; ax = wx; ay = wy; piece = cand; break; }
                    wx += stepX; wy += stepY;                                //:221-238
                }
            }
            if (!anchored)
            {
                // "nodigmark" aborts the generation in story.tcl (:191) with no visible trace —
                // name the reason once per attempt.
                Debug.Log($"[worldgen] lg_set_starttemplatepro NODIGMARK — anchor walk failed " +
                          $"(latch dir={ctx.StoryLatchDir} at ({ctx.StoryLatchX},{ctx.StoryLatchY}), rect {rect})");
                ctx.StoryUtemp = "";
                return rect + " nodigmark";
            }

            // ── RECT RE-PLACEMENT LAW — FUN_005fc0a0 (call site FUN_00650f90:252, gated on a utemp) ──
            // sm_get_level_info CENTERS the rect on the scan cell; a utemp generation must instead SIZE
            // it to fit utemp + walked-distance in the dig direction AND RE-PLACE it so the scan/dig
            // cell lands at the DIG-SIDE EDGE — the utemp then falls AWAY in the dig direction and the
            // walk piece (one+ cells past the breakthrough) sits just inside that edge. The old port
            // GREW the rect but never re-positioned it, so for a RIGHT dig the band sat LEFT of the
            // anchor (breakthrough side) → the utemp NOT-PLACED. Faithful port of FUN_005fc0a0:
            // (2) clamp the persisted raw free rect's dig-side bound to the cursor (:34-64);
            // (3-4) size W/H cells: the DIG-AXIS dim gets +digDist, digDist = |Ywalk|+4+|Xwalk|
            // (FUN_00650f90:252), W=(utW+addW+8)/4+1, H=(utH+addH+8)/4+1 (:65-90; SM+0x68≈0);
            // (5) up/down → mode 4 (clamp the orientation X-hint), left/right → mode 1 (utemp→random,
            // which collapses onto the dig-side edge because the dig-side bound = the cursor);
            // (6) RE-PLACE via the shared FreeRectFit (:132); (7) store +0x38/+0x3c/+0x40/+0x44.
            if (ctx.StoryUtemp.Length > 0 && side >= 0 && ctx.StoryRawRectW > 0 && ctx.StoryRawRectH > 0)
            {
                var (utW, utH) = StoryTempSize(ctx, ctx.StoryUtemp);
                if (utW > 0 && utH > 0)
                {
                    int cursorX = ctx.StoryScanX, cursorY = ctx.StoryScanY;   // +0x48/+0x4c (FUN_005fac90:628-629)
                    int walkedXAbs = System.Math.Abs(ax - (ctx.StoryLatchX & ~3));
                    int walkedYAbs = System.Math.Abs(ay - (ctx.StoryLatchY & ~3));
                    int dist = walkedYAbs + 4 + walkedXAbs;                   // FUN_00650f90:252 (|Ywalk|+4+|Xwalk|)

                    // (2) clamp the RAW free rect's DIG-SIDE bound to the cursor (FUN_005fc0a0:34-64).
                    int minX = ctx.StoryRawRectX, minY = ctx.StoryRawRectY;
                    int maxX = ctx.StoryRawRectX + ctx.StoryRawRectW, maxY = ctx.StoryRawRectY + ctx.StoryRawRectH;
                    switch (ctx.StoryLatchDir)
                    {
                        case 1: if (maxY > cursorY) maxY = cursorY; break;                                   // up (:36-38 case0)
                        case 2: { int d = cursorY - minY; if (d > 0) { maxY -= d; minY = cursorY; } } break; // down(:40-45 case1)
                        case 4: if (maxX > cursorX) maxX = cursorX; break;                                   // left(:53-56 case3)
                        case 8: { int d = cursorX - minX; if (d > 0) { maxX -= d; minX = cursorX; } } break; // right(:58-63 case7)
                    }

                    // (3-4) size W/H — the DIG-AXIS dimension carries +digDist (:65-90).
                    int addW = (ctx.StoryLatchDir == 4 || ctx.StoryLatchDir == 8) ? dist : 0;   // horizontal→W
                    int addH = (ctx.StoryLatchDir == 1 || ctx.StoryLatchDir == 2) ? dist : 0;   // vertical→H
                    int W = StoryCell(utW + addW + 8) + 1;                    //:83-84
                    int H = StoryCell(utH + addH + 8) + 1;                    //:88-90 (SM+0x68≈0)

                    // (5) containment point + mode. The scan cursor is the containment point → dig-edge.
                    int px, py, mode;
                    if (ctx.StoryLatchDir == 1 || ctx.StoryLatchDir == 2)     // up/down → mode 4 + X hint
                    {
                        // EXACT FUN_005fc0a0:91-127 up/down X law (replaces the prior utDir-3-only
                        // approximation). The utemp's connect-door abs-X (FUN_005f7660, TILES) drives the
                        // orientation utDir (FUN_005fc010:17-27) and the X hint; then three watermark clamps.
                        // utDir: doorX<5→3, utW≤doorX→2, else→1 (doorX & utW both in TILES)
                        // hint = utDir1: cursorX − doorX/4 (door column at cursor,:19)
                        // utDir2: (cursorX − utWcells) − 2 (right edge at cursor,:110-112)
                        // utDir3: cursorX + 2 (:113-115)
                        // For the WIDE Urwald composites the connect sub-piece sits on the far side, so
                        // utDir 1 aligns THAT sub-piece at the cursor and the bulk hangs to one side in X
                        // with a SHORT connect corridor. The native's ~one-composite-width sideways
                        // placement is therefore FAITHFUL; the prior approximation's opposite-side placement
                        // (connect door left ~a full composite-width from the cursor → LONG corridor) was
                        // the artifact. The dig-axis (Y) edge is untouched — still AWAY-OK.
                        int utWcells = StoryCell(utW);                       // local_1c = utW/4 (:99-100)
                        int doorXtiles = StoryUtempConnectDoorX(ctx, ctx.StoryUtemp);   // FUN_005f7660 (flagged)
                        int utDir = doorXtiles < 5 ? 3 : (utW <= doorXtiles ? 2 : 1);   // FUN_005fc010
                        int hint = utDir == 1 ? cursorX - StoryCell(doorXtiles)
                                 : utDir == 2 ? (cursorX - utWcells) - 2
                                 :              cursorX + 2;
                        if (maxX - utWcells < hint) hint = maxX - utWcells;  //:116-119 min(hint, rawRight−utW)
                        if (hint < minX) hint = minX;                        //:121-123 max(hint, rawLeft)
                        px = System.Math.Min(hint, cursorX);                 //:125-127 min(hint, cursorX)
                        py = cursorY; mode = 4;                              // local_14 (:31); uVar4=4 (:120)
                    }
                    else                                                      // left/right → mode 1 (utemp→random)
                    {
                        px = cursorX; py = cursorY; mode = 1;                // local_1c/local_14 (:30-31); uVar4=1 (:130)
                    }

                    // (6) RE-PLACE via the shared FUN_005fa9a0 (grow + position). hasUtemp is always true
                    // here (the early-out already required +0x54 != -1). Fallback on a degenerate rect =
                    // FUN_005fc0a0:133-141: 1×1 at the cursor, drop the utemp (→ story "too small" abort).
                    if (FreeRectFit(minX, minY, maxX, maxY, ref px, ref py, ref W, ref H, mode, hasUtemp: true, ctx.StoryRng))
                    {
                        ctx.StoryRectX = px; ctx.StoryRectY = py; ctx.StoryRectW = W; ctx.StoryRectH = H;   // (7) +0x38..+0x44
                        Debug.Log($"[worldgen] RECT-REPLACE (FUN_005fc0a0) utemp='{ctx.StoryUtemp}' ut={utW}x{utH} " +
                                  $"dist={dist} dir={ctx.StoryLatchDir} scan=({cursorX},{cursorY}) → rect {W}x{H}c @({px},{py})");
                    }
                    else
                    {
                        ctx.StoryRectX = cursorX; ctx.StoryRectY = cursorY; ctx.StoryRectW = 1; ctx.StoryRectH = 1;
                        ctx.StoryUtemp = "";
                        Debug.Log($"[worldgen] RECT-REPLACE FALLBACK (FUN_005fc0a0:133-141) degenerate raw rect " +
                                  $"dir={ctx.StoryLatchDir} scan=({cursorX},{cursorY}) → 1×1, utemp dropped");
                    }
                }
            }

            if (ctx.StoryRectW > 0 && ctx.StoryRectH > 0)
                lg.SetArea(ctx.StoryRectX * 4, ctx.StoryRectY * 4,
                          (ctx.StoryRectX + ctx.StoryRectW) * 4, (ctx.StoryRectY + ctx.StoryRectH) * 4);

            // NEAR-BORDER SWAP (:261-273): iVar13 = (latchX − rectX·4) + |walkedX post-step|;
            // <5 or ≥ rectW·4−4 → re-pick with the MODE-0 law (single onward door) and replace
            // the record's template (same 4×4 footprint — the native swaps without re-probing).
            {
                int walkedXAbs = stepX != 0 ? System.Math.Abs(ax - (ctx.StoryLatchX & ~3)) + 4 : 0;
                int relX = ((ctx.StoryLatchX & ~3) - ctx.StoryRectX * 4) + walkedXAbs;
                if (ctx.StoryRectW > 0 && (relX < 5 || relX >= ctx.StoryRectW * 4 - 4))
                {
                    var swap = lg.PickStartPiece(downThrough ? 3 : side, false);   // mode 0: down→bottom
                    if (swap != null) piece = swap;
                }
            }

            // [worldgen] the anchor + the rect the utemp must fit into (FUN_005fc0a0-re-placed above
            // for utemp generations: the scan cell now sits at the dig-side edge, utemp lands away).
            Debug.Log($"[worldgen] ANCHOR dir={ctx.StoryLatchDir} latch=({ctx.StoryLatchX},{ctx.StoryLatchY}) " +
                      $"→ piece='{piece}' @({ax},{ay}) rect=({ctx.StoryRectX},{ctx.StoryRectY} {ctx.StoryRectW}x{ctx.StoryRectH}c) " +
                      $"utemp='{ctx.StoryUtemp}'");

            // Forced record 0 = the walked dig-direction piece — ALWAYS (native: the walk's
            // FUN_0058a510 append precedes the utemp block; the junction mates through it).
            int gx = ax - ctx.StoryRectX * 4, gy = ay - ctx.StoryRectY * 4;
            lg.AddForcedRecord(piece, gx, gy);

            // UTEMP POSITIONING (FUN_00650f90:281-503): the unique temp goes AWAY from the
            // breakthrough — ≤4 attempts at random 4-aligned positions inside the area with the
            // DIG-SIDE BAND EXCLUDED by the walked distance (:290-302: up→maxY−=4+|wy|,
            // down→minY+=|wy|+4, −x→maxX−=4+|wx|, +x→minX=|wx|+8); each attempt registers the
            // group pieces TRANSACTIONALLY (FUN_005f7d10 pops on any failure); ALL attempts
            // fail ⇒ NO utemp records — the generation proceeds with just the walk piece and
            // the utemp registry record stays pending for a later, bigger rect (:305 goto).
            // This is what un-livelocks the einsiedler depth band: its composite spans ~110×48
            // tiles and can rarely fit — the native simply skips it that generation.
            // ⚠ approximations (flagged): attempt 0's deterministic stoneidx/cursor column
            // alignment (:321-353) is randomized like attempts 1-3 (the story-cursor law is
            // un-RE'd). The start↔utemp corridor CONNECTION (FUN_005fc3b0) is now REGISTERED and
            // routed by RouteCorridor (FUN_00587480) in lg_start — the composite is
            // connected to the walk piece by a carved tunnel, no longer an orphan island. (The old
            // port-only walk-overlap guard is gone: the FUN_005fc0a0 re-placement puts the dig-side
            // band on the breakthrough side, so the utemp band no longer collides with the walk piece.)
            if (ctx.StoryUtemp.Length > 0)
            {
                var entries = new System.Collections.Generic.List<(string tcl, int dx, int dy)>();
                if (ctx.StoryTempGroups.TryGetValue(ctx.StoryUtemp, out var groupSpec))
                    entries.AddRange(ParseTempGroupEntries(groupSpec));
                else
                {
                    string cand = ctx.StoryUtemp + ".tcl";
                    var (tw0, th0) = lg.GetTempSize(cand);
                    if (tw0 > 0 && th0 > 0) entries.Add((cand, 0, 0));
                }
                int extW = 0, extH = 0;
                foreach (var (tcl, dx, dy) in entries)
                {
                    var (tw, th) = lg.GetTempSize(tcl);
                    if (tw <= 0 || th <= 0) { extW = -1; break; }
                    extW = System.Math.Max(extW, dx + tw);
                    extH = System.Math.Max(extH, dy + th);
                }
                int areaW = ctx.StoryRectW * 4, areaH = ctx.StoryRectH * 4;
                if (entries.Count > 0 && extW > 0)
                {
                    int minX = 4, maxX = areaW - extW - 4;                   //:283-289
                    int minY = 4, maxY = areaH - extH - 4;                    // (SM+0x68 ≈ 0)
                    int wD = System.Math.Abs(stepX) != 0 ? System.Math.Abs(ax - (ctx.StoryLatchX & ~3)) + 4 : 0;
                    int hD = System.Math.Abs(stepY) != 0 ? System.Math.Abs(ay - (ctx.StoryLatchY & ~3)) + 4 : 0;
                    switch (ctx.StoryLatchDir)                                //:290-302
                    {
                        case 1: maxY -= 4 + hD; break;
                        case 2: minY += hD + 4; break;
                        case 4: maxX -= 4 + wD; break;
                        case 8: minX = wD + 8; break;
                    }
                    minX = (minX + 3) & ~3; minY = (minY + 3) & ~3;
                    bool utempPlaced = false;
                    for (int attempt = 0; attempt < 4 && minX <= maxX && minY <= maxY; attempt++)   //:303-502
                    {
                        int px = minX + ctx.StoryRng.Next((maxX - minX) / 4 + 1) * 4;
                        int py = minY + ctx.StoryRng.Next((maxY - minY) / 4 + 1) * 4;
                        // (the port-only walk-piece overlap guard is not applied: with the FUN_005fc0a0
                        // re-placement the dig-side band excludes the breakthrough side, so the utemp
                        // band no longer overlaps the walk piece — the native has no such guard.)
                        bool ok = true;
                        foreach (var (tcl, dx, dy) in entries)
                            if (!lg.ProbeForcedPose(tcl, ctx.StoryRectX * 4 + px + dx, ctx.StoryRectY * 4 + py + dy))
                            { ok = false; break; }
                        if (!ok) continue;
                        // FUN_005fc3b0 registers the start↔utemp corridor CONNECTION here (weight
                        // 0.4): srcNode = the walk piece (forced record 0), dstNode = the utemp's
                        // entry sub-piece (its first forced record). The router (RouteCorridor /
                        // FUN_00587480) carves the tunnel in lg_start so the composite is no longer
                        // an orphan island. Doors are auto-picked (-1) — the native +0x38 entry-door
                        // field is unmodeled (flagged in LevelGenerator.Connection).
                        int utempRec = lg.ForcedRecordCount;   // walk piece = record 0
                        foreach (var (tcl, dx, dy) in entries)
                            lg.AddForcedRecord(tcl, px + dx, py + dy);
                        lg.AddConnection(0, utempRec, weight: 0.4f);
                        // B8 — MARK-AREA courtyard reservation (FUN_00650f90:424-434 → +0x17c). Forward
                        // the composite's TYPE-0 `mat` rects, offset by THIS placement's grid origin
                        // (px,py), as keep-clear rects — the grow loop then leaves the inter-building
                        // courtyards open (lg_start FUN_00589790:117-171 stamps their cells = 1). These
                        // touch ONLY the LevelGenerator placement grid, NOT terrain.
                        if (ctx.StoryTempGroups.TryGetValue(ctx.StoryUtemp, out var matSpec))
                            foreach (var (mx0, my0, mx1, my1, mtype) in ParseTempGroupMatRects(matSpec))
                                if (mtype == 0)
                                    lg.AddReservedRect(px + mx0, py + my0, px + mx1, py + my1);
                        // B9 — capture the composite's PLACED WORLD rect (origin + extent, tiles) for the
                        // post-growth `sm_draw_stone -exlimit`/`-utmat` (story.tcl:227-228). World origin =
                        // area origin (StoryRectX/Y·4) + this attempt's grid position (px,py).
                        ctx.StoryUtempPlaced = true;
                        ctx.StoryUtempPlacedName = ctx.StoryUtemp;
                        ctx.StoryUtempWorldX = ctx.StoryRectX * 4 + px;
                        ctx.StoryUtempWorldY = ctx.StoryRectY * 4 + py;
                        ctx.StoryUtempExtW = extW;
                        ctx.StoryUtempExtH = extH;
                        utempPlaced = true;
                        break;
                    }
                    // INSTRUMENTATION: a utemp is PICKED but the composite never fits the
                    // rect ⇒ NO story cave. This — not the router (which never even runs: connections
                    // stay 0) — is the real "story caves never generate" bug. Log the exact band so the
                    // rect-size law (FUN_005fc0a0, un-ported) can be sized correctly.
                    if (!utempPlaced)
                        Debug.Log($"[worldgen] UTEMP '{ctx.StoryUtemp}' NOT PLACED: ext={extW}x{extH} " +
                                  $"area={areaW}x{areaH} band X[{minX}..{maxX}] Y[{minY}..{maxY}] " +
                                  $"entries={entries.Count} dir={ctx.StoryLatchDir}");
                }
                else
                    Debug.Log($"[worldgen] UTEMP '{ctx.StoryUtemp}' no-fit-setup: entries={entries.Count} extW={extW} areaW={areaW}x{areaH}");
            }
            return rect + " " + (ctx.StoryUtemp.Length == 0 ? "{}" : ctx.StoryUtemp);
        }

        // CStoryMgr::DigNotify — FUN_005fd060, called from dig_apply with the SAME rounded node
        // coords, BEFORE the carve (DigApply FUN_006347a0:103-119). Every in-bounds dig: fires the
        // sm_register'd story callback ("<cb> dig shut -1" in the object's interp,:33-64), CLEARS
        // the latch (:66-70 — only the LATEST breakthrough survives), marks the story cell (:71-73).
        // Then, gated on the dug tile's mode-1 dummy nibble (:75-77), probes the four ±4 neighbours
        // in +x, +y, −x, −y order: the FIRST direction whose story cell is un-grown AND whose tile
        // passes the same nibble gate bumps the weight (+2 ±x, +4 +y=down, +1 −y=up via
        // FUN_005fcfb0) and returns; weight > threshold latches {dir, x, y} for sm_scan.
        static void StoryDigNotify(DigglesTclContext ctx, Diggles.Runtime.World.DiggableTerrain T, int x, int y)
        {
            // Bounds gate (:29-32): ≥8 tiles from every map border.
            if (x < 8 || y < 8 || x >= T.Width - 8 || y >= T.Height - 8) return;

            // Registered story callback → the StoryMgr's `callback` proc → newslist → newsticker_dig.
            // GATED (BA) on the dig-message flag +0xa0 AND a registered callback +0x78!=-1
            // (FUN_005fd060:33-34: `if (+0xa0 != 0 && +0x78 != -1)`). +0xa0 = sm_dig_message (default
            // 0, FUN_005fa7c0:33), DEAD in shipped data (the `sm_dig_message 1` calls are commented,
            // story.tcl:524/539/555). Without this gate the port fired unconditionally, so
            // newsticker_dig prematurely `sm_set_event Uebergang_*` after ~20 digs (story.tcl:786-793)
            // → the Umzug (zone move) fires EARLY → the wrong zone's caves generate. +0x78 != -1 = a
            // callback registered via sm_register (ctx.StoryCallbacks non-empty).
            // (Snapshot: the eval runs arbitrary Tcl that could re-register and invalidate the dict.)
            if (ctx.StoryDigMessage && ctx.StoryCallbacks.Count > 0)
                foreach (var kv in new List<KeyValuePair<int, string>>(ctx.StoryCallbacks))
                    if (ctx.Entities.TryGetValue(kv.Key, out var smEnt))
                        RunInEntityContext(ctx, smEnt, kv.Value + " dig shut -1", "sm_callback");

            ctx.StoryLatchDir = 0; ctx.StoryLatchX = 0; ctx.StoryLatchY = 0; ctx.StoryLatchConsumed = false;
            ctx.StoryMap[(StoryCell(x), StoryCell(y))] = 1;

            if (!StoryDigNibbleGate(T, x, y, ctx.LocalPlayer)) return;

            // Probe order +x, +y, −x, −y; weights/dir codes per:89/:113/:137 + FUN_005fcfb0:16-25.
            if (StoryDigProbe(ctx, T, x + 4, y, dir: 8, weight: 2)) return;
            if (StoryDigProbe(ctx, T, x, y + 4, dir: 2, weight: 4)) return;
            if (StoryDigProbe(ctx, T, x - 4, y, dir: 4, weight: 2)) return;
            StoryDigProbe(ctx, T, x, y - 4, dir: 1, weight: 1);
        }

        // One DigNotify direction probe. False = not eligible (fall through to the next direction);
        // true = the weight was bumped (the native early-returns after at most ONE bump per dig).
        static bool StoryDigProbe(DigglesTclContext ctx, Diggles.Runtime.World.DiggableTerrain T,
            int nx, int ny, int dir, int weight)
        {
            if (StoryMapGet(ctx, StoryCell(nx), StoryCell(ny)) == 1) return false;   // already grown
            if (!StoryDigNibbleGate(T, nx, ny, ctx.LocalPlayer)) return false;       // front not dummy-marked
            ctx.StoryDigWeight += weight;   // reset only by a placement-emitting lg_get_level (FUN_00652d10:280)
            if (ctx.StoryDigWeight > ctx.StoryDigThreshold)
            {
                ctx.StoryLatchX = nx; ctx.StoryLatchY = ny;                          // RAW coords (:94-97)
                ctx.StoryLatchDir = dir;
                ctx.StoryLatchConsumed = false;
            }
            return true;
        }

        // ----- cave-skin completion pass (CMapBeautifier, FUN_0058ba00) -----

        // Runs once per dig_apply, after the carve (FUN_006347a0:127-133). For the FIRST cave rect
        // (DiggableTerrain.CaveRecords = beautifier+0x30) containing the dig node whose ENTIRE area
        // has the digger-owner's nibble cleared (fully dug out):
        // 1. remove every LIVE object of a cave-skin-flagged class (class+0xcc2, set only by
        // add_cave_skin_class) whose position lies inside the rect (:159-446 block sweep;
        // inclusive float bounds:397-406) — the old lining/props swap OUT;
        // 2. spawn the three detail slots (a=2, b=1, c=0,:232-249) of the matching
        // {set = zone band at the dig y (FUN_005fcdf0:211-228, <0 → 0), size = rect's
        // template idx} rules at the rect centre (cx = (x1+x2)/2+1:187, cy = (y1+y2)/2:199,
        // z = 15.0 — the front deco plane, the 0x41700000 arg of FUN_0058c450) — the three
        // slots SHARE one random pick index (the &local_34 in/out of FUN_0058e9c0), so a/b/c
        // land on the SAME variant family;
        // 3. retire the rect (FUN_0058f2a0:250) and STOP — one completed rect per dig (:252).
        // Not complete → try the next containing rect (:153 → LAB_0058bc5d).
        // (The native also purges that rect's PENDING deferred spawn entries (:281-388, +0xcc2-
        // gated); the port spawns template deco eagerly and has no per-block deferred queue —
        // instead step 1 below carries a documented PORT DEVIATION sweeping MapDecoSpawned
        // tunnel deco out of the completed rect (see the note there).
        // The detail gate vs the graphics setting (FUN_0058c450:73-89) = max detail here: all 3.)
        internal static void CaveSkinDigCheck(DigglesTclContext ctx, Diggles.Runtime.World.DiggableTerrain T,
            int x, int y, int owner)
        {
            var recs = T.CaveRecords;
            for (int i = 0; i < recs.Count; i++)
            {
                var r = recs[i];
                if (x < r.X0 || x > r.X1 || y < r.Y0 || y > r.Y1) continue;   // point-in-rect (:72-95)
                // Completion scan — half-open [x1,x2) × [y1,y2) (:102-158; the loops break when
                // bound <= coord). Any surviving nibble on the digger's layer → not finished.
                bool done = true;
                for (int ty = r.Y0; ty < r.Y1 && done; ty++)
                    for (int tx = r.X0; tx < r.X1; tx++)
                        if (T.RawDigNibble(tx, ty, owner) != 0) { done = false; break; }
                if (!done) continue;

                // 1. Sweep out the old cave-skin props (class+0xcc2 inside the rect, inclusive)
                // — PLUS the during-dig tunnel deco (MapDecoBeautifier spawns) standing in
                // the completed cave. The native completion sweep tests ONLY class+0xcc2
                // (FUN_0058ba00:319/:406) — but its DUMMYDES pass has already been killing
                // tunnel Dummy_* deco per-carve while the cave was dug (FUN_005eb7b0; see
                // MapDecoCarveSweep), so including the DUMMYDES-eligible pieces here
                // (MapDecoKillable: beautifier spawns + authored set_physic template deco)
                // is the port's coarser backstop for the same end state (beams floating
                // mid-air inside finished caves). Scope mirrors the native sweep exactly:
                // the completed rect, inclusive bounds. Removal =
                // MapDecoRetire (the native FUN_0058ba00 removes old skins through the SAME
                // FUN_005eae80 seam).
                List<DigglesTclEntity>? doomed = null;
                foreach (var kv in ctx.Entities)
                {
                    var e = kv.Value;
                    if (e.Class == null || (!e.Class.CaveSkin && !MapDecoKillable(e))) continue;
                    if (e.MapDecoRetiring) continue;
                    if (e.Position.x < r.X0 || e.Position.x > r.X1 ||
                        e.Position.y < r.Y0 || e.Position.y > r.Y1) continue;
                    (doomed ??= new List<DigglesTclEntity>()).Add(e);
                }
                if (doomed != null)
                    foreach (var e in doomed) MapDecoRetire(ctx, e);

                // 2. Spawn the fresh skins.
                int cx = (r.X0 + r.X1) / 2 + 1, cy = (r.Y0 + r.Y1) / 2;
                int set = StoryZoneIndex(ctx, y); if (set < 0) set = 0;
                int pick = -1;
                foreach (int detail in new[] { 2, 1, 0 })
                {
                    string? cls = PickCaveSkin(ctx, set, detail, r.TemplateIdx, ref pick);
                    if (cls == null) continue;
                    try
                    {
                        var sh = CmdNew(ctx, new[] { "new", cls, "",
                            string.Format(CultureInfo.InvariantCulture, "{0} {1} 15", cx, cy) });
                        // The cave-skin lining props hover-aura'd and ate clicks. Native
                        // cave-skin classes are `def_class … dummy` with NO obj_init
                        // and never call set_hoverable/set_selectable (animinit.tcl:419-429,
                        // class_collision 0), so the default-off native flags leave them
                        // NON-interactive. The port's pick model defaults ON (opt-out vars), so opt
                        // out here — the same seam MapDecoSpawn uses for tunnel deco.
                        if (int.TryParse(sh.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sk)
                            && ctx.Entities.TryGetValue(sk, out var se))
                        {
                            se.VarsScope.Vars["_selectable"] = "0";
                            se.VarsScope.Vars["_hoverable"] = "0";
                            // The fresh skins slide out of the back wall too (Play-mode only;
                            // see MapDecoSlide header).
                            if (se.GameObject != null && Application.isPlaying)
                                Diggles.Runtime.MapDecoSlide.BeginEmerge(se.GameObject, new Vector3(cx, cy, 15f));
                        }
                    }
                    catch (System.Exception e) { Debug.LogError($"[cave_skin] new {cls}: {e.Message}"); }
                }

                T.RemoveCaveRecord(i);   // FUN_0058f2a0
                return;                  // one completed rect per dig (:252)
            }
        }

        // FUN_0058e9c0 — pick ONE registered skin class for {set, detail, size}. `pick` is the
        // SHARED variant index across the three detail slots: an empty match list returns null and
        // leaves it untouched; otherwise it isrolled only when unset (−1) or out of range for
        // this slot's list (rand % count,:62-74), then indexes the matches in registration order.
        static string? PickCaveSkin(DigglesTclContext ctx, int set, int detail, int size, ref int pick)
        {
            List<string>? matches = null;
            foreach (var rule in ctx.CaveSkinRules)
                if (rule.Set == set && rule.Detail == detail && rule.Size == size)
                    (matches ??= new List<string>()).Add(rule.ClassName);
            if (matches == null) return null;
            if (pick < 0 || pick >= matches.Count) pick = ctx.StoryRng.Next(matches.Count);
            return matches[pick];
        }

        // ----- during-dig template deco (CMapBeautifier beams/treasure/critters) -----
        // FUN_0058b130 (ScanAtPos, before-carve) + FUN_0058c920 (pending queue) + FUN_0058d370
        // (per-SrvDoThink pending tick). Full RE + port law: Docs/DIGGING_MECHANIC.md §9.B and
        // the MapDecoBeautifier header. The beautifier is lazy like the native singleton
        // (DAT_00772414, FUN_0058a820) — created on the first dig once Terrain + LevelGen exist.
        public static Diggles.Runtime.World.MapDecoBeautifier? MapDecoOf(DigglesTclContext ctx)
        {
            var t = ctx.Terrain;
            var lg = ctx.LevelGen;
            // Rebind on a terrain swap (in-session map reload): stale rects/pendings belong to
            // the old world — start fresh, like the native singleton after a map re-create.
            if (ctx.MapDeco != null && ReferenceEquals(ctx.MapDeco.Terrain, t)) return ctx.MapDeco;
            if (t == null || lg == null) return null;
            var md = new Diggles.Runtime.World.MapDecoBeautifier
            {
                Terrain = t,
                Templates = lg,
                Bdb = t.Bdb,
                Rng = ctx.StoryRng,
                // Zone band of the dig row (FUN_005fcdf0; ScanAtPos maps <0 → 0,:232-235).
                ZoneOfY = y => { int z = StoryZoneIndex(ctx, y); return z < 0 ? 0 : z; },
            };
            md.SpawnEntity = p => MapDecoSpawn(ctx, p);
            ctx.MapDeco = md;
            // Per-carve deco sweep: DiggableTerrain fires OnCarveNode after
            // EVERY successful carve (marked dig CarveStep + Ctrl+LMB CarveTileNow) — the port's
            // MapValidator DUMMYDES hook (FUN_005a46d0 label 0x10 → FUN_005eb7b0). Removes
            // beautifier/cave-skin deco whose floor or anchor wall the carve just took (see
            // MapDecoCarveSweep). Wired once on the fresh terrain binding.
            t.OnCarveNode = (nx, ny) => MapDecoCarveSweep(ctx, nx, ny);
            return md;
        }

        // Per-carve deco sweep — the port analog of the native MapValidator DUMMYDES phase
        // (FUN_005a46d0 label 0x10 → FUN_005eb7b0), THE per-carve deco killer. Native chain:
        // • every authored template deco line runs `set_physic $objhandle true`
        // (Templates/urw_gng_001_a.tcl:8/14/22/28 — back-wall boards at z=11.02, front
        // supports at 15.04, moss alike) → FUN_0067fba0:141-146 sets obj+0x3d0 |= 0x10 and
        // creates the piece's DUMMY RECORD (FUN_005ebc90 + FUN_005eb470);
        // • per carve dirty rect, FUN_005eb7b0 destroys every recorded objtype-dummy object
        // ((obj+0x3d0 & 0x12) == 0x10, class+0x54 == 0 → verdict class+0x68 = 3, unlinked
        // +0x278 == −1) whose ANIM BBOX (pos ± frame bbox, FUN_004059a0+0x44..0x54)
        // overlaps the rect — strict float compare,:83 — via FUN_005eae80 (deferred
        // destroy, obj+0x404 guard). Boards/beams/moss vanish one by one as the carve
        // front reaches them; a wall board's bbox protrudes into the open space in front
        // of its wall face, so carving the block in front of it kills it too (an earlier
        // anchor-tile-only gate missed exactly that).
        // • cave-skin lining props are class_physic 0 and never set_physic
        // (SetCaveSkinClasses, animinit.tcl:419-429) → NO dummy record → DUMMYDES never
        // touches them; only the completion pass (CaveSkinDigCheck) swaps them.
        // Port mapping: enrolment/eligibility = MapDecoKillable (MapDecoSpawned OR authored
        // set_physic — see its note; ROUND 4); bbox = the entity's renderer union (FowExtX/Y,
        // native units) once computed, else MapDecoBeautifier.DecoExt; the carve dirty rect =
        // the node's four corner tiles [nx-1,nx]×[ny-1,ny] in world space.
        // Port deviation (documented): a BAND GUARD — the kill also requires some tile of the
        // carve window to be in the cave band / open (hmap ≤ 10, MapDecoBeautifier
        //.FloorStranded) — because the port's default box is isotropic and FATTER than a real
        // 3DB bbox; without the guard, tunnel-band re-digs beside a piece (fresh corridor
        // carves at 11) would over-kill. A cave/deep carve reaches ≤ 10 by its second swing.
        // Removal runs through MapDecoRetire: crumble + deferred delete in Play, instant in
        // headless — the native's deferred-destroy timing, one piece at a time.
        static void MapDecoCarveSweep(DigglesTclContext ctx, int nx, int ny)
        {
            var md = ctx.MapDeco;
            if (md == null || ctx.Entities.Count == 0) return;
            // The band guard: did this carve leave cave-band/open ground in its window?
            bool opened = false;
            for (int gy = ny - 1; gy <= ny && !opened; gy++)
                for (int gx = nx - 1; gx <= nx && !opened; gx++)
                    if (md.FloorStranded(gx, gy)) opened = true;
            if (!opened) return;
            List<DigglesTclEntity>? doomed = null;
            foreach (var kv in ctx.Entities)
            {
                var e = kv.Value;
                if (!MapDecoKillable(e)) continue;
                if (e.MapDecoRetiring) continue;               // already queued (obj+0x404)
                float ex = e.FowExtX > 0f ? e.FowExtX : Diggles.Runtime.World.MapDecoBeautifier.DecoExt;
                float ey = e.FowExtY > 0f ? e.FowExtY : Diggles.Runtime.World.MapDecoBeautifier.DecoExt;
                // Strict-inequality overlap, FUN_005eb7b0:83 (tile t spans [t-0.5, t+0.5)).
                if (nx - 1.5f < e.Position.x + ex && e.Position.x - ex < nx + 0.5f &&
                    ny - 1.5f < e.Position.y + ey && e.Position.y - ey < ny + 0.5f)
                    (doomed ??= new List<DigglesTclEntity>()).Add(e);
            }
            if (doomed != null)
                foreach (var e in doomed) MapDecoRetire(ctx, e);

            // Marker-7 SAVE SEEDS (ctx.MapDecoSeeds, DigglesTclEngine.SpawnDecoSeed) live OUTSIDE
            // ctx.Entities but obey the same DUMMYDES bbox law — a later dig that strands their
            // footing removes them one by one, exactly like the native lightweight instances the
            // engine culls when their block is re-carved. Retired by destroying the prop directly.
            if (ctx.MapDecoSeeds.Count > 0)
            {
                List<DigglesTclEntity>? doomedSeeds = null;
                foreach (var e in ctx.MapDecoSeeds)
                {
                    if (e.MapDecoRetiring) continue;
                    float sx = e.FowExtX > 0f ? e.FowExtX : Diggles.Runtime.World.MapDecoBeautifier.DecoExt;
                    float sy = e.FowExtY > 0f ? e.FowExtY : Diggles.Runtime.World.MapDecoBeautifier.DecoExt;
                    if (nx - 1.5f < e.Position.x + sx && e.Position.x - sx < nx + 0.5f &&
                        ny - 1.5f < e.Position.y + sy && e.Position.y - sy < ny + 0.5f)
                        (doomedSeeds ??= new List<DigglesTclEntity>()).Add(e);
                }
                if (doomedSeeds != null)
                    foreach (var e in doomedSeeds) MapDecoRetireSeed(ctx, e);
            }
        }

        // The native DUMMYDES eligibility. A piece is enrolled for
        // the per-carve kill iff it holds a DUMMY RECORD:
        // • the port beautifier's own spawns (MapDecoSpawned — that spawn path never runs
        // set_physic, so the flag stands in for the record the native spawn registers), OR
        // • AUTHORED template deco — every template init line runs `set_physic $objhandle
        // true` (Templates/urw_gng_001_a.tcl:8/14/22/28) → FUN_0067fba0:141-146 sets
        // obj+0x3d0 |= 0x10 and creates the record (FUN_005ebc90 + FUN_005eb470). The port
        // stores set_physic as the `_physic` entity prop (RegisterEntityProp) — truthy ⇒
        // enrolled. An earlier pass missed this leg: template boards (wandboden_Holz_*)
        // hung after the block in front was carved while beautifier pieces crumbled.
        // Per-object gates mirrored from FUN_005eb7b0:56-57 + the verdict law:
        // • not link-attached: obj+0x278 == −1 ⇔ LinkedToHandle == 0;
        // • objtype-dummy class only: class+0x54 == 0 → verdict class+0x68 = 3 (destroy);
        // all other objtypes (items/characters that also call set_physic, e.g.
        // characters/brut.tcl) get verdict 0 = keep ⇔ Kind != "dummy" here;
        // • cave skins stay completion-pass-only (class_physic 0, never set_physic —
        // SetCaveSkinClasses animinit.tcl:419-429) — explicit exclusion for the port's
        // CaveSkinDigCheck-spawned props.
        static bool MapDecoKillable(DigglesTclEntity e)
        {
            if (e.Class == null || e.Class.CaveSkin) return false;
            if (e.MapDecoSpawned) return true;
            if (e.LinkedToHandle != 0) return false;                       // obj+0x278 != −1
            if (!string.Equals(e.Class.Kind, "dummy", System.StringComparison.OrdinalIgnoreCase))
                return false;
            return e.VarsScope.Vars.TryGetValue("_physic", out var v) && ParseTclBool(v);
        }

        // Deco removal seam — the native FUN_005eae80: SHATTER the model, then destroy
        // DEFERRED (obj+0x404 → MapDecoRetiring guards the double-queue). Correcting an
        // earlier assumption of "no VFX on destroy": FUN_005eae80's first act IS
        // the shatter — it calls FUN_004845a0 (frame+bbox resolve, frustum gate, shatter SOUND
        // FUN_00453f00) → FUN_004830a0, the MeshDestructor: the CURRENT-frame mesh bursts into
        // tessellated textured shards (per-vertex decode+world transform:289-303, adaptive
        // triangle split FUN_00482950, per-vertex palette colour:314-327, random velocity
        //:167-172, gravity + settle FUN_005ea850). Deco killed by DUMMYDES, old skins purged
        // by FUN_0058ba00 and creatures dying in modes classdef+0x68 ∈ {1,2,3} ALL shatter
        // through this seam — matching how a creature shatters into pieces on death.
        // Port: ShatterEntity (set 43 fragment burst from the model's
        // current-frame vertices, tinted by its texture) + IMMEDIATE delete — the native
        // two-stage timing (shards now, object freed at the tick boundary). Fallbacks: the
        // slide-back retreat behind MapDecoSlide.UseSlideRetreat; dirt-burst + crumble-drop
        // when no mesh is reachable. Headless/EditMode deletes instantly (deterministic).
        static void MapDecoRetire(DigglesTclContext ctx, DigglesTclEntity e)
        {
            if (e.MapDecoRetiring) return;
            e.MapDecoRetiring = true;
            int h = e.Handle;
            void Del()
            {
                if (!ctx.Entities.ContainsKey(h)) return;
                try { CmdDel(ctx, new[] { "del", h.ToString(CultureInfo.InvariantCulture) }); }
                catch (System.Exception ex) { Debug.LogError($"[map_deco] retire del #{h}: {ex.Message}"); }
            }
            if (e.GameObject != null && Application.isPlaying)
            {
                if (Diggles.Runtime.MapDecoSlide.UseSlideRetreat)
                {
                    Diggles.Runtime.MapDecoSlide.BeginRetreat(e.GameObject, e.Position, Del);
                    return;
                }
                if (ShatterEntity(ctx, e)) { Del(); return; }   // shards now, free now (native)
                // No reachable mesh: fall back to a dirt burst + crumble drop.
                try { ctx.Particles?.CreateTemp(20, e.Position, Vector3.zero, 2, 0.5f); }
                catch (System.Exception) { /* particles are cosmetic */ }
                Diggles.Runtime.MapDecoSlide.BeginCrumble(e.GameObject, e.Position, Del);
            }
            else
                Del();
        }

        // ---- THE SHATTER SEAM (public: deco removal now, CREATURE DEATH later) ----
        // Native law (MeshDestructor, RE): FUN_005eae80 → FUN_004845a0 (resolve the
        // CURRENT anim frame FUN_004059a0 + frustum gate + shatter sound FUN_00453f00) →
        // FUN_004830a0: decode the frame's 16-bit vertices, world-transform (:289-303), walk
        // the index buffer per triangle, tessellate large ones (FUN_00482950), emit REAL
        // TRIANGLE SHARDS keeping the model's material/texture + UVs + per-vertex colours
        // (:314-327); symmetric random velocity (:167-172), gravity DAT_00737058, physics-
        // until-settled (FUN_005ea850) then free. Death modes classdef+0x68 ∈ {1,3} (and 2
        // via FUN_005e9700) route creature deaths here — this method is that seam for the
        // port; combat is NOT wired by design (call it from the death path when that lands).
        //
        // The particle-set-43 billboard rendition lost the geometry (the original mesh
        // literally crumbles apart) — the port now builds a true mesh-fragment
        // effect (MeshShatterEffect: per-triangle shards of the CURRENT-frame mesh, midpoint-
        // split oversized triangles, the model's OWN sharedMaterial + original UVs, one
        // dynamic mesh = one draw call, CPU scatter/gravity/settle/fade). Set 43 +
        // CreateTempShatter stay as dormant sprite-burst infra (documented in
        // ParticleTypeTable t[43]) — NOT wired here anymore. Returns false when there is no
        // GameObject or no usable mesh — MapDecoRetire then falls back to dirt+crumble.
        // No isPlaying gate HERE (the retire path gates); EditMode law tests may call this
        // directly (the spawned effect just doesn't Update outside Play).
        public static bool ShatterEntity(DigglesTclContext ctx, DigglesTclEntity e)
        {
            _ = ctx;   // reserved: the shatter SOUND hook (FUN_00453f00) will need it
            var go = e.GameObject;
            if (go == null) return false;
            Mesh? mesh = null;
            Transform? tf = null;
            var anim = go.GetComponentInChildren<Diggles.Runtime.DigglesAnimator>();
            if (anim != null) { mesh = anim.CurrentFrameMesh(); tf = anim.transform; }
            if (mesh == null)
            {
                var mf = go.GetComponentInChildren<MeshFilter>();
                if (mf != null) { mesh = mf.sharedMesh; tf = mf.transform; }
            }
            if (mesh == null || tf == null || mesh.vertexCount == 0) return false;

            var rend = go.GetComponentInChildren<Renderer>();
            var mat = rend != null ? rend.sharedMaterial : null;
            // The model's texture also drives the foliage ALPHA CULL (round 7: moss shatters
            // into its visible sprigs only — fully-transparent shards are dropped).
            var tex = mat != null ? mat.mainTexture as Texture2D : null;
            var fx = Diggles.Runtime.MeshShatterEffect.Spawn(
                mesh, tf, mat,
                go.transform.position.y - Diggles.Runtime.MeshShatterEffect.GroundDrop, tex);
            return fx != null;
        }

        // One pending-entry evaluation per 10 Hz logic tick — the native calls FUN_0058d370 once
        // per CGameThread::SrvDoThink (005d4250:87). Wired from DigglesGameSim's think step;
        // headless tests call it directly.
        public static void MapDecoTick(DigglesTclContext ctx)
        {
            try { MapDecoOf(ctx)?.Tick(); }
            catch (System.Exception e) { Debug.LogError($"[map_deco] tick failed: {e.Message}"); }
        }

        // Spawn one matured deco entry (the FUN_0058d370:334-419 spawn path) through the normal
        // `new` pipeline (obj_init + scene spawner + class_light + particle replay), then clear
        // the owner like authored template deco (template.tcl: `set_owner $objhandle -1`).
        // The class-exists gate stands in for the native's parse-time class filter
        // (FUN_00581d30:142-148 — exists + class+0xca0 == 0; see the port-deviation note).
        static void MapDecoSpawn(DigglesTclContext ctx, Diggles.Runtime.World.MapDecoBeautifier.PendingDeco p)
        {
            if (!ctx.Classes.ContainsKey(p.ClassName)) return;
            try
            {
                var h = CmdNew(ctx, new[]
                {
                    "new", p.ClassName, "",
                    FormatVec3(new Vector3(p.Px, p.Py, p.Pz)),
                    FormatVec3(new Vector3(p.Rx, p.Ry, p.Rz)),
                });
                if (int.TryParse(h.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var hh)
                    && ctx.Entities.TryGetValue(hh, out var e))
                {
                    e.Owner = -1;
                    // Native deco classes (SetFrontDummyClassesNoPhys etc., animinit.tcl:198 —
                    // `def_class … dummy` + class_disablescripting, "keine obj_init") never call
                    // set_selectable/set_hoverable, and the native flags DEFAULT OFF (bit 0x2 /
                    // 0x1 of the obj+0xdc flag record, set only by FUN_00681220 & sibling —
                    // interactive classes OPT IN, e.g. SetWeaponClasses animinit.tcl:375-376).
                    // The port's pick model defaults ON (opt-out vars), so beautifier deco must
                    // opt out here or it hover-auras and eats clicks.
                    e.VarsScope.Vars["_selectable"] = "0";
                    e.VarsScope.Vars["_hoverable"] = "0";
                    // Tag = "has a dummy record": the authored template init runs `set_physic
                    // $objhandle true` on every deco piece (Templates/urw_gng_001_a.tcl:8-31 →
                    // FUN_0067fba0 → FUN_005eb470), enrolling it for the per-carve DUMMYDES
                    // kill — MapDecoSpawned is the port's record flag (MapDecoCarveSweep).
                    e.MapDecoSpawned = true;
                    // Diagnostic: every beautifier spawn logs — a spawned-but-invisible support
                    // is then a render question, a missing line an upstream (scan/fit) one.
                    // Headless legs pinned green by MapDecoBeamTests.
                    Debug.Log($"[map_deco] new {p.ClassName} #{hh} @({p.Px:0.##},{p.Py:0.##},{p.Pz:0.##})"
                        + (e.GameObject != null ? $" go='{e.GameObject.name}'" : " go=none"));
                    // Presentation: deco slides out of the back wall on spawn (Play-mode only;
                    // headless spawns stay instant). See MapDecoSlide header.
                    if (e.GameObject != null && Application.isPlaying)
                        Diggles.Runtime.MapDecoSlide.BeginEmerge(e.GameObject, new Vector3(p.Px, p.Py, p.Pz));
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[map_deco] new {p.ClassName}: {e.Message}");
            }
        }

        // ----- marker-7 dormant scenery seeds (save load) -----
        // Register ONE marker-7 spatial-index record as a dormant presentation-only deco shell. B3 §3
        // (Docs/save-re-reports/B3-support-class-spatial.md) identifies these as one-shot dormant
        // scenery seeds the native per-tick LOD pass FUN_005d4b40 turns into LIGHTWEIGHT render
        // instances (not CObj bodies). The frame pump spawns an eligible class model and
        // DELIBERATELY bypasses CmdNew: no obj_init, no class_light, no particle
        // replay — the native seeds carry none, and suppressing them keeps the save-capture object/
        // class-light/particle graph byte-identical to the archive (the retained marker-7 bytes
        // round-trip verbatim). The seed is NOT registered in ctx.Entities; it is tracked in
        // ctx.MapDecoSeeds and carries MapDecoSpawned+MapDecoSaveSeed so the per-carve DUMMYDES
        // sweep (MapDecoCarveSweep) strands it when a later dig removes its footing. Position/
        // rotation are native world coords used directly (identity map, same as restored entities —
        // the spawner's NativeToLocal does the axis conversion).
        public static DigglesTclEntity? RegisterDecoSeed(
            DigglesTclContext ctx,
            string className,
            Vector3 position,
            Vector3 rotation,
            Diggles.Runtime.Save.OriginalSaveSpatialSeed? sourceRecord = null)
        {
            if (ctx == null) return null;
            if (string.IsNullOrEmpty(className) || !ctx.Classes.TryGetValue(className, out var cls))
                return null;

            var ent = new DigglesTclEntity
            {
                Class = cls,
                InstanceName = className,
                Position = position,
                Rotation = rotation,
                SpawnPosition = position,
                HasSpawnPosition = true,
                Owner = -1,
                MapDecoSpawned = true,
                MapDecoSaveSeed = true,
                MapDecoSeedRecord = sourceRecord,
            };
            ctx.MapDecoSeeds.Add(ent);
            return ent;
        }

        public static DigglesTclEntity? SpawnDecoSeed(
            DigglesTclContext ctx,
            string className,
            Vector3 position,
            Vector3 rotation,
            Transform parent,
            Diggles.Runtime.Save.OriginalSaveSpatialSeed? sourceRecord = null)
        {
            DigglesTclEntity? ent = RegisterDecoSeed(
                ctx,
                className,
                position,
                rotation,
                sourceRecord);
            if (ent == null) return null;
            if (MaterializeDecoSeed(ctx, ent, parent)) return ent;
            ctx.MapDecoSeeds.Remove(ent);
            return null;
        }

        public static bool MaterializeDecoSeed(
            DigglesTclContext ctx,
            DigglesTclEntity ent,
            Transform parent)
        {
            if (ctx?.EntitySpawner is not RuntimeWorldSpawner spawner || ent == null || parent == null)
                return false;
            if (ent.GameObject != null) return true;
            if (ent.MapDecoSeedMaterializationAttempted) return false;
            ent.MapDecoSeedMaterializationAttempted = true;

            bool previousSettle = spawner.SuppressSpawnSettle;
            spawner.SuppressSpawnSettle = true;
            try
            {
                using var _perf = Diggles.Runtime.Diagnostics.PerfTrace.Section(
                    "seed.materialize",
                    50);
                ent.GameObject = spawner.Spawn(ent, parent, registerClassLight: false);
                // Presentation-only prop: native map-deco is never pickable and never collides
                // with the placement/pick rays. Stripping the spawner's box colliders keeps the
                // 1800+ seed props of a loaded save out of every raycast (clicks aimed at
                // objects behind cave beams, placement-ghost snapping) and out of physics cost.
                if (ent.GameObject != null)
                {
                    foreach (Collider seedCollider in
                        ent.GameObject.GetComponentsInChildren<Collider>(true))
                    {
                        if (Application.isPlaying) UnityEngine.Object.Destroy(seedCollider);
                        else UnityEngine.Object.DestroyImmediate(seedCollider);
                    }
                }
                return ent.GameObject != null;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[map_deco] seed spawn {ent.Class?.Name}: {e.Message}");
                return false;
            }
            finally
            {
                spawner.SuppressSpawnSettle = previousSettle;
            }
        }

        public static int PumpDecoSeedLod(
            DigglesTclContext ctx,
            Vector2 focus,
            int scanBudget = 64,
            int materializeBudget = 2)
        {
            if (ctx == null || ctx.SequencePresentationDrain || ctx.SpawnParent == null
                || scanBudget <= 0 || materializeBudget <= 0 || ctx.MapDecoSeeds.Count == 0)
                return 0;

            int materialized = 0;
            int scanned = 0;
            while (scanned++ < scanBudget && materialized < materializeBudget
                && ctx.MapDecoSeeds.Count > 0)
            {
                if (ctx.MapDecoSeedLodCursor >= ctx.MapDecoSeeds.Count)
                    ctx.MapDecoSeedLodCursor = 0;
                DigglesTclEntity ent = ctx.MapDecoSeeds[ctx.MapDecoSeedLodCursor++];
                if (ent.MapDecoRetiring || ent.GameObject != null
                    || ent.MapDecoSeedMaterializationAttempted)
                    continue;
                byte lodTier = ent.MapDecoSeedRecord?.LodTier ?? 0;
                if (!Diggles.Runtime.World.MapDecoPresentationCull.ShouldMaterialize(
                        ent.Position,
                        lodTier,
                        Diggles.Runtime.World.MapDecoPresentationCull.ActiveDetail,
                        focus,
                        ent.FowHidden))
                    continue;

                long started = Diggles.Runtime.Diagnostics.HitchFamilyTimingDiag.Start();
                bool didMaterialize = MaterializeDecoSeed(ctx, ent, ctx.SpawnParent);
                Diggles.Runtime.Diagnostics.HitchFamilyTimingDiag.Record(
                    "seed.materialize",
                    started,
                    didMaterialize ? 1 : 0);
                if (didMaterialize) materialized++;
            }
            return materialized;
        }

        // Retire a marker-7 save seed: the SAME shatter/crumble presentation as MapDecoRetire, but
        // the seed lives in ctx.MapDecoSeeds (not ctx.Entities), so it is freed by destroying its
        // GameObject rather than through CmdDel. Removed from the seed list immediately so the
        // per-carve sweep never re-hits it while a Play-mode crumble is still animating.
        static void MapDecoRetireSeed(DigglesTclContext ctx, DigglesTclEntity e)
        {
            if (e.MapDecoRetiring) return;
            e.MapDecoRetiring = true;
            ctx.MapDecoSeeds.Remove(e);
            if (e.GameObject != null && Application.isPlaying)
            {
                if (Diggles.Runtime.MapDecoSlide.UseSlideRetreat)
                {
                    Diggles.Runtime.MapDecoSlide.BeginRetreat(
                        e.GameObject, e.Position, () => DestroySeedGameObject(e));
                    return;
                }
                if (ShatterEntity(ctx, e)) { DestroySeedGameObject(e); return; }
                try { ctx.Particles?.CreateTemp(20, e.Position, Vector3.zero, 2, 0.5f); }
                catch (System.Exception) { /* particles are cosmetic */ }
                Diggles.Runtime.MapDecoSlide.BeginCrumble(
                    e.GameObject, e.Position, () => DestroySeedGameObject(e));
            }
            else DestroySeedGameObject(e);
        }

        static void DestroySeedGameObject(DigglesTclEntity e)
        {
            if (e.GameObject == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(e.GameObject);
            else UnityEngine.Object.DestroyImmediate(e.GameObject);
            e.GameObject = null;
        }

        // ----- bulk-stub helpers -----

        // Helper: register a list of names as no-op stubs returning "0".
        // Skips any name already registered — protects real implementations
        // that were registered above this call.
        static void RegisterStubs(TclInterpreter interp, string[] names)
        {
            foreach (var n in names)
                if (!interp.CommandExists(n))
                    interp.RegisterCommand(n, (_, _) => "0");
        }

        // Names that should resolve to a quiet no-op when not implemented.
        static string[] GetBulkStubNames() => new[]
        {
            // Sequencer DSL (sq_* / do_* / Actor / Object / parse_* / sequence_* …) is NOT
            // stubbed here: it is defined as Tcl PROCS in classes/story/sequencer.tcl and runs
            // for real. Native stubs would shadow those procs (dispatch checks native first),
            // so the cinematic engine would never execute. The native LEAF primitives those
            // procs call (set_view, set_speechout, seq_audiostream, screenvibe, …) are
            // implemented separately. See Docs/CUTSCENE_SYSTEM.md.
            // Sound / audio
            "sound", "adaptive_sound", "snd_animlist",
            // Visual / FX
            "set_particlesource", "change_particlesource",
            "create_particlesource", "free_particlesource",
            "blow_particlesource", "obj_to_particle",
            "particle_processing",
            "set_texturevariation",
            "start_fade", "cancel_fade", "change_light", "lightning",
            "laserbeam", "delete_laserbeams", "delete_lightning",
            "screenvibe", "set_view_begin",
            "set_light_begin", "set_ground_begin", "set_fow_begin",
            "set_fov", "set_fogofwar", "set_brightness", "set_contrast",
            "load_scape_texture", "load_all_textures",
            "flush_all_textures", "loading_objdetail",
            "camctrlmapping",
            // UI / text
            // textwin/layout are registered by DocumentWindowSystem. ohlp_initstyle, paragraph,
            // linkline, questlog, and pickone are real Tcl procs in misc/onlinehelputils.tcl;
            // native fallbacks would shadow them because registered commands resolve first.
            "scroller", "scrollrange",
            "notifyflare", "tclhelp", "talkissue",
            "speechicon", "set_icon", "set_objicon", "set_objiconoffset",
            "set_objworkicons",
            // set_objname / set_objinfo are REAL now (native obj+0xD8 display name incl. the
            // `auto` names.txt draw + obj+0x360 info string) — do not re-stub; blank gnome names
            // in the selection menu of original-loaded saves was this stub.
            // Fight definitions, queries and action resolution are registered by FightSystem
            // before this fallback list.
            // Map / level
            "sm_add_event", "sm_add_temp", "sm_add_zone", "sm_create_map",
            "sm_def_temp_group", "sm_dig_message", "sm_draw_stone",
            "sm_force_zone", "sm_get_event", "sm_get_level_info",
            "sm_get_resolution", "sm_get_temppos", "sm_get_zone",
            "sm_log", "sm_map_get", "sm_map_move", "sm_map_set",
            "sm_mark_temparea", "sm_music_theme", "sm_register",
            "sm_reset", "sm_scan", "sm_send_message", "sm_set_digcount",
            "sm_set_event", "sm_set_temp", "sm_set_zone",
            "map_template", "map_setlayer2", "split_template", "map",
            // add_cave_skin_class + cave_skin are REAL now (the cave-skin prop-swap law,
            // FUN_00617570 / FUN_0066a9a0 / FUN_0058ba00) — do not re-stub.
            "generate_color_variation", "remove_black_fog",
            "reset_map", "save_map", "save_scene",
            "set_level", "show_loading", "show_maped", "show_fogofwar",
            // Level generator
            "lg_addfilterclass", "lg_dec_templatecount",
            "lg_get_level", "lg_get_objcount", "lg_get_temp_size",
            "lg_mark_area", "lg_set_area", "lg_set_callback",
            "lg_set_leveltype", "lg_set_objcount", "lg_set_starttemplate",
            "lg_set_starttemplatepro", "lg_set_templategroup",
            "lg_set_templategroupprops", "lg_set_templategroupvalue",
            "lg_set_templateprops", "lg_set_templateratio",
            "lg_set_templatevalue", "lg_sort_level", "lg_start",
            "lg_tp_addtemplates", "lg_tp_addtemplatesets", "lg_tp_clear",
            "lg_tp_log", "lg_tp_mapfilter", "lg_tp_objfilter",
            "lg_tp_setfilter", "template_set",
            // AI policy and native CAIManager queries are registered by AiSystem.
            "sparetime",
            // Inventory & tasklist
            "inv_add", "inv_check", "inv_cnt", "inv_cnt_raw", "inv_find",
            "inv_find_obj", "inv_find_raw", "inv_get", "inv_get_raw",
            "inv_getsize", "inv_list", "inv_rem", "is_contained",
            "is_wearing_pannier", "global_inv_rem",
            "tasklist_add", "tasklist_addfront", "tasklist_clear",
            "tasklist_cnt", "tasklist_find", "tasklist_get",
            "tasklist_list", "tasklist_rem",
            // Object queries / actions
            "obj_find", "obj_find_list", "obj_find_nearest",
            "obj_find_nearest_storable", "obj_find_nearest_storable_cb",
            "obj_find_nearest_type", "obj_find_own", "obj_find_own_list",
            "obj_find_var", "obj_list", "obj_next_visible", "obj_valid",
            "obj_eval", "link_obj", "sel",
            // NOTE: "destroy" is deliberately NOT stubbed — it is a Tcl PROC everywhere it is
            // called (sequencer.tcl:2153 — the shut_sequencer→destroy_overload/del path that
            // makes skip cleanup + the menu trigger loop work — plus brut.tcl:472,
            // spinne.tcl:861, Lorenbahn.tcl:449). A native stub shadows the proc (dispatch
            // checks native first) and silently killed the whole trigger destroy chain.
            "snap", "get_next_relocobj",
            // NOTE: "trigger" is NOT stubbed — the native trigger engine (TriggerService,
            // FUN_0060e920) registers it for real above.
            "action", "play",
            "set_classaniminfo", "get_classaniminfo", "reset_anim",
            "ref2path", "ref_checkvar", "ref_get", "ref_set",
            "path", "cwd", "proddump", "partner_info",
            // Class / def
            "inventionlinks", "init_techtree",
            "auto_choose_workingtime",
            "autoloadlevel", "Create_Instant_Trigger_Class",
            // The SetDummyClasses*/SetFrontDummyClasses*/SetWeaponClasses/SetCaveSkinClasses
            // GENERATORS are real Tcl procs (animinit.tcl:59-432) and MUST NOT be stubbed:
            // native commands shadow procs at dispatch, so the "0" stubs silently no-op'd every
            // deco/weapon dummy-class definition — Dummy_Gras/Moos/Liane/… never got def_class'd
            // and spawned as tolerant empty shells → pink placeholders. Create_Instant_Trigger_Class
            // stays stubbed for now — its generated trigger classes would double-drive TriggerService.
            // Misc
            "elf", "gametime", "locale", "cd", "lmsg",
            "set_diplomacy", "get_diplomacy", "calc_text_time",
            "log_mask", "logdebug", "logmod",
            // NOTE: "reset" is deliberately NOT stubbed — like "destroy" above it is a Tcl PROC
            // everywhere it is called (sequencer.tcl:355 — sequencer_activate:366 runs it to clear
            // actors/object_list/followaction_list per play; Lavawelt.tcl:570; the bare `reset`
            // lines in sequence scripts run inside sequencer triggers where the proc is in scope).
            // The native "0" stub shadowed the proc (dispatch checks native first), so on the
            // PERSISTENT menu trigger followaction_list never cleared between backdrops — a
            // previous scene's `sq_actor actionlist … loop` (e.g. start_004's disco) replayed on
            // the NEXT scene's actor 0: seq_enable popped the stale list instead of running
            // sequence_enable, the actor "danced" forever and the scene stalled at its first
            // sq_wait (MenuBackdropChainTests pins this).
            "startcache",
            "find_free_place", "find_free_place_self",
            "meminfo", "perfoptions", "perfwin", "playerinfo",
            "render_enable", "render_gray", "screen2world",
            // NOTE: "selection" + set/get_selectedobject are REAL (registered above with the
            // faithful SelectionSet) — kept out of the stub list so a reorder can't shadow them.
            "screen3world", "screen_mode",
            "set_ingame_loading", "set_split_load", "set_speechout",
            "set_objvariation", "set_run_info", "set_sequence",
            "horizon_clear", "horizon_rem", "horizon_set",
            "set_user_groups", "get_user_groups", "laser",
            "vector_pack", "vector_unpackx", "vector_unpacky",
            "vector_unpackz", "vector_setx", "vector_sety",
            "vector_setz", "vector_angle",
            "vector_abs", "vector_inbox", "vector_fix", "vector_fixdig",
            "vector_rotx", "vector_rotz",
            // Owner / attribs aux
            "set_owner_attrib", "get_owner_attrib_list",
            "reset_owner_attribs", "transfer_attribs",
            "set_processactions", "get_processactions",
            "fairy_move",
            "get_class_era", "set_maxprodera", "get_maxprodera",
            "fogofwar", "gameisloading",
            "gamestats", "gamedelayannounce", "gui_new_game",
            "uncapturemouse", "vtune", "waterdbg", "toggle_wf",
            // Production
            "prod_assignworker",
            "prod_gnome_get_last_workplace",
            "prod_gnome_get_preferred_workplace",
            "prod_gnome_last_workplace", "prod_gnome_preferred_workplace",
            "prod_gnome_state", "prod_gnomeidle", "prod_guest",
            "prod_valid",
            "set_prod_buildup", "get_prod_buildup",
            "set_prod_directevents", "get_prod_directevents",
            "set_prod_enabled", "get_prod_enabled",
            "set_prod_materialneed", "get_prod_materialneed",
            "set_prod_ownerstrength", "get_prod_ownerstrength",
            "set_prod_pack", "get_prod_pack",
            "set_prod_slot_cnt", "get_prod_slot_cnt",
            "get_prod_slot_buildable", "get_prod_slot_inventable",
            "get_prod_slot_invented", "get_prod_slot_list",
            "set_prod_toolneed", "get_prod_toolneed",
            "set_prod_unpack", "get_prod_unpack",
            "set_prodalloclock", "get_prodalloclock",
            "set_prodautoschedule", "get_prodautoschedule",
            "info_end_prod", "info_progress_prod", "info_start_prod",
            // Misc engine query getters
            "get_attrib_display_name",
            // "get_bestexp" + "get_expattrib" are REAL now (registered above) — the "0" stubs
            // silently wiped skills on every life-stage copy (z_methods/pzwerg/baby/reprod).
            "get_buildupstate", "set_buildupstate",
            "get_camerafollowdist", "get_cameramoving",
            "get_digedge",
            "get_escape_pos",
            "get_fogofwar", "get_framevel",
            "get_gnomeposition",
            "get_hmap", "get_ingame_loading",
            "get_instore",
            "get_lmap_blue", "get_lmap_green", "get_lmap_light",
            "get_lmap_red", "get_logactions",
            "get_map_height", "get_map_width", "get_material",
            "get_max_fow", "get_mpstartpos", "get_obj_logicfogskip",
            "get_obj_logicskip", "get_objectcollision",
            "get_pathlength", "get_pathrandflags",
            "get_place",   // get_place_info + get_place_long are REAL now (registered above; skipped here)
            "get_player_color",
            "get_posbottom", "set_posbottom",
            "get_rel_pos", "get_rendermaterial",
            "get_system_name", "get_vectorxz",
            "get_vel", "get_view",
            "get_walkresult", "get_worktime",
            "set_inventoryslotuse",
            "set_logactions", "set_pathrandflags",
            "set_renderlast", "set_rendermaterial",
            // Shader / material
            "shader", "Material", "textstage",
            "gnome_announce_dig", "gnome_failed_work", "gnome_idle",
            "gnome_stopped_work",
            "dig_apply", "dig_mark", "dig_next", "dig_resetid",
            "inc_playerdigmarkid", "is_dig_marked", "dist_between",
            "ground_fix", "ground_pos",
            "load_info", "load_done", "lnand", "lor", "lrem", "lrep", "lcount",
            "notifyflare", "peek", "poke", "print",
            "reconnect", "set_class_anim", "set_class_animset",
            "set_speechout", "talkissue", "tclhelp", "time_line_log",
            "time_schedule", "update_objdetail",
            "db_search", "db_findanim", "db_animlength",
            "net",
            "keybind", "inputmode", "option",
        };

        // `+foo args` → dispatch `foo args` (drops the `+`). Diggles uses
        // the prefix to mean "queue this for the sequencer to execute
        // later" — we collapse to immediate execution for now. Falls
        // through to the unknown handler / stub if `foo` isn't registered.
        static void RegisterPlusVariants(TclInterpreter interp)
        {
            var names = new[]
            {
                "sq_pen", "sq_actor", "sq_camera", "sq_object", "sq_wait",
                "sq_text", "sq_color", "sq_sound", "sq_screenvibe", "sq_activate",
                "do_action", "do_wait", "do_change", "do_elf", "do_fee",
                "do_text", "do_toolputaway",
                "adaptive_sound", "del", "set_anim", "set_roty", "set_rotz",
                "set_visibility", "set_particlesource", "change_particlesource",
                "free_particlesource", "create_particlesource",
                "start_fade", "cancel_fade", "gametime", "lightning",
                "link_obj", "set_pos", "set_autolight", "set_collision",
                "set_attrib", "set_textureanimation",
                "call_method", "set", "global", "foreach", "catch",
                "elf", "scroller", "option", "set_activegameplay",
                "inv_add", "opendoor",
            };
            foreach (var n in names)
            {
                var plus = "+" + n;
                string bareName = n;
                interp.RegisterCommand(plus, (interp2, a) =>
                {
                    a[0] = bareName;
                    var lst = new System.Collections.Generic.List<string>(a);
                    return interp2.EvalWords(lst);
                });
            }
        }

        // `-foo args` → end / cancel variant. Treat identically to `+`.
        static void RegisterMinusVariants(TclInterpreter interp)
        {
            var names = new[]
            {
                "sound", "sq_sound", "sq_screenvibe", "do_text", "do_elf",
                "do_fee", "screenvibe", "scroller", "if",
            };
            foreach (var n in names)
            {
                var minus = "-" + n;
                string bareName = n;
                interp.RegisterCommand(minus, (interp2, a) =>
                {
                    a[0] = bareName;
                    var lst = new System.Collections.Generic.List<string>(a);
                    return interp2.EvalWords(lst);
                });
            }
        }

        // ----- helpers -----

        // Throws when a class_* command runs outside a def_class body —
        // unless the context is in tolerant mode (real-file smoke tests).
        // In tolerant mode caller receives a throwaway class object that
        // accepts the writes harmlessly.
        static DigglesClass? scratchClass;
        private static readonly ConditionalWeakTable<DigglesClass, ClanFlagClassMetadata>
            s_clanFlagClasses = new();

        private sealed class ClanFlagClassMetadata
        {
            public ClanFlagClassMetadata(DigglesTclContext context, Vector2 offset)
            {
                Context = context;
                Offset = offset;
            }

            public DigglesTclContext Context { get; }
            public Vector2 Offset { get; }
        }

        static DigglesClass RequireClass(DigglesTclContext ctx)
        {
            if (ctx.CurrentClass != null) return ctx.CurrentClass;
            if (ctx.TolerateMissingClass)
                return scratchClass ??= new DigglesClass { Name = "_scratch_" };
            throw new TclException("class_* command outside def_class body");
        }

        // Lenient: a non-numeric value (a stubbed "0"-ish result, an unresolved pen name, an
        // "Auto" token, …) degrades to 0 instead of throwing — matching ParseVec3 and the
        // engine's tolerant-by-design style (keeps sequence stepping robust).
        static float ParseFloat(string s)
            => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;

        static int NativeRound(float value)
            => value >= 0f ? Mathf.FloorToInt(value + 0.5f) : Mathf.CeilToInt(value - 0.5f);

        static void RemoveHorizonRegionAt(DigglesTclContext context, Vector3 position)
        {
            for (int index = 0; index < context.HorizonRegions.Count; index++)
            {
                Vector3 delta = context.HorizonRegions[index].Position - position;
                if (delta.x * delta.x + delta.y * delta.y >= 1f) continue;
                context.HorizonRegions.RemoveAt(index);
                return;
            }
        }

        // Round-trippable float → Tcl string ("R" = shortest exact).
        static string Ftos(float f) => f.ToString("R", CultureInfo.InvariantCulture);

        // Cutscene subtitle reading time (seconds per character) for calc_text_time.
        const float CutsceneTextSecondsPerChar = 0.06f;
        // Parse a Tcl "r g b" colour list → Color32 (missing components → 255).
        static Color32 ParseColor(string s)
        {
            var p = TclList.Parse(s);
            byte C(int idx) => idx < p.Count && byte.TryParse(p[idx], NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) ? b : (byte)255;
            return new Color32(C(0), C(1), C(2), 255);
        }

        // Try entity resolution without throwing — returns null instead.
        // Used by query commands that prefer empty/zero returns to errors.
        static DigglesTclEntity? ResolveEntityOrNull(DigglesTclContext ctx, string handleOrThis)
        {
            if (handleOrThis == "this" || handleOrThis == "$this" || handleOrThis == ".")
                return ctx.This;
            return ctx.GetEntity(handleOrThis);
        }

        // `set_objname OBJ auto GENDER` name pool — Scripts/classes/zwerg/names.txt: a MALE
        // header, male names one per line, then a FEMALE header and female names (cp1252).
        static readonly System.Collections.Generic.Dictionary<string, (string[] male, string[] female)>
            _autoNamePools = new();

        static string PickAutoObjName(DigglesTclContext ctx, string gender)
        {
            string root = ctx.DataRoot ?? "";
            if (!_autoNamePools.TryGetValue(root, out var pools))
            {
                var male = new System.Collections.Generic.List<string>();
                var female = new System.Collections.Generic.List<string>();
                try
                {
                    string path = System.IO.Path.Combine(root, "Scripts", "classes", "zwerg", "names.txt");
                    var current = male;
                    foreach (var raw in System.IO.File.ReadAllLines(
                        path, System.Text.Encoding.GetEncoding("ISO-8859-1")))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0) continue;
                        if (line == "MALE") { current = male; continue; }
                        if (line == "FEMALE") { current = female; continue; }
                        current.Add(line);
                    }
                }
                catch (System.Exception e)
                {
                    UnityEngine.Debug.LogWarning($"[Tcl] set_objname auto: names.txt unavailable ({e.Message})");
                }
                pools = (male.ToArray(), female.ToArray());
                _autoNamePools[root] = pools;
            }
            string[] pool = gender == "female" ? pools.female : pools.male;
            if (pool.Length == 0) return "";
            return pool[UnityEngine.Random.Range(0, pool.Length)];
        }

        // vector_add a b / vector_sub a b → 3-element list result.
        static string VectorOp(string[] args, System.Func<float, float, float> op)
        {
            if (args.Length < 3) return "0 0 0";
            var u = ParseVec3(args[1]);
            var v = ParseVec3(args[2]);
            return FormatVec3(new Vector3(op(u.x, v.x), op(u.y, v.y), op(u.z, v.z)));
        }

        static string VectorScalar(string[] args, System.Func<float, float, float> op)
        {
            if (args.Length < 3) return "0 0 0";
            var u = ParseVec3(args[1]);
            float s = ParseFloat(args[2]);
            return FormatVec3(new Vector3(op(u.x, s), op(u.y, s), op(u.z, s)));
        }

        static Vector3 ParseVec3(string s)
        {
            // Lenient: take up to 3 numbers, missing components → 0. Vector math on a
            // not-yet-implemented engine value (stubs return "0") then degrades to a
            // zero vector instead of throwing/spamming during the real class load.
            var parts = TclList.Parse(s);
            return new Vector3(
                parts.Count > 0 ? ParseFloat(parts[0]) : 0f,
                parts.Count > 1 ? ParseFloat(parts[1]) : 0f,
                parts.Count > 2 ? ParseFloat(parts[2]) : 0f);
        }

        static string FormatVec3(Vector3 v)
            => $"{v.x.ToString("R", CultureInfo.InvariantCulture)} " +
               $"{v.y.ToString("R", CultureInfo.InvariantCulture)} " +
               $"{v.z.ToString("R", CultureInfo.InvariantCulture)}";

        // ----- sound helpers -----

        // Resolve a script-given data path ("data/sound/specials/foo.wav") to an absolute file
        // under ctx.DataRoot; null when the file doesn't exist. The leading "data/" segment is the
        // original game root prefix — our DataRoot already points AT data/.
        internal static string? ResolveDataFile(DigglesTclContext ctx, string path)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(ctx.DataRoot)) return null;
            string rel = path.Replace('\\', '/');
            if (rel.StartsWith("data/", System.StringComparison.OrdinalIgnoreCase)) rel = rel.Substring(5);
            string full = System.IO.Path.Combine(ctx.DataRoot, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
            return System.IO.File.Exists(full) ? full : null;
        }

        // `sound anim` material/room validation (values are then discarded — see the command).
        // Material table: FUN_00643a60:374-409; room table::419-442.
        static bool IsSoundMaterial(string s)
        {
            switch (s.ToLowerInvariant())
            {
                case "any": case "sand": case "soil": case "stone":
                case "wood": case "metal": case "water": case "lava": return true;
                default: return false;
            }
        }

        static bool IsSoundRoom(string s)
        {
            switch (s.ToLowerInvariant())
            {
                case "any": case "tunnel": case "small": case "medium": case "big": return true;
                default: return false;
            }
        }

        // ----- def_class -----

        // def_class NAME MATERIAL TYPE FLAG CAPABILITIES BODY
        static string CmdDefClass(DigglesTclContext ctx, string[] args)
        {
            if (args.Length != 7)
                throw new TclException(
                    "wrong # args: should be \"def_class name material type flag capabilities body\"");
            var cls = new DigglesClass
            {
                Name = args[1],
                Material = args[2],
                Kind = args[3],
                Flag = int.Parse(args[4], CultureInfo.InvariantCulture),
                Capabilities = TclList.Parse(args[5]).ToArray(),
                Body = args[6],
            };
            ctx.Classes[cls.Name] = cls;

            // Execute body with CurrentClass set so class_* commands attach to it.
            var prev = ctx.CurrentClass;
            ctx.CurrentClass = cls;
            try { ctx.Interp.Eval(args[6]); }
            finally { ctx.CurrentClass = prev; }
            return "";
        }

        // def_idiobjclass NAME BODY — simpler 2-arg form for ‘idiot objects’
        // (Diggles internal: rendering-only, no full class machinery).
        static string CmdDefIdiObjClass(DigglesTclContext ctx, string[] args)
        {
            if (args.Length != 3)
                throw new TclException("wrong # args: should be \"def_idiobjclass name body\"");
            var cls = new DigglesClass
            {
                Name = args[1],
                Material = "none",
                Kind = "dummy",
                Body = args[2],
                // Idiobj = rendering-only: native never gives these objects a Tcl interp
                // (echo-diff: the original re-saved our rasen deco with no interp state even
                // though the generator's class_disablescripting line is commented out — the
                // idiobj machinery itself is the scripting cut-off).
                DisableScripting = true,
            };
            ctx.Classes[cls.Name] = cls;
            var prev = ctx.CurrentClass;
            ctx.CurrentClass = cls;
            try { ctx.Interp.Eval(args[2]); }
            finally { ctx.CurrentClass = prev; }
            return "";
        }

        // class_light {ox oy oz} range {r g b} [flag] — a point light on each instance of the
        // class (LightSrc markers, torches). Colour components are floats 0..1 (NOT bytes).
        static string CmdClassLight(DigglesTclContext ctx, string[] args)
        {
            if (args.Length < 4) return "";
            var cls = RequireClass(ctx);
            var ofs = TclList.Parse(args[1]);
            var col = TclList.Parse(args[3]);
            float At(System.Collections.Generic.IReadOnlyList<string> l, int idx)
                => idx < l.Count ? ParseFloat(l[idx]) : 0f;
            int flag = 0;
            if (args.Length >= 5)
                int.TryParse(args[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out flag);
            cls.Light = new LightSpec
            {
                Ox = At(ofs, 0), Oy = At(ofs, 1), Oz = At(ofs, 2),
                Range = ParseFloat(args[2]),
                R = At(col, 0), G = At(col, 1), B = At(col, 2),
                Flag = flag,
            };
            return "";
        }

        private static string CmdClassFlagOffset(DigglesTclContext ctx, string[] args)
        {
            if (args.Length != 3)
                throw new TclException("wrong # args: should be \"class_flagoffset x height\"");

            // CTclClassDefMethods::ClassFlagOffset stores {x, 1, height} at class
            // +0xCEC/+0xCF0/+0xCF4 (FUN_00613fc0:110-129). Presence of this command is the
            // native per-class flag declaration; there is no building-name allowlist.
            var cls = RequireClass(ctx);
            s_clanFlagClasses.Remove(cls);
            s_clanFlagClasses.Add(
                cls,
                new ClanFlagClassMetadata(
                    ctx,
                    new Vector2(ParseFloat(args[1]), ParseFloat(args[2]))));
            return "";
        }

        // Post-activation sweep for restored sessions: staging skips flag presentation on the
        // inactive candidate root (the icon-lifecycle load law), so the load service re-runs
        // the producer once the hierarchy is active — the native first-tick equivalent.
        internal static void RefreshAllBuildingClanFlags(DigglesTclContext ctx)
        {
            foreach (var entity in ctx.Entities.Values)
                RefreshBuildingClanFlag(entity);
        }

        private static void RefreshBuildingClanFlag(DigglesTclEntity entity)
        {
            var owner = entity.GameObject;
            if (owner == null) return;

            // CProd::Tick's native producer removes the CScapeIcon when boxed or when the class
            // has no flag offset (FUN_005edea0:92-104). The port seats the equivalent child on
            // the construction-complete edge requested by the building lifecycle.
            bool boxed = entity.VarsScope.Vars.TryGetValue("_boxed", out string boxedValue) &&
                TruthyFlagVar(boxedValue);
            if (entity.Class == null ||
                !s_clanFlagClasses.TryGetValue(entity.Class, out ClanFlagClassMetadata metadata) ||
                !entity.BuildupComplete || boxed || entity.Owner < 0)
            {
                Diggles.Runtime.BuildingClanFlag.Detach(owner);
                return;
            }

            int variant = entity.Owner < metadata.Context.OwnerRace.Length
                ? metadata.Context.OwnerRace[entity.Owner]
                : 5;
            // Non-network CScapeIcon selection reads the owner's race at player+8 and uses
            // variants 0..4, with 5 as the green fallback (FUN_004c3c90:66-74).
            if (variant is < 0 or > 4) variant = 5;
            bool visible = !entity.FowHidden &&
                (!entity.VarsScope.Vars.TryGetValue("_visibility", out string visibility) ||
                 TruthyFlagVar(visibility));
            Diggles.Runtime.BuildingClanFlag.Attach(
                owner,
                metadata.Offset,
                variant,
                metadata.Context.DataRoot,
                visible);
        }

        // class_particle <setId> {ox oy oz} {vx vy vz} <count> <argA=rate> <argB> <argC> <flag>
        // Records a per-class emitter template; replayed onto each instance at spawn (ReplayParticleEmitters
        // in CmdNew) so deco objects (waterfalls/crystals/lava/troll-smoke) auto-emit. Was a no-op stub.
        static string CmdClassParticle(DigglesTclContext ctx, string[] args)
        {
            if (args.Length < 4) return "";
            var cls = RequireClass(ctx);
            var off = ParseVec3(args[2]);
            var vel = ParseVec3(args[3]);
            cls.ParticleEmitters.Add(new ParticleEmitterDef
            {
                SetId = (int)ParseFloat(args[1]),
                Ox = off.x, Oy = off.y, Oz = off.z,
                Vx = vel.x, Vy = vel.y, Vz = vel.z,
                Count = args.Length > 4 ? (int)ParseFloat(args[4]) : 32,
                Rate = args.Length > 5 ? (int)ParseFloat(args[5]) : 1,
                Flag = args.Length > 8 ? ParseFloat(args[8]) : 0f,
            });
            return "";
        }

        static string CmdClassDefaultAnim(DigglesTclContext ctx, string[] args)
        {
            // class_defaultanim obj.anim ?playMode?
            if (args.Length < 2 || args.Length > 3)
                throw new TclException("wrong # args: should be \"class_defaultanim obj.anim ?mode?\"");
            var dcls = RequireClass(ctx);
            dcls.DefaultAnim = args[1];
            if (args.Length == 3 &&
                int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mode))
                dcls.DefaultAnimMode = mode;
            return "";
        }

        static string CmdClassObjSnapClass(DigglesTclContext ctx, string[] args)
        {
            if (args.Length != 3)
                throw new TclException(
                    "wrong # args: should be \"class_objsnapclass class range\"");
            var cls = RequireClass(ctx);
            cls.ObjectSnapClass = args[1];
            cls.ObjectSnapRange = ParseFloat(args[2]);
            return "";
        }

        // set_class_anim <state> <obj>.<anim> [extra] — builds the per-class
        // state→clip base map the DigglesAnimController resolves against. A few
        // lines carry a trailing face/eye payload we don't use yet.
        static string CmdSetClassAnim(DigglesTclContext ctx, string[] args)
        {
            if (args.Length < 3) return "";
            RequireClass(ctx).AnimMap[args[1]] = args[2];
            return "";
        }

        // set_class_animset <id> { {state <obj>.<anim>} {state <obj>.<anim>}... }
        // — builds a named animset (gait override map) indexed by id.
        static string CmdSetClassAnimset(DigglesTclContext ctx, string[] args)
        {
            if (args.Length < 3) return "";
            if (!int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                return "";
            var cls = RequireClass(ctx);
            if (!cls.Animsets.TryGetValue(id, out var set))
            {
                set = new Dictionary<string, string>();
                cls.Animsets[id] = set;
            }
            foreach (var pairStr in TclList.Parse(args[2]))
            {
                var pair = TclList.Parse(pairStr);
                if (pair.Count < 2) continue;
                set[pair[0]] = pair[1];
            }
            return "";
        }

        // action <obj> <verb> <args...> [finishcode] [breakcode]
        // The engine action layer: at most one action per object; when it
        // finishes the runtime evals finishcode (DigglesGameSim), and breakcode
        // if it is interrupted (here, when replaced). Verbs: anim, wait
        // (rotate/walk are brief stubs for now so callbacks still fire).
        static string CmdAction(DigglesTclContext ctx, string[] args)
        {
            if (args.Length < 3)
            {
                if (WalkExecutorDiagnostics)
                    Debug.Log("[walk-executor] action-precondition REJECT: action requires an object and verb");
                return "";
            }
            var ent = ctx.ResolveEntity(args[1]);
            string verb = args[2];
            IEntityAction act;
            string? finish = null, brk = null;
            int walkTraceId = 0;
            switch (verb)
            {
                case "anim":
                    finish = args.Length > 4 ? args[4] : null;
                    brk = args.Length > 5 ? args[5] : null;
                    act = new AnimAction(GetController(ent), args.Length > 3 ? args[3] : "");
                    break;
                case "wait":
                    finish = args.Length > 4 ? args[4] : null;
                    brk = args.Length > 5 ? args[5] : null;
                    double sec = args.Length > 3 &&
                        double.TryParse(args[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 0.0;
                    act = new WaitAction(sec);
                    break;
                case "walk":
                {
                    walkTraceId = BeginWalkExecutorTrace();
                    if (args.Length <= 3)
                    {
                        TraceWalkExecutor(
                            walkTraceId,
                            "action-precondition",
                            $"REJECT #{ent.Handle}: missing walk argument");
                        throw new TclException(
                            "wrong # args: should be \"action obj walk options ?finishcode? ?breakcode?\"");
                    }
                    // FAITHFUL option form (what the real class scripts call):
                    // action <obj> walk "-target {x y z} -animsets {walk run …} -speedtype N" [finish] [break]
                    // → MetaWalk via the nav graph (the single C# move executor); gait
                    // (walk/run) chosen by speedtype + distance, animsets from Tcl.
                    string walkArgument = args[3].Trim();
                    if (walkArgument.StartsWith("-", System.StringComparison.Ordinal))
                    {
                        finish = args.Length > 4 ? args[4] : null;
                        brk = args.Length > 5 ? args[5] : null;
                        TraceWalkExecutor(
                            walkTraceId,
                            "action-precondition",
                            $"ACCEPT #{ent.Handle}: option walk state={ent.CurrentState ?? "-"} " +
                            $"enabled={ent.StateEnabledCount} prior={ent.ActiveAction?.GetType().Name ?? "-"}");
                        act = BuildMetaWalkFromOptions(ctx, ent, args[3], walkTraceId);
                        break;
                    }
                    // DISTANCE form (demo/legacy): action <obj> walk <distance> [animset]...
                    // forward along facing; turns are separate rotate actions.
                    if (!float.TryParse(
                            args[3],
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out float dist))
                    {
                        TraceWalkExecutor(
                            walkTraceId,
                            "action-precondition",
                            $"REJECT #{ent.Handle}: '{args[3]}' is neither a walk option list nor a scalar legacy distance");
                        throw new TclException(
                            "walk argument must be an option list beginning with -target/-randompath or a scalar legacy distance");
                    }
                    int aset = 0, fi = 4;
                    if (args.Length > 4 &&
                        int.TryParse(args[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var asv))
                    { aset = asv; fi = 5; }
                    finish = args.Length > fi ? args[fi] : null;
                    brk = args.Length > fi + 1 ? args[fi + 1] : null;
                    TraceWalkExecutor(
                        walkTraceId,
                        "action-precondition",
                        $"ACCEPT #{ent.Handle}: legacy distance={dist:0.###} animset={aset}");
                    act = new WalkAction(GetController(ent), GetTransform(ent), dist, aset);
                    break;
                }
                case "rotate":
                    // action <obj> rotate <angle> — the sequencer's convention is an ABSOLUTE
                    // Y facing in RADIANS (do_action: front=0, left=π/2, back=π, right=3π/2, or a
                    // computed get_anglexz). Turn the entity to it; a brief beat lets the finish-
                    // code fire after the turn (a Play turn-clip is cosmetic over this).
                    finish = args.Length > 4 ? args[4] : null;
                    brk = args.Length > 5 ? args[5] : null;
                    // args[3] = "<angle> ?animset?" (FUN_00416da0:64-77 reads an optional animset id
                    // after the angle — rotate_toangle passes it for tired/box turn-clip variants).
                    var rToks = TclList.Parse(args.Length > 3 ? args[3] : "");
                    float ang = rToks.Count > 0 &&
                        float.TryParse(rToks[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var rr) ? rr : ent.Rotation.y;
                    // A rotate WITHOUT an animset defaults to 0 = STANDARDWALK, NOT "keep current":
                    // the native Init zeroes action+0x24 when the list has no 2nd element
                    // (FUN_00416da0:66) and the slot resolve reads the standstill pose THROUGH that
                    // animset (FUN_00417280:36-39) — so every plain rotate re-poses via animset 0
                    // (mann.standard). That reset is load-bearing: prod_turnfront after the boxed
                    // product drop (feuerstelle.tcl:62 → rotate_tofront, z_dignwalk.tcl:954) is
                    // what drops ANIMSET_WALKWITHBOX 7 (kiste_stand) — the old -1 "keep current"
                    // default left the gnome frozen in the box-carry pose forever.
                    // rotate_towards_with_box/rotate_toangle pass an explicit id and are unaffected.
                    int rAnimset = rToks.Count > 1 &&
                        int.TryParse(rToks[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ra) ? ra : 0;
                    ent.Rotation = new Vector3(ent.Rotation.x, ang, ent.Rotation.z);
                    // Characters TURN to the target facing playing a turn clip (and interrupting any
                    // prior loop like lookdown), via the OUTER transform (SpawnFaceWorld) consistent
                    // with the walk. Props fall back to the inner-rotation reflect + a brief wait.
                    var dobj = ent.GameObject != null ? ent.GameObject.GetComponent<DigglesObject>() : null;
                    if (dobj != null)
                        act = new TurnToFaceAction(dobj, GetController(ent), dobj.FaceRotationForNativeAngle(ang), rAnimset);
                    else { ReflectRotation(ctx, ent); act = new WaitAction(0.2); }
                    break;
                case "fly":
                    // action <obj> fly "-range R [-around {x y z}] [-target {x y z}]" [finish] [break]
                    // Butterfly wander (CAct_Fly): one glide to a random/explicit point; the idle
                    // state re-arms it via the finishcode for endless fluttering.
                    finish = args.Length > 4 ? args[4] : null;
                    brk = args.Length > 5 ? args[5] : null;
                    act = BuildFlyFromOptions(ctx, ent, args.Length > 3 ? args[3] : "");
                    break;
                case "move":
                {
                    // action <obj> move "{x y z} speed" [finish] [break] — CAct_Move (factory
                    // FUN_00405e10 "move" → ctor FUN_00416680; Init FUN_00416720): a straight
                    // linear glide from the object's CURRENT pos to the target. The sequencer's
                    // `sq_object move N <pen> <speed>` (sequencer.tcl:939-945) builds exactly this
                    // arg string — start_002's Hand of God descent. See MoveAction for the
                    // step-count semantics.
                    finish = args.Length > 4 ? args[4] : null;
                    brk = args.Length > 5 ? args[5] : null;
                    var mToks = TclList.Parse(args.Length > 3 ? args[3] : "");
                    Vector3 mTarget = mToks.Count > 0 ? ParseVec3(mToks[0]) : ent.Position;
                    float mSpeed = mToks.Count > 1 ? ParseFloat(mToks[1]) : 0f;
                    act = new MoveAction(ctx, ent, mTarget, mSpeed);
                    break;
                }
                default:
                    // rotate / walk / other — not yet implemented. Run a brief
                    // action so the finishcode still fires (scripts don't hang).
                    finish = args.Length > 4 ? args[4] : (args.Length == 4 ? args[3] : null);
                    brk = args.Length > 5 ? args[5] : null;
                    act = new WaitAction(0.05);
                    break;
            }
            // Chain-break inheritance: an action created DURING a finishcode (e.g. hatonhand/hatonhead,
            // started by the previous link's finish) with an EMPTY breakcode inherits the chain's
            // pending break (hatongone's create_muetze). So interrupting any link still runs the
            // deferred cleanup — the urw_3 walk replaces hatonhead → create_muetze fires → hat appears.
            if (string.IsNullOrEmpty(brk) && !string.IsNullOrEmpty(ctx.ChainBreak))
                brk = ctx.ChainBreak;

            // Replacing an in-flight action fires its breakcode — in the owning entity's object
            // scope (per-object `global`), same as the finishcode. The urw_3 hat chain's OUTER
            // breakcode calls create_muetze; without per-object scope the 5 Wiggles' hats
            // cross-delete (shared current_muetze_ref) and none attach during the cutscene.
            // CActWalk::Break (FUN_00407b30:12): interrupting a WALK leaves get_walkresult == 1
            // (WS_WALKABORTED) — the walkfail idiom `4 != [get_walkresult]` treats 1 and 3 alike,
            // but anything testing ==2 must see the walk is no longer in flight.
            if (ent.ActiveAction is WalkResultAction or RetryWalkAction or WalkGuardAction or RestoredWalkAction
                && ent.WalkResult == 2)
                ent.WalkResult = 1;
            if (ent.ActiveAction is IWaterReactiveWalk waterWalk)
                waterWalk.SetWaterMovement(false);
            if (ent.ActiveAction != null)
                InterruptElevatorRide(ctx, ent.Handle);
            if (ent.ActiveAction != null)
                RestoreActionPlaybackSpeed(ent);
            if (ent.ActiveAction != null && !string.IsNullOrEmpty(ent.ActionBreak))
            {
                var b = ent.ActionBreak;
                ent.ActiveAction = null; ent.ActionFinish = null; ent.ActionBreak = null;
                RunInEntityContext(ctx, ent, b!, "breakcode");
            }
            if (WorkDebug)   // guard: this fires on EVERY action dispatch — no eager interpolation
                WorkLog(ctx, ent, $"action SET verb='{verb}' → {act?.GetType().Name ?? "null"}");
            ent.ActiveAction = act;
            ent.ActionFinish = finish;
            ent.ActionBreak = brk;
            if (verb == "walk")
                TraceWalkExecutor(
                    walkTraceId,
                    "action-precondition",
                    $"INSTALLED #{ent.Handle}: {act.GetType().Name} finish={(string.IsNullOrEmpty(finish) ? "-" : "set")} " +
                    $"break={(string.IsNullOrEmpty(brk) ? "-" : "set")}");
            // CAct_Anim::Init starts the clip and stamps the deadline SYNCHRONOUSLY
            // (FUN_004085d0/FUN_004082a0:147: action+0x20 = gameclock + frames/10 — before
            // `action … anim` returns to Tcl). The port's AnimAction lazily started on its
            // FIRST phase-2b Tick — one sim step AFTER the pop — so every rep of a tasklist
            // anim chain cost clip + one dead logic tick. For the 1-frame aufbauen work taps
            // of prod_buildup_rep (z_work_common.tcl:473-508: per rep exactly one
            // `prod_anim $bani`, no waits) that is 0.2 s instead of 0.1 s — construction ran
            // at HALF the original speed. Tick(0)
            // starts the clip + stamps the duration now; the dt=0 first tick consumes nothing.
            if (verb == "anim") act.Tick(0.0);
            // CActWalk's CONSTRUCTOR stamps the pathfind slot SYNCHRONOUSLY — FUN_00407430:63-65
            // writes action+0x24 = 2 AND obj+0x248 = 2 (WS_NOTFINISHED) before `action … walk`
            // even returns to Tcl. The crowd dispersal logic reads NEIGHBOURS' walkresult
            // `!= 2` to decide who side-steps (act_when_idle z_procs.tcl:1273-1278/1301-1309;
            // sparetime_find_talker z_spare_fun.tcl:375 skips `== 2`): with the old first-Tick
            // stamp, every clustered gnome still read its already-moving neighbours as
            // "standing" within the SAME 10 Hz think pass, so ALL of them hopped EVERY pass —
            // natively the later thinkers see the early movers' 2 and STAY (anchors), which is
            // what lets the post-cutscene camp cluster relax instead of shuffling forever.
            if (verb == "walk") ent.WalkResult = 2;
            return "";
        }

        // tasklist_get <obj> [idx] — a PURE READ (the original FastArray get). The Tcl pump idiom is
        // `tasklist_get this 0` THEN `tasklist_rem this 0` (z_work_states.tcl:55-57); an earlier revision
        // popped inside get, so once tasklist_rem was fixed to remove by INDEX the pair consumed TWO
        // tasks per pass — every second task (e.g. dig_continue right after walk_pos_dig) was
        // silently destroyed un-executed and the dig chain looped forever.
        static string CmdTasklistGet(DigglesTclContext ctx, string[] args)
        {
            var list = ctx.ResolveEntity(args[1]).TaskList;
            int idx = args.Length > 2
                && int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var k) ? k : 0;
            return idx >= 0 && idx < list.Count ? list[idx] : "";
        }

        static DigglesAnimController? GetController(DigglesTclEntity ent)
        {
            // GetComponentInChildren (includes self): the controller may live on
            // the entity's main GO (RuntimeLibrarySpawner / demo) OR on an inner
            // "Model" child (RuntimeWorldSpawner — world coordinate convention).
            return ent.GameObject != null
                ? ent.GameObject.GetComponentInChildren<DigglesAnimController>()
                : null;
        }

        // Walk actions temporarily write the animator cadence. Replacement bypasses their
        // natural stop phase, so the action owner must restore that modifier before dispatching
        // another order (including a ladder segment that reads the current playback cadence).
        internal static void RestoreActionPlaybackSpeed(DigglesTclEntity ent)
        {
            var animator = GetController(ent)?.Animator;
            if (animator != null) animator.PlaybackSpeed = 1f;
        }

        static Transform? GetTransform(DigglesTclEntity ent)
            => ent.GameObject != null ? ent.GameObject.transform : null;

        // Rest an object on the terrain below it — a stand-in for the original's drop physics (a
        // full physics pass is a separate system). Raycast straight down and shift the GO so its
        // renderer-bounds bottom sits on the highest surface that isn't the object itself. Best-
        // effort: no hit ⇒ leave it where it is. (#5 boxed crate floated ~1u above the ground.)
        static void SnapToGround(GameObject? go)
        {
            if (go == null) return;
            var rend = go.GetComponentInChildren<MeshRenderer>();
            if (rend == null) return;
            Physics.SyncTransforms();
            var origin = go.transform.position + Vector3.up * 16f;
            var hits = Physics.RaycastAll(origin, Vector3.down, 64f, ~0, QueryTriggerInteraction.Ignore);
            float groundY = float.NegativeInfinity; bool found = false;
            foreach (var h in hits)
            {
                if (h.collider.transform.IsChildOf(go.transform)) continue;   // skip the crate itself
                if (h.point.y > groundY) { groundY = h.point.y; found = true; }
            }
            if (!found) return;
            float delta = groundY - rend.bounds.min.y;     // box bottom → ground
            var p = go.transform.position;
            go.transform.position = new Vector3(p.x, p.y + delta, p.z);
        }

        // Parse `action fly` options (`-range R [-around {x y z}] [-target {x y z}] [-swim B]`) and build a
        // FlyAction. Picks ONE destination now (an explicit -target, else a random spot within `range`
        // of the around-point / the entity's current native pos); the idle state re-arms it via the
        // finishcode for perpetual fluttering. Native coords → world via the spawn root (like walk).
        static IEntityAction BuildFlyFromOptions(DigglesTclContext ctx, DigglesTclEntity ent, string options)
        {
            float range = Diggles.Runtime.Tcl.Engine.FlyAction.DefaultRange;
            Vector3 around = ent.Position; bool hasAround = false;
            Vector3 explicitTarget = default; bool hasTarget = false;
            bool swim = false;
            var toks = TclList.Parse(options);
            for (int i = 0; i + 1 < toks.Count; i += 2)
            {
                string key = toks[i], val = toks[i + 1];
                if (key == "-range")
                    float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out range);
                else if (key == "-swim")
                    swim = ParseTclBool(val);
                else if (key == "-around" || key == "-target")
                {
                    var c = TclList.Parse(val);
                    if (c.Count >= 3
                        && float.TryParse(c[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                        && float.TryParse(c[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                        && float.TryParse(c[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
                    {
                        if (key == "-around") { around = new Vector3(x, y, z); hasAround = true; }
                        else { explicitTarget = new Vector3(x, y, z); hasTarget = true; }
                    }
                }
            }

            var tf = GetTransform(ent);
            if (tf == null) return new WaitAction(0.05);
            if (range <= 0f) range = Diggles.Runtime.Tcl.Engine.FlyAction.DefaultRange;

            // Pick the destination in NATIVE coords (x=east, y=depth small box; z=height tiny jitter),
            // matching the original ±range wander, then convert to world the same way walk does.
            // Anchor: explicit -around (tethered), else the current pos (the original "free" wander).
            Vector3 center = hasAround ? around : ent.Position;
            Vector3 worldTarget = hasTarget
                ? (!swim || ctx.Fluids.IsUnderwater(explicitTarget)
                    ? FlyNativeToWorld(ctx, explicitTarget) : tf.position)
                : (swim ? PickValidatedSwimTarget(ctx, center, range, tf.position)
                    : PickValidatedFlyTarget(ctx, center, range, tf.position));
            return new Diggles.Runtime.Tcl.Engine.FlyAction(tf, worldTarget, Diggles.Runtime.Tcl.Engine.FlyAction.DefaultSpeed);
        }

        static Vector3 PickValidatedSwimTarget(
            DigglesTclContext ctx, Vector3 centerNative, float range, Vector3 fallbackWorld)
        {
            for (int i = 0; i < 32; i++)
            {
                Vector3 candidate = centerNative + new Vector3(
                    UnityEngine.Random.Range(-range, range),
                    UnityEngine.Random.Range(-range, range),
                    UnityEngine.Random.Range(-0.3f, 0.5f));
                if (ctx.Fluids.IsUnderwater(candidate)) return FlyNativeToWorld(ctx, candidate);
            }
            return fallbackWorld;
        }

        static Vector3 FlyNativeToWorld(DigglesTclContext ctx, Vector3 native)
            => ctx.SpawnParent != null
                ? ctx.SpawnParent.TransformPoint(RuntimeWorldSpawner.NativeToLocal(native))
                : native;

        // Unity-native replacement for CAct_Fly's 16-try voxel validation: pick a random point within
        // `range` of the anchor (small vertical bob), raycast straight DOWN (terrain floors face world
        // +Y — see NavGraphBuilder) to find the surface; REJECT candidates with no terrain below them
        // (off the meadow → keeps the butterfly over solid ground), and LIFT any that would sit below
        // the surface so it never dives underground. Falls back to the current pos if all 16 miss.
        static Vector3 PickValidatedFlyTarget(DigglesTclContext ctx, Vector3 centerNative, float range, Vector3 fallbackWorld)
        {
            for (int i = 0; i < 16; i++)
            {
                Vector3 candNative = centerNative + new Vector3(
                    UnityEngine.Random.Range(-range, range),
                    UnityEngine.Random.Range(-range, range),
                    UnityEngine.Random.Range(-0.3f, 0.5f));
                Vector3 candWorld = FlyNativeToWorld(ctx, candNative);
                if (Physics.Raycast(candWorld + Vector3.up * 20f, Vector3.down, out var hit, 60f, ~0,
                        QueryTriggerInteraction.Ignore))
                {
                    float minY = hit.point.y + Diggles.Runtime.Tcl.Engine.FlyAction.MinClearance;
                    if (candWorld.y < minY) candWorld.y = minY;   // never below the terrain
                    return candWorld;                              // valid: over solid ground
                }
            }
            return fallbackWorld;   // nothing solid nearby → stay put this beat
        }

        // The `-randompath N` goal pick (native FUN_005a22f0 + FUN_004158a0). Faithful pieces:
        // • start sub-cell = round(pos × 8) (FUN_004158a0:75 ROUND(x · DAT_0073752c=8));
        // • an unwalkable start is un-stuck by probing +y/+x/−x/−y at growing distance ≤ 33
        // sub-cells against the +0x3c walk bit (FUN_005a22f0:78-114);
        // • the goal lands on a FLOOR cell ~N tiles away (the native picks a random target tile
        // via its distance-field helpers FUN_0059fda0/005a0f00/005a1920/005a0650, greedy-walks
        // ≤100 steps toward it and takes the LAST waypoint ×0.125 = DAT_0073703c as the goal,
        // FUN_004158a0:113-143 — the port samples random floor cells in the annulus directly
        // and lets Calc1 do the walking);
        // • goal depth = uniform in [max(z−M, hmap(goal)+1+walldist), min(z+M, 14.5)]
        // (FUN_004158a0:124-140; DAT_00737564 = 14.5).
        // Pure NavWorld math — headless-testable. Returns false when no floor exists in range.
        public static bool TryPickRandomPathTarget(Diggles.Runtime.Navigation.NavWorld nav,
            Vector3 startNative, int pathLen, float randomZ, float wallDist, out Vector3 targetNative)
        {
            targetNative = default;
            int sx = Mathf.RoundToInt(startNative.x * 8f), sy = Mathf.RoundToInt(startNative.y * 8f);
            if (!nav.SubWalkable(sx, sy))
            {
                bool unstuck = false;
                for (int i = 1; i <= 33 && !unstuck; i++)   // FUN_005a22f0:80-114 probe order
                {
                    if      (nav.SubWalkable(sx, sy + i)) { sy += i; unstuck = true; }
                    else if (nav.SubWalkable(sx + i, sy)) { sx += i; unstuck = true; }
                    else if (nav.SubWalkable(sx - i, sy)) { sx -= i; unstuck = true; }
                    else if (nav.SubWalkable(sx, sy - i)) { sy -= i; unstuck = true; }
                }
                if (!unstuck) return false;
            }
            int n = Mathf.Max(1, pathLen);
            for (int attempt = 0; attempt < 64; attempt++)
            {
                // random direction × random distance 1..N tiles (8 sub-cells per tile)
                float ang = UnityEngine.Random.value * 2f * Mathf.PI;
                float r = (1f + UnityEngine.Random.value * (n - 1)) * 8f;
                int tx = sx + Mathf.RoundToInt(Mathf.Cos(ang) * r);
                int ty0 = sy + Mathf.RoundToInt(Mathf.Sin(ang) * r);
                if (tx < 0 || tx >= nav.SubW) continue;
                // scan the candidate column for a ground line (the native field-walk crawls along
                // floors to whatever line is reachable; the port scans the column near the sample)
                for (int dy = 0; dy <= 32; dy++)
                    for (int s = -1; s <= 1; s += 2)
                    {
                        int ty = ty0 + (dy == 0 && s > 0 ? 0 : dy * s);
                        if (ty < 0 || ty >= nav.SubH) continue;
                        if (!nav.SubIsFloor(tx, ty)) continue;
                        if (tx == sx && ty == sy) continue;          // a real move, not the start
                        // goal depth (FUN_004158a0:124-140)
                        float lo = startNative.z - randomZ;
                        float floorMin = nav.GetHmapSub(tx, ty) + 1f + wallDist;
                        if (lo <= floorMin) lo = floorMin;
                        if (lo > 14.5f) lo = 14.5f;
                        float hi = Mathf.Min(startNative.z + randomZ, 14.5f);
                        float z = hi > lo ? lo + UnityEngine.Random.value * (hi - lo) : lo;
                        targetNative = new Vector3(tx * 0.125f, ty * 0.125f, z);
                        return true;
                    }
            }
            return false;
        }

        private static readonly string[] NativeWalkSlotNames =
        {
            "standard",
            "climb_standard",
            "turn_left_90",
            "turn_right_90",
            "turn_left_180",
            "turn_right_180",
            "walk_start",
            "walk_loop",
            "walk_stop",
            "climb_up",
            "climb_down",
            "climb_right",
            "climb_left",
            "walk_loop_wave",
            "ladder_climb_up",
            "ladder_climb_down",
            "ground_to_wall",
            "wall_to_ground",
            "ground_to_ladder",
            "ladder_to_ground",
            "walk_begin",
            "walk_end",
            "ladder_climb_right",
            "ladder_climb_left",
        };

        private static (int[] BankStates, int[] SlotBits) BuildNativeWalkAnimationBanks(
            DigglesTclContext context,
            DigglesTclEntity owner,
            int[] animationSetIds)
        {
            if (context == null) throw new System.ArgumentNullException(nameof(context));
            if (owner == null) throw new System.ArgumentNullException(nameof(owner));
            if (animationSetIds == null) throw new System.ArgumentNullException(nameof(animationSetIds));
            if (animationSetIds.Length != 4)
                throw new System.ArgumentException("A native Walk action has four animation banks.",
                    nameof(animationSetIds));

            int[] bankStates = (int[])animationSetIds.Clone();
            var slotBits = new int[4 * NativeWalkSlotNames.Length * 5];
            int alternateDatabase = (CaptureNativeObjectFlags(owner, 0) >> 13) & 1;
            for (int bank = 0; bank < bankStates.Length; bank++)
            {
                owner.Class.Animsets.TryGetValue(bankStates[bank], out var animationSet);
                for (int slot = 0; slot < NativeWalkSlotNames.Length; slot++)
                {
                    int offset = (bank * NativeWalkSlotNames.Length + slot) * 5;
                    WriteMissingNativeWalkSlot(slotBits, offset);
                    if (animationSet == null ||
                        !animationSet.TryGetValue(NativeWalkSlotNames[slot], out string qualifiedAnimation) ||
                        !TryResolveNativeWalkAnimation(
                            context.ModelLibrary,
                            qualifiedAnimation,
                            out ThreeDbModel model,
                            out int animationIndex,
                            out ThreeDbAnim animation))
                    {
                        continue;
                    }

                    int databaseSlot = OriginalSaveRuntimeSessionStager.NativeModelSlot(model);
                    if (databaseSlot < 0) continue;
                    slotBits[offset] = databaseSlot + alternateDatabase;
                    slotBits[offset + 1] = animationIndex;
                    slotBits[offset + 2] = System.BitConverter.SingleToInt32Bits(
                        CalculateNativeWalkRootArc(animation.VecA));
                    float turnIncrement = animation.VecB.z;
                    if (turnIncrement == 0f)
                    {
                        if (slot is 2 or 3) turnIncrement = 0.10f;
                        else if (slot is 4 or 5) turnIncrement = 0.15f;
                    }
                    slotBits[offset + 3] = System.BitConverter.SingleToInt32Bits(turnIncrement);
                    slotBits[offset + 4] = animation.Indices.Length;
                }
            }
            return (bankStates, slotBits);
        }

        private static void WriteMissingNativeWalkSlot(int[] slotBits, int offset)
        {
            slotBits[offset] = -1;
            slotBits[offset + 1] = -1;
            slotBits[offset + 2] = System.BitConverter.SingleToInt32Bits(1f);
            slotBits[offset + 3] = System.BitConverter.SingleToInt32Bits(10f);
            slotBits[offset + 4] = 0;
        }

        private static bool TryResolveNativeWalkAnimation(
            DigglesModelLibrary? library,
            string qualifiedAnimation,
            out ThreeDbModel model,
            out int animationIndex,
            out ThreeDbAnim animation)
        {
            model = null!;
            animationIndex = -1;
            animation = null!;
            if (library == null || string.IsNullOrEmpty(qualifiedAnimation)) return false;
            int separator = qualifiedAnimation.IndexOf('.');
            if (separator <= 0 || separator == qualifiedAnimation.Length - 1) return false;
            string objectName = qualifiedAnimation.Substring(0, separator);
            string animationName = qualifiedAnimation.Substring(separator + 1);
            string? databaseName = library.GetObjDb(objectName);
            if (databaseName == null) return false;
            ThreeDbModel? resolvedModel = library.GetDatabaseModel(databaseName);
            if (resolvedModel == null) return false;

            ThreeDbObject? resolvedObject = null;
            foreach (ThreeDbObject candidate in resolvedModel.Objects)
            {
                if (candidate.Name != objectName) continue;
                resolvedObject = candidate;
                break;
            }
            if (resolvedObject == null) return false;
            foreach (uint candidateIndex in resolvedObject.Indices)
            {
                if (candidateIndex >= resolvedModel.Anims.Count) continue;
                ThreeDbAnim candidate = resolvedModel.Anims[(int)candidateIndex];
                if (candidate.Name1 != animationName) continue;
                model = resolvedModel;
                animationIndex = (int)candidateIndex;
                animation = candidate;
                return true;
            }
            return false;
        }

        private static float CalculateNativeWalkRootArc(Vector3 vector)
        {
            float xSquared = (float)(vector.x * vector.x);
            float zSquared = (float)(vector.z * vector.z);
            float xzSquared = (float)(xSquared + zSquared);
            float ySquared = (float)(vector.y * vector.y);
            float magnitudeSquared = (float)(xzSquared + ySquared);
            float magnitude = (float)System.Math.Sqrt(magnitudeSquared);
            return (float)(magnitude * 0.05f);
        }

        private static string BuildNativeWalkArgument(
            Vector3 target,
            bool hasTarget,
            byte waving,
            bool forceTarget,
            int speedType,
            int[] animationSetIds,
            IReadOnlyList<string> sourceTokens)
        {
            var arguments = new List<string>();
            if (hasTarget)
            {
                arguments.Add("-target");
                arguments.Add($"{Ftos(target.x)} {Ftos(target.y)} {Ftos(target.z)}");
            }
            arguments.Add("-waving");
            arguments.Add(waving != 0 ? "1" : "0");
            arguments.Add("-forcetarget");
            arguments.Add(forceTarget ? "1" : "0");
            arguments.Add("-speedtype");
            arguments.Add(speedType.ToString(CultureInfo.InvariantCulture));
            arguments.Add("-animsets");
            arguments.Add(string.Join(" ", animationSetIds));

            for (int index = 0; index + 1 < sourceTokens.Count; index += 2)
            {
                string key = sourceTokens[index];
                if (key is "-target" or "-waving" or "-forcetarget" or "-speedtype" or "-animsets")
                    continue;
                arguments.Add(key);
                arguments.Add(sourceTokens[index + 1]);
            }
            return TclList.Format(arguments);
        }

        private static bool NativeWalkOptionEnabled(string nativeArgument, string option)
        {
            var tokens = TclList.Parse(nativeArgument ?? string.Empty);
            for (int index = 0; index + 1 < tokens.Count; index += 2)
                if (tokens[index] == option) return ParseTclBool(tokens[index + 1]);
            return false;
        }

        // Parse the faithful walk option string (`-target {x y z} -animsets {a b …}
        // -speedtype N -canclimb B`), pathfind to the target via the FAITHFUL Calc1
        // (NavWorldService) and build the single MetaWalk executor. Falls back to a
        // brief WaitAction (so the finishcode still fires) when there's no target /
        // nav / path. Unhandled options (-stopat, …) are ignored for now.
        static IEntityAction BuildMetaWalkFromOptions(DigglesTclContext ctx,
            DigglesTclEntity ent, string options, int walkTraceId = 0)
        {
            bool hasTarget = false; Vector3 target = default;
            bool canClimb = true;                      // -canclimb default 1 (FUN_00420520)
            bool useObjects = true;
            bool forceTarget = false;
            byte waving = 1;
            var animationSetIds = new[] { 0, -1, -1, 0 };
            int speedtype = 0;
            // `-randompath N` (int → walk record +0x7b4, FUN_00420840:826/:860) switches the
            // path calc from Calc1 to the RANDOM-path generator (FUN_004158a0:33 → FUN_005a22f0):
            // wildlife/baby wander (`walk_random`, hamster.tcl:141, baby.tcl:178, rh_procs.tcl:11)
            // and the flee behaviours (z_dignwalk.tcl:656 `-randompath [irandom 4 10]`).
            // `-randomz M` (int → +0x7b8,:969/:1003) bounds the goal's DEPTH jitter;
            // `-randompathwalldist W` (float → +0x7dc,:1237/:1298) offsets the depth floor.
            int randomPath = 0; float randomZ = 0f, randomPathWallDist = 0f;
            // Lifecycle options (walk-lifecycle.md §4): -targetdist default −1 (disabled),
            // -walktimercounter default off; the rest of the ~22-option table is parsed where it
            // affects behaviour and tolerated (no-op) otherwise.
            float targetDist = -1f;
            // -1 = walk timer DISABLED — the native ctor default. The tick dispatcher guards
            // `counter % interval` only against -1 (FUN_004075b0:187), so a serialized 0 is an
            // INTEGER DIVIDE BY ZERO on the original's first walk tick (corpus: 1860/1861 walk
            // records carry -1, none carry 0).
            string walkTimerCallback = ""; int walkTimerCounter = -1;
            DigglesTclEntity? walkObj = null;   // -object ref (work/guest walks)
            int dummyId = -1;                   // -dummy link id; -1 = absent (FUN_00420840:125)
            var toks = TclList.Parse(options);
            for (int i = 0; i + 1 < toks.Count; i += 2)
            {
                string key = toks[i], val = toks[i + 1];
                switch (key)
                {
                    case "-targetdist":
                        float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out targetDist);
                        break;
                    case "-walktimercallback":
                        walkTimerCallback = val;
                        break;
                    case "-walktimercounter":
                        int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out walkTimerCounter);
                        break;
                    case "-target":
                    {
                        var c = TclList.Parse(val);
                        if (c.Count >= 3
                            && float.TryParse(c[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                            && float.TryParse(c[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                            && float.TryParse(c[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
                        { target = new Vector3(x, y, z); hasTarget = true; }
                        break;
                    }
                    case "-animsets":
                    {
                        var ids = TclList.Parse(val);
                        for (int bank = 0; bank < ids.Count && bank < animationSetIds.Length; bank++)
                        {
                            if (int.TryParse(ids[bank], NumberStyles.Integer,
                                    CultureInfo.InvariantCulture, out int animationSetId))
                            {
                                animationSetIds[bank] = animationSetId;
                            }
                        }
                        break;
                    }
                    case "-speedtype":
                        int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out speedtype);
                        break;
                    case "-canclimb":
                        canClimb = ParseTclBool(val);   // box-carriers pass 0 (z_dignwalk.tcl:613,837)
                        break;
                    case "-useobjects":
                        useObjects = ParseTclBool(val);
                        break;
                    case "-waving":
                        waving = ParseTclBool(val) ? (byte)1 : (byte)0;
                        break;
                    case "-forcetarget":
                        forceTarget = ParseTclBool(val);
                        break;
                    case "-randompath":
                        // scripts pass ints ([irandom 4 10]); a float string still rounds sanely
                        if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var rp))
                            randomPath = Mathf.RoundToInt(rp);
                        break;
                    case "-randomz":
                        float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out randomZ);
                        break;
                    case "-randompathwalldist":
                        float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out randomPathWallDist);
                        break;
                    // walk_dummy / prod work walks pass `-object <ref> -dummy <id>` (no -target): walk to
                    // the object's work-dummy point. The native (CActWalk options, FUN_00420840:1901-1944)
                    // takes the -object/-dummy path only when BOTH are present (dummy default = -1 absent,
                    //:125): target = obj.pos + the model link point scaled to native (x,y × DAT_00736fcc,
                    // z × DAT_00736fe0 = 2×; NO rotation), then goal-fixed. Resolved after the loop so the
                    // option order doesn't matter. -object alone falls back to the object's origin (the
                    // previous behaviour — without it the walk had no target → 0.05s WaitAction → walkfail
                    // → the dwarf bailed to idle, the 'chops in place' bug).
                    case "-object":
                        walkObj = ResolveEntityOrNull(ctx, val);
                        break;
                    case "-dummy":
                        // present but unparsable → 0 (Tcl_GetIntFromObj failure, FUN_00420840:425-428)
                        if (!int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out dummyId))
                            dummyId = 0;
                        break;
                }
            }
            if (walkObj != null && dummyId >= 0)
            {
                // Both -object and -dummy ⇒ the dummy target wins over -target (the native's
                // `if (dummy == -1 || obj == 0)` guards the -target path, FUN_00420840:1901).
                target = walkObj.Position;
                if (TryGetLinkOffsetNative(ctx, walkObj, dummyId, applyRotation: false, out var dummyOff))
                    target += dummyOff;   // FUN_00420840:1936-1938 — obj pos + raw scaled link point
                hasTarget = true;
            }
            else if (walkObj != null && !hasTarget)
            {
                target = walkObj.Position;   // -object alone: keep the object-origin fallback
                hasTarget = true;
            }

            // Resolve a random-path destination once while handling the live command. Capturing an
            // active walk must retain this selected target so restore never consumes RNG again.
            if (!hasTarget && randomPath > 0)
            {
                var navWorld = ctx.Terrain?.Nav;
                if (navWorld != null && TryPickRandomPathTarget(navWorld, ent.Position, randomPath,
                        randomZ, randomPathWallDist, out var wanderGoal))
                {
                    target = wanderGoal;
                    hasTarget = true;
                }
            }

            var nativeBanks = BuildNativeWalkAnimationBanks(ctx, ent, animationSetIds);
            string nativeArgument = BuildNativeWalkArgument(
                target, hasTarget, waving, forceTarget, speedtype, animationSetIds, toks);
            var state = new WalkActionSaveState(
                target,
                hasTarget,
                nativeBanks.BankStates,
                nativeBanks.SlotBits,
                animationSetIds[1] != -1 ? 1 : 0,
                waving,
                0,
                2f,
                canClimb ? (byte)1 : (byte)0,
                useObjects ? (byte)1 : (byte)0,
                randomPath,
                Mathf.RoundToInt(randomZ),
                new byte[] { 1, 0, 0, 0 },
                // objectBreakClassId: -1 = no -breakclass — the native disabled value (the
                // corpus carries -1 in all 1,861 walk records; a serialized 0 is a live class
                // id to the original's break scan).
                -1,
                -1f,
                targetDist,
                new Vector3(0f, System.BitConverter.Int32BitsToSingle(0x40490FDB), 0f),
                0,
                randomPathWallDist,
                walkTimerCounter,
                0,
                speedtype,
                walkTimerCallback,
                "walk_collision_callback",
                "obj_use_callback",
                nativeArgument,
                unchecked((int)0xDFDFDFDF));
            IEntityAction inner = BuildMetaWalkFromSaveState(ctx, ent, state, walkTraceId);
            return new RestoredWalkAction(ctx, ent, state, inner);
        }

        internal static IEntityAction BuildMetaWalkFromSaveState(
            DigglesTclContext ctx,
            DigglesTclEntity ent,
            WalkActionSaveState state,
            int walkTraceId = 0)
        {
            if (state == null) throw new System.ArgumentNullException(nameof(state));
            bool hasTarget = state.HasTarget;
            Vector3 target = state.TargetNative;
            bool canClimb = state.CanClimb != 0;
            int walkAnimset = state.WalkAnimationSet;
            int runAnimset = state.RunAnimationSet;
            int speedtype = state.SpeedType;
            int randomPath = state.RandomPath;
            float randomZ = state.RandomZ;
            float randomPathWallDist = state.RandomPathWallDistance;
            float targetDist = state.TargetDistance;
            string walkTimerCallback = state.WalkTimerCallback;
            int walkTimerCounter = state.WalkTimerCounter;
            // Native -dig is stored at walk+0x7bc; value 1 skips the floor pre-snap
            // (FUN_00420840:1063-1080,1901-1919). -digpos is accepted natively but is a no-op.
            bool digWalk = NativeWalkOptionEnabled(state.NativeArgument, "-dig");

            // -randompath: synthesize the wander goal (an earlier revision no-op'd this option and FAKED
            // an arrival, freezing Hamster/Raupe/baby wander and the flee walks). The native
            // generator (FUN_005a22f0) random-walks the floor grid from the start sub-cell
            // (= round(pos × 8), DAT_0073752c) toward a random target ~N tiles out and then makes
            // the GOAL the last waypoint ×0.125 (DAT_0073703c) with the depth drawn uniformly from
            // [max(z−M, hmap+1+walldist), min(z+M, 14.5)] (FUN_004158a0:113-143, DAT_00737564=14.5).
            // The port picks the random floor goal directly and lets the faithful Calc1 walk there.
            if (!hasTarget && randomPath > 0)
            {
                var navWorld = ctx.Terrain?.Nav;
                if (navWorld != null && TryPickRandomPathTarget(navWorld, ent.Position, randomPath,
                        randomZ, randomPathWallDist, out var wanderGoal))
                {
                    target = wanderGoal;
                    hasTarget = true;
                }
                // no nav / no floor found → fall through to the degraded WaitAction below, which
                // still reports ARRIVED so the wildlife idle loop keeps scheduling (headless-safe).
            }

            var tf = GetTransform(ent);
            var nav = ctx.NavService;
            if (!hasTarget || tf == null || nav == null || !nav.HasWorld)
            {
                TraceWalkExecutor(
                    walkTraceId,
                    "goal-relax",
                    $"REJECT #{ent.Handle}: target={hasTarget} transform={tf != null} nav={nav != null} world={nav?.HasWorld == true}");
                if (s_loggedBodyErrors.Add("[action walk] no target / nav world"))
                    Debug.LogWarning("[action walk] no target / nav world — move skipped. (further repeats suppressed)");
                // Degraded (no GO/nav, e.g. headless tests): treat as an instant ARRIVAL (WalkResult=4)
                // so the work loop's walkfail proceeds instead of giving up — the LOGIC stays verifiable.
                return new WalkResultAction(new WaitAction(0.05), ent, 4);
            }
            // `-target {x y z}` is NATIVE coords (Tcl space); the pathfinder + tf.position are
            // WORLD-space. Convert through the spawn root (NativeToLocal under WorldRoot) so the
            // path endpoints are in the same space — else FindPath always returns "no path". (#3)
            Vector3 worldTarget = ctx.SpawnParent != null
                ? ctx.SpawnParent.TransformPoint(RuntimeWorldSpawner.NativeToLocal(target))
                : target;
            var path = FindPathWithDoors(ctx, nav, ent, tf.position, worldTarget, canClimb, digWalk);
            TraceWalkExecutor(
                walkTraceId,
                "goal-relax",
                $"#{ent.Handle}: {Diggles.Runtime.Navigation.NavWorldService.LastGoalResolution}");
            if (WorkDebug)   // guard: fires on EVERY walk build — no eager interpolation
            {
                WorkLog(ctx, ent, $"WALK build: target(native)=({target.x:0.0},{target.y:0.0},{target.z:0.0}) " +
                    $"path={(path == null ? "NULL" : path.Count + "wp")} canClimb={canClimb}");
                if (path != null && ctx.SpawnParent != null)
                    for (int wi = 0; wi < path.Count && wi < 12; wi++)
                    {
                        Vector3 wn = RuntimeWorldSpawner.LocalToNative(
                            ctx.SpawnParent.InverseTransformPoint(path[wi].Pos));
                        WorkLog(ctx, ent, $"  wp[{wi}] {(path[wi].IsWall ? "WALL " : "floor")} " +
                            $"({wn.x:0.00},{wn.y:0.00},{wn.z:0.00})");
                    }
            }
            if (path == null || path.Count == 0)
            {
                TraceWalkExecutor(
                    walkTraceId,
                    "layer-selection",
                    $"REJECT #{ent.Handle}: {Diggles.Runtime.Navigation.NavWorldService.LastNullReason}; " +
                    $"layers={Diggles.Runtime.Navigation.NavWorldService.LastLayerResolution}");
                // Rate-limited: a blocked-goal loop (dispatcher retries at think cadence) fired this
                // warning MANY times a second — each editor console log captures a stack trace, a
                // steady main-thread cost and a console flood. One line per 5 s keeps the diagnostic.
                if (UnityEngine.Time.realtimeSinceStartupAsDouble >= s_nextNoPathWarnAt)
                {
                    s_nextNoPathWarnAt = UnityEngine.Time.realtimeSinceStartupAsDouble + 5.0;
                    Debug.LogWarning($"[action walk] no path. native={target} worldTarget={worldTarget} " +
                        $"actorWorld={tf.position} canClimb={canClimb} " +
                        $"layer[{Diggles.Runtime.Navigation.NavWorldService.LastNullReason}] (rate-limited: 1 per 5s)");
                }
                // No path → WS_NOPASSAGE = 3 (doc §3.4; the Tcl walkfail gate `4 != result` abandons the job).
                return new WalkResultAction(new WaitAction(0.2), ent, 3);   // failure latency: the native burns >=1 logic tick (0.1s) before the result lands + the Tcl recycle costs 0.1s/hop — 0.05s under-priced failure 4-12x and fueled the churn
            }
            // FUN_004163a0: a WALL segment with bCanClimb==0 ⇒ WS_NOPASSAGE (3) immediately — the
            // box-carrier contract (-canclimb 0, z_dignwalk.tcl:613,837). Calc1's climb gate already
            // refuses wall TILES for non-climbers, but a relaxed endpoint can still land on a wall
            // sub-cell; the original aborts at segment-dispatch time, so do we.
            if (!canClimb)
            {
                bool wallRun0 = false;
                foreach (var wp0 in path) if (wp0.IsWall) { wallRun0 = true; break; }
                if (wallRun0)
                {
                    TraceWalkExecutor(
                        walkTraceId,
                        "layer-selection",
                        $"REJECT #{ent.Handle}: path contains a climb run while canClimb=false");
                    return new WalkResultAction(new WaitAction(0.2), ent, 3);   // failure latency: the native burns >=1 logic tick (0.1s) before the result lands + the Tcl recycle costs 0.1s/hop — 0.05s under-priced failure 4-12x and fueled the churn
                }
            }

            // Faithful CAct_MetaWalk segmentation (FUN_004163a0): group consecutive compatible
            // waypoints into runs and dispatch each run to its sub-action — FLOOR → MetaWalkAction,
            // free/ladder WALL → ClimbAction, typed elevator WALL → ElevatorRideAction (bounding
            // floor waypoints included for mount/dismount transitions) — chained in path order.
            // Shared with the AutoRetry re-plans via BuildWalkSegments.
            // Floor runs carry the LIVE support probe (the CAct_WalkFloor per-leg rebuild law —
            // NavWorldService.HasFloorSupportWorld): a carve opening the ground under the in-flight
            // route ends the run with WS_NOPASSAGE (3) and the RetryWalkAction below replans from
            // the CURRENT position (FUN_00408160:17-19) — a digger whose floor was carved away
            // MOUNTS the wall (the fresh start classifies WALL → climb) instead of hanging in the
            // air / gliding sideways along the stale plan.
            System.Func<Vector3, bool> liveSupport = nav.HasFloorSupportWorld;
            var segments = BuildWalkSegments(
                ctx,
                ent,
                GetController(ent),
                tf,
                path,
                walkAnimset,
                runAnimset,
                speedtype,
                liveSupport,
                walkTraceId);
            if (segments == null)
            {
                TraceWalkExecutor(
                    walkTraceId,
                    "layer-selection",
                    $"REJECT #{ent.Handle}: mover factory rejected layers " +
                    Diggles.Runtime.Navigation.NavWorldService.LastLayerResolution);
                return new WalkResultAction(new WaitAction(0.2), ent, 3);   // failure latency: the native burns >=1 logic tick (0.1s) before the result lands + the Tcl recycle costs 0.1s/hop — 0.05s under-priced failure 4-12x and fueled the churn
            }
            TraceWalkExecutor(
                walkTraceId,
                "layer-selection",
                $"ACCEPT #{ent.Handle}: {path.Count} waypoints, {segments.Count} movers; " +
                Diggles.Runtime.Navigation.NavWorldService.LastLayerResolution);
            IEntityAction walkChain = segments.Count == 1 ? segments[0] : new SequenceAction(segments);
            IEntityAction result = new WalkResultAction(walkChain, ent, 4);

            // CActWalk_AutoRetry (FUN_00408160 + FUN_00407fb0): NOPASSAGE-if-moved replans from
            // the current position; an arrived-short chain re-plans too (§5.3 re-aim stand-in).
            Vector3 retryTarget = worldTarget;
            result = new RetryWalkAction(result, ent, tf, retryTarget, () =>
            {
                var p2 = FindPathWithDoors(ctx, nav, ent, tf.position, retryTarget, canClimb, digWalk);
                if (p2 == null || p2.Count < 2) return null;
                var segs2 = BuildWalkSegments(
                    ctx,
                    ent,
                    GetController(ent),
                    tf,
                    p2,
                    walkAnimset,
                    runAnimset,
                    speedtype,
                    liveSupport,
                    walkTraceId);
                if (segs2 == null) return null;
                return new WalkResultAction(
                    segs2.Count == 1 ? segs2[0] : new SequenceAction(segs2), ent, 4);
            });

            // The CActWalk guards: -walktimercallback (→ WS_WALKEVENT 5 on non-"1") and
            // -targetdist finish-early — OUTSIDE the result writer so the 5 sticks.
            if (walkTimerCallback.Length > 0 || targetDist >= 0f)
            {
                System.Func<string>? fire = null;
                if (walkTimerCallback.Length > 0 && walkTimerCounter > 0)
                {
                    string body = walkTimerCallback;
                    fire = () =>
                    {
                        var prevThis = ctx.This;
                        ctx.This = ent;
                        try { return ctx.Interp.Eval(body).Trim(); }
                        finally { ctx.This = prevThis; }
                    };
                }
                result = new WalkGuardAction(result, ent, tf, fire, walkTimerCounter, worldTarget, targetDist);
            }
            return result;
        }

        // The CAct_MetaWalk segmentation shared by the first plan and the AutoRetry re-plans:
        // group consecutive compatible waypoints; dispatch FLOOR → MetaWalkAction, generic WALL →
        // ClimbAction, a typed vertical-ladder span → LadderRiderAction, and typed elevator WALL →
        // ElevatorRideAction. Ladder-tagged FLOOR records inside a ride keep the native rider
        // continuous across corridor crossings; they remain FLOOR for every other path.
        // `support` = the live per-leg support probe for FLOOR runs (null = frozen legs — the
        // headless/cutscene movers); climb runs keep their records faithfully stale (the native
        // CAct_Climb never re-reads terrain mid-run, FUN_00418db0).
        static System.Collections.Generic.List<IEntityAction>? BuildWalkSegments(
            DigglesTclContext ctx,
            DigglesTclEntity entity,
            DigglesAnimController? ctrl,
            Transform tf,
            System.Collections.Generic.List<Diggles.Runtime.Navigation.NavWaypoint> path,
            int walkAnimset, int runAnimset, int speedtype,
            System.Func<Vector3, bool>? support = null,
            int walkTraceId = 0)
        {
            var segments = new System.Collections.Generic.List<IEntityAction>();
            for (int si = 0; si < path.Count;)
            {
                if (path[si].IsWall
                    && path[si].TransportKind == Diggles.Runtime.Navigation.NavTransportKind.Ladder
                    && path[si].TransportSourceHandle >= 0)
                {
                    int rideEnd = LadderRiderAction.FindContinuousRideEnd(path, si);
                    int rideTarget = rideEnd - 1;
                    if (!TryCreateLadderRiderAction(
                            ctx,
                            ctrl,
                            tf,
                            path[rideTarget].Pos,
                            path[si].TransportSourceHandle,
                            walkAnimset,
                            runAnimset,
                            speedtype,
                            out LadderRiderAction rider))
                        return null;
                    segments.Add(rider);
                    // FUN_00411870/FUN_00411c90 remove the completed rider and MetaWalk from
                    // the ladder plane to the exact floor exit. Keep the final tagged FLOOR as
                    // the next floor segment's first target; only interior floor crossings (a
                    // same-source ladder wall follows them) are consumed inside this one ride.
                    si = path[rideTarget].IsWall ? rideEnd : rideTarget;
                    continue;
                }

                bool wallRun = path[si].IsWall;
                var transportKind = wallRun
                    ? path[si].TransportKind
                    : Diggles.Runtime.Navigation.NavTransportKind.None;
                int transportSource = wallRun ? path[si].TransportSourceHandle : -1;
                int sj = si;
                while (sj < path.Count && path[sj].IsWall == wallRun)
                {
                    if (wallRun && (path[sj].TransportKind != transportKind
                        || path[sj].TransportSourceHandle != transportSource))
                        break;
                    sj++;
                }
                if (wallRun)
                {
                    var run = new System.Collections.Generic.List<Diggles.Runtime.Navigation.NavWaypoint>();
                    if (si > 0) run.Add(path[si - 1]);
                    for (int k = si; k < sj; k++) run.Add(path[k]);
                    if (sj < path.Count) run.Add(path[sj]);
                    if (transportKind == Diggles.Runtime.Navigation.NavTransportKind.Elevator
                        && transportSource >= 0)
                    {
                        Vector3 pickup = WorldWaypointToNative(ctx, run[0].Pos);
                        Vector3 exit = WorldWaypointToNative(ctx, run[run.Count - 1].Pos);
                        segments.Add(new ElevatorRideAction(
                            ctx,
                            entity,
                            transportSource,
                            pickup.y,
                            exit.y,
                            exit));
                    }
                    else
                    {
                        segments.Add(new ClimbAction(
                            ctrl,
                            tf,
                            run,
                            walkAnimset,
                            runAnimset,
                            speedtype));
                    }
                }
                else
                {
                    var pts = new System.Collections.Generic.List<Vector3>(sj - si);
                    for (int k = si; k < sj; k++) pts.Add(path[k].Pos);
                    segments.Add(new MetaWalkAction(
                        ctrl,
                        tf,
                        pts,
                        walkAnimset,
                        runAnimset,
                        speedtype,
                        support,
                        walkTraceId,
                        $"floor[{si}..{sj})/{path.Count}"));
                }
                si = sj;
            }
            return segments.Count > 0 ? segments : null;
        }

        static bool TryCreateLadderRiderAction(
            DigglesTclContext ctx,
            DigglesAnimController? ctrl,
            Transform tf,
            Vector3 target,
            int ladderHandle,
            int walkAnimset,
            int runAnimset,
            int speedtype,
            out LadderRiderAction action)
        {
            action = null!;
            if (!ctx.Entities.TryGetValue(ladderHandle, out DigglesTclEntity ladder)
                || ladder.TransportLogic?.Ladder == null)
                return false;

            Vector3 anchorNative = new(
                ladder.Position.x,
                ladder.Position.y,
                ladder.Position.z + 1f);
            Vector3 downNative = anchorNative + Vector3.up;
            Transform? worldRoot = ctx.SpawnParent ?? ctx.NavService?.transform;
            Vector3 anchorLocal = RuntimeWorldSpawner.NativeToLocal(anchorNative);
            Vector3 downLocal = RuntimeWorldSpawner.NativeToLocal(downNative);
            Vector3 anchor = worldRoot != null ? worldRoot.TransformPoint(anchorLocal) : anchorLocal;
            Vector3 downPoint = worldRoot != null ? worldRoot.TransformPoint(downLocal) : downLocal;
            Vector3 downAxis = downPoint - anchor;
            if (downAxis.sqrMagnitude <= 1e-6f) return false;

            Quaternion facing = Quaternion.LookRotation(
                -Diggles.Runtime.Navigation.NavWorldService.WallOutward,
                Vector3.up);
            if (tf.TryGetComponent(out DigglesObject digglesObject))
                facing = digglesObject.FaceRotationForNativeAngle(Mathf.PI);

            int animset = speedtype == 2 && runAnimset >= 0 ? runAnimset : walkAnimset;
            action = new LadderRiderAction(
                ctrl,
                tf,
                anchor,
                downAxis.normalized,
                target,
                facing,
                animset);
            return true;
        }

        static Vector3 WorldWaypointToNative(DigglesTclContext ctx, Vector3 worldPosition)
        {
            Vector3 local = ctx.SpawnParent != null
                ? ctx.SpawnParent.InverseTransformPoint(worldPosition)
                : worldPosition;
            return RuntimeWorldSpawner.LocalToNative(local);
        }

        // Spiral-snap to the nearest sub-cell whose +0x3c byte has ALL `requiredBits` set, within
        // `maxR` square rings. This is the vector_fixdig step-2/floor-refinement primitive
        // (FUN_00594e00; also the dig_next/get_place floor snaps). Native get_digedge itself
        // returns a tile and never reads +0x3c (FUN_005981d0 disassembly 0x5981d0-0x59860d).
        // NEAREST means EUCLIDEAN: the
        // old first-ring-hit return picked the top-left-most cell of the winning CHEBYSHEV ring,
        // which can be ~√2× farther than the true nearest (repro: CaveDispatch_WedgeHunt seed 4):
        // a dig standoff snapped to a floor cell beside the gnome (walkdist 0.18 ≤ nonewwalk ⇒ no
        // walk) instead of the floor under the voxel, while the voxel sat outside dig_execute's
        // swing tether (3.02 ≥ 3.0) — dig_continue reselected the same voxel forever at zero
        // progress, the gnome never digging into the cave.
        // Scan rings, keep the min-d² cell, and stop only when no closer cell can exist (r² >
        // bestD²) — first-found wins ties (stable scan order).
        static bool TrySnapToSub(Diggles.Runtime.Navigation.NavWorld nav,
            int sx, int sy, int maxR, byte requiredBits, out int fx, out int fy)
        {
            fx = sx; fy = sy;
            if ((nav.SubAt(sx, sy) & requiredBits) == requiredBits) return true;
            long bestD2 = long.MaxValue;
            for (int r = 1; r <= maxR; r++)
            {
                if ((long)r * r > bestD2) break;   // farther rings can't beat the best hit
                for (int dy = -r; dy <= r; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Mathf.Abs(dx) != r && Mathf.Abs(dy) != r) continue;   // ring perimeter only
                        if ((nav.SubAt(sx + dx, sy + dy) & requiredBits) != requiredBits) continue;
                        long d2 = (long)dx * dx + (long)dy * dy;
                        if (d2 < bestD2) { bestD2 = d2; fx = sx + dx; fy = sy + dy; }
                    }
            }
            return bestD2 != long.MaxValue;
        }

        static bool ParseTclBool(string s) =>
            s == "1" || s == "true" || s == "yes" || s == "on";

        // Generic "class_<field>" setter — stores the joined argument tail
        // on the current class. Accepts any arity ≥ 2; `class_defaulttxtanim`
        // for instance is 5-arg (submesh, animlist, start, mode).
        static string CmdSetField(DigglesTclContext ctx, string[] args, string fieldName)
        {
            if (args.Length < 2) return "";   // tolerant — nothing to store
            var c = RequireClass(ctx);
            string value = args.Length == 2
                ? args[1]
                : string.Join(" ", args, 1, args.Length - 1);
            switch (fieldName)
            {
                case "DefaultMaterial": c.DefaultMaterial = value; break;
                case "DefaultTxtAnim":  c.DefaultTxtAnim  = value; break;
                case "Physic":          c.Physic          = value; break;
                case "Collision":       c.Collision       = value; break;
            }
            return "";
        }

        // ----- new / del -----

        // new CLASS_NAME ?INSTANCE_NAME? ?POS? ?ROT? → returns handle
        // pos / rot are 3-element Tcl lists or quoted "X Y Z" strings
        // def_class kind string → the native class type (class+0x54). Corpus-derived
        // (40 saves × archived obj+0xE8 joined with the vanilla def_class kinds;
        // zero ambiguity): gnome 1, monster 2, material 3, tool 4, production 5, energy 6,
        // protection 7, transport 9, elevator 10, baby 11, info 12; dummies and everything
        // else 0. FUN_005c9fa0 allocates the type-dependent body sub-objects from this value.
        public static int NativeObjectTypeFromKind(string? kind) => kind switch
        {
            "gnome" => 1,
            "monster" => 2,
            "material" => 3,
            "tool" => 4,
            "production" => 5,
            "energy" => 6,
            "protection" => 7,
            "transport" => 9,
            "elevator" => 10,
            "baby" => 11,
            "info" => 12,
            _ => 0,
        };

        static string CmdNew(DigglesTclContext ctx, string[] args)
        {
            if (args.Length < 2)
                throw new TclException("wrong # args: should be \"new className ?name? ?pos? ?rot?\"");
            DigglesClass cls;
            if (!ctx.Classes.TryGetValue(args[1], out cls!))
            {
                // Real campaigns spawn thousands of `[new XYZ...]` markers
                // whose classes are def'd in Scripts/classes/**/*.tcl —
                // files we don't always load. Synthesize a placeholder so
                // the spawner can still try to resolve the model via
                // MarkerModelResolver's pre-baked class→3db map.
                if (!ctx.TolerateMissingClass)
                    throw new TclException($"unknown class \"{args[1]}\"");
                cls = new DigglesClass { Name = args[1] };
                ctx.Classes[args[1]] = cls;
            }
            var ent = new DigglesTclEntity
            {
                Class = cls,
                InstanceName = args.Length > 2 ? args[2] : "",
                // The native derives the object TYPE from the class descriptor (class+0x54,
                // FUN_005c9fa0 overwrites the archived word with it) and the SAVE FORMAT keys
                // the type-dependent body extensions (family/talkissue/planner/CProd) off it.
                // Fresh Tcl spawns previously left 0 → the writer omitted the family block for
                // gnomes → the ORIGINAL's loader (allocating per class) mis-read the following
                // bytes as family and crashed on a -1 children count (crash dump FUN_005d0ce0 →
                // CArray::SetSize ARRAY_BOUNDS).
                NativeObjectType = NativeObjectTypeFromKind(cls.Kind),
                // FUN_005c83f0 stamps obj+0x350 once at construction as game seconds / 150.
                CreationTimeHours = (float)((ctx.Sim?.GameTime ?? 0.0) /
                    ProdDispatcher.WorktimeHourSeconds),
            };
            if (args.Length > 3 && args[3].Length > 0) ent.Position = ParseVec3(args[3]);
            if (args.Length > 4 && args[4].Length > 0) ent.Rotation = ParseVec3(args[4]);

            // The native auto-names every unnamed object "<Class>_<n>" with a per-class
            // counter (begining corpus: Bier_1..Bier_3, Dummy_Gras_04_3, Dummy_Balken_e_2 —
            // every body carries a nonempty N12). set_objname later overrides (gnomes).
            if (ent.InstanceName.Length == 0)
                ent.InstanceName = ctx.NextAutoInstanceName(cls.Name);

            int handle = ctx.RegisterEntity(ent);
            ent.InitializeNativeCoreState();

            // Apply class defaults
            if (cls.DefaultAnim != null) ent.CurrentAnim = cls.DefaultAnim;

            // Seed `this` so bare `$this` interpolates to the handle in bodies (engine
            // commands special-case the literal `this`, but scripts also read `$this`).
            ent.VarsScope.Vars["this"] = handle.ToString(CultureInfo.InvariantCulture);

            // Initialize declared member vars to their class-def default (usually "", but e.g.
            // ANIM_STILL/ONCE/LOOP = 0/1/2) so reads during obj_init don't trip "no such variable"
            // AND don't shadow a genuine global const with empty (the $ANIM_STILL→Loop mushroom bug).
            foreach (var mv in ctx.CollectMemberVars(cls))
                if (!ent.VarsScope.Vars.ContainsKey(mv))
                    ent.VarsScope.Vars[mv] = ctx.MemberDefaultFor(cls, mv);

            // Run obj_init body with `this` bound + entity scope on top.
            // We do parents first so they can prep state subclasses depend on.
            RunInitChain(ctx, ent, cls);

            // Bridge to Unity scene: if a host spawner is wired and a
            // SpawnParent target is set, materialize a GameObject pair
            // (outer controller + inner Model). The spawner owns axis
            // conversion (Diggles native → Unity-frame localPosition,
            // -90X pitch chain on inner). obj_init has already populated
            // CurrentAnim, so the spawner can pick the right default clip.
            //
            // ISOLATED: a spawner/particle throw must NOT escape `new` — the entity is already
            // registered, and callers do bookkeeping right after (`sq_object summon`:
            // set object [new $item]; …; lappend object_list $object, sequencer.tcl:884-890).
            // A throw here aborted that proc mid-way: the handle never reached object_list, so
            // every later `[Object N]` index shifted by one (hats/sabers linked to the wrong
            // props, items "vanished"). The entity lives on un-rendered instead; return the
            // handle so the script's list stays aligned.
            try
            {
                if (ctx.SpawnParent != null && ctx.EntitySpawner != null)
                {
                    if (ctx.SequencePresentationDrain)
                    {
                        ctx.DeferSequencePresentation(ent);
                        Diggles.Runtime.Diagnostics.HitchFamilyTimingDiag.Count(
                            "cutscene.presentation-suppressed");
                    }
                    else
                    {
                        ent.GameObject = ctx.EntitySpawner.Spawn(ent, ctx.SpawnParent);
                        RefreshBuildingClanFlag(ent);
                    }
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[new {cls.Name}] spawner failed for #{handle}: {e.Message} — " +
                               "entity continues un-rendered (handle stays valid)");
            }

            // Replay class_particle emitter templates onto this instance so deco objects auto-emit
            // (waterfalls/crystals/lava/troll-smoke — the most common particle uses).
            try { ReplayParticleEmitters(ctx, handle, cls); }
            catch (System.Exception e)
            { Debug.LogError($"[new {cls.Name}] particle replay failed for #{handle}: {e.Message}"); }

            return handle.ToString(CultureInfo.InvariantCulture);
        }

        // LOGICAL payloads recorded by presentation commands issued during a skip drain, so the
        // pump can replay the exact state (not just re-assert whatever the entity fields hold).
        // Keyed by entity handle; the entity queue itself lives on the context. Records are
        // consumed on replay and wholesale-cleared when a new drain arms.
        static readonly ConditionalWeakTable<DigglesTclContext,
            Dictionary<int, (string Clip, int Frame, int Mode)>> s_drainDeferredAnims = new();
        static readonly ConditionalWeakTable<DigglesTclContext,
            Dictionary<int, System.Collections.Generic.List<(int Submesh,
                System.Collections.Generic.List<int> Indices, int Start, int Type)>>>
            s_drainDeferredTextureAnims = new();

        internal static void ResetDrainDeferredPayloads(DigglesTclContext ctx)
        {
            if (s_drainDeferredAnims.TryGetValue(ctx, out var anims)) anims.Clear();
            if (s_drainDeferredTextureAnims.TryGetValue(ctx, out var tex)) tex.Clear();
        }

        // Diagnostic/test surface: the recorded (not yet replayed) flipbook payload for an
        // entity+submesh deferred by a skip drain. Tests pin the record+consume law with it
        // (no InternalsVisibleTo into Runtime).
        public static bool TryPeekDrainDeferredTextureAnim(
            DigglesTclContext ctx,
            int handle,
            int submesh,
            out int[] frames,
            out int startFrame,
            out int animType)
        {
            frames = System.Array.Empty<int>();
            startFrame = 0;
            animType = 0;
            if (!s_drainDeferredTextureAnims.TryGetValue(ctx, out var map)
                || !map.TryGetValue(handle, out var records))
                return false;
            foreach (var record in records)
            {
                if (record.Submesh != submesh) continue;
                frames = record.Indices.ToArray();
                startFrame = record.Start;
                animType = record.Type;
                return true;
            }
            return false;
        }

        public static int PumpDeferredSequencePresentation(
            DigglesTclContext ctx,
            int budget = 1)
        {
            if (ctx == null) return 0;
            CompleteSequencePresentationDrain(ctx);
            if (ctx.SequencePresentationDrain || budget <= 0
                || ctx.SpawnParent == null || ctx.EntitySpawner == null)
                return 0;

            int remaining = ctx.DeferredSequencePresentationCount;
            int presented = 0;
            while (remaining-- > 0 && presented < budget
                && ctx.TryTakeDeferredSequencePresentation(out DigglesTclEntity ent))
            {
                if (!ctx.Entities.TryGetValue(ent.Handle, out DigglesTclEntity? live)
                    || !ReferenceEquals(live, ent))
                    continue;

                long started = Diggles.Runtime.Diagnostics.HitchFamilyTimingDiag.Start();
                using var _perf = Diggles.Runtime.Diagnostics.PerfTrace.Section(
                    "cutscene.presentation-replay",
                    50);
                try
                {
                    bool boxed = ent.VarsScope.Vars.TryGetValue("_boxed", out string boxedValue)
                        && TruthyFlagVar(boxedValue);
                    bool spawnedNow = false;
                    if (ent.GameObject == null)
                    {
                        // Deferred spawn resolves its pose from ent.CurrentAnim — but the drain
                        // may have left a pose the spawner cannot BUILD, and a failed clip build
                        // destroys the inner model → pink placeholder WITHOUT a controller, so
                        // the boxed swap below silently no-ops (example: a skipped intro's
                        // summoned Feuerstelle → packtobox → stop_production → set_buildupanim
                        // runs `set_anim this.standard` — $standard_anim is empty so $cname
                        // splits to "" (genericprod.tcl:803-806); the native set_anim ERRORS
                        // into the surrounding catch and changes nothing, but our CmdSetAnim
                        // latched CurrentAnim=".standard" → pump spawn placeholder'd:
                        // "[spawner] PLACEHOLDER class=Feuerstelle — clip build failed for
                        // '.standard'"). Seed the spawn pose the way the save loader stages a
                        // boxed body (OriginalSaveRuntimeSessionStager.MaterializeEntityPresent-
                        // ation: boxed → kiste.standard), and drop a non-buildable recorded
                        // pose so the class/TSV default dresses the model (= the native display
                        // state after an errored set_anim). CurrentAnim itself is restored —
                        // it keeps the RAW script name by the save-echo law.
                        string? echoAnim = ent.CurrentAnim;
                        int echoMode = ent.CurrentAnimMode;
                        if (boxed)
                        {
                            ent.CurrentAnim = "kiste.standard";
                            ent.CurrentAnimMode = (int)DigglesAnimator.AnimMode.Loop;
                        }
                        else if (!string.IsNullOrEmpty(echoAnim) && ctx.ModelLibrary != null
                            && ResolveAnimationClip(ctx, ent, echoAnim!) == null)
                        {
                            ent.CurrentAnim = null;
                        }
                        try { ent.GameObject = ctx.EntitySpawner.Spawn(ent, ctx.SpawnParent); }
                        finally
                        {
                            ent.CurrentAnim = echoAnim;
                            ent.CurrentAnimMode = echoMode;
                        }
                        spawnedNow = ent.GameObject != null;
                    }
                    if (ent.GameObject == null) continue;

                    ctx.EntitySpawner.ApplyPosition(ent.GameObject, ent.Position);
                    ReflectRotation(ctx, ent);
                    // The LOGICAL set_anim recorded at issue time (R3): ent.CurrentAnim keeps the
                    // RAW script name (save-echo law) and PlayClip resolves obj prefixes but NOT
                    // Class.AnimMap aliases — replay must use the alias-resolved clip + the real
                    // start frame, exactly what CmdSetAnim would have applied without the drain.
                    (string Clip, int Frame, int Mode) animRecord = default;
                    bool hasDeferredAnim = s_drainDeferredAnims.TryGetValue(ctx, out var animMap)
                        && animMap.TryGetValue(ent.Handle, out animRecord);
                    if (boxed)
                    {
                        GetController(ent)?.PlayClip(
                            "kiste.standard",
                            DigglesAnimator.AnimMode.Loop);
                        if (ent.Class != null)
                            Diggles.Runtime.BoxContentIcon.Attach(
                                ent.GameObject,
                                ent.Class.Name,
                                ctx.DataRoot);
                    }
                    else
                    {
                        Diggles.Runtime.BoxContentIcon.Detach(ent.GameObject);
                        if (hasDeferredAnim)
                        {
                            var mode = animRecord.Mode is >= 0 and <= 2
                                ? (DigglesAnimator.AnimMode)animRecord.Mode
                                : DigglesAnimator.AnimMode.Loop;
                            ctx.EntitySpawner.ApplyAnim(
                                ent.GameObject,
                                animRecord.Clip,
                                animRecord.Frame,
                                mode);
                        }
                        else if (!spawnedNow && !string.IsNullOrEmpty(ent.CurrentAnim))
                        {
                            var mode = ent.CurrentAnimMode is >= 0 and <= 2
                                ? (DigglesAnimator.AnimMode)ent.CurrentAnimMode
                                : DigglesAnimator.AnimMode.Loop;
                            ctx.EntitySpawner.ApplyAnim(ent.GameObject, ent.CurrentAnim!, 0, mode);
                        }
                        else if (!spawnedNow)
                        {
                            DigglesAnimController? controller = GetController(ent);
                            if (!string.IsNullOrEmpty(ent.Class?.DefaultAnim))
                                controller?.PlayClip(
                                    ent.Class!.DefaultAnim!,
                                    (DigglesAnimator.AnimMode)ent.Class.DefaultAnimMode);
                            else
                                controller?.PlayClip(
                                    "standard",
                                    DigglesAnimator.AnimMode.Loop);
                        }
                    }
                    if (hasDeferredAnim) animMap!.Remove(ent.Handle);
                    // Texture-animation replay (R1): flipbooks started/reconfigured during the
                    // drain were recorded as logical (submesh, frames, start, type) tuples —
                    // apply them now that the GameObject exists. ApplyFaceTextureAnim degrades
                    // safely when the submesh/material doesn't resolve (e.g. a boxed kiste).
                    if (s_drainDeferredTextureAnims.TryGetValue(ctx, out var texMap)
                        && texMap.TryGetValue(ent.Handle, out var texRecords))
                    {
                        foreach (var record in texRecords)
                            ApplyFaceTextureAnim(
                                ctx,
                                ent,
                                record.Submesh,
                                record.Indices,
                                record.Start,
                                record.Type);
                        texMap.Remove(ent.Handle);
                    }
                    SyncRenderVisible(ent);
                    RefreshBuildingClanFlag(ent);
                    // link_obj issued during the drain stored only the LOGICAL link (child+0x278
                    // mirror — RestoreLinkAttachment early-outs while either GameObject is still
                    // deferred); re-run the visual attach now that this GameObject exists, in
                    // both roles (drain-born hat/tool onto its carrier, and riders waiting on
                    // THIS entity as their carrier). Runs after SyncRenderVisible so the
                    // carrier's renderer sweep (inside RestoreLinkAttachment) owns the final
                    // visibility of the linked child.
                    ReplayLinkAttachments(ctx, ent);
                    presented++;
                    Diggles.Runtime.Diagnostics.HitchFamilyTimingDiag.Record(
                        "cutscene.presentation-replay",
                        started);
                }
                catch (System.Exception e)
                {
                    Debug.LogError(
                        $"[cutscene] deferred presentation failed for {ent.Class?.Name}#{ent.Handle}: {e.Message}");
                }
            }
            return presented;
        }

        static void CompleteSequencePresentationDrain(DigglesTclContext ctx)
        {
            if (!ctx.SequencePresentationDrain || ctx.SequenceActive) return;
            Diggles.Runtime.Diagnostics.HitchFamilyTimingDiag.Record(
                "cutscene.skip-drain",
                ctx.SequencePresentationDrainStarted,
                ctx.SequencePresentationDeferredOps);
            ctx.SequencePresentationDrain = false;
        }

        static void ReplayParticleEmitters(DigglesTclContext ctx, int handle, DigglesClass c)
        {
            if (ctx.Particles == null) return;
            int idx = 0;
            ReplayParticleEmittersRec(ctx, handle, c, ref idx);
        }

        static void ReplayParticleEmittersRec(DigglesTclContext ctx, int handle, DigglesClass c, ref int idx)
        {
            foreach (var baseName in c.Inherits)
                if (ctx.Classes.TryGetValue(baseName, out var bc))
                    ReplayParticleEmittersRec(ctx, handle, bc, ref idx);
            foreach (var e in c.ParticleEmitters)
            {
                var s = ctx.Particles!.Configure(handle, idx++, e.SetId,
                    new Vector3(e.Ox, e.Oy, e.Oz), new Vector3(e.Vx, e.Vy, e.Vz),
                    e.Count, e.Rate, false, -1, -1);
                s.Flag = e.Flag;
                s.Active = true;
            }
        }

        static void RunInitChain(DigglesTclContext ctx, DigglesTclEntity ent, DigglesClass c)
        {
            foreach (var baseName in c.Inherits)
                if (ctx.Classes.TryGetValue(baseName, out var bc))
                    RunInitChain(ctx, ent, bc);
            if (!string.IsNullOrEmpty(c.InitCode))
                RunInEntityContext(ctx, ent, c.InitCode, "obj_init");
        }

        // Load-path shell constructor half: the NATIVE loader creates every archived object
        // through the SAME creation callback as `new` (FUN_005d6790:106 → FUN_005c7b70:219 →
        // obj_init eval in the object's fresh interp), THEN overlays the archived variables.
        // obj_init is what installs the per-object behavior procs (z_events/z_procs/z_dignwalk
        // `else`-branches) — without it a restored dwarf's class has NO procs and every
        // order/state body silently no-ops (gnomes became unorderable/frozen after a reload).
        // The stager calls this per shell and
        // re-applies the archived identity afterwards, mirroring the native order.
        internal static void RunObjInitForRestoredShell(DigglesTclContext ctx, DigglesTclEntity ent)
        {
            if (ent.Class == null || ent.Class.DisableScripting) return;
            RunInitChain(ctx, ent, ent.Class);
        }

        static void RunExitChain(DigglesTclContext ctx, DigglesTclEntity ent, DigglesClass c)
        {
            if (!string.IsNullOrEmpty(c.ExitCode))
                RunInEntityContext(ctx, ent, c.ExitCode, "obj_exit");
            for (int i = c.Inherits.Count - 1; i >= 0; i--)
                if (ctx.Classes.TryGetValue(c.Inherits[i], out var baseClass))
                    RunExitChain(ctx, ent, baseClass);
        }

        // EditMode-safe Object destruction: the headless boot/think tests run the full game sim in
        // the editor where Object.Destroy logs "Destroy may not be called from edit mode!" (an
        // unhandled-error test failure). Same guard pattern as RuntimeWorldSpawner.Destroy.
        static void DestroyObjectSafe(Object? o)
        {
            if (o == null) return;
            if (Application.isPlaying) Object.Destroy(o);
            else Object.DestroyImmediate(o);
        }

        static string CmdDel(DigglesTclContext ctx, string[] args)
        {
            // Bare `del` (no handle) = delete SELF — FogRemover's event_delete handler is exactly
            // `handle_event event_delete { del }` (infoobjs.tcl:989-991); the strict arity threw
            // "[work-chain BROKE] FogRemover … wrong # args" after the 10-minute cutscene.
            if (args.Length > 2) throw new TclException("wrong # args: should be \"del handle\"");
            var ent = ctx.ResolveEntity(args.Length == 2 ? args[1] : "this");
            return DeleteEntity(ctx, ent, runExitChain: true);
        }

        // The shared logical teardown. `del` (FUN_00623810 → the script delete path) runs the
        // class obj_exit chain first; obj_clear's ShutDynObjects sweep (FUN_005d2920) deletes
        // through the raw C++ dtor (`(**(code **)*slot)(1)`,:455) WITHOUT any Tcl obj_exit —
        // FUN_0042fbb0 there is a plain DSFastArray truncate, not a scripted delete. The
        // distinction is load-bearing: a sequencer trigger's obj_exit→shut_sequencer chain
        // replays `set_view $viewpos` (sequencer.tcl:550-551), which on the tutorial's
        // obj_clear+map-create world swap clobbered the NEW world's authored set_view with the
        // RETIRED world's camera pose.
        static string DeleteEntity(DigglesTclContext ctx, DigglesTclEntity ent, bool runExitChain)
        {
            if (ent.DeleteInProgress) return "";
            ent.DeleteInProgress = true;

            System.Exception? exitFailure = null;
            if (runExitChain)
            {
                try
                {
                    RunExitChain(ctx, ent, ent.Class);
                }
                catch (System.Exception e)
                {
                    exitFailure = e;
                }
            }

            InterruptElevatorRide(ctx, ent.Handle);
            if (ent.TransportLogic?.Elevator is { } elevator)
                InterruptAllElevatorRides(ctx, ent.Handle, elevator);
            DeleteElevatorCabin(ctx, ent);

            if (ent.GameObject != null)
            {
                // Presentation teardown must NEVER abort the logical delete: sq_object delete
                // lrem's the handle from object_list BEFORE del (sequencer.tcl:905-913), so one
                // throwing destroy orphans every later summon — the gnome left standing after a
                // skip that wedged all following menu cutscenes. The native never fails a
                // logical delete over rendering.
                try
                {
                    if (ctx.EntitySpawner != null) ctx.EntitySpawner.Destroy(ent.GameObject);
                    else DestroyObjectSafe(ent.GameObject);
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[del #{ent.Handle} {ent.Class?.Name}] spawner destroy failed: {e.Message} — entity removed anyway");
                }
                ent.GameObject = null;
            }
            // Containment cleanup — the native delete pulls the object out of its container.
            // Without this the holder's Inventory kept a DEAD handle forever: counts never
            // dropped, so the worn basket stayed on after building/consuming from inventory,
            // capacity slots leaked and is_wearing_pannier stuck slow.
            if (ent.ContainedIn >= 0 && ctx.Entities.TryGetValue(ent.ContainedIn, out var holder))
            {
                holder.Inventory.Remove(ent.Handle);
                UpdateWornContainerVisual(ctx, holder);
            }
            // Release OUR contained children's back-reference (they'd dangle at a dead handle;
            // scripts empty containers before del — z_procs.tcl:331 — this is the safety net).
            foreach (var chH in ent.Inventory)
                if (ctx.Entities.TryGetValue(chH, out var ch) && ch.ContainedIn == ent.Handle)
                    ch.ContainedIn = -1;
            // Cancel any scheduled timer events so they don't deliver to a
            // freed handle later.
            ctx.Sim?.Scheduler.UnsetAll(ent.Handle);
            ctx.Timers.DropHandle(ent.Handle);
            PathWorld(ctx)?.RemoveInfluence(ent.Handle);
            ctx.Energy.OnEntityRemoved(ent.Handle);
            ctx.Fluids.OnEntityRemoved(ent.Handle);
            ctx.Particles?.OnEntityRemoved(ent.Handle);
            ctx.Entities.Remove(ent.Handle);
            ctx.EntitiesVersion++;

            if (exitFailure != null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exitFailure).Throw();
            return "";
        }

        static string CmdObjClear(DigglesTclContext ctx)
        {
            // FUN_006229f0 resets DAT_0076a338 placement mode before invoking
            // CObjMgr_ShutDynObjects (FUN_005d2920), whose handle-table sweep deletes every
            // dynamic object through the raw C++ dtor — NO Tcl obj_exit runs (that chain lives
            // only in the `del` command path, FUN_00623810). Snapshot the handles and run the
            // shared resource teardown WITHOUT the exit chain: GameObjects, containment, timers,
            // particle emitters, NTID slots all release, while script hooks (e.g. a sequencer's
            // shut/viewpos restore — sequencer.tcl:550-551) stay silent, exactly like the native
            // sweep. A self-deleting exec_deferred source cannot invalidate this enumeration or
            // the interpreter's remaining commands.
            ctx.Placement?.Cancel();
            ctx.Selection.Clear();

            var handles = new int[ctx.Entities.Count];
            ctx.Entities.Keys.CopyTo(handles, 0);
            foreach (int handle in handles)
            {
                if (!ctx.Entities.TryGetValue(handle, out var entity) || entity.DeleteInProgress)
                    continue;
                try
                {
                    DeleteEntity(ctx, entity, runExitChain: false);
                }
                catch (System.Exception)
                {
                    // Native CObjMgr_ShutDynObjects continues its entire slot sweep; obj_clear
                    // therefore must not abort Campaign.tcl's following statement.
                }
            }

            // Natively obj_clear never observes a RUNNING sequence: the sequencer closes itself
            // (set_sequence 0 + the viewpos restore) BEFORE its destroy chain queues the deferred
            // boot. The port's multi-step skip drain can interleave the deferred batch into that
            // window — a sequencer swept mid-drain would leave SequenceActive latched forever
            // (camera suppressed, menu cycling dead). Close the dangling flag raw, like the
            // native world state after the sweep.
            if (ctx.SequenceActive)
            {
                ctx.SequenceActive = false;
                ctx.SequenceBreaked = false;
            }
            ctx.ClearDeferredSequencePresentation();

            // FUN_006229f0's final FUN_00564a60(0) loop releases every GUI news row.
            ctx.NewsTicker.Clear();
            return "";
        }

        static string CmdDestruct(DigglesTclContext ctx, string[] args)
        {
            if (args.Length != 2)
                throw new TclException("wrong # args: should be \"destruct object\"");
            var ent = ResolveEntityOrNull(ctx, args[1]);
            if (ent == null) return "1";

            ShatterEntity(ctx, ent);
            ApplyRenderVisible(ent, false);
            RefreshTransportInfluence(ctx, ent);
            return "0";
        }

        static string CmdSetPathInfluence(DigglesTclContext ctx, string[] args)
        {
            if (args.Length != 8)
                throw new TclException(
                    "wrong # args: should be \"set_pf_influence object x0 y0 x1 y1 cost type\"");
            var entity = ctx.ResolveEntity(args[1]);
            entity.PathInfluence = new PathInfluenceState
            {
                X0 = ParseInt(args[2]),
                Y0 = ParseInt(args[3]),
                X1 = ParseInt(args[4]),
                Y1 = ParseInt(args[5]),
                Cost = ParseInfluenceCost(args[6]),
                Type = ParseInt(args[7]),
            };
            ApplyPathInfluence(ctx, entity);
            return "";
        }

        static string CmdCreateDoorLogic(DigglesTclContext ctx, string[] args)
        {
            if (args.Length != 2)
                throw new TclException("wrong # args: should be \"create_doorlogic object\"");
            var entity = ctx.ResolveEntity(args[1]);
            var transport = entity.TransportLogic ??= new TransportLogicState();
            transport.Door ??= new DoorLogicState();
            RefreshDoorInfluence(ctx, entity);
            return "";
        }

        static string CmdSetDoorProperties(DigglesTclContext ctx, string[] args)
        {
            if (args.Length is < 3 or > 4)
                throw new TclException(
                    "wrong # args: should be \"set_doorproperties object mode ?classes?\"");
            var entity = ctx.ResolveEntity(args[1]);
            var door = entity.TransportLogic?.Door
                ?? throw new TclException("object has no door logic");
            door.Mode = ParseDoorAccessMode(args[2]);
            door.AllowedClasses.Clear();
            if (door.Mode == DoorAccessMode.OpenForClasses && args.Length == 4)
                foreach (var className in TclList.Parse(args[3]))
                    door.AllowedClasses.Add(className);
            bool shouldOpen = door.Mode is DoorAccessMode.Open or DoorAccessMode.OpenForAll;
            door.IsTemporarilyOpen = shouldOpen;
            if (shouldOpen != door.IsVisuallyOpen)
                SetDoorPresentation(ctx, entity, door, shouldOpen);
            RefreshDoorInfluence(ctx, entity);
            return "";
        }

        static string CmdCreateLadderLogic(DigglesTclContext ctx, string[] args)
        {
            if (args.Length != 2)
                throw new TclException("wrong # args: should be \"create_ladderlogic object\"");
            var entity = ctx.ResolveEntity(args[1]);
            var transport = entity.TransportLogic ??= new TransportLogicState();
            transport.Ladder ??= new LadderLogicState();
            RefreshLadderInfluence(ctx, entity);
            return "";
        }

        static string CmdSetLadderRange(DigglesTclContext ctx, string[] args)
        {
            if (args.Length != 3)
                throw new TclException("wrong # args: should be \"set_ladderrange object range\"");
            var entity = ctx.ResolveEntity(args[1]);
            if (entity.TransportLogic?.Ladder is { } ladder)
            {
                ladder.Range = ParseFloat(args[2]);
                RefreshLadderInfluence(ctx, entity);
            }
            return "";
        }

        static string CmdCreateElevatorLogic(DigglesTclContext ctx, string[] args)
        {
            if (args.Length != 4)
                throw new TclException(
                    "wrong # args: should be \"create_elevatorlogic object cabin registeredTrack\"");
            var entity = ctx.ResolveEntity(args[1]);
            var cabin = ctx.ResolveEntity(args[2]);
            var transport = entity.TransportLogic ??= new TransportLogicState();
            transport.Elevator ??= new ElevatorLogicState
            {
                CabinHandle = cabin.Handle,
                UsesRegisteredTrack = ParseTclBool(args[3]),
            };
            RefreshElevatorInfluence(ctx, entity);
            return "";
        }

        static string CmdSetElevatorProperties(DigglesTclContext ctx, string[] args)
        {
            if (args.Length is < 5 or > 7)
                throw new TclException(
                    "wrong # args: should be \"set_elevatorproperties object range mode speed ?brake? ?riderCapacity?\"");
            var entity = ctx.ResolveEntity(args[1]);
            var elevator = entity.TransportLogic?.Elevator
                ?? throw new TclException("object has no elevator logic");
            elevator.MaxRange = ParseFloat(args[2]);
            elevator.AccelerationMode = ParseInt(args[3]);
            elevator.Speed = ParseFloat(args[4]);
            if (args.Length >= 6) elevator.BrakeAcceleration = ParseFloat(args[5]);
            if (args.Length == 7) elevator.RiderCapacity = Mathf.Clamp(
                ParseInt(args[6]),
                0,
                ElevatorLogicState.MaxRiders);
            RefreshElevatorInfluence(ctx, entity);
            return "";
        }

        static string CmdDeleteTransportLogic(DigglesTclContext ctx, string[] args)
        {
            if (args.Length != 2)
                throw new TclException("wrong # args: should be \"delete_transportlogic object\"");
            var entity = ctx.ResolveEntity(args[1]);
            if (entity.TransportLogic?.Elevator is { } elevator)
                InterruptAllElevatorRides(ctx, entity.Handle, elevator);
            DeleteElevatorCabin(ctx, entity);
            entity.TransportLogic = null;
            entity.PathInfluence = null;
            PathWorld(ctx)?.RemoveInfluence(entity.Handle);
            return "";
        }

        static void DeleteElevatorCabin(DigglesTclContext ctx, DigglesTclEntity drive)
        {
            int cabinHandle = drive.TransportLogic?.Elevator?.CabinHandle ?? -1;
            if (cabinHandle < 0 || cabinHandle == drive.Handle
                || !ctx.Entities.TryGetValue(cabinHandle, out var cabin)
                || cabin.DeleteInProgress)
                return;
            CmdDel(ctx, new[] { "del", cabinHandle.ToString(CultureInfo.InvariantCulture) });
        }

        static string CmdSetPlaceSnapMode(DigglesTclContext ctx, string[] args)
        {
            // fallen.tcl:60 writes `set_placesnapmode this 2 // 2 == floor …` — the shipped
            // interpreter swallows the trailing `//` comment tokens, so extra args are legal.
            if (args.Length < 3)
                throw new TclException("wrong # args: should be \"set_placesnapmode object mode\"");
            ctx.ResolveEntity(args[1]).PlaceSnapMode = ParseInt(args[2]);
            return "";
        }

        static string CmdGetPlaceSnapMode(DigglesTclContext ctx, string[] args)
        {
            if (args.Length != 2)
                throw new TclException("wrong # args: should be \"get_placesnapmode object\"");
            return ctx.ResolveEntity(args[1]).PlaceSnapMode.ToString(CultureInfo.InvariantCulture);
        }

        static string CmdSetSnapToWall(DigglesTclContext ctx, string[] args)
        {
            if (args.Length != 3)
                throw new TclException("wrong # args: should be \"set_snaptowall object value\"");
            var entity = ctx.ResolveEntity(args[1]);
            entity.SnapToWall = ParseTclBool(args[2]);
            if (entity.SnapToWall) SettleObjectSnapToWall(ctx, entity);
            RefreshTransportInfluence(ctx, entity);
            return "";
        }

        static string CmdGetSnapToWall(DigglesTclContext ctx, string[] args)
        {
            if (args.Length != 2)
                throw new TclException("wrong # args: should be \"get_snaptowall object\"");
            return ctx.ResolveEntity(args[1]).SnapToWall ? "1" : "0";
        }

        static string CmdSetForceInterpolation(DigglesTclContext ctx, string[] args)
        {
            if (args.Length != 3)
                throw new TclException("wrong # args: should be \"set_forceipol object value\"");
            ctx.ResolveEntity(args[1]).ForcePositionInterpolation = ParseTclBool(args[2]);
            return "";
        }

        static string CmdGetForceInterpolation(DigglesTclContext ctx, string[] args)
        {
            if (args.Length != 2)
                throw new TclException("wrong # args: should be \"get_forceipol object\"");
            return ctx.ResolveEntity(args[1]).ForcePositionInterpolation ? "1" : "0";
        }

        static int ParseInt(string value) =>
            int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);

        static int ParseInfluenceCost(string value) => value switch
        {
            "INT_MAX" => int.MaxValue,
            "INT_MIN" => int.MinValue,
            _ => ParseInt(value),
        };

        static DoorAccessMode ParseDoorAccessMode(string value) => value switch
        {
            "closed" => DoorAccessMode.Closed,
            "openforplayer" => DoorAccessMode.OpenForPlayer,
            "openforfriends" => DoorAccessMode.OpenForFriends,
            "openforclasses" => DoorAccessMode.OpenForClasses,
            "openforall" => DoorAccessMode.OpenForAll,
            _ => DoorAccessMode.Open,
        };

        static Diggles.Runtime.Navigation.NavWorld? PathWorld(DigglesTclContext ctx) =>
            ctx.Terrain?.Nav ?? ctx.NavService?.World;

        static void RefreshDoorInfluence(DigglesTclContext ctx, DigglesTclEntity entity)
        {
            var door = entity.TransportLogic?.Door;
            if (door == null)
            {
                entity.PathInfluence = null;
                ApplyPathInfluence(ctx, entity);
                return;
            }

            bool conditional = IsConditionalDoorMode(door.Mode);
            bool closed = door.Mode == DoorAccessMode.Closed
                || (conditional && !door.IsTemporarilyOpen);
            bool active = TransportInfluenceActive(entity);
            entity.PathInfluence = new PathInfluenceState
            {
                X0 = DoorInfluenceX0,
                Y0 = DoorInfluenceY0,
                X1 = DoorInfluenceX1,
                Y1 = DoorInfluenceY1,
                Cost = closed && active ? int.MaxValue : 0,
                Type = 4,
                TransportKind = Diggles.Runtime.Navigation.NavTransportKind.Door,
            };
            ApplyPathInfluence(ctx, entity);
        }

        static void RefreshTransportInfluence(DigglesTclContext ctx, DigglesTclEntity entity)
        {
            if (entity.TransportLogic?.Door != null) RefreshDoorInfluence(ctx, entity);
            else if (entity.TransportLogic?.Ladder != null) RefreshLadderInfluence(ctx, entity);
            else if (entity.TransportLogic?.Elevator != null) RefreshElevatorInfluence(ctx, entity);
        }

        internal static void RestorePathInfluence(DigglesTclContext ctx, DigglesTclEntity entity)
        {
            PathWorld(ctx)?.RemoveInfluence(entity.Handle);
            if (entity.TransportLogic != null) RefreshTransportInfluence(ctx, entity);
            // A restored transport can already have the same cached stamp as the snapshot.
            // Refresh then returns early, so apply the saved influence explicitly as well.
            ApplyPathInfluence(ctx, entity);
        }

        static void RefreshLadderInfluence(DigglesTclContext ctx, DigglesTclEntity entity)
        {
            var ladder = entity.TransportLogic?.Ladder;
            if (ladder == null) return;
            var nav = PathWorld(ctx);
            int centerX = Mathf.RoundToInt(entity.Position.x * 8f);
            int centerY = Mathf.RoundToInt(entity.Position.y * 8f);
            bool active = nav != null && TransportInfluenceActive(entity);
            float span = active ? ComputeLadderSpan(nav!, centerX, centerY, ladder.Range) : 0f;
            int y0 = span > 0f ? -Mathf.RoundToInt(span * 8f) - 4 : 0;
            bool changed = ladder.LastStampCenterX != centerX
                || ladder.LastStampCenterY != centerY
                || ladder.LastStampActive != active
                || System.Math.Abs(ladder.ProjectedSpan - span) > 1e-4f
                || entity.PathInfluence == null;
            ladder.ProjectedSpan = span;
            RefreshLadderPresentation(ctx, entity, ladder, span);
            if (!changed) return;
            ladder.LastStampCenterX = centerX;
            ladder.LastStampCenterY = centerY;
            ladder.LastStampActive = active;
            entity.PathInfluence = new PathInfluenceState
            {
                X0 = span > 0f ? -12 : 0,
                Y0 = y0,
                X1 = span > 0f ? 12 : 0,
                Y1 = span > 0f ? 6 : 0,
                Cost = span > 0f ? -20 : 0,
                Type = 4,
                AddsClimbSurface = span > 0f,
                HasClimbBounds = span > 0f,
                ClimbX0 = 0,
                ClimbY0 = y0,
                ClimbX1 = 0,
                ClimbY1 = span > 0f ? 6 : 0,
                TransportKind = Diggles.Runtime.Navigation.NavTransportKind.Ladder,
            };
            ApplyPathInfluence(ctx, entity);
        }

        public static float ComputeLadderSpan(
            Diggles.Runtime.Navigation.NavWorld nav,
            int centerX,
            int centerY,
            float range)
        {
            if (range <= 0f || centerX < 4 || centerX >= nav.SubW - 4
                || centerY < 4 || centerY >= nav.SubH - 4)
                return 0f;

            int baseHeight = nav.GetHmapSub(centerX, centerY - 2);
            if (baseHeight != 11) return 0f;
            float span = 0f;
            int scanY = centerY - 4;
            for (int step = 0; scanY > 4 && scanY < nav.SubH - 4; step++, scanY -= 4)
            {
                int leftDelta = baseHeight - nav.GetHmapSub(centerX - 4, scanY);
                int rightDelta = baseHeight - nav.GetHmapSub(centerX + 4, scanY);
                if ((uint)leftDelta > 1 || (uint)rightDelta > 1) break;
                span = step * 0.5f + 1f;
                if (span >= range) return range;
            }
            return Mathf.Min(span, range);
        }

        // The mode-4 acceptance threshold: placement requires `FUN_0060b560 != 0`
        // (FUN_0046b450 mode-4 accept block), i.e. span + 1 (DAT_00737138) must reach the
        // SECOND registered leiter_* rung entry — heights are registered a..i in order as
        // (bboxMaxY − bboxMinY)·DAT_00736fcc − 0.5 (FUN_0060ad40:90-168,:163-164). Returns
        // the minimum legal span (height(slot 1) − 1) from the loaded transport.3db.
        public static bool TryGetLadderMinimumSpan(
            DigglesModelLibrary library,
            out float minimumSpan)
        {
            minimumSpan = 0f;
            int found = 0;
            for (char suffix = 'a'; suffix <= 'i'; suffix++)
            {
                if (!library.TryGetMeshBBox(
                        "leiter_" + suffix,
                        "standard",
                        0,
                        out Vector3 minimum,
                        out Vector3 maximum))
                    continue;
                if (++found < 2) continue;
                minimumSpan = (maximum.y - minimum.y)
 * DigglesModelLibrary.ModelToWorldScale - 0.5f - 1f;
                return true;
            }
            return false;
        }

        public static bool TrySelectLadderPresentation(
            DigglesModelLibrary library,
            float span,
            out string objectName,
            out Vector3 minimum,
            out Vector3 maximum)
        {
            objectName = "";
            minimum = maximum = default;
            string firstObject = "";
            Vector3 firstMinimum = default;
            Vector3 firstMaximum = default;
            for (char suffix = 'a'; suffix <= 'i'; suffix++)
            {
                string candidate = "leiter_" + suffix;
                if (!library.TryGetMeshBBox(
                        candidate,
                        "standard",
                        0,
                        out Vector3 candidateMinimum,
                        out Vector3 candidateMaximum))
                    continue;
                if (firstObject.Length == 0)
                {
                    firstObject = candidate;
                    firstMinimum = candidateMinimum;
                    firstMaximum = candidateMaximum;
                }
                float threshold = (candidateMaximum.y - candidateMinimum.y)
 * DigglesModelLibrary.ModelToWorldScale - 0.5f;
                if (span > 0f && span + 1f + 1e-4f >= threshold)
                {
                    objectName = candidate;
                    minimum = candidateMinimum;
                    maximum = candidateMaximum;
                }
            }

            if (objectName.Length != 0) return true;
            objectName = firstObject;
            minimum = firstMinimum;
            maximum = firstMaximum;
            return objectName.Length != 0;
        }

        static void RefreshLadderPresentation(
            DigglesTclContext ctx,
            DigglesTclEntity entity,
            LadderLogicState ladder,
            float span)
        {
            if (span <= 0f)
            {
                ladder.CurrentPresentation = "";
                ladder.CurrentPresentationClip = "";
                return;
            }
            if (ctx.ModelLibrary == null || entity.GameObject == null
                || ctx.EntitySpawner == null
                || !TrySelectLadderPresentation(
                    ctx.ModelLibrary,
                    span,
                    out string objectName,
                    out _,
                    out _))
                return;
            if (!TryReassertLadderPresentationState(entity, ladder, objectName, out string clip))
                return;
            ctx.EntitySpawner.ApplyAnim(
                entity.GameObject,
                clip,
                0,
                Diggles.Runtime.DigglesAnimator.AnimMode.Still);
        }

        /// <summary>
        /// Reasserts the selected ladder rung against the entity's factual animation state.
        /// </summary>
        public static bool TryReassertLadderPresentationState(
            DigglesTclEntity entity,
            LadderLogicState ladder,
            string objectName,
            out string clip)
        {
            if (entity == null) throw new System.ArgumentNullException(nameof(entity));
            if (ladder == null) throw new System.ArgumentNullException(nameof(ladder));
            if (string.IsNullOrEmpty(objectName))
                throw new System.ArgumentException("A ladder presentation name is required.", nameof(objectName));

            if (!string.Equals(
                    ladder.CurrentPresentation,
                    objectName,
                    System.StringComparison.Ordinal)
                || ladder.CurrentPresentationClip.Length == 0)
            {
                ladder.CurrentPresentation = objectName;
                ladder.CurrentPresentationClip = objectName + ".standard";
            }

            clip = ladder.CurrentPresentationClip;
            int stillMode = (int)Diggles.Runtime.DigglesAnimator.AnimMode.Still;
            // Native CTransportLogic selects the rung and calls SetAnimation on EVERY transport
            // tick (FUN_00607000:49-54). The presentation-name cache is not observable truth:
            // another animation writer can replace the entity clip while the selected rung stays
            // unchanged. Compare the factual CurrentAnim/mode, then repair only on divergence.
            if (string.Equals(entity.CurrentAnim, clip, System.StringComparison.Ordinal)
                && entity.CurrentAnimMode == stillMode)
                return false;

            entity.CurrentAnim = clip;
            entity.CurrentAnimMode = stillMode;
            return true;
        }

        static void RefreshElevatorInfluence(DigglesTclContext ctx, DigglesTclEntity entity)
        {
            var elevator = entity.TransportLogic?.Elevator;
            if (elevator == null) return;
            var nav = PathWorld(ctx);
            int centerX = Mathf.RoundToInt(entity.Position.x * 8f);
            int centerY = Mathf.RoundToInt(entity.Position.y * 8f);
            bool driveActive = TransportInfluenceActive(entity);
            bool active = nav != null && driveActive;
            float span = active ? ComputeElevatorSpan(nav!, centerX, centerY, elevator.MaxRange) : 0f;
            bool projected = span > 0.5f;
            bool changed = elevator.LastStampCenterX != centerX
                || elevator.LastStampCenterY != centerY
                || elevator.LastStampActive != active
                || System.Math.Abs(elevator.ProjectedSpan - span) > 1e-4f
                || entity.PathInfluence == null;
            elevator.ProjectedSpan = span;
            elevator.TrackTopY = entity.Position.y;
            elevator.TrackBottomY = System.Math.Max(
                elevator.TrackTopY,
                entity.Position.y + span + 0.5f);

            bool driveMoved = !elevator.HasLastDrivePosition
                || (elevator.LastDrivePosition - entity.Position).sqrMagnitude > 1e-8f;
            if (driveMoved)
            {
                if (elevator.HasLastDrivePosition)
                    InterruptAllElevatorRides(ctx, entity.Handle, elevator);
                elevator.LastDrivePosition = entity.Position;
                elevator.HasLastDrivePosition = true;
                elevator.CurrentCabinY = entity.Position.y + 2f;
                elevator.TargetY = elevator.CurrentCabinY;
                elevator.Velocity = 0f;
                elevator.TravelDirection = 0f;
                elevator.TransferDwellSeconds = 0f;
                elevator.MotionState = ElevatorMotionState.Idle;
                elevator.Initialized = true;
            }

            if (ctx.Entities.TryGetValue(elevator.CabinHandle, out DigglesTclEntity cabin))
            {
                if (driveMoved)
                    ApplyElevatorCabinPosition(ctx, entity, cabin, elevator.CurrentCabinY);
                if (!elevator.HasCabinVisibility || elevator.CabinVisible != driveActive)
                {
                    elevator.CabinVisible = driveActive;
                    elevator.HasCabinVisibility = true;
                    ApplyRenderVisible(cabin, driveActive);
                }
            }
            if (!changed) return;
            elevator.LastStampCenterX = centerX;
            elevator.LastStampCenterY = centerY;
            elevator.LastStampActive = active;
            entity.PathInfluence = new PathInfluenceState
            {
                X0 = projected ? -16 : 0,
                Y0 = projected ? 8 : 0,
                X1 = projected ? 16 : 0,
                Y1 = projected ? Mathf.RoundToInt(span * 8f) + 4 : 0,
                Cost = projected ? -20 : 0,
                Type = 12,
                AddsClimbSurface = projected,
                TransportKind = Diggles.Runtime.Navigation.NavTransportKind.Elevator,
            };
            ApplyPathInfluence(ctx, entity);
        }

        public static float ComputeElevatorSpan(
            Diggles.Runtime.Navigation.NavWorld nav,
            int centerX,
            int centerY,
            float maxRange)
        {
            if (maxRange <= 0.5f || centerX < 4 || centerX >= nav.SubW - 4
                || centerY < 0 || centerY >= nav.SubH - 3)
                return 0f;

            float span = 0.375f; // DAT_00737070
            for (int scanY = centerY + 3;
                scanY < nav.SubH - 4 && span < maxRange;
                scanY++, span += 0.125f) // DAT_0073703c
            {
                if (nav.GetHmapSub(centerX - 4, scanY) != 11
                    || nav.GetHmapSub(centerX + 4, scanY) != 11)
                    break;
            }
            return Mathf.Min(span, maxRange);
        }

        static void SettleObjectSnapToWall(DigglesTclContext ctx, DigglesTclEntity entity)
        {
            if (!entity.SnapToWall || entity.ContainedIn >= 0 || FlagVarSet(entity, "_boxed")
                || PathWorld(ctx) is not { } nav)
                return;
            int sx = Mathf.RoundToInt(entity.Position.x * 8f);
            int sy = Mathf.RoundToInt(entity.Position.y * 8f);
            if (sx < 2 || sx >= nav.SubW - 2 || sy < 2 || sy >= nav.SubH - 2) return;
            int height = nav.GetHmapSub(sx, sy);
            height = System.Math.Min(height, nav.GetHmapSub(sx - 2, sy));
            height = System.Math.Min(height, nav.GetHmapSub(sx + 2, sy));
            height = System.Math.Min(height, nav.GetHmapSub(sx, sy - 2));
            height = System.Math.Min(height, nav.GetHmapSub(sx, sy + 2));
            float z = height + Diggles.Runtime.World.TunnelTorchSystem.SettleLift;
            if (System.Math.Abs(entity.Position.z - z) < 1e-4f) return;
            entity.Position = new Vector3(entity.Position.x, entity.Position.y, z);
            if (entity.GameObject != null && ctx.EntitySpawner != null)
                ctx.EntitySpawner.ApplyPosition(entity.GameObject, entity.Position);
        }

        static bool TransportInfluenceActive(DigglesTclEntity entity) =>
            entity.BuildupComplete
            && !FlagVarSet(entity, "_boxed")
            && (!entity.VarsScope.Vars.TryGetValue("_visibility", out var visible)
                || TruthyFlagVar(visible));

        // The door influence rect, sub-cells relative to the seat: obj+0x254..+0x260 =
        // [-8,-20,+8,+4] (FUN_0060ceb0:78-81). Shared by the pathfinding stamp AND the
        // auto-open occupancy test below — the aperture band whose cost the door governs.
        //
        // ⚠ There is NO native distance envelope. The previously used 13.5 (0x41580000,
        // FUN_0040cf60:82) is NOT a radius: Ghidra's FUN_00415d10 arg mapping is shifted —
        // the float lands in CAct_MetaWalk+0x24 (+0x1c is the path END INDEX, an int:
        // FUN_004163a0:27,:46,:67; FUN_00407fb0:97-100 direct-fills [7]=+0x1c from
        // CPath+0xc and [9]=+0x24 = -1.0f). +0x24 flows FUN_00415f40:24 → FUN_0041a910
        // param_8 → CAct_WalkFloor+0x44 → FUN_0041a2f0:41-42: the destination Z (depth)
        // coordinate of the walk's final waypoint when not the -1 sentinel — 13.5 sits
        // between climb-up z=14.5 (DAT_00737564) and climb-down z=12.5 (DAT_00737548),
        // i.e. "stand at the door" depth. Every other CAct passes -1.0 there.
        //
        // The native open/close law is MEMBERSHIP, not proximity: FUN_0060d220's tail
        // (:80-103) registers the mover id in the DoorLogic DSFastArray (+0x24/+0x28)
        // when the CAct_Door pass-check grants passage (FUN_0040d230:69), and
        // FUN_0060d470:43-44 flips the door state to 3 (close) when the last registrant
        // deregisters (act end/abort, FUN_0040d230:29,:96). The port has no per-mover
        // CAct_Door, so the observable stand-in = a qualifying mover ACTIVELY WALKING
        // (DrivesTransform ≈ the walk-act-alive gate) inside the door's own influence
        // rect — the cells a native door-leg traverses while registered.
        private const int DoorInfluenceX0 = -8;
        private const int DoorInfluenceY0 = -20;
        private const int DoorInfluenceX1 = 8;
        private const int DoorInfluenceY1 = 4;

        private static bool IsConditionalDoorMode(DoorAccessMode mode) =>
            mode is DoorAccessMode.OpenForPlayer
                or DoorAccessMode.OpenForFriends
                or DoorAccessMode.OpenForClasses;

        public static bool DoorAllows(
            DigglesTclContext ctx,
            DigglesTclEntity doorEntity,
            DigglesTclEntity mover)
        {
            var door = doorEntity.TransportLogic?.Door;
            if (door == null) return true;
            switch (door.Mode)
            {
                case DoorAccessMode.Open:
                case DoorAccessMode.OpenForAll:
                    return true;
                case DoorAccessMode.Closed:
                    return false;
                case DoorAccessMode.OpenForPlayer:
                    return mover.Owner == doorEntity.Owner;
                case DoorAccessMode.OpenForFriends:
                    if (mover.Owner == doorEntity.Owner) return true;
                    if ((uint)mover.Owner >= 8 || (uint)doorEntity.Owner >= 8) return false;
                    // FUN_0060d220 case 3 (:41-56): BOTH lookups read the DOOR OWNER'S diplomacy
                    // row — FUN_004c4130 selects the row (`this + rowIdx*0x34`) with the door
                    // owner (:47, re-loaded from door_obj+0x36c at:50), FUN_0045f830 selects the
                    // column (`row + colIdx*4`) with the mover owner (:48,:51). First test expects
                    // 2 (friend), the fallback expects 1 (ally). The mover's own row is NEVER
                    // consulted — one-directional, composing with the one-way set_diplomacy.
                    return ctx.Diplomacy[doorEntity.Owner, mover.Owner] is 1 or 2;
                case DoorAccessMode.OpenForClasses:
                    if (mover.Class == null) return false;
                    foreach (string allowedClass in door.AllowedClasses)
                        if (ClassIsA(ctx, mover.Class, allowedClass)) return true;
                    return false;
                default:
                    return false;
            }
        }

        static System.Collections.Generic.List<Diggles.Runtime.Navigation.NavWaypoint>? FindPathWithDoors(
            DigglesTclContext ctx,
            Diggles.Runtime.Navigation.NavWorldService nav,
            DigglesTclEntity mover,
            Vector3 worldStart,
            Vector3 worldTarget,
            bool canClimb,
            bool digWalk = false)
        {
            System.Collections.Generic.List<(DigglesTclEntity entity, bool wasOpen)>? changed = null;
            try
            {
                foreach (var candidate in ctx.Entities.Values)
                {
                    var door = candidate.TransportLogic?.Door;
                    if (door == null || !IsConditionalDoorMode(door.Mode)
                        || !TransportInfluenceActive(candidate))
                        continue;
                    bool shouldOpen = DoorAllows(ctx, candidate, mover);
                    if (door.IsTemporarilyOpen == shouldOpen) continue;
                    (changed ??= new System.Collections.Generic.List<(DigglesTclEntity, bool)>())
                        .Add((candidate, door.IsTemporarilyOpen));
                    door.IsTemporarilyOpen = shouldOpen;
                    RefreshDoorInfluence(ctx, candidate);
                }
                return nav.FindPath(
                    worldStart,
                    worldTarget,
                    canClimb,
                    preserveRelaxedGoal: digWalk);
            }
            finally
            {
                if (changed != null)
                    foreach (var entry in changed)
                    {
                        entry.entity.TransportLogic!.Door!.IsTemporarilyOpen = entry.wasOpen;
                        RefreshDoorInfluence(ctx, entry.entity);
                    }
            }
        }

        public static void TickTransportLogic(DigglesTclContext ctx, double dt = 0.1)
        {
            foreach (var entity in ctx.Entities.Values)
            {
                if (entity.TransportLogic?.Ladder != null)
                {
                    SettleObjectSnapToWall(ctx, entity);
                    RefreshLadderInfluence(ctx, entity);
                }
                if (entity.TransportLogic?.Elevator is { } elevator)
                {
                    RefreshElevatorInfluence(ctx, entity);
                    TickElevatorLogic(ctx, entity, elevator, (float)System.Math.Max(0.0, dt));
                }
                var door = entity.TransportLogic?.Door;
                if (door == null) continue;
                bool conditional = IsConditionalDoorMode(door.Mode);
                if (!conditional)
                {
                    door.IsTemporarilyOpen = door.Mode is DoorAccessMode.Open or DoorAccessMode.OpenForAll;
                    continue;
                }

                DigglesTclEntity? opener = null;
                if (TransportInfluenceActive(entity))
                {
                    int doorSubX = Mathf.RoundToInt(entity.Position.x * 8f);
                    int doorSubY = Mathf.RoundToInt(entity.Position.y * 8f);
                    foreach (var mover in ctx.Entities.Values)
                    {
                        if (ReferenceEquals(mover, entity) || mover.ContainedIn >= 0
                            || mover.ActiveAction == null || !mover.ActiveAction.DrivesTransform)
                            continue;
                        // Occupancy of the door's own influence rect (FUN_0060ceb0:78-81) —
                        // see the DoorInfluence* law note. No distance sphere exists natively.
                        int dx = Mathf.RoundToInt(mover.Position.x * 8f) - doorSubX;
                        int dy = Mathf.RoundToInt(mover.Position.y * 8f) - doorSubY;
                        if (dx < DoorInfluenceX0 || dx > DoorInfluenceX1
                            || dy < DoorInfluenceY0 || dy > DoorInfluenceY1)
                            continue;
                        if (!DoorAllows(ctx, entity, mover)) continue;
                        opener = mover;
                        break;
                    }
                }

                bool shouldOpen = opener != null;
                if (shouldOpen == door.IsTemporarilyOpen) continue;
                door.IsTemporarilyOpen = shouldOpen;
                if (opener != null) door.OpensLeft = opener.Position.x >= entity.Position.x;
                SetDoorPresentation(ctx, entity, door, shouldOpen);
                RefreshDoorInfluence(ctx, entity);
            }
        }

        private const double ElevatorCallLeaseSeconds = 1.6;
        private const int ElevatorCabinLinkId = 0;
        // Mirrors CAct_Elevator destructor ownership without a world scan on every action change.
        private static readonly ConditionalWeakTable<DigglesTclContext, Dictionary<int, int>>
            s_elevatorRideDrives = new();

        internal static void RegisterElevatorRide(
            DigglesTclContext ctx,
            int riderHandle,
            int driveHandle)
        {
            InterruptElevatorRide(ctx, riderHandle);
            s_elevatorRideDrives.GetOrCreateValue(ctx)[riderHandle] = driveHandle;
        }

        internal static void UnregisterElevatorRide(
            DigglesTclContext ctx,
            int riderHandle,
            int driveHandle)
        {
            if (s_elevatorRideDrives.TryGetValue(ctx, out Dictionary<int, int> drives)
                && drives.TryGetValue(riderHandle, out int indexedDrive)
                && indexedDrive == driveHandle)
                drives.Remove(riderHandle);
        }

        public static bool InterruptElevatorRide(DigglesTclContext ctx, int riderHandle)
        {
            if (!s_elevatorRideDrives.TryGetValue(ctx, out Dictionary<int, int> drives)
                || !drives.TryGetValue(riderHandle, out int driveHandle))
                return false;
            drives.Remove(riderHandle);
            if (!ctx.Entities.TryGetValue(driveHandle, out DigglesTclEntity drive)
                || drive.TransportLogic?.Elevator is not { } elevator
                || !elevator.Riders.TryGetValue(riderHandle, out ElevatorRiderRequest request))
                return true;
            request.Phase = ElevatorRidePhase.Interrupted;
            request.CabinSlot = -1;
            elevator.Riders.Remove(riderHandle);
            ResetElevatorWhenUnserviced(elevator);
            return true;
        }

        private static void InterruptAllElevatorRides(
            DigglesTclContext ctx,
            int driveHandle,
            ElevatorLogicState elevator)
        {
            foreach (ElevatorRiderRequest request in elevator.Riders.Values)
            {
                request.Phase = ElevatorRidePhase.Interrupted;
                request.CabinSlot = -1;
                UnregisterElevatorRide(ctx, request.RiderHandle, driveHandle);
            }
            elevator.Riders.Clear();
            ResetElevatorWhenUnserviced(elevator);
        }

        internal static void ResetElevatorWhenUnserviced(ElevatorLogicState elevator)
        {
            if (elevator.Riders.Count != 0) return;
            elevator.TargetY = elevator.CurrentCabinY;
            elevator.Velocity = 0f;
            elevator.TravelDirection = 0f;
            elevator.TransferDwellSeconds = 0f;
            elevator.MotionState = ElevatorMotionState.Idle;
        }

        private static bool HasElevatorTransfer(ElevatorLogicState elevator)
        {
            foreach (ElevatorRiderRequest request in elevator.Riders.Values)
                if (request.Phase is ElevatorRidePhase.Boarding or ElevatorRidePhase.Exiting)
                    return true;
            return false;
        }

        static void TickElevatorLogic(
            DigglesTclContext ctx,
            DigglesTclEntity drive,
            ElevatorLogicState elevator,
            float dt)
        {
            if (!ctx.Entities.TryGetValue(elevator.CabinHandle, out var cabin))
            {
                InterruptAllElevatorRides(ctx, drive.Handle, elevator);
                return;
            }

            bool driveActive = TransportInfluenceActive(drive);
            bool active = driveActive && elevator.ProjectedSpan > 0.5f;
            if (!elevator.Initialized)
            {
                elevator.CurrentCabinY = drive.Position.y + 2f; // DAT_00737330
                elevator.TargetY = elevator.CurrentCabinY;
                elevator.Initialized = true;
                ApplyElevatorCabinPosition(ctx, drive, cabin, elevator.CurrentCabinY);
            }

            if (!active)
            {
                InterruptAllElevatorRides(ctx, drive.Handle, elevator);
                ApplyElevatorCabinPosition(ctx, drive, cabin, elevator.CurrentCabinY);
                // A boxed drive is rendered by the port's kiste clip swap. Only an active,
                // unboxed drive may replace that presentation with a lift animation.
                if (driveActive)
                    SetElevatorPresentation(ctx, drive, elevator, "lift_still");
                else
                    elevator.CurrentPresentation = "";
                return;
            }

            System.Collections.Generic.List<int>? expired = null;
            foreach (var pair in elevator.Riders)
            {
                var request = pair.Value;
                request.LeaseSeconds += dt;
                bool waitingCallExpired = request.Phase == ElevatorRidePhase.Calling
                    && request.LeaseSeconds > ElevatorCallLeaseSeconds;
                if (waitingCallExpired
                    || !ctx.Entities.ContainsKey(request.RiderHandle)
                    || request.Phase is ElevatorRidePhase.Completed or ElevatorRidePhase.Interrupted)
                {
                    request.Phase = ElevatorRidePhase.Interrupted;
                    request.CabinSlot = -1;
                    (expired ??= new System.Collections.Generic.List<int>()).Add(pair.Key);
                    continue;
                }
            }
            if (expired != null)
                foreach (int handle in expired)
                {
                    elevator.Riders.Remove(handle);
                    UnregisterElevatorRide(ctx, handle, drive.Handle);
                }
            ResetElevatorWhenUnserviced(elevator);

            float before = elevator.CurrentCabinY;
            if (HasElevatorTransfer(elevator) || elevator.TransferDwellSeconds > 0f)
            {
                elevator.TransferDwellSeconds = Mathf.Max(
                    0f,
                    elevator.TransferDwellSeconds - dt);
                elevator.TargetY = elevator.CurrentCabinY;
                elevator.Velocity = 0f;
                elevator.MotionState = ElevatorMotionState.StoppedForTransfer;
            }
            else if (TrySelectElevatorTarget(elevator, out float target))
            {
                elevator.TargetY = target;
                float direction = Mathf.Sign(target - elevator.CurrentCabinY);
                if (direction != 0f) elevator.TravelDirection = direction;
                AdvanceElevatorMotion(elevator, dt);
                elevator.MotionState = Mathf.Abs(elevator.CurrentCabinY - target) <= 1e-4f
                    ? ElevatorMotionState.StoppedForTransfer
                    : ElevatorMotionState.Moving;
            }
            else
            {
                elevator.TargetY = elevator.CurrentCabinY;
                elevator.Velocity = 0f;
                elevator.TravelDirection = 0f;
                elevator.MotionState = ElevatorMotionState.Idle;
            }

            ApplyElevatorCabinPosition(ctx, drive, cabin, elevator.CurrentCabinY);
            float movement = elevator.CurrentCabinY - before;
            SetElevatorPresentation(
                ctx,
                drive,
                elevator,
                movement > 1e-5f ? "lift_down" : movement < -1e-5f ? "lift_up" : "lift_still");

            Vector3 cabinLinkPosition = cabin.Position;
            bool cabinLinkResolved = false;
            foreach (var request in elevator.Riders.Values)
            {
                if (request.Phase != ElevatorRidePhase.Riding
                    || !ctx.Entities.TryGetValue(request.RiderHandle, out var rider))
                    continue;
                if (!cabinLinkResolved)
                {
                    if (TryGetLinkOffsetNative(
                            ctx,
                            cabin,
                            ElevatorCabinLinkId,
                            applyRotation: false,
                            out Vector3 cabinLinkOffset))
                        cabinLinkPosition += cabinLinkOffset;
                    cabinLinkResolved = true;
                }
                Vector3 position = cabinLinkPosition + request.CabinOffset;
                rider.Position = position;
                if (rider.GameObject != null && ctx.EntitySpawner != null)
                    ctx.EntitySpawner.ApplyPosition(rider.GameObject, position);
            }
        }

        static bool TrySelectElevatorTarget(ElevatorLogicState elevator, out float target)
        {
            int fixedRiderCount = 0;
            foreach (var request in elevator.Riders.Values)
                if (request.Phase == ElevatorRidePhase.Riding)
                    fixedRiderCount++;

            float direction = Mathf.Sign(elevator.TravelDirection);
            float bestDistance = float.MaxValue;
            float bestInDirection = float.MaxValue;
            target = elevator.CurrentCabinY;
            float directionalTarget = target;
            bool found = false;
            bool foundDirectional = false;
            foreach (var request in elevator.Riders.Values)
            {
                if (request.Phase != ElevatorRidePhase.Riding) continue;
                float candidate = request.DestinationY;
                float delta = candidate - elevator.CurrentCabinY;
                float distance = Mathf.Abs(delta);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    target = candidate;
                    found = true;
                }
                if (direction != 0f && Mathf.Sign(delta) == direction && distance < bestInDirection)
                {
                    bestInDirection = distance;
                    directionalTarget = candidate;
                    foundDirectional = true;
                }
            }
            if (fixedRiderCount > 0 && !foundDirectional && found)
            {
                direction = Mathf.Sign(target - elevator.CurrentCabinY);
                if (direction != 0f)
                {
                    directionalTarget = target;
                    bestInDirection = bestDistance;
                    foundDirectional = true;
                }
            }

            if (fixedRiderCount < elevator.RiderCapacity)
            {
                foreach (var request in elevator.Riders.Values)
                {
                    if (request.Phase != ElevatorRidePhase.Calling) continue;
                    float callDirection = Mathf.Sign(request.DestinationY - request.PickupY);
                    if (fixedRiderCount > 0 && direction != 0f
                        && callDirection * direction < 0f)
                        continue;

                    float candidate = request.PickupY;
                    float delta = candidate - elevator.CurrentCabinY;
                    float distance = Mathf.Abs(delta);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        target = candidate;
                        found = true;
                    }
                    if (direction != 0f
                        && Mathf.Sign(delta) == direction
                        && distance < bestInDirection)
                    {
                        bestInDirection = distance;
                        directionalTarget = candidate;
                        foundDirectional = true;
                    }
                }
            }
            if (foundDirectional) target = directionalTarget;
            return found;
        }

        public static void AdvanceElevatorMotion(ElevatorLogicState elevator, float dt)
        {
            float delta = elevator.TargetY - elevator.CurrentCabinY;
            if (Mathf.Abs(delta) <= 1e-4f || dt <= 0f)
            {
                elevator.CurrentCabinY = elevator.TargetY;
                elevator.Velocity = 0f;
                return;
            }

            float direction = Mathf.Sign(delta);
            if (elevator.AccelerationMode == 0)
            {
                float step = Mathf.Max(0.001f, elevator.Speed) * dt;
                elevator.CurrentCabinY = Mathf.MoveTowards(
                    elevator.CurrentCabinY,
                    elevator.TargetY,
                    step);
                elevator.Velocity = direction * Mathf.Max(0.001f, elevator.Speed);
                if (Mathf.Abs(elevator.TargetY - elevator.CurrentCabinY) <= 1e-4f)
                    elevator.Velocity = 0f;
                return;
            }

            const float nativeTickSeconds = 0.1f; // DAT_00737034
            float acceleration = Mathf.Max(0.001f, elevator.Speed);
            float brake = Mathf.Max(0.001f, elevator.BrakeAcceleration);
            float tickScale = dt / nativeTickSeconds;
            float signedVelocity = elevator.Velocity;
            if (signedVelocity != 0f && Mathf.Sign(signedVelocity) != direction)
            {
                signedVelocity = Mathf.MoveTowards(signedVelocity, 0f, brake * tickScale);
            }
            else
            {
                float brakePerSecond = brake / nativeTickSeconds;
                float stoppingDistance = signedVelocity * signedVelocity / (2f * brakePerSecond);
                signedVelocity = stoppingDistance >= Mathf.Abs(delta)
                    ? Mathf.MoveTowards(signedVelocity, 0f, brake * tickScale)
                    : signedVelocity + direction * acceleration * tickScale;
            }

            float next = elevator.CurrentCabinY + signedVelocity * dt;
            if ((direction > 0f && next >= elevator.TargetY)
                || (direction < 0f && next <= elevator.TargetY))
            {
                next = elevator.TargetY;
                signedVelocity = 0f;
            }
            elevator.CurrentCabinY = next;
            elevator.Velocity = signedVelocity;
        }

        static void ApplyElevatorCabinPosition(
            DigglesTclContext ctx,
            DigglesTclEntity drive,
            DigglesTclEntity cabin,
            float cabinY)
        {
            cabin.Position = new Vector3(drive.Position.x, cabinY, drive.Position.z);
            if (cabin.GameObject != null && ctx.EntitySpawner != null)
                ctx.EntitySpawner.ApplyPosition(cabin.GameObject, cabin.Position);
        }

        static void SetElevatorPresentation(
            DigglesTclContext ctx,
            DigglesTclEntity drive,
            ElevatorLogicState elevator,
            string alias)
        {
            if (elevator.CurrentPresentation == alias) return;
            elevator.CurrentPresentation = alias;
            string clip = alias;
            if (drive.Class?.AnimMap != null
                && drive.Class.AnimMap.TryGetValue(alias, out var resolved)
                && !string.IsNullOrEmpty(resolved))
                clip = resolved;
            drive.CurrentAnim = clip;
            drive.CurrentAnimMode = (int)Diggles.Runtime.DigglesAnimator.AnimMode.Loop;
            if (drive.GameObject != null && ctx.EntitySpawner != null)
                ctx.EntitySpawner.ApplyAnim(
                    drive.GameObject,
                    clip,
                    0,
                    Diggles.Runtime.DigglesAnimator.AnimMode.Loop);
        }

        static void SetDoorPresentation(
            DigglesTclContext ctx,
            DigglesTclEntity entity,
            DoorLogicState door,
            bool open)
        {
            door.IsVisuallyOpen = open;
            string alias = open
                ? (door.OpensLeft ? "opening_l" : "opening_r")
                : (door.OpensLeft ? "closing_l" : "closing_r");
            string clip = alias;
            if (entity.Class?.AnimMap != null
                && entity.Class.AnimMap.TryGetValue(alias, out var resolved)
                && !string.IsNullOrEmpty(resolved))
                clip = resolved;
            entity.CurrentAnim = clip;
            entity.CurrentAnimMode = (int)Diggles.Runtime.DigglesAnimator.AnimMode.Once;
            if (entity.GameObject != null && ctx.EntitySpawner != null)
                ctx.EntitySpawner.ApplyAnim(
                    entity.GameObject,
                    clip,
                    0,
                    Diggles.Runtime.DigglesAnimator.AnimMode.Once);
        }

        static void ApplyPathInfluence(DigglesTclContext ctx, DigglesTclEntity entity)
        {
            var nav = PathWorld(ctx);
            if (nav == null) return;
            var influence = entity.PathInfluence;
            if (influence == null)
            {
                nav.RemoveInfluence(entity.Handle);
                return;
            }

            int centerX = Mathf.RoundToInt(entity.Position.x * 8f);
            int centerY = Mathf.RoundToInt(entity.Position.y * 8f);
            nav.SetInfluence(
                entity.Handle,
                centerX + influence.X0,
                centerY + influence.Y0,
                centerX + influence.X1,
                centerY + influence.Y1,
                influence.Cost,
                influence.AddsClimbSurface,
                influence.TransportKind,
                influence.HasClimbBounds ? (int?)(centerX + influence.ClimbX0) : null,
                influence.HasClimbBounds ? (int?)(centerY + influence.ClimbY0) : null,
                influence.HasClimbBounds ? (int?)(centerX + influence.ClimbX1) : null,
                influence.HasClimbBounds ? (int?)(centerY + influence.ClimbY1) : null);
        }

        // ----- position -----

        static void ReflectRotation(DigglesTclContext ctx, DigglesTclEntity ent)
        {
            // Rotation garbage/NaN guard (FUN_005cd840, entity-anchor.md §3): any component
            // outside (−10000, 10000) — incl. NaN, which fails both compares — snaps to 0.
            ent.Rotation = new Vector3(
                ent.Rotation.x > -10000f && ent.Rotation.x < 10000f ? ent.Rotation.x : 0f,
                ent.Rotation.y > -10000f && ent.Rotation.y < 10000f ? ent.Rotation.y : 0f,
                ent.Rotation.z > -10000f && ent.Rotation.z < 10000f ? ent.Rotation.z : 0f);
            if (ent.GameObject == null || ctx.EntitySpawner == null) return;
            // ONE rotation channel for yaw. The native has a single yaw field that set_roty
            // writes and the walk integrator + render read; the port had grown TWO — set_roty spun
            // the inner Model (Q_x(−90)·Euler(−y), no −0.5·cos law), while walk / `action rotate`
            // yaw the OUTER transform via FaceRotationForNativeAngle. The channels COMPOSE: any
            // gnome that ever took a set_roty (sleep pose, wall pose z_procs.tcl:1707, seat, a
            // cutscene `do_action setrot`) carried a residual inner twist under every later
            // walk/rotate — the dig/seat/cutscene wrong-facing cluster. Route
            // pure-YAW rotations through the SAME outer channel (every non-Dummy_ entity carries a
            // DigglesObject — items with `set_roty $ns [random 6.28]` included); a rotation with
            // real x/z components (spinning rings, tilted deco) keeps the full-Euler inner path,
            // which is the only writer that can express it.
            var dobj = ent.GameObject.GetComponent<DigglesObject>();
            if (dobj != null && Mathf.Abs(ent.Rotation.x) < 1e-4f && Mathf.Abs(ent.Rotation.z) < 1e-4f)
            { dobj.FaceNativeAngle(ent.Rotation.y); return; }
            ctx.EntitySpawner.ApplyRotation(ent.GameObject, ent.Rotation);
        }

        // The yaw a script must see: live from the transform when a character GameObject exists
        // (movement executors rotate the transform directly — the stored Rotation.y goes stale
        // after the first walk), the stored field otherwise (headless / props).
        static float NativeYawOf(DigglesTclEntity e)
        {
            var dobj = e.GameObject != null ? e.GameObject.GetComponent<DigglesObject>() : null;
            return dobj != null ? dobj.CurrentNativeYaw() : e.Rotation.y;
        }

        // set_pos HANDLE X Y Z or set_pos HANDLE "X Y Z"
        static string CmdSetPos(DigglesTclContext ctx, string[] args)
        {
            if (args.Length < 3) throw new TclException("wrong # args: \"set_pos handle x y z\" or \"set_pos handle pos\"");
            var ent = ctx.ResolveEntity(args[1]);
            if (args.Length == 3) ent.Position = ParseVec3(args[2]);
            else if (args.Length == 5)
                ent.Position = new Vector3(ParseFloat(args[2]), ParseFloat(args[3]), ParseFloat(args[4]));
            else throw new TclException("wrong # args for set_pos");
            ent.MarkNativeMutation(DigglesTclEntity.NativeMutationTransform);
            // A PHYSIC-ENABLED free entity positioned mid-air settles to the floor. The native
            // relies on the drop-physics job here (set_pos on a physic item kicks
            // FUN_0067dc80:128-129 → FUN_005ebc90/FUN_005eb470), which the port lacks as a
            // continuous system; SettleFreeItemDrop is the one-shot stand-in (gravity down the
            // own column + vector_fix's depth seat law at the LANDED row, FUN_00595770:255-267).
            // The native enqueue gate is FLAG-based, not kind-based (FUN_005eb470: flag 0x10
            // set_physic + NOT contained +0x3d0 bit 2, no carrier) — it covers ALL the raw
            // set_physic 1 → set_pos chains equally: the boxed Trigger_Tutorial crate (:340,
            // previously floated), the DEATH SCATTER (inv_rem_all z_procs.tcl:1859-1860 — one
            // tile above the corpse, no vector_fix; previously hung in the air) and
            // the production release_item (genericprod.tcl:520-537). The earlier `_boxed`-only
            // scope was the death-drop bug. A CONTAINED entity's pose stays script truth (the
            // Lager shelf set_pos AFTER inv_add, lager.tcl store_item:734-744, must stick).
            if (ent.ContainedIn < 0 && PhysicEnabled(ent)
                && ctx.Terrain != null && ctx.Terrain.Nav is { } nv)
                ent.Position = Diggles.Runtime.RuntimeWorldSpawner.SettleFreeItemDrop(nv, ent.Position);
            // Reflect to bound GameObject via spawner (handles axis convert).
            if (ent.GameObject != null && ctx.EntitySpawner != null)
                ctx.EntitySpawner.ApplyPosition(ent.GameObject, ent.Position);
            if (ent.TransportLogic != null) RefreshTransportInfluence(ctx, ent);
            else if (ent.PathInfluence != null) ApplyPathInfluence(ctx, ent);
            return "";
        }

        static string CmdGetPos(DigglesTclContext ctx, string[] args)
        {
            if (args.Length != 2) throw new TclException("wrong # args: \"get_pos handle\"");
            var ge = ctx.ResolveEntity(args[1]);
            return FormatVec3(ge.Position);
        }

        static string CmdSetVel(DigglesTclContext ctx, string[] args)
        {
            if (args.Length < 3) throw new TclException("wrong # args: \"set_vel handle x y z\" or \"set_vel handle vel\"");
            var ent = ctx.ResolveEntity(args[1]);
            if (args.Length == 3) ent.Velocity = ParseVec3(args[2]);
            else if (args.Length == 5)
                ent.Velocity = new Vector3(ParseFloat(args[2]), ParseFloat(args[3]), ParseFloat(args[4]));
            else throw new TclException("wrong # args for set_vel");
            return "";
        }

        static string CmdGetVel(DigglesTclContext ctx, string[] args)
        {
            if (args.Length != 2) throw new TclException("wrong # args: \"get_vel handle\"");
            return FormatVec3(ctx.ResolveEntity(args[1]).Velocity);
        }

        // ----- animation -----

        // set_anim HANDLE OBJ.ANIM ?STARTFRAME? ?MODE?
        // Mode: 0=STILL, 1=ONCE, 2=LOOP — matches animinit.tcl ANIM_* globals.
        // The "obj." prefix is engine-side bookkeeping; the per-entity
        // animator already targets a specific.3db object, so we strip
        // it and look up the anim by short name.
        static string CmdSetAnim(DigglesTclContext ctx, string[] args)
        {
            if (args.Length < 3) throw new TclException("wrong # args: \"set_anim handle obj.anim ?frame? ?mode?\"");
            var ent = ctx.ResolveEntity(args[1]);
            ent.CurrentAnim = args[2];
            ent.MarkNativeMutation(DigglesTclEntity.NativeMutationAnimation);

            // Pass the FULL "obj.anim" through — the controller's PlayClip resolves the obj prefix
            // and keys CROSS-object clips distinctly. Stripping it here forced e.g.
            // `set_anim this kiste_tragen.standard` (change_look tragen) to replay the model's own
            // "standard" (the open box) instead of the carried-box object. (#4)
            string fullName = args[2];
            // Bare names first resolve through the class anim table — the native set_anim accepts
            // set_class_anim ALIASES: start_001's `set_anim [Actor 0] rebound 11 1` plays
            // mann.abprallen (z_anims.tcl:1031). Real bare clip names fall through unchanged.
            if (!fullName.Contains('.') && ent.Class?.AnimMap != null
                && ent.Class.AnimMap.TryGetValue(fullName, out var aliased) && !string.IsNullOrEmpty(aliased))
                fullName = aliased;

            int startFrame = 0;
            if (args.Length > 3 && !string.IsNullOrEmpty(args[3]))
                int.TryParse(args[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out startFrame);

            var mode = Diggles.Runtime.DigglesAnimator.AnimMode.Loop;
            if (args.Length > 4 && !string.IsNullOrEmpty(args[4])
                && int.TryParse(args[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m))
            {
                mode = (Diggles.Runtime.DigglesAnimator.AnimMode)m;
            }
            ent.CurrentAnimMode = (int)mode;
            // Seed BEFORE the drain gate: the seed is pure logical state
            // (ResolveAnimationClip is a library lookup, no presentation), and an entity whose
            // first set_anim lands inside a skip drain must not latch its first POST-drain clip
            // as "initial" — that drifts the save-capture initial-anim/N36 fields vs an
            // unskipped run.
            if (!ent.NativeInitialAnimationWasSeeded)
            {
                Diggles.Runtime.DigglesAnimClip? initialAnimation =
                    ResolveAnimationClip(ctx, ent, fullName);
                if (initialAnimation != null)
                {
                    ent.NativeInitialAnimationWasSeeded = true;
                    ent.NativeInitialAnimationUsesRawIdentity = false;
                    ent.NativeInitialAnimation = fullName;
                    ent.NativeInitialAnimationClip = initialAnimation;
                    ent.NativeInitialAnimationStartFrame = startFrame;
                    ent.NativeInitialAnimationMode = (int)mode;
                }
            }
            if (ctx.SequencePresentationDrain)
            {
                // Record the LOGICAL payload (alias-resolved clip + start frame + mode) for the
                // pump's replay — CurrentAnim keeps the raw script name by the
                // save-echo law, and the replay path cannot re-resolve Class.AnimMap aliases.
                s_drainDeferredAnims.GetOrCreateValue(ctx)[ent.Handle] =
                    (fullName, startFrame, (int)mode);
                ctx.DeferSequencePresentation(ent);
                Diggles.Runtime.Diagnostics.HitchFamilyTimingDiag.Count(
                    "cutscene.presentation-suppressed");
                return "";
            }
            // Record the chosen pose+mode even before the GameObject exists — obj_init runs BEFORE the
            // spawner builds the model, so the spawner re-applies CurrentAnim/CurrentAnimMode (rings,
            // closed barrels, butterflies use their obj_init set_anim, not the class/TSV default).
            // Building construction stages: genericprod's set_buildupanim shows stage N via
            // `set_anim this <class>.bau_<letter> 0 0` (single-frame STILL meshes, letters a..k
            // with j skipped — genericprod.tcl:776-813). Mirror the stage index on the entity and
            // route the visual through the anim-controller's dedicated buildings API
            // (DigglesAnimController.SetBuildupStep — previously implemented but UNCALLED), so the
            // still-frame semantics + standard fallback live in one place. Completion shows
            // "standard" (set_buildupanim's `> max` branch) which flows through ApplyAnim below.
            {
                int bdot = fullName.LastIndexOf('.');
                string bareClip = bdot >= 0 ? fullName.Substring(bdot + 1) : fullName;
                int bauStep = Diggles.Runtime.DigglesAnimController.BauStepIndex(bareClip);
                if (bauStep >= 0)
                {
                    ent.BuildupStep = bauStep;
                    var bctrl = GetController(ent);
                    if (bctrl != null)
                    {
                        // genericprod.tcl:784 compares with strict `>` — stage N == max still shows
                        // bau_N, so pass max+1; without the Tcl var, just show this stage.
                        int maxArg = bauStep + 1;
                        if (ent.VarsScope.Vars.TryGetValue("max_buildup_step", out var msv)
                            && int.TryParse(msv, NumberStyles.Integer, CultureInfo.InvariantCulture, out var msi)
                            && msi >= bauStep)
                            maxArg = msi + 1;
                        bctrl.SetBuildupStep(bauStep, maxArg);
                        return "";
                    }
                }
            }

            if (Diggles.Runtime.Diagnostics.DigglesDebugLog.IsTracked(ent.Handle))
                Diggles.Runtime.Diagnostics.DigglesDebugLog.Log(
                    $"[set_anim] #{ent.Handle} {args[2]} frame={(args.Length > 3 ? args[3] : "-")} " +
                    $"mode={(args.Length > 4 ? args[4] : "-")} go={ent.GameObject != null}");
            if (ent.GameObject == null || ctx.EntitySpawner == null) return "";

            ctx.EntitySpawner.ApplyAnim(ent.GameObject, fullName, startFrame, mode);
            return "";
        }

        // Item-only half of set_posbottom's native anchor conversion. Material/tool and
        // logic-less transport classes are the inventory putdown branches in
        // eventgen.tcl:245-274/:34; the port's boxed and transport-logic gates keep the
        // crate/building paths out. The mesh max-Y read and 0.005 scale are
        // FUN_005cb600:17-41; FUN_005cdae0:68-93 identifies the live display animation/mesh
        // fields used by that read.
        internal static float GetItemMeshBottomOffset(
            DigglesTclContext context,
            DigglesTclEntity entity)
        {
            if (!UsesItemBottomSeat(entity) || context.ModelLibrary is not { } library) return 0f;

            DigglesClass cls = entity.Class;
            if (TryGetAnimationMeshMax(library, cls, entity.CurrentAnim, out Vector3 max)
                || TryGetClassMeshMax(library, cls, out max))
                return max.y * Diggles.Runtime.DigglesModelLibrary.ModelToWorldScale;
            return 0f;
        }

        private static bool UsesItemBottomSeat(DigglesTclEntity entity)
        {
            if (entity.Class == null || FlagVarSet(entity, "_boxed")) return false;
            string kind = entity.Class.Kind;
            if (kind is "material" or "tool") return true;
            return kind == "transport" && entity.TransportLogic == null;
        }

        private static bool TryGetAnimationMeshMax(
            Diggles.Runtime.DigglesModelLibrary library,
            DigglesClass cls,
            string? animationName,
            out Vector3 max)
        {
            max = default;
            if (string.IsNullOrEmpty(animationName)) return false;

            string resolvedName = animationName!;
            if (resolvedName.IndexOf('.') < 0
                && cls.AnimMap.TryGetValue(resolvedName, out string alias)
                && !string.IsNullOrEmpty(alias))
                resolvedName = alias;

            int separator = resolvedName.IndexOf('.');
            string objectName;
            string clipName;
            if (separator > 0)
            {
                objectName = resolvedName.Substring(0, separator);
                clipName = resolvedName.Substring(separator + 1);
            }
            else
            {
                objectName = library.ResolveClass(cls.Name)?.Obj ?? "";
                if (string.IsNullOrEmpty(objectName)) return false;
                clipName = resolvedName;
            }

            return library.TryGetMeshBBox(objectName, clipName, 0, out _, out max);
        }

        private static bool TryGetClassMeshMax(
            Diggles.Runtime.DigglesModelLibrary library,
            DigglesClass cls,
            out Vector3 max)
        {
            max = default;
            var resolvedClass = library.ResolveClass(cls.Name);
            if (resolvedClass != null && !string.IsNullOrEmpty(resolvedClass.Value.Obj))
            {
                string objectName = resolvedClass.Value.Obj;
                string animationName = !string.IsNullOrEmpty(cls.DefaultAnim)
                    && cls.DefaultAnim!.StartsWith(objectName + ".", System.StringComparison.Ordinal)
                    ? cls.DefaultAnim!
                    : objectName + "." + (string.IsNullOrEmpty(resolvedClass.Value.DefaultAnim)
                        ? "standard"
                        : resolvedClass.Value.DefaultAnim);
                return TryGetAnimationMeshMax(library, cls, animationName, out max);
            }

            if (TryGetAnimationMeshMax(library, cls, cls.DefaultAnim, out max)) return true;
            return library.GetObjDb(cls.Name) != null
                && library.TryGetMeshBBox(cls.Name, "standard", 0, out _, out max);
        }

        internal static Diggles.Runtime.DigglesAnimClip? ResolveAnimationClip(
            DigglesTclContext context,
            DigglesTclEntity entity,
            string animationName)
        {
            if (context == null) throw new System.ArgumentNullException(nameof(context));
            if (entity == null) throw new System.ArgumentNullException(nameof(entity));
            if (string.IsNullOrEmpty(animationName)) return null;

            int separator = animationName.IndexOf('.');
            string objectName;
            string clipName;
            if (separator > 0)
            {
                objectName = animationName.Substring(0, separator);
                clipName = animationName.Substring(separator + 1);
            }
            else
            {
                clipName = animationName;
                objectName = context.ModelLibrary?.ResolveClass(entity.Class.Name)?.Obj ?? "";
                if (string.IsNullOrEmpty(objectName) &&
                    !string.IsNullOrEmpty(entity.Class.DefaultAnim))
                {
                    int defaultSeparator = entity.Class.DefaultAnim!.IndexOf('.');
                    if (defaultSeparator > 0)
                        objectName = entity.Class.DefaultAnim.Substring(0, defaultSeparator);
                }
            }

            if (entity.AlternateAnimDb && string.Equals(
                    objectName,
                    "mann",
                    System.StringComparison.Ordinal))
            {
                objectName = "frau";
            }
            if (string.IsNullOrEmpty(objectName) || context.ModelLibrary == null) return null;
            return context.ModelLibrary.GetClip(objectName, clipName);
        }

        // link_obj <child> <parent> <linkid> → attach; link_obj <child> → detach.
        // Entity-side state (LinkedToHandle/LinkedSlot — native child+0x278/+0x27c) is kept
        // even without GameObjects so headless tests + get_linked_to see the link.
        static string CmdLinkObj(DigglesTclContext ctx, string[] args)
        {
            if (args.Length < 2) return "";
            var child = ResolveEntityOrNull(ctx, args[1]);
            if (child == null) return "";

            if (args.Length < 3)   // 1-arg = unlink
            {
                child.LinkedToHandle = 0;
                child.LinkedSlot = -1;
                if (child.GameObject == null) return "";
                var existing = child.GameObject.GetComponent<Diggles.Runtime.DigglesLinkAttachment>();
                if (existing != null)
                {
                    existing.Detach();                   // restore the pre-link hierarchy FIRST
                    DestroyObjectSafe(existing);         // edit-mode safe (think-loop tests)
                }
                return "";
            }

            var parent = ResolveEntityOrNull(ctx, args[2]);
            if (parent == null) return "";

            // linkid is usually an int (head=4). The named-dummy form isn't supported
            // yet → fall back to head.
            int linkId = Diggles.Runtime.DigglesLinkPoints.Head;
            if (args.Length > 3) int.TryParse(args[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out linkId);
            RestoreLinkAttachment(child, parent, linkId, true);
            return "";
        }

        // Restores a resolved native attachment without evaluating Tcl, starting an action, or
        // replaying link_obj side effects. The save loader calls this only after every identity and
        // GameObject shell exists. A missing native parent is resolved to null by the loader.
        internal static void RestoreLinkAttachment(
            DigglesTclEntity child,
            DigglesTclEntity parent,
            int linkId,
            bool logFirstAttach = false)
        {
            if (child == null) throw new System.ArgumentNullException(nameof(child));
            if (parent == null) throw new System.ArgumentNullException(nameof(parent));
            child.LinkedToHandle = parent.Handle;
            child.LinkedSlot = linkId;
            if (child.GameObject == null || parent.GameObject == null) return;

            var parentInner = FindInnerModel(parent.GameObject);
            if (parentInner == null) return;
            var parentAnim = parentInner.GetComponent<Diggles.Runtime.DigglesAnimator>();
            if (parentAnim == null) return;
            var childInner = FindInnerModel(child.GameObject);

            var att = child.GameObject.GetComponent<Diggles.Runtime.DigglesLinkAttachment>()
                      ?? child.GameObject.AddComponent<Diggles.Runtime.DigglesLinkAttachment>();
            att.Attach(parentInner, parentAnim, linkId, childInner);
            // The child now rides the carrier: re-apply the CARRIER's composite visibility so
            // a hat linked to a fog-hidden gnome starts hidden too (the fog pass skips link
            // children — the carrier's renderer sweep is their single visibility owner).
            child.FowHidden = false;
            SyncRenderVisible(parent);
            if (logFirstAttach && s_loggedBodyErrors.Add("link_obj first attach"))
            {
                // Unity-null-safe probe: GetComponent for a MISSING component returns the
                // editor's fake-null sentinel, which PASSES a C# recursive pattern
                // (`is { sharedMesh: … }`) and then THROWS MissingComponentException on the
                // property read ("There is no 'MeshFilter' attached to the \"Model\" game
                // object…"). Headless rigs (tests/tools) legitimately have no MeshFilter on
                // the Model — a debug log must never break the work chain. Use the overloaded
                // != (sentinel-aware) instead of the pattern.
                var mfProbe = childInner != null ? childInner.GetComponent<UnityEngine.MeshFilter>() : null;
                bool hasMesh = mfProbe != null && mfProbe.sharedMesh != null && mfProbe.sharedMesh.vertexCount > 0;
                Debug.Log($"[link_obj] {child.Class?.Name}#{child.Handle} → {parent.Class?.Name}#{parent.Handle} " +
                          $"point {linkId}; childModel={(childInner != null)} childHasMesh={hasMesh} (further attaches silent)");
            }
        }

        // Drain-skip link replay (pump-only). CmdLinkObj during a skip drain records the
        // logical link (LinkedToHandle/LinkedSlot) but RestoreLinkAttachment cannot attach
        // while the child's spawn is deferred (child.GameObject == null early-out) — the
        // work-hat chain (create_muetze: `new $muetze_name` + `link_obj $m_ref this 4`,
        // z_work_common.tcl:767-775) and cutscene tool links (do_tooltakeout) both land in
        // that window. When the pump finally spawns an entity, replay BOTH directions of the
        // final link state: this entity as the CHILD of an existing carrier, and as the
        // CARRIER of riders that spawned first. Detaches need no replay — a pump-spawned
        // GameObject never carries a stale DigglesLinkAttachment, and unlinks on live
        // GameObjects run immediately even during the drain (CmdLinkObj's 1-arg path is not
        // drain-gated). The component-presence guard keeps a re-Attach from re-capturing an
        // already-linked child's hierarchy.
        static void ReplayLinkAttachments(DigglesTclContext ctx, DigglesTclEntity ent)
        {
            if (ent.GameObject == null) return;
            if (ent.LinkedToHandle > 0
                && ctx.Entities.TryGetValue(ent.LinkedToHandle, out DigglesTclEntity? carrier)
                && carrier.GameObject != null
                && ent.GameObject.GetComponent<Diggles.Runtime.DigglesLinkAttachment>() == null)
            {
                RestoreLinkAttachment(ent, carrier, ent.LinkedSlot);
            }
            foreach (DigglesTclEntity rider in ctx.Entities.Values)
            {
                if (rider.LinkedToHandle != ent.Handle || ReferenceEquals(rider, ent)
                    || rider.GameObject == null)
                    continue;
                if (rider.GameObject.GetComponent<Diggles.Runtime.DigglesLinkAttachment>() == null)
                    RestoreLinkAttachment(rider, ent, rider.LinkedSlot);
            }
        }

        // obj_query <obj> <filters…> — faithful port of the native CObjQuery
        // (TCL:CObjQuery::TclObjQurey = FUN_005de500 option parser, FUN_005e0e50 per-object
        // filter, FUN_005e12a0 execute). Filters may be separate args OR one combined string.
        // Returns a Tcl list of matching handles, or "0" when none match (FUN_005e12a0
        // result path: no matches → Tcl_NewIntObj(0)).
        //
        // Parser ground truth (FUN_005de500):
        // • options are consumed in PAIRS; an option matching none of the known strings
        // falls through every strcmp to LAB_005e0bc8 and is SKIPPED — unknown options
        // never bail the query (an earlier revision returned "0" for -visibility/-water/-alloc,
        // which silently killed the food-search / lager-store / takeitems loops).
        // • -flagpos/-flagneg take a LIST of flag words; each KNOWN word ORs one bit into
        // the mask (boxed=0x4000 visible=1 contained=2 locked=4 storable=8
        // selectable=0x200 hoverable=0x400 male=0x8000 female=0x10000 build=0x400000
        // instore=0x4000000); a word the strcmp chain doesn't know ORs NOTHING
        // (LAB_005df6be/LAB_005dfb2e — neutral, not an exclusion).
        // • -cloaked absent or 0 ⇒ flagneg |= 0x8000000 (cloaked): cloaked objects are
        // excluded by default (LAB_005e0bd3: `if (local_44 == 0) local_ac |= 0x8000000`).
        // • -limit < 1 is ignored; default unlimited (ctor FUN_005ddd60: limit=0x7fffffff).
        // • default -sorting = 1 = by distance ASCENDING (parser inits local_4c=1; Execute
        // FUN_005e12a0:557-601 sorts the (obj,dist²) pairs and copies the FIRST limit
        // entries) — so `-limit N` returns the NEAREST N. Scripts never pass -sorting;
        // we always use the default.
        // Filter ground truth (FUN_005e0e50):
        // • flagpos: (obj.flags & flagpos) == flagpos — ALL requested bits must be set;
        // • flagneg: (obj.flags & flagneg) != 0 → reject — ANY requested bit set excludes;
        // • -visibility IS the fog-of-war filter and is REAL (previously dropped: gnomes
        // hauled loot out of unexplored fog — the Lager auto-store lager.tcl:99/112
        // and the sparetime food/fun searches z_spare_procs.tcl:72/81/194/196 all pass
        // -visibility, which the port used to drop). Law: the parser ORs word masks
        // (FUN_005de500:897-1041 — playervisible=0x10000 playerinvisible=0x20000
        // enemyvisible=0x40000 enemyinvisible=0x80000, "any"@.rdata 0x72bdf0=0xffffffff,
        // "own"@0x7295f0 = 1<<self.Owner, single digit 0-7 = 1<<digit); the filter builds
        // the candidate's bits from its 8 per-player max-fow bytes with threshold 0x80
        // (FUN_005e0e50:84-105: byte<0x80 ⇒ 1<<(lane+8) + 0x20000/0x80000, else 1<<lane +
        // 0x10000/0x40000, player split on lane==self.Owner) and rejects on empty
        // intersection. No -visibility ⇒ no fog gate (mask stays -1/unset) — faithful:
        // local pickup queries intentionally omit it.
        // • -water is backed by CFluid's point query; -alloc remains neutral because the port
        // has no per-class allocation-bit table. The native default is dry, so -water 0 keeps
        // matching every object when the fluid system is disabled.
        // The query subject itself is excluded (FUN_005de0c0 pushes it on the exclude list).
        static string CmdObjQuery(DigglesTclContext ctx, string[] a)
        {
            if (a.Length < 2) return "0";
            var self = ResolveEntityOrNull(ctx, a[1]);
            // Tokenize like the native sees objv: each Tcl WORD is ONE token. Scripts use TWO
            // call shapes — separate words (`obj_query this -class {Zwerg Baby} -boundingbox
            // {-0.6 -0.3 -1.2 0.6 0.3 1.2}`, z_procs.tcl:1301) and ONE combined quoted string
            // of option pairs (`obj_query this "-class Zwerg -owner own -range 200 …"`,
            // sequencer.tcl:405). Only the combined form may be split; it is recognized by its
            // first element being an OPTION word (-letter…). A VALUE word that happens to list
            // multiple elements (a class list, a box, `-owner {own -1}`, `-pos $sp`) must stay
            // WHOLE — the old unconditional flatten split the crowd box into six dangling
            // number tokens, so -boundingbox parsed nothing (hasBox stayed false): the
            // act_when_idle crowd query matched every gnome on the map and the camp side-stepped
            // forever (and `-class {Zwerg Baby}` lost the Baby, `-owner {own -1}` swallowed the
            // NEXT option word). Boxes start with a NUMBER ("-0.6 …"), never a -letter word.
            var toks = new System.Collections.Generic.List<string>();
            for (int k = 2; k < a.Length; k++)
            {
                var parts = TclList.Parse(a[k]);
                bool combined = parts.Count > 1 && parts[0].Length > 1
                    && parts[0][0] == '-' && char.IsLetter(parts[0][1]);
                if (combined) toks.AddRange(parts);
                else toks.Add(a[k]);
            }

            System.Collections.Generic.List<string>? classes = null;
            string? owner = null;
            var flagPos = new System.Collections.Generic.List<string>();
            var flagNeg = new System.Collections.Generic.List<string>();
            bool cloakedOk = false;          // -cloaked 1 ⇒ don't auto-exclude cloaked
            int limit = int.MaxValue;
            bool hasRange = false; float range = 0f;
            System.Collections.Generic.List<string>? types = null;   // -type {material tool …} → Class.Kind
            bool hasBox = false; Vector3 bbMin = default, bbMax = default;   // -boundingbox (relative to queryPos)
            System.Collections.Generic.List<string>? visWords = null;        // -visibility (fog filter, see header)
            bool? water = null;
            Vector3 queryPos = self?.Position ?? Vector3.zero;   // -pos overrides; defaults to self (ctor center = subject pos)
            for (int k = 0; k < toks.Count; k++)
            {
                switch (toks[k])
                {
                    case "-class":   if (++k < toks.Count) classes = TclList.Parse(toks[k]); break;
                    case "-owner":   if (++k < toks.Count) owner = toks[k]; break;
                    case "-flagpos": if (++k < toks.Count) flagPos.AddRange(TclList.Parse(toks[k])); break;
                    case "-flagneg": if (++k < toks.Count) flagNeg.AddRange(TclList.Parse(toks[k])); break;
                    // -limit < 1 ignored (FUN_005de500: `if ((int)uVar19 < 1)` skips the store).
                    // Scripts pass -limit twice in one query (z_spare_procs.tcl:72) — last wins.
                    case "-limit":
                        if (++k < toks.Count && int.TryParse(toks[k], NumberStyles.Integer, CultureInfo.InvariantCulture, out var lim) && lim >= 1)
                            limit = lim;
                        break;
                    // Spatial query (Getobjpos / nearest-object): -pos {x y z} is the centre (else self),
                    // -range R the radius (native units). Implemented so e.g. urw_6a's
                    // `Getobjpos Info_Pos_Spinne 0 400` resolves (else the cave-painting montage anchored
                    // at {0 0 0} → camera flew far upper-left).
                    case "-pos":     if (++k < toks.Count) queryPos = ParseVec3(toks[k]); break;
                    case "-range":   if (++k < toks.Count && float.TryParse(toks[k], NumberStyles.Float, CultureInfo.InvariantCulture, out var rg) && rg > 0f) { range = rg; hasRange = true; } break;
                    // -type filters by the class Kind (material/tool/transport/…); takeitems/lager
                    // rely on it. -boundingbox is a box around queryPos: either 6 flat floats
                    // {minX minY minZ maxX maxY maxZ} or two sub-lists {{min} {max}}
                    // (both accepted by the native, FUN_005de500:2241-2419).
                    // AXIS ORDER VERIFIED (the "crowd box is fat" hypothesis is FALSE):
                    // the flat floats land f0→minX f1→minY f2→minZ f3→maxX f4→maxY f5→maxZ, each
                    // ADDED to the query pos (FUN_005de500:2294-2419 — locals local_80/7c/78 =
                    // min.xyz, local_74/70/6c = max.xyz), and the mode-3 box test compares the
                    // candidate's NATIVE pos components 1:1 — min.x≤obj.x(+0xc) ∧ min.y≤obj.y(+0x10)
                    // ∧ min.z≤obj.z(+0x14) ∧ ≤max (FUN_005e12a0:326-333). Our d = pos − queryPos
                    // component test below is exactly that. So act_when_idle's crowd box
                    // {-0.6 -0.3 -1.2 0.6 0.3 1.2} (z_procs.tcl:1301) reads ±0.6 east (x),
                    // ±0.3 ground-line row (y), ±1.2 slab depth (z) — same as the original.
                    case "-type":    if (++k < toks.Count) types = TclList.Parse(toks[k]); break;
                    case "-boundingbox":
                        if (++k < toks.Count)
                        {
                            var b = TclList.Parse(toks[k]);
                            if (b.Count == 2)
                            { bbMin = ParseVec3(b[0]); bbMax = ParseVec3(b[1]); hasBox = true; }
                            else if (b.Count >= 6
                                && float.TryParse(b[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var bx0)
                                && float.TryParse(b[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var by0)
                                && float.TryParse(b[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var bz0)
                                && float.TryParse(b[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var bx1)
                                && float.TryParse(b[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var by1)
                                && float.TryParse(b[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var bz1))
                            { bbMin = new Vector3(bx0, by0, bz0); bbMax = new Vector3(bx1, by1, bz1); hasBox = true; }
                        }
                        break;
                    case "-cloaked":
                        if (++k < toks.Count) cloakedOk = ParseTclBool(toks[k]);
                        break;
                    // The fog-of-war filter (REAL — see header): value = a list of words,
                    // masks OR together (the native loops Tcl_ListObjIndex over the value).
                    case "-visibility":
                        if (++k < toks.Count) (visWords ??= new System.Collections.Generic.List<string>()).AddRange(TclList.Parse(toks[k]));
                        break;
                    case "-water":
                        if (++k < toks.Count) water = ParseTclBool(toks[k]);
                        break;
                    // Parsed-but-neutral (see header): no port allocation table to constrain on.
                    case "-sorting": case "-alloc":
                        if (++k < toks.Count) { } break;
                    // Unknown option → SKIP the pair (FUN_005de500 fall-through), never bail.
                    default:
                        if (toks[k].Length > 0 && toks[k][0] == '-' && k + 1 < toks.Count) k++;
                        break;
                }
            }
            // Cloaked-by-default exclusion (LAB_005e0bd3).
            if (!cloakedOk) flagNeg.Add("cloaked");
            // Contained items: the native keeps them as flagged CObjs ('contained' bit 2) but a
            // packed item is no longer in the world's spatial grid, so the gameplay searches
            // (range/bbox) never see it. The port mirrors that by skipping contained entities
            // UNLESS the query explicitly asks for them via `-flagpos contained`
            // (Trigger_Tournament.tcl:307 counts items inside the brewery that way) — or via
            // `-flagpos instore`: shelved Lager items are contained-in-the-Lager yet REMAIN
            // world-queryable in the original (they have real shelf positions, lager.tcl:737;
            // the eat-from-storage search uses `-flagpos {visible instore}`,
            // z_spare_procs.tcl:81,196, and pickup handles them via get_instore →
            // pickup_from_store, z_procs.tcl:461-462).
            bool wantContained = flagPos.Contains("contained") || flagPos.Contains("instore");

            int selfOwner = self?.Owner ?? 0;
            // -visibility words → the fog mask (FUN_005de500:897-1041; word strings verified in
            //.rdata: "any"@0x72bdf0, "own"@0x7295f0). 0 = unset (native init -1 → no filter).
            uint visMask = 0;
            if (visWords != null)
                foreach (var w in visWords)
                    visMask |= w switch
                    {
                        "playervisible"   => 0x10000u,
                        "playerinvisible" => 0x20000u,
                        "enemyvisible"    => 0x40000u,
                        "enemyinvisible"  => 0x80000u,
                        "any"             => 0xffffffffu,
                        // 1 << (owner & 0x1f) exactly like the native (owner −1 → bit 31,
                        // which no candidate ever sets — matches nothing, faithfully).
                        "own"             => 1u << (selfOwner & 0x1f),
                        _ => w.Length == 1 && w[0] >= '0' && w[0] <= '7' ? 1u << (w[0] - '0') : 0u,
                    };
            var matches = new System.Collections.Generic.List<(DigglesTclEntity e, float d2)>();
            float range2 = range * range;
            foreach (var e in ctx.Entities.Values)
            {
                if (e == self) continue;
                if (!wantContained && e.ContainedIn >= 0) continue;
                // Filter ORDER is a pure-conjunction perf choice (no semantic effect — every
                // test is side-effect-free): the spatial tests are a few float ops while the
                // class/flag tests are string compares + per-entity Vars dictionary probes.
                // Range/box queries (the AI's "nearest X within R" bread and butter) reject
                // almost every entity spatially, so test THAT first — measured 417 µs → ~³⁄₁₀
                // per call at 1200 dug-out drops.
                float d2 = (e.Position - queryPos).sqrMagnitude;
                if (hasRange && d2 > range2) continue;       // FUN_005e12a0 case 1: dist² ≤ r²
                if (hasBox)
                {
                    Vector3 d = e.Position - queryPos;
                    if (d.x < bbMin.x || d.x > bbMax.x || d.y < bbMin.y || d.y > bbMax.y || d.z < bbMin.z || d.z > bbMax.z) continue;
                }
                if (classes != null && classes.Count > 0 && !EntityMatchesAnyClass(ctx, e, classes)) continue;
                if (types != null && types.Count > 0 && !types.Contains(e.Class?.Kind ?? "")) continue;
                if (owner != null && !OwnerMatches(ctx, e.Owner, owner, selfOwner)) continue;
                if (water.HasValue && ctx.Fluids.IsUnderwater(e.Position) != water.Value) continue;
                if (!FlagsAllSet(e, flagPos)) continue;      // (flags & flagpos) == flagpos
                if (FlagsAnySet(e, flagNeg)) continue;       // (flags & flagneg) != 0 → reject
                // Fog gate LAST (it samples 8 fow lanes — cached per entity per fog update,
                // but the spatial/class filters above reject almost everything for free).
                if (visMask != 0 && !ObjQueryVisibilityPass(ctx, e, visMask, selfOwner)) continue;
                matches.Add((e, d2));
            }
            if (matches.Count == 0) return "0";
            // Default sorting (=1): distance ascending, THEN take the first `limit` —
            // `-limit N` returns the NEAREST N (FUN_005e12a0:557-601).
            matches.Sort((x, y) => x.d2.CompareTo(y.d2));
            int n = matches.Count < limit ? matches.Count : limit;
            var result = new System.Collections.Generic.List<string>(n);
            for (int k = 0; k < n; k++) result.Add(matches[k].e.Handle.ToString(CultureInfo.InvariantCulture));
            return TclList.Format(result);
        }

        // The obj_query -visibility candidate test (FUN_005e0e50:84-105): build the candidate's
        // visibility bits from its 8 per-player max-fow bytes — lane byte < 0x80 ⇒ invisible to
        // that lane: 1<<(lane+8) plus 0x20000 (lane == the query subject's owner) / 0x80000
        // (other lane); byte ≥ 0x80 ⇒ visible: 1<<lane plus 0x10000 / 0x40000 — and reject on an
        // empty intersection with the requested mask. GetMaxFow carries the native per-object
        // fow-array semantics (FUN_005cb4a0: carrier chain + 7-point tile rect + per-update
        // cache). No fog wired (headless fixtures, menu) ⇒ no constraint — mirrors FowEligible.
        static bool ObjQueryVisibilityPass(DigglesTclContext ctx, DigglesTclEntity e, uint mask, int refPlayer)
        {
            if (ctx.FogOfWar == null) return true;
            uint bits = 0;
            for (int lane = 0; lane < Diggles.Runtime.World.FogOfWarState.Players; lane++)
            {
                if (GetMaxFow(ctx, e, lane) < 0x80)
                    bits |= (1u << (lane + 8)) | (lane == refPlayer ? 0x20000u : 0x80000u);
                else
                    bits |= (1u << lane) | (lane == refPlayer ? 0x10000u : 0x40000u);
            }
            return (bits & mask) != 0;
        }

        // The dwarf model's male/female pair. The engine encodes the alternate db as
        // load-order adjacency (animinit.tcl loads mann then frau); the port maps the one
        // pair directly. Non-dwarf objects have no alternate (returned unchanged → no swap).
        static string AlternateModelObj(string obj) => obj == "mann" ? "frau" : obj;

        // get_place body-block veto: TRUE when `cand` lies inside the act_when_idle avoid box
        // ({±0.6 lateral, ±0.3 row, ±1.2 depth} — z_procs.tcl:1301) of any OTHER live
        // gnome/baby body (`def_class Zwerg none gnome`, zwerg.tcl:1 / `Baby none baby`,
        // baby.tcl:1). The native CPlaceFinder body-checks candidates itself — its -except
        // option exists precisely to exempt one body (FUN_00656560; sparetime_slp_start
        // `-except this`, z_spare_procs.tcl:529). The exact native body radius is not RE'd;
        // the avoid box is the tight port-side choice that guarantees a fresh walk_random /
        // side-step target can never re-trigger the arrival's own crowd check.
        // One pass over the entity table per get_place call (NOT per candidate — the old
        // per-candidate full scan was O(candidates × entities): 38 ms per get_place on the
        // 1579-entity save_test world, the dominant term of the 10-FPS sim tick). Collects the
        // gnome/baby bodies that could possibly veto a candidate of this scan.
        static List<Vector3>? CollectBodyBlockers(
            DigglesTclContext ctx,
            Vector3 center,
            float scanRadius,
            int exceptA,
            int exceptB)
        {
            List<Vector3>? result = null;
            float reach = scanRadius + 1.5f;   // avoid-box extent (±0.6/±0.3/±1.2) + slack
            foreach (var e in ctx.Entities.Values)
            {
                if (e.Handle == exceptA || e.Handle == exceptB) continue;
                if (e.ContainedIn >= 0) continue;
                var kind = e.Class?.Kind;
                if (kind != "gnome" && kind != "baby") continue;
                if (Mathf.Abs(e.Position.x - center.x) > reach) continue;
                if (Mathf.Abs(e.Position.y - center.y) > reach) continue;
                (result ??= new List<Vector3>()).Add(e.Position);
            }
            return result;
        }

        static bool BodyBlocked(List<Vector3>? blockers, Vector3 cand)
        {
            if (blockers == null) return false;
            for (int index = 0; index < blockers.Count; index++)
            {
                Vector3 d = blockers[index] - cand;
                if (d.x > -0.6f && d.x < 0.6f && d.y > -0.3f && d.y < 0.3f && d.z > -1.2f && d.z < 1.2f)
                    return true;
            }
            return false;
        }

        // -owner takes a LIST of owner words (lager.tcl:112 `-owner {[get_owner this] -1}`);
        // the native ORs a MASK per word and tests the candidate's owner BITS against it.
        // • Word → mask (FUN_005e2330; documented list ENGINE_TCL_API.txt:988):
        // world→0x10000 (:75-77), player→0x20000 (:99-100),
        // enemy→(Σ 1<<i where dip[self][i]==4) | 0x10000 — the WORLD BIT IS ORed IN, wild
        // critters count as enemies (:122-153), friend→rel 2 (:156-201), ally→rel 1
        // (:272-299), neutral→rel 3 (:242-269), any→0xffffffff (:235), allplayers→0xff
        // (:239), own→1<<self (:230-232), other→(0xff & ~(1<<self)) | 0x10000 (:223-227),
        // numeric n→1<<n, -1→0x10000 (:216-221). Subject owner -1 → 0xffffffff for the
        // relation words (:124-125 etc.).
        // • Candidate → bits (FUN_005e0e50:66-80): owner<0 → 0x10000, else
        // (owner==subject ? 0x20000: 0x40000) | 1<<owner. Match = (bits & mask) != 0.
        // The old port passed EVERY diplomacy word through ("don't over-filter") — that is
        // exactly why traps (`-owner enemy`, fallen.tcl:87/:123/:268/:299) snapped the
        // owner's own gnomes.
        // internal: TriggerService re-parses the trigger's raw owner spec per scan with the
        // same word table (native: FUN_005e2330 from rec+0x5c, FUN_0060e410's filter).
        internal static bool OwnerMatches(DigglesTclContext ctx, int owner, string spec, int selfOwner)
        {
            var words = TclList.Parse(spec);
            if (words.Count == 0) return true;
            uint mask = 0;
            foreach (var w in words) mask |= OwnerWordMask(ctx, w, selfOwner);
            uint bits = owner < 0
                ? 0x10000u
                : ((owner == selfOwner ? 0x20000u : 0x40000u) | (owner < 32 ? 1u << owner : 0u));
            return (bits & mask) != 0;
        }

        // One owner word → its native match mask (FUN_005e2330). Relation words collect the
        // diplomacy-table row of the SUBJECT owner; rows only exist for owners 0..7.
        static uint OwnerWordMask(DigglesTclContext ctx, string w, int selfOwner)
        {
            uint RelBits(int rel)
            {
                if (selfOwner < 0 || selfOwner > 7) return 0xffffffff;   //:124-125 — world subject matches all
                uint m = 0;
                for (int i = 0; i < 8; i++) if (ctx.Diplomacy[selfOwner, i] == rel) m |= 1u << i;
                return m;
            }
            switch (w)
            {
                case "world": return 0x10000;
                case "player": return 0x20000;
                case "enemy": return RelBits(4) | 0x10000;   //:151 — the world bit rides along
                case "friend": return RelBits(2);
                case "ally": return RelBits(1);
                case "neutral": return RelBits(3);
                case "any": return 0xffffffff;
                case "allplayers": return 0xff;
                case "own": return (selfOwner >= 0 && selfOwner < 8) ? 1u << selfOwner : 0u;   //:230-232
                case "other":
                    return (selfOwner >= 0 && selfOwner < 8)
                        ? (0xffu & ~(1u << selfOwner)) | 0x10000u : 0xffu;                      //:223-227
                default:
                    if (int.TryParse(w, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                    {
                        if (n == -1) return 0x10000;                                            //:216
                        if (n >= 0 && n <= 7) return 1u << n;                                   //:221
                    }
                    return 0;   // out-of-range/unknown word contributes nothing (:217-219)
            }
        }

        // The sparetime registry walkers' per-place gate (FUN_005f4d00:75-76 queryrect /
        // FUN_005f4b20:75-76 queryrange): same owner as the asker AND
        // (flags & 0x1404002) == 0x1400000 — built(0x400000)+active(0x1000000) set,
        // boxed(0x4000)+contained(0x2) clear. Port mapping: BuildupComplete = built,
        // "_boxed" var = boxed, ContainedIn = contained; the active bit is the CObj ctor
        // default and set_activegameplay is unimplemented → treated as set. The native also
        // rejects underwater places through FUN_0057a940.
        static bool SparePlaceEligible(DigglesTclContext context, DigglesTclEntity place,
            DigglesTclEntity asker)
            => place.Owner == asker.Owner
               && place.BuildupComplete
               && place.ContainedIn < 0
               && EntityFlag(place, "boxed") != true
               && !context.Fluids.IsUnderwater(place.Position);

        // The native object-flag word (obj+0x3d0 bits) backed by port state. NULL = the
        // flag has no port backing → it contributes NOTHING to -flagpos/-flagneg, exactly
        // like an unknown word in the native parser (it ORs no bit, FUN_005de500:LAB_005df6be).
        static bool? EntityFlag(DigglesTclEntity e, string flag) => flag switch
        {
            // Engine default flags: a freshly spawned object IS visible; only an explicit
            // set_visibility 0 clears the bit (food items never call set_visibility, yet
            // z_spare_procs.tcl:72 finds them with `-flagpos visible`).
            "visible"    => !(e.VarsScope.Vars.TryGetValue("_visibility", out var vis) && !TruthyFlagVar(vis)),
            "contained"  => e.ContainedIn >= 0,
            "locked"     => FlagVarSet(e, "_lock"),
            "storable"   => FlagVarSet(e, "_storable"),
            "selectable" => FlagVarSet(e, "_selectable"),
            "hoverable"  => FlagVarSet(e, "_hoverable"),
            "boxed"      => FlagVarSet(e, "_boxed"),
            "cloaked"    => FlagVarSet(e, "_cloaked"),
            "instore"    => e.InStore,
            "male"       => e.Gender == "male",
            "female"     => e.Gender == "female",
            "build"      => e.BuildupComplete,   // 0x400000 — buildup finished (Trigger_Tutorial.tcl:1466 gates on it)
            _ => null,
        };

        static bool FlagVarSet(DigglesTclEntity e, string varName) =>
            e.VarsScope.Vars.TryGetValue(varName, out var v) && TruthyFlagVar(v);

        static bool TruthyFlagVar(string v) => v == "1" || v == "true";

        // The native physics gate: FUN_005eb470 enqueues a point-physics job only for objects
        // with the +0x3d0 flag 0x10 set (set_physic 1). The CObj ctor flag word 0x01100101
        // (FUN_005c83f0:224) leaves it CLEAR, so an entity that never ran set_physic reads OFF —
        // cutscenes pin props mid-air exactly by set_physic 0 (Schatztonne evt_timer1 float-away).
        static bool PhysicEnabled(DigglesTclEntity e) => FlagVarSet(e, "_physic");

        // -flagpos: (obj.flags & mask) == mask — every BACKED flag word must be set
        // (FUN_005e0e50:29). Unbacked words are neutral (contribute no bit).
        static bool FlagsAllSet(DigglesTclEntity e, System.Collections.Generic.List<string> flags)
        {
            foreach (var f in flags)
                if (EntityFlag(e, f) == false) return false;
            return true;
        }

        // -flagneg: (obj.flags & mask) != 0 → reject — ANY backed flag word set excludes
        // (FUN_005e0e50:31). The old `_ => true` default here made every -flagneg with an
        // untracked word (locked/boxed/contained…) exclude EVERYTHING — the single bug that
        // zeroed the gnome food search, the Lager store loop and the talk-partner queries.
        static bool FlagsAnySet(DigglesTclEntity e, System.Collections.Generic.List<string> flags)
        {
            foreach (var f in flags)
                if (EntityFlag(e, f) == true) return true;
            return false;
        }

        static bool EntityMatchesAnyClass(DigglesTclContext ctx, DigglesTclEntity e, System.Collections.Generic.List<string> names)
        {
            if (e.Class == null) return false;
            foreach (var n in names) if (ClassIsA(ctx, e.Class, n)) return true;
            return false;
        }

        // True if class `c` is `name` or (transitively) inherits it.
        // internal: TriggerService's any_object candidate filter matches set_target_class
        // with the same rule obj_query's -class uses.
        internal static bool ClassIsA(DigglesTclContext ctx, DigglesClass c, string name)
        {
            // NOTE: a (class,name) memo dictionary was tried here and
            // MEASURED SLOWER — no shipped script uses `inherit` (0 hits in data/scripts/
            // classes/**), so this walk is 1 string compare + an empty-list loop; a tuple
            // dict probe costs more. Don't re-add without data.
            if (c.Name == name) return true;
            foreach (var b in c.Inherits)
                if (ctx.Classes.TryGetValue(b, out var bc) && ClassIsA(ctx, bc, name)) return true;
            return false;
        }

        // auto_choose_gender <obj> — balance the owner's dwarf population: count = #female −
        // #male among same-owner Zwerge (neutrals ignored); females≥males → "male", else
        // "female" (ties → male). Faithful to engine FUN_00676790.
        static string CmdAutoChooseGender(DigglesTclContext ctx, string[] a)
        {
            var self = a.Length > 1 ? ResolveEntityOrNull(ctx, a[1]) : null;
            int owner = self?.Owner ?? 0;
            int count = 0;
            foreach (var e in ctx.Entities.Values)
            {
                if (e == self || e.Owner != owner || e.Class == null) continue;
                if (!ClassIsA(ctx, e.Class, "Zwerg")) continue;
                if (e.Gender == "male") count -= 1;
                else if (e.Gender == "female") count += 1;
            }
            var g = count >= 0 ? "male" : "female";
            if (self != null)
            {
                self.Gender = g;
                self.MarkNativeMutation(DigglesTclEntity.NativeMutationGender);
            }
            return g;
        }

        // The spawner builds each entity as outer + inner "Model" (the animator host).
        static UnityEngine.Transform? FindInnerModel(UnityEngine.GameObject go)
        {
            var t = go.transform.Find("Model");
            if (t != null) return t;
            var anim = go.GetComponentInChildren<Diggles.Runtime.DigglesAnimator>();
            return anim != null ? anim.transform : null;
        }

        // The link/dummy point's offset from the entity origin in NATIVE coords — the shared
        // resolver behind get_linkpos (FUN_00686d10) and `action walk -object/-dummy`
        // (FUN_00420840:1923-1944). Two faithful variants:
        // • applyRotation=true (get_linkpos): the native rotates the model-space link point
        // by the object's rotation (FUN_00686d10:131-154) before scaling. We get the same
        // by walking the live transform chain (inner Model = Q_x(-90)·markerRot·0.005
        // under the rotated outer) and mapping the world delta back through LocalToNative.
        // • applyRotation=false (walk -dummy): the native target = obj.pos + RAW scaled
        // link point — x,y × DAT_00736fcc, z × DAT_00736fe0 = 2× (NO rotation applied,
        // FUN_00420840:1929-1938). Model (a,b,c) → native (a·s, b·s, c·2s) with
        // s = ModelToWorldScale; identical to what the transform chain yields for an
        // unrotated entity (Q_x(-90) maps model b→−localZ→native +y, c→localY→native z·2).
        // Headless contract: with no GameObject / animator / link channel this returns false
        // and never throws — callers fall back to the entity origin.
        internal static bool TryGetLinkOffsetNative(DigglesTclContext ctx, DigglesTclEntity ent,
            int linkId, bool applyRotation, out Vector3 nativeOffset)
        {
            nativeOffset = Vector3.zero;
            if (ent.GameObject == null) return false;
            var inner = FindInnerModel(ent.GameObject);
            if (inner == null) return false;
            var anim = inner.GetComponent<Diggles.Runtime.DigglesAnimator>();
            if (anim == null) return false;

            if (!applyRotation || ctx.SpawnParent == null)
            {
                // Raw mapping (the walk -dummy contract): the ENGINE dequant u16/4 − 8192
                // (FUN_00408a10) in raw model axes, scaled x,y × 0.005, z × 0.01 — the doubled
                // height unit (FUN_00420840:1929-1938). NOT the per-mesh vertex affine: that is
                // a different linear map (kept for the hat seat), and feeding it here displaced
                // harvest/chop stand positions on the DEPTH axis.
                if (!anim.TryGetLinkOffsetEngine(linkId, out var engineLocal)) return false;
                const float s = Diggles.Runtime.DigglesModelLibrary.ModelToWorldScale;
                nativeOffset = new Vector3(engineLocal.x * s, engineLocal.y * s, engineLocal.z * 2f * s);
                return true;
            }
            if (!anim.TryGetLinkOffset(linkId, out var modelLocal)) return false;
            // Rotated mapping through the live chain: link point world pos − entity world pos,
            // both mapped to native space under the spawn root (the affine offset cancels).
            var root = ctx.SpawnParent;
            Vector3 linkNative = RuntimeWorldSpawner.LocalToNative(
                root.InverseTransformPoint(inner.TransformPoint(modelLocal)));
            Vector3 entNative = RuntimeWorldSpawner.LocalToNative(
                root.InverseTransformPoint(ent.GameObject.transform.position));
            nativeOffset = linkNative - entNative;
            return true;
        }

        static readonly char[] s_ws = { ' ', '\t', '\n', '\r' };

        // set_textureanimation <obj> <submesh> <framelist> [startframe=0] [animtype=2]:
        // resolve the obj's animator → the submesh's base material → def_texture variant
        // textures, then drive the per-submesh flipbook via DigglesTextureAnimator.
        static string CmdSetTextureAnimation(DigglesTclContext ctx, string[] a)
        {
            if (a.Length < 4) return "";
            var ent = ResolveEntityOrNull(ctx, a[1]);
            if (ent == null) return "";
            // During a skip drain the GameObject may not exist yet (spawn itself is deferred) —
            // parse and RECORD the logical state instead of dropping; outside
            // a drain the pre-existing no-library/no-GameObject bails stand.
            if (!ctx.SequencePresentationDrain
                && (ctx.ModelLibrary == null || ent.GameObject == null))
                return "";
            if (!int.TryParse(a[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int submesh))
                return "";

            // framelist: whitespace-separated ints (a braced {0 0 9} arrives debraced as "0 0 9").
            var parts = a[3].Split(s_ws, System.StringSplitOptions.RemoveEmptyEntries);
            var indices = new System.Collections.Generic.List<int>(parts.Length);
            foreach (var p in parts)
                if (int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                    indices.Add(v);
            if (indices.Count == 0) return "";

            // During a cutscene, a FROZEN actor's auto face-mood fires SINGLE-frame face/eye swaps
            // (set_fanim → set_textureanimation this 3/4 <oneFrame>) that overwrite the sequencer's
            // scripted facial anim (start_fanim_sequence always appends a filler → ≥2 frames). Reject
            // those single-frame auto overwrites so the scripted eyes/mouth keep animating. (face #)
            if ((submesh == 3 || submesh == 4) && indices.Count == 1
                && ctx.SequenceActive
                && (ent.StateEnabledCount < 0 || ent.Parked || !ent.SequenceActiveFlag))
                return "";
            int startFrame = a.Length > 4 && int.TryParse(a[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int sf) ? sf : 0;
            int animType = a.Length > 5 && int.TryParse(a[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out int at) ? at : 2;

            // A scripted single mouth/eyes pose — `sq_actor mouth|eyes N <v>` → start_fanim_sequence
            // emits the 2-frame "kick" {v <resting>} as play-once. The original HOLDS that pose until
            // the next sq_actor mouth/eyes (the urw_3 laugh sets mouth 9, plays the laugh ~3.5s, then
            // resets mouth 0; start_004:18 `sq_actor eyes 0 {11}` puts the dancer's SUNGLASSES on —
            // eye texture variant 11 — for the whole dance). Our play-once would step to the trailing
            // resting frame after 0.2s ("flashes one frame then resets"). Collapse the trailing frame
            // so the set pose is held. Cutscene only; face(3)/eyes(4) submeshes (count==2 ⟺ single
            // value: a scripted list keeps its own trailing filler → ≥3).
            if ((submesh == 3 || submesh == 4) && indices.Count == 2 && animType == 1 && ctx.SequenceActive)
                indices[1] = indices[0];
            if (ctx.SequencePresentationDrain)
            {
                // Deferred flipbook: the gates above ran at ISSUE time (SequenceActive
                // is still true during the drain — same reductions a played-through cutscene gets),
                // so the recorded tuple is the final logical state; the pump replays it after the
                // deferred spawn. Last-wins per submesh, matching DigglesTextureAnimator slots.
                var perEntity = s_drainDeferredTextureAnims.GetOrCreateValue(ctx);
                if (!perEntity.TryGetValue(ent.Handle, out var records))
                    perEntity[ent.Handle] = records =
                        new System.Collections.Generic.List<(int,
                            System.Collections.Generic.List<int>, int, int)>();
                records.RemoveAll(r => r.Submesh == submesh);
                records.Add((submesh, indices, startFrame, animType));
                ctx.DeferSequencePresentation(ent);
                Diggles.Runtime.Diagnostics.HitchFamilyTimingDiag.Count(
                    "cutscene.presentation-suppressed");
                return "";
            }
            ApplyFaceTextureAnim(ctx, ent, submesh, indices, startFrame, animType);
            return "";
        }

        // Resolve the per-submesh texture-variant frames against the current model's base material
        // and hand them to the per-instance DigglesTextureAnimator (10 FPS flipbook). Shared by
        // set_textureanimation and the text-driven mouth lip-sync. revertToBaseOnComplete is the
        // lip-sync-only end law (finished talk → base face; see DigglesTextureAnimator.Channel).
        static void ApplyFaceTextureAnim(DigglesTclContext ctx, DigglesTclEntity ent,
            int submesh, System.Collections.Generic.List<int> indices, int startFrame, int animType,
            bool revertToBaseOnComplete = false)
        {
            if (ctx.ModelLibrary == null || ent.GameObject == null || indices.Count == 0) return;
            var inner = FindInnerModel(ent.GameObject);
            var anim = inner != null ? inner.GetComponent<Diggles.Runtime.DigglesAnimator>() : null;
            if (anim == null) return;
            var baseMat = anim.GetSubmeshBaseMaterialName(submesh);
            if (string.IsNullOrEmpty(baseMat)) return;

            var frames = new UnityEngine.Texture2D?[indices.Count];
            for (int k = 0; k < indices.Count; k++)
                frames[k] = ctx.ModelLibrary.GetVariantTexture(baseMat!, indices[k]);

            var tex = inner!.GetComponent<Diggles.Runtime.DigglesTextureAnimator>()
                      ?? inner.gameObject.AddComponent<Diggles.Runtime.DigglesTextureAnimator>();
            tex.SetTextureAnimation(submesh, frames, indices, startFrame, animType,
                revertToBaseOnComplete);
        }

        // ----- text-driven mouth lip-sync (faithful to the native engine) -----
        // The original drives the talking mouth NATIVELY from the voice playback: each spoken line's
        // text is converted, at runtime, into a per-frame viseme sequence on the FACE submesh (3), held
        // for the line's duration.
        // No Tcl script touches the mouth during dialogue, which is why we never found a driver there.
        // We reproduce it from the same inputs we already have in set_speechout: the spoken text + the
        // line duration. Eyes stay on submesh 4 (scripted sq_actor eyes), so the two compose.
        //
        // Letter → face viseme frame (CP1252, case-insensitive). Groups + frames are the exact ones
        // read from the binary: airk/lnt→9(A_I) cdzs→10(C_D) e,ä→11(E) fv→12(F_V) mbp→13(M_B)
        // o,u,ö,ü,y→14(O_U) wq→15(W_Q); everything else (incl. space/punct, b? no—b is in mbp) → 0 (closed).
        static int VisemeFrameForChar(char c)
        {
            c = char.ToLowerInvariant(c);
            switch (c)
            {
                case 'a': case 'i': case 'r': case 'k':
                case 'l': case 'n': case 't': return 9;   // A_I
                case 'c': case 'd': case 'z': case 's': return 10;  // C_D
                case 'e': case 'ä': return 11;                      // E
                case 'f': case 'v': return 12;                      // F_V
                case 'm': case 'b': case 'p': return 13;            // M_B
                case 'o': case 'u': case 'ö': case 'ü': case 'y': return 14;  // O_U
                case 'w': case 'q': return 15;                      // W_Q
                default: return 0;                                  // closed
            }
        }

        // Build a 10 FPS viseme framelist for `text` spanning `durationSec`. Vowels hold longer than
        // consonants, then the
        // whole timeline is normalized to the line duration so the mouth finishes with the voice.
        static System.Collections.Generic.List<int> BuildVisemeFrames(string text, float durationSec)
        {
            const string vowels = "aeiouäöüy0123456789";
            int n = UnityEngine.Mathf.Clamp(UnityEngine.Mathf.RoundToInt(durationSec * 10f), 1, 400);
            var result = new System.Collections.Generic.List<int>(n);
            if (string.IsNullOrEmpty(text)) { result.Add(0); return result; }

            // Per-char weight (relative on-screen time).
            var weights = new float[text.Length];
            float total = 0f;
            for (int k = 0; k < text.Length; k++)
            {
                char c = char.ToLowerInvariant(text[k]);
                float w = char.IsWhiteSpace(c) ? 0.15f
                        : vowels.IndexOf(c) >= 0 ? 0.40f
                        : char.IsLetter(c) ? 0.02f : 0.15f;   // punctuation ≈ pause
                weights[k] = w; total += w;
            }
            if (total <= 0f) { result.Add(0); return result; }

            // Sample the normalized timeline at each 10 FPS step → the char active there → its viseme.
            for (int f = 0; f < n; f++)
            {
                float target = (f + 0.5f) / n * total;   // mid-frame position along the weight axis
                float acc = 0f; int ci = 0;
                for (int k = 0; k < text.Length; k++) { acc += weights[k]; if (target <= acc) { ci = k; break; } ci = k; }
                result.Add(VisemeFrameForChar(text[ci]));
            }
            return result;
        }

        // ----- file loader -----

        static string CmdCall(DigglesTclContext ctx, string[] args)
        {
            if (args.Length < 2)
                throw new TclException("wrong # args: should be \"call scriptname\"");
            var content = LoadScriptFile(ctx, args[1]);
            if (content == null) return "";
            return ctx.Interp.Eval(content);
        }

        static string CmdLoadFile(DigglesTclContext ctx, string[] args)
        {
            if (args.Length < 2)
                throw new TclException("wrong # args: should be \"load_file filename ?cached?\"");
            var content = LoadScriptFile(ctx, args[1]);
            if (content != null) return content;
            // Faithful Tcl `open`/`load_file` semantics: a missing file is an ERROR, not "".
            // misc/file_wrapper.tcl's `open` wraps load_file in `catch` to return 0 on miss
            // (the sequencer's parse_file relies on this). Only swallow when explicitly tolerant.
            if (ctx.TolerateMissingFiles) return "";
            throw new TclException($"couldn't open \"{args[1]}\": no such file or directory");
        }

        // ----- filesystem (cwd-relative to DataRoot) -----

        // Combine a DataRoot-relative base with arg, normalising `.`/`..` and a
        // leading `data/` or `/` (→ from DataRoot). Returns forward-slash relative
        // path, no leading/trailing slash.
        static string CombineRel(string baseRel, string arg)
        {
            arg = (arg ?? "").Replace('\\', '/').Trim();
            string start = baseRel;
            if (arg.StartsWith("data/", System.StringComparison.OrdinalIgnoreCase))
            { arg = arg.Substring(5); start = ""; }
            else if (arg.StartsWith("/")) { arg = arg.TrimStart('/'); start = ""; }
            var toks = new System.Collections.Generic.List<string>();
            foreach (var t in start.Split('/')) if (t.Length > 0) toks.Add(t);
            foreach (var t in arg.Split('/'))
            {
                if (t.Length == 0 || t == ".") continue;
                if (t == "..") { if (toks.Count > 0) toks.RemoveAt(toks.Count - 1); }
                else toks.Add(t);
            }
            return string.Join("/", toks);
        }

        // DataRoot-relative path → absolute OS path.
        static string AbsFromRel(DigglesTclContext ctx, string rel)
        {
            rel = (rel ?? "").Replace('/', System.IO.Path.DirectorySeparatorChar);
            return string.IsNullOrEmpty(ctx.DataRoot) ? rel
                 : (rel.Length == 0 ? ctx.DataRoot : System.IO.Path.Combine(ctx.DataRoot, rel));
        }

        static string CmdCd(DigglesTclContext ctx, string[] args)
        {
            ctx.Cwd = args.Length < 2 ? "" : CombineRel(ctx.Cwd, args[1]);
            return "";
        }

        static string CmdPwd(DigglesTclContext ctx, string[] args)
            => AbsFromRel(ctx, ctx.Cwd).Replace('\\', '/');

        // ref_get <objref> <member> — read another object's per-instance variable.
        static string CmdRefGet(DigglesTclContext ctx, string[] args)
        {
            if (args.Length < 3) return "";
            var ent = ResolveEntityOrNull(ctx, args[1]);
            return ent != null && ent.VarsScope.Vars.TryGetValue(args[2], out var v) ? v : "";
        }

        // ref_set <objref> <member> <value> — write another object's per-instance var.
        static string CmdRefSet(DigglesTclContext ctx, string[] args)
        {
            if (args.Length < 4) return "";
            var ent = ResolveEntityOrNull(ctx, args[1]);
            if (ent != null) ent.VarsScope.Vars[args[2]] = args[3];
            return "";
        }

        // glob ?-nocomplain? ?-directory dir? ?-types t? ?--? pattern...
        // Enumerates files+dirs in (DataRoot + Cwd [+ -directory]) matching each
        // pattern, returns a Tcl list of base names (what classinit's `call
        // scripts/.../$f` expects). Honors -nocomplain.
        static string CmdGlob(DigglesTclContext ctx, string[] args)
        {
            bool nocomplain = false;
            string? dir = null;
            var patterns = new System.Collections.Generic.List<string>();
            for (int i = 1; i < args.Length; i++)
            {
                var a = args[i];
                if (a == "-nocomplain") nocomplain = true;
                else if (a == "-directory" && i + 1 < args.Length) dir = args[++i];
                else if (a == "-path" && i + 1 < args.Length) dir = args[++i];
                else if (a == "-types" && i + 1 < args.Length) i++;          // ignore value
                else if (a == "-tails" || a == "-join") { }                   // ignore
                else if (a == "--") { for (i++; i < args.Length; i++) patterns.Add(args[i]); break; }
                else if (a.StartsWith("-", System.StringComparison.Ordinal)) { } // unknown flag
                else patterns.Add(a);
            }
            string baseRel = dir == null ? ctx.Cwd : CombineRel(ctx.Cwd, dir);
            var results = new System.Collections.Generic.List<string>();
            foreach (var pat in patterns)
            {
                // A pattern may carry its own directory (e.g. "data/templates/urw_gng_*.tcl", as lgtools.tcl
                // builds): split the dir off and resolve it via CombineRel (which strips a leading "data/"
                // → DataRoot). EnumerateFiles' searchPattern must be a BARE filename glob — passing one with
                // path separators throws (swallowed → 0 matches), which is why globbed template sets were empty.
                string patNorm = pat.Replace('\\', '/');
                int slash = patNorm.LastIndexOf('/');
                string filePat = slash >= 0 ? patNorm.Substring(slash + 1) : patNorm;
                string enumRel = slash >= 0 ? CombineRel(baseRel, patNorm.Substring(0, slash)) : baseRel;
                string enumAbs = AbsFromRel(ctx, enumRel);
                if (!System.IO.Directory.Exists(enumAbs) || filePat.Length == 0) continue;
                try
                {
                    foreach (var f in System.IO.Directory.EnumerateFiles(enumAbs, filePat))
                        results.Add(System.IO.Path.GetFileName(f));
                    foreach (var d in System.IO.Directory.EnumerateDirectories(enumAbs, filePat))
                        results.Add(System.IO.Path.GetFileName(d));
                }
                catch { /* bad pattern → no matches */ }
            }
            if (results.Count == 0 && !nocomplain && patterns.Count > 0)
                throw new TclException($"no files matched glob pattern(s): {string.Join(" ", patterns)}");
            return TclList.Format(results);
        }

        // file <sub>... — at least `exists` (the init path needs it); plus a few
        // cheap path helpers. Unknown subcommands return "".
        static string CmdFile(DigglesTclContext ctx, string[] args)
        {
            if (args.Length < 2) return "";
            string sub = args[1];
            string p = args.Length > 2 ? args[2] : "";
            switch (sub)
            {
                case "exists":
                {
                    string abs = AbsFromRel(ctx, CombineRel(ctx.Cwd, p));
                    return (System.IO.File.Exists(abs) || System.IO.Directory.Exists(abs)) ? "1" : "0";
                }
                case "isdirectory":
                    return System.IO.Directory.Exists(AbsFromRel(ctx, CombineRel(ctx.Cwd, p))) ? "1" : "0";
                case "isfile":
                    return System.IO.File.Exists(AbsFromRel(ctx, CombineRel(ctx.Cwd, p))) ? "1" : "0";
                case "tail":      return System.IO.Path.GetFileName(p.Replace('\\', '/'));
                case "dirname":   { var d = System.IO.Path.GetDirectoryName(p.Replace('\\', '/')); return d?.Replace('\\', '/') ?? "."; }
                case "rootname":  { int dot = p.LastIndexOf('.'); return dot > 0 ? p.Substring(0, dot) : p; }
                case "extension": { int dot = p.LastIndexOf('.'); return dot >= 0 ? p.Substring(dot) : ""; }
                case "join":
                {
                    var parts = new System.Collections.Generic.List<string>();
                    for (int i = 2; i < args.Length; i++) parts.Add(args[i].Replace('\\', '/').Trim('/'));
                    return string.Join("/", parts);
                }
                default: return "";
            }
        }

        // Resolves a Diggles-style path (`scripts/misc/foo.tcl`, optionally
        // prefixed with `data/`) against ctx.DataRoot. Returns null when
        // file isn't present and the context tolerates missing files; else
        // throws. File contents are cached so repeated `call` doesn't
        // re-touch disk.
        static string? LoadScriptFile(DigglesTclContext ctx, string requested)
        {
            string path = requested.Replace('\\', '/');
            if (path.StartsWith("data/", System.StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("data\\", System.StringComparison.OrdinalIgnoreCase))
                path = path.Substring(5);
            // Diggles treats a leading slash as DataRoot-relative, not as an operating-system
            // absolute path. Document pages use this form for onlinehelputils.tcl.
            path = path.TrimStart('/');

            string full = string.IsNullOrEmpty(ctx.DataRoot)
                ? path
                : System.IO.Path.Combine(ctx.DataRoot,
                    path.Replace('/', System.IO.Path.DirectorySeparatorChar));

            if (!System.IO.File.Exists(full))
            {
                if (ctx.TolerateMissingFiles) return null;
                throw new TclException($"can't read file \"{requested}\" (resolved: \"{full}\")");
            }

            if (ctx.FileCache.TryGetValue(full, out var cached)) return cached;

            string content = Diggles.Runtime.DigglesText.ReadAllText(full);
            // Normalise CRLF/CR → LF, matching Tcl's text-mode reading. Several shipped data
            // files are CRLF; file_wrapper.tcl's `open` does `split $data "\n"`, so without this
            // every line keeps a trailing \r — which silently breaks exact string compares like
            // soundmarker parsing (`"marker\r" != "marker"` → no audio markers load → no voice).
            if (content.IndexOf('\r') >= 0)
                content = content.Replace("\r\n", "\n").Replace('\r', '\n');
            content = PreprocessScript(ctx, content);
            ctx.FileCache[full] = content;
            return content;
        }

        // The native script-loader PREPROCESSOR (FUN_006bb140): every line whose first
        // non-blank chars are `//#` is a DIRECTIVE, parsed as `%s %s` and uppercased (:100-102).
        // Verbs (decompile-verified):
        // ENDIF pops the skip stack — ALWAYS processed (:105/:124-131);
        // ELSE / ELIF flips the current skip flag — ALWAYS processed (:402);
        // gated on NOT currently skipping (:173 `if (local_95 == '\0')`):
        // DEFINE <sym> adds to the symbol table (:174/FUN_006baca0);
        // UNDEF/UNDEFINE <sym> removes (:197/:217, __stricmp scan:383-396);
        // IF / IFDEF <sym> push, skip = !defined (:372-376);
        // IFNOT / IFNDEF <sym> push, skip = defined (:365-368);
        // STOPIF <sym> stop reading the file when defined (:237/:256-257);
        // STOPIFNOT <sym> stop when NOT defined (:260/:279-281).
        // Non-directive lines reach the interpreter only while the skip flag is clear
        // (:407-409). Directive lines NEVER reach it (our TclParser strips `//` lines anyway).
        // Unknown verbs are ignored (the:343-363 fall-through). Symbols persist across files
        // (one global table); the port seeds FULL (retail full game — `//# STOPIFNOT FULL`
        // heads every class the shipped game loads, e.g. saegewerk.tcl:1).
        static string PreprocessScript(DigglesTclContext ctx, string content)
        {
            if (content.IndexOf("//#", System.StringComparison.Ordinal) < 0) return content;
            var sb = new System.Text.StringBuilder(content.Length);
            var stack = new System.Collections.Generic.Stack<bool>();
            bool skip = false;
            int pos = 0;
            while (pos <= content.Length)
            {
                int eol = content.IndexOf('\n', pos);
                string line = eol < 0 ? content.Substring(pos) : content.Substring(pos, eol - pos);
                pos = eol < 0 ? content.Length + 1 : eol + 1;
                var t = line.TrimStart();
                if (t.StartsWith("//#", System.StringComparison.Ordinal))
                {
                    var parts = t.Substring(3).Split(
                        (char[]?)null, System.StringSplitOptions.RemoveEmptyEntries);
                    string dir = parts.Length > 0 ? parts[0].ToUpperInvariant() : "";
                    string sym = parts.Length > 1 ? parts[1] : "";
                    switch (dir)
                    {
                        case "ENDIF":
                            skip = stack.Count > 0 && stack.Pop();
                            break;
                        case "ELSE":
                        case "ELIF":
                            skip = !skip;
                            break;
                        case "DEFINE":
                            if (!skip && sym.Length > 0) ctx.PreprocDefines.Add(sym);
                            break;
                        case "UNDEF":
                        case "UNDEFINE":
                            if (!skip) ctx.PreprocDefines.Remove(sym);
                            break;
                        case "IF":
                        case "IFDEF":
                            if (!skip) { stack.Push(false); skip = !ctx.PreprocDefines.Contains(sym); }
                            break;
                        case "IFNOT":
                        case "IFNDEF":
                            if (!skip) { stack.Push(false); skip = ctx.PreprocDefines.Contains(sym); }
                            break;
                        case "STOPIF":
                            if (!skip && ctx.PreprocDefines.Contains(sym)) return sb.ToString();
                            break;
                        case "STOPIFNOT":
                            if (!skip && !ctx.PreprocDefines.Contains(sym)) return sb.ToString();
                            break;
                    }
                    continue;   // a directive line never reaches the interpreter
                }
                if (!skip) { sb.Append(line); sb.Append('\n'); }
            }
            return sb.ToString();
        }

        // ----- class lifecycle implementations -----

        static string CmdMethod(DigglesTclContext ctx, string[] args)
        {
            // method NAME ARGS CODE
            if (args.Length != 4)
                throw new TclException("wrong # args: should be \"method name args code\"");
            var c = RequireClass(ctx);
            var paramSpec = TclList.Parse(args[2]);
            var paramNames = new string[paramSpec.Count];
            var paramDefaults = new string?[paramSpec.Count];
            bool hasArgsTail = false;
            for (int i = 0; i < paramSpec.Count; i++)
            {
                var parts = TclList.Parse(paramSpec[i]);
                paramNames[i] = parts[0];
                paramDefaults[i] = parts.Count > 1 ? parts[1] : null;
                if (paramNames[i] == "args" && i == paramSpec.Count - 1)
                    hasArgsTail = true;
            }
            c.Methods[args[1]] = new TclProc
            {
                Name = args[1],
                ParamNames = paramNames,
                ParamDefaults = paramDefaults,
                HasArgsTail = hasArgsTail,
                Body = args[3],
            };
            return "";
        }

        static string CmdHandleEvent(DigglesTclContext ctx, string[] args)
        {
            // handle_event NAME CODE
            if (args.Length != 3)
                throw new TclException("wrong # args: should be \"handle_event name code\"");
            RequireClass(ctx).EventHandlers[args[1]] = args[2];
            return "";
        }

        static string CmdState(DigglesTclContext ctx, string[] args)
        {
            // state NAME CODE (alias for state_enter in some scripts)
            if (args.Length != 3)
                throw new TclException("wrong # args: should be \"state name code\"");
            var stateClass = RequireClass(ctx);
            stateClass.States[args[1]] = args[2];
            stateClass.RegisterStateName(args[1]);
            return "";
        }

        static string CmdStateEnter(DigglesTclContext ctx, string[] args)
        {
            if (args.Length != 3) throw new TclException("wrong # args: should be \"state_enter name code\"");
            var enterClass = RequireClass(ctx);
            enterClass.StateEnter[args[1]] = args[2];
            enterClass.RegisterStateName(args[1]);
            return "";
        }

        static string CmdStateLeave(DigglesTclContext ctx, string[] args)
        {
            if (args.Length != 3) throw new TclException("wrong # args: should be \"state_leave name code\"");
            var leaveClass = RequireClass(ctx);
            leaveClass.StateLeave[args[1]] = args[2];
            leaveClass.RegisterStateName(args[1]);
            return "";
        }

        // state_trigger HANDLE [NEW_STATE]
        // state_triggerfresh HANDLE [NEW_STATE]
        static string CmdStateTrigger(DigglesTclContext ctx, string[] args, bool fresh)
        {
            if (args.Length < 2 || args.Length > 3)
                throw new TclException("wrong # args for state_trigger");
            var ent = ctx.ResolveEntity(args[1]);
            string? newState = args.Length == 3 ? args[2] : null;

            // Leave handler for the old state — ONLY if that state actually went LIVE (its think
            // tick ran). Native law: StateTrigger (TCL:CTclObjMethods::StateTrigger FUN_0069ec80:
            // 109) only writes the TARGET index obj+0x204 and runs NO handlers; leave/enter fire
            // in CObj::HandleStates (005cac90) at think time, comparing LIVE +0x200 vs TARGET
            // +0x204 (leave:30-55 guard `live != target && live != -1`, enter:56-80, live=target
            //:81, body:82-104). A state set and replaced within ONE event handler (no think
            // between) never becomes live → its leave NEVER runs natively. The old unconditional
            // leave here broke MANUAL CHOP of an unripe Pilz: evt_task_harvest_
            // proc sets `task` (z_events.tcl:922), harvest→lock_item claims the pilz (z_procs.tcl:
            // 746 →:63-73), then `work_dispatch` (:933) ran state_leave task = unlock_item
            // (zwerg.tcl:338-342), which clears current_lock_obj unconditionally (z_procs.tcl:83)
            // → the still-locked unripe pilz (pilz.tcl:23 locks at spawn; only agephase 3 unlocks,
            //:108) failed check_lockedbyother (z_procs.tcl:1721-1731) → tasklist_clear → the
            // "gnome refuses to chop an unripe mushroom" regression. Same transient task→
            // work_dispatch pattern: mine (z_events.tcl:964→:975), dig (:1030→:1051).
            // Leave of a LIVE state still runs synchronously here (think-deferral not modeled —
            // documented divergence; shipped leave handlers are order-insensitive within the
            // tick that leaves them).
            if (ent.CurrentState != null && ent.Class != null && ent.StateWentLive)
            {
                var leaveCode = TryFindStateLeave(ctx, ent.Class, ent.CurrentState);
                if (leaveCode != null) RunInEntityContext(ctx, ent, leaveCode, "state_leave " + (ent.CurrentState ?? ""));
            }

            if (fresh) ent.StateEnabledCount = 0;
            if (!string.Equals(newState, ent.CurrentState, System.StringComparison.Ordinal))
                ent.StateWentLive = false;   // target changed; it goes live at the next think (005cac90:81)
            ent.CurrentState = newState;
            ent.StateWordsDirty = true;      // +0x204 (and for fresh +0x208) changed vs any restored words

            // NO synchronous state_enter — the ENTER runs at the think that makes the state live
            // (CObj_HandleStates 005cac90:56-80, right before live=target:81 and the body:82-104;
            // see TickEntityState). A former synchronous enter paired with a live-gated leave was
            // ASYMMETRIC — a TRANSIENT idle ran
            // `state_enter idle { gnome_idle this 1 }` (zwerg.tcl:273-276; work_idle likewise,
            // z_work_states.tcl:219-226) with the paired leave-remove skipped, leaking the gnome
            // into the dispatcher pool; the sweep then stole him mid-manual-chop (axe out → walk
            // up → putaway). Natively a transient state runs NEITHER handler.
            return "";
        }

        static string? TryFindStateEnter(DigglesTclContext ctx, DigglesClass c, string name)
        {
            if (c.StateEnter.TryGetValue(name, out var code)) return code;
            foreach (var b in c.Inherits)
                if (ctx.Classes.TryGetValue(b, out var bc))
                {
                    var f = TryFindStateEnter(ctx, bc, name);
                    if (f != null) return f;
                }
            return null;
        }

        static string? TryFindStateLeave(DigglesTclContext ctx, DigglesClass c, string name)
        {
            if (c.StateLeave.TryGetValue(name, out var code)) return code;
            foreach (var b in c.Inherits)
                if (ctx.Classes.TryGetValue(b, out var bc))
                {
                    var f = TryFindStateLeave(ctx, bc, name);
                    if (f != null) return f;
                }
            return null;
        }

        // call_method OBJREF METHOD ARGS... (instance method)
        // call_method_static CLASS METHOD ARGS (no this)
        static string CmdCallMethod(DigglesTclContext ctx, string[] args, bool isStatic)
        {
            if (args.Length < 3) throw new TclException("wrong # args for call_method");
            DigglesClass cls;
            DigglesTclEntity? ent = null;
            if (isStatic)
            {
                if (!ctx.Classes.TryGetValue(args[1], out var c)) return "";
                cls = c;
            }
            else
            {
                ent = ctx.ResolveEntity(args[1]);
                cls = ent.Class;
            }
            var method = ctx.FindMethod(cls, args[2]);
            if (method == null)
            {
                // Silent miss — many scripts probe for methods that may not exist on a
                // particular subclass. Return "0" (not ""), matching the unknown-command
                // default + Diggles' numeric idiom: callers test `[call_method …] != 0` /
                // `== 0`, and "" != 0 is TRUE in expr (string compare), which silently
                // broke chains like get_info → gender selection. "0" is the faithful falsy.
                return "0";
            }
            // Diggles methods don't separate "locals" from "members" the
            // way standard Tcl procs do — any `set foo X` in a class
            // method or obj_init persists on the instance. We model this
            // by running method bodies WITH the entity's VarsScope as the
            // current scope, temporarily shadowing entity vars with the
            // bound parameter values.
            var scope = ent != null ? ent.VarsScope : new TclScope();
            var saved = new System.Collections.Generic.Dictionary<string, string?>();
            int argIdx = 3;
            for (int p = 0; p < method.ParamNames.Length; p++)
            {
                string pname = method.ParamNames[p];
                string value;
                if (method.HasArgsTail && p == method.ParamNames.Length - 1)
                {
                    var rest = new System.Collections.Generic.List<string>();
                    while (argIdx < args.Length) rest.Add(args[argIdx++]);
                    value = TclList.Format(rest);
                }
                else if (argIdx < args.Length) value = args[argIdx++];
                else if (method.ParamDefaults[p] != null) value = method.ParamDefaults[p]!;
                else throw new TclException($"wrong # args to method \"{method.Name}\"");
                saved[pname] = scope.Vars.TryGetValue(pname, out var ex) ? ex : null;
                scope.Vars[pname] = value;
                if (method.HasArgsTail && p == method.ParamNames.Length - 1) break;
            }

            var prevThis = ctx.This;
            var prevGlobal = ctx.Interp.ActiveGlobalOverride;
            ctx.This = ent;
            ctx.Interp.Scopes.Add(scope);
            // Instance method: `global` binds to the object's scope (per-object interp).
            // Static method (ent == null): leave the shared global active.
            if (ent != null) ctx.Interp.ActiveGlobalOverride = ent.VarsScope;
            try { return ctx.Interp.Eval(method.Body); }
            catch (TclReturnException ret) { return ret.Value; }
            finally
            {
                ctx.Interp.ActiveGlobalOverride = prevGlobal;
                ctx.Interp.Scopes.RemoveAt(ctx.Interp.Scopes.Count - 1);
                ctx.This = prevThis;
                // Restore previous values of shadowed names.
                foreach (var kv in saved)
                {
                    if (kv.Value == null) scope.Vars.Remove(kv.Key);
                    else scope.Vars[kv.Key] = kv.Value;
                }
            }
        }

        // Eval a piece of class code (obj_init / event handler / state body)
        // with `this` bound to the entity. Uses the entity's VarsScope as the
        // top of the scope stack so `set foo bar` round-trips to the entity.
        // Distinct body-eval errors already logged (dedup so a per-tick error on
        // hundreds of entities doesn't flood the console / freeze the editor).
        static readonly System.Collections.Generic.HashSet<string> s_loggedBodyErrors = new();
        // Rate limiter for the per-failure "[action walk] no path" warning (see the walk builder).
        static double s_nextNoPathWarnAt;

        // Script `log` output is off by default (it freezes the editor at scale). Toggle
        // on to trace a specific script. See the `log` command registration.
        // ALSO drives the per-command work-chain trace + un-deduped body-error logging in
        // RunInEntityContext — flip it to see exactly where an event/state chain stops.
        public static bool VerboseScriptLog = false;

        // [WORKDBG] — a TARGETED work-loop trace. When on, every StateEnabledCount change, task pop,
        // actiondispatch and walk-result is written to the debug log tagged "[WORKDBG]", so a
        // single `grep [WORKDBG]` (or Select-String "[WORKDBG]") isolates the work chain — no need to
        // send the whole verbose log. Tied ON by DigglesWorldRuntime when verboseChainTrace is set.
        // Diagnoses the x100/instant-anim re-pop: look for a per-tick state_enable/triggerfresh
        // re-enabling a gnome whose anim action is still in flight (→ the pump pops the next task and
        // replaces the anim before it finishes), and the walk-to-target result (NULL ⇒ "chops in place").
        public static bool WorkDebug = false;

        // Targeted, opt-in walk trace used by gated PlayMode repros. One request id follows the
        // synchronous command parse, goal relaxation, layered route assembly, and the first mover
        // tick. Disabled by default so ordinary AI walks stay silent.
        public static bool WalkExecutorDiagnostics = false;
        static int s_nextWalkExecutorTraceId;

        static int BeginWalkExecutorTrace() =>
            WalkExecutorDiagnostics ? ++s_nextWalkExecutorTraceId : 0;

        internal static void TraceWalkExecutor(int traceId, string stage, string message)
        {
            if (!WalkExecutorDiagnostics || traceId == 0) return;
            Debug.Log($"[walk-executor #{traceId}] {stage}: {message}");
        }

        static void WorkLog(DigglesTclContext ctx, DigglesTclEntity? e, string what)
        {
            if (!WorkDebug || e == null) return;
            double t = ctx.Sim != null ? ctx.Sim.GameTime : 0;
            Diggles.Runtime.Diagnostics.DigglesDebugLog.Log(
                $"[WORKDBG] t={t:0.000} #{e.Handle} {e.Class?.Name} :: {what} | state={e.CurrentState} " +
                $"cnt={e.StateEnabledCount} act={e.ActiveAction?.GetType().Name ?? "-"} tasks={e.TaskList.Count}");
        }

        // Builtins the work-chain trace skips (pure value-ops that would drown the engine/proc calls).
        // A thrown command (res starts "!!ERR") is ALWAYS shown, never skipped.
        static readonly System.Collections.Generic.HashSet<string> s_traceNoise = new()
        { "set","expr","incr","lindex","llength","lrange","list","lappend","lreplace","string","format",
          "global","append","subst","lsearch","join","split","info","array","concat","return","upvar" };

        // Runs a state body / event handler / obj_init in the entity's scope. A bare
        // `return` is NORMAL Tcl control flow ending the body — these aren't `proc`
        // bodies (no InvokeProc to absorb it), so we must catch TclReturnException
        // here, else it escapes every tick × every entity and floods/freezes. Other
        // errors are isolated + logged once per distinct message (never abort the sim).
        // Public so the sim (action finishcode) + action breakcode eval can run callbacks in the
        // OWNING entity's object scope — `global` inside a called proc (e.g. create_muetze's
        // current_muetze_ref/hatcolor) must bind per-object, not to the shared global.
        public static void RunInEntityContext(DigglesTclContext ctx, DigglesTclEntity ent, string code, string context = "body")
        {
            var prevThis = ctx.This;
            var prevGlobal = ctx.Interp.ActiveGlobalOverride;
            ctx.This = ent;
            ctx.Interp.Scopes.Add(ent.VarsScope);
            // Object-scope: `global x` (and the read-fallback) bind to THIS entity's
            // vars while the body runs — the original gives each object its own interp.
            ctx.Interp.ActiveGlobalOverride = ent.VarsScope;
            // VerboseScriptLog: trace the body command-by-command so the EXACT step where a chain stops is
            // visible — a " => !!ERR" line is a thrown command (e.g. an unimplemented/missing engine verb);
            // a body that simply ends is a proc that returned early (e.g. dig_starttask bailing on
            // get_digedge → -1). Restored after the body so nested callbacks keep their own trace state.
            var prevTrace = ctx.Interp.CommandTrace;
            // Focus the (verbose) trace on the SELECTED gnome so idle-AI bodies of the other entities don't
            // bury the chain you're debugging. No selection (e.g. during a cutscene) → trace all; tracked too.
            if (VerboseScriptLog && (ctx.SelectedHandle == 0 || ent.Handle == ctx.SelectedHandle
                    || Diggles.Runtime.Diagnostics.DigglesDebugLog.IsTracked(ent.Handle)))
            {
                ctx.Interp.CommandTrace = (cmd, cargs, res) =>
                {
                    bool err = res != null && res.StartsWith("!!ERR", System.StringComparison.Ordinal);
                    if (!err && s_traceNoise.Contains(cmd)) return;
                    string a = cargs.Length > 1 ? " " + string.Join(" ", cargs, 1, System.Math.Min(cargs.Length - 1, 4)) : "";
                    if (a.Length > 80) a = a.Substring(0, 80);
                    string r = res ?? ""; if (r.Length > 70) r = r.Substring(0, 70);
                    string head = $"[chain {ent.Class?.Name}#{ent.Handle} {context}] {cmd}{a}";
                    string line = err ? $"{head}  => {r}" : $"{head} -> {r}";
                    // FILE ONLY — per-command Debug.Log at this volume freezes the editor (stalls the sim).
                    // A genuinely thrown command is surfaced in the console by the deduped BROKE catch below.
                    Diggles.Runtime.Diagnostics.DigglesDebugLog.Log(line);
                };
            }
            try { ctx.Interp.Eval(code); }
            catch (TclReturnException) { /* `return` ends the body — normal */ }
            catch (System.Exception e)
            {
                // The chain BROKE here (this event/state/callback), at the command named in the message.
                // Visible as an ERROR (was a deduped Warning). Under VerboseScriptLog, logged every time.
                string brk = $"[work-chain BROKE] {ent.Class?.Name}#{ent.Handle} in {context}: {e.Message}";
                if (VerboseScriptLog || s_loggedBodyErrors.Add(context + " | " + e.GetType().Name + ": " + e.Message))
                    Debug.LogError(brk);
                Diggles.Runtime.Diagnostics.DigglesDebugLog.Log(brk);   // also to the work-chain file (gated by Enabled)
            }
            finally
            {
                ctx.Interp.CommandTrace = prevTrace;
                ctx.Interp.ActiveGlobalOverride = prevGlobal;
                ctx.Interp.Scopes.RemoveAt(ctx.Interp.Scopes.Count - 1);
                ctx.This = prevThis;
            }
        }

        // ===== Phase 5: map / world commands =====

        // `map create W H` — initialise world grid.
        // `map getoffset` — the relocation offset; the port never relocates, so "0 0". MUST be
        // a valid 2-int list, not "" — generate_next's fenris branches and
        // check_uebergang do `lindex [map getoffset] 1` + expr on it
        // (story.tcl:235/:641; the "" stub made the expr throw).
        // `map <other>` — other subcommands (size, move,...) are no-ops.
        static string CmdMap(DigglesTclContext ctx, string[] args)
        {
            if (args.Length >= 4 && args[1] == "create")
            {
                if (!int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int w)) w = 0;
                if (!int.TryParse(args[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h)) h = 0;
                ctx.MapBuilder?.CreateMap(w, h);
            }
            else if (args.Length >= 2 && args[1] == "getoffset") return "0 0";
            return "";
        }

        // `sm_create_map W H` — allocates the (W/4)×(H/4) story-cell occupancy grid
        // (FUN_0065f430:176-186, zeroed) AND sizes the world map. A re-run (New Game →
        // CampaignInit again) starts from a FRESH zeroed grid, like the native realloc+memset.
        static string CmdSmCreateMap(DigglesTclContext ctx, string[] args)
        {
            if (args.Length < 3) return "";
            if (!int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int w)) w = 0;
            if (!int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h)) h = 0;
            ctx.MapBuilder?.CreateMap(w, h);
            ctx.StoryMapW = StoryCell(w);
            ctx.StoryMapH = StoryCell(h);
            ctx.StoryMap.Clear();
            ctx.StoryLatchDir = 0; ctx.StoryLatchX = 0; ctx.StoryLatchY = 0; ctx.StoryLatchConsumed = false;
            // New-game StoryMgr reset (FUN_005fa7c0:31-32): dig pressure 0, threshold back to the
            // ctor default 5 (CampaignInit re-runs sm_set_digcount 32 right after,:36).
            ctx.StoryDigWeight = 0;
            ctx.StoryDigThreshold = 5;
            ctx.StoryDigMessage = false;   // +0xa0 back to the ctor default (FUN_005fa7c0:33)
            ctx.StoryPlacedTemps.Clear();
            // Zone/temp registries reset too: CampaignInit.tcl calls sm_create_map (:34) BEFORE its
            // sm_add_zone/sm_add_temp tables (:675+), and on a New-Game re-run sm_add_zone's dedup
            // would otherwise leave all 4 zones standing — making every re-registered temp stamp
            // Zone = count−1 = 3 (Lava). The native gets the equivalent via its new-game StoryMgr
            // reset (FUN_005fa7c0).
            ctx.StoryZones.Clear();
            ctx.StoryZoneFloor = 0;
            ctx.StoryTemps.Clear();
            ctx.StoryTempGroups.Clear();
            // Story-event registry (BB) back to empty for a fresh campaign — CampaignInit.tcl re-runs
            // its ~55 sm_add_event registrations right after (:589+). Matches the native new-game
            // StoryMgr reconstruction; without it a second New-Game would inherit the prior game's
            // event flags (and the idempotent sm_add_event would keep their stale indices).
            ctx.StoryEvents.Clear();
            ctx.StoryEventOrder.Clear();
            ctx.StoryEventTriggers.Clear();
            ctx.Fluids.Reset();
            return "";
        }

        // `map_template <pmp> X Y` (canonical) or rarely `map_template X Y <pmp>`.
        // PMP-path-first form dominates in actual scripts — detect by ".pmp" extension.
        static string CmdMapTemplate(DigglesTclContext ctx, string[] args)
        {
            if (args.Length < 4) return "";
            string a1 = args[1], a2 = args[2], a3 = args[3];
            string pmp; string xs; string ys;
            if (a1.EndsWith(".pmp", System.StringComparison.OrdinalIgnoreCase))
            { pmp = a1; xs = a2; ys = a3; }
            else if (a3.EndsWith(".pmp", System.StringComparison.OrdinalIgnoreCase))
            { pmp = a3; xs = a1; ys = a2; }
            else return "";
            if (!int.TryParse(xs, NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)) x = 0;
            if (!int.TryParse(ys, NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)) y = 0;
            ctx.MapBuilder?.LoadPmp(pmp, x, y);
            return "";
        }

        // `map_setlayer2 X Y <l2m>` — X Y first, path third.
        static string CmdMapSetLayer2(DigglesTclContext ctx, string[] args)
        {
            if (args.Length < 4) return "";
            if (!int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)) x = 0;
            if (!int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)) y = 0;
            ctx.MapBuilder?.LoadL2m(args[3], x, y);
            return "";
        }

        // `split_template <parent_tcl> <parent_pmp> W H { {sub_tcl sub_pmp dx dy}... }`
        // Walks the chunk list, sources each sub_tcl (which defines a
        // `MapTemplateSet` proc), loads its PMP at the chunk's absolute
        // world position, then invokes the proc — which in turn calls
        // `map_setlayer2` + `[new ClassName...]` for that chunk.
        //
        // "Absolute" position = current $temppos + chunk offset. $temppos
        // is the canonical Diggles convention for "what world position
        // does this template start at" (set just before `call <foo>.tcl`).
        //
        // The parent_pmp argument is informational (some Diggles tools
        // use it for low-res preview); the static parser ignored it and
        // so do we.
        static string CmdSplitTemplate(DigglesTclContext ctx, string[] args)
        {
            // FAITHFULNESS GUARD (worldgen-wiring-re.md §5): split_template is NOT a native in the
            // original — it is a per-object Tcl proc (story.tcl:368 queue+immediate modes,
            // mp_tools.tcl:4), and in the object-interp model a proc REPLACES any command. The
            // port dispatches natives first, so this invented boot shim would otherwise SHADOW the
            // faithful proc — breaking runtime growth (queue mode: with temppos==0 the shim stamps
            // every chunk at the origin, unpaced). Defer to the active object's class proc when one
            // exists (the StoryMgr path); the native body below remains the classless fallback for
            // the menu diorama / test worlds.
            var activeCls = ctx.This?.Class ?? ctx.CurrentClass;
            if (activeCls != null)
            {
                var proc = ctx.FindProc(activeCls, "split_template");
                if (proc != null) return ctx.Interp.InvokeProc(proc, args);
            }
            if (args.Length < 6) return "";
            string chunkList = args[5];
            (int tx, int ty) = ReadTempposGlobal(ctx);
            var chunks = TclList.Parse(chunkList);
            foreach (var rawChunk in chunks)
            {
                var fields = TclList.Parse(rawChunk);
                if (fields.Count < 4) continue;
                string subTcl = fields[0], subPmp = fields[1];
                if (!int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int dx)) dx = 0;
                if (!int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int dy)) dy = 0;
                int absX = tx + dx, absY = ty + dy;

                // Per-chunk PMP — caller didn't go through map_template,
                // they declared the PMP inline in the chunk record. Builder
                // resolves the filename against its data root.
                ctx.MapBuilder?.LoadPmp(subPmp, absX, absY);

                // Source the sub-tcl. It's in Templates/tcl/split/ — the
                // existing call/source/load_file resolver knows nothing
                // about subdirs, so prepend the canonical path here.
                string scriptPath = "templates/tcl/split/" + subTcl;
                var content = LoadScriptFileTolerant(ctx, scriptPath);
                if (content == null) continue;
                ctx.Interp.Eval(content);

                // Invoke MapTemplateSet (defined by the sub-tcl).
                if (ctx.Interp.ProcExists("MapTemplateSet"))
                {
                    string call = $"MapTemplateSet {absX.ToString(CultureInfo.InvariantCulture)}"
                                + $" {absY.ToString(CultureInfo.InvariantCulture)}";
                    try { ctx.Interp.Eval(call); }
                    catch (TclException) { /* per-chunk failure shouldn't abort the campaign */ }
                }
            }
            return "";
        }

        // Read $temppos as 2-element list. Returns (0,0) if missing/malformed.
        static (int, int) ReadTempposGlobal(DigglesTclContext ctx)
        {
            string? raw = ctx.Interp.GlobalScope.Vars.TryGetValue("temppos", out var v) ? v : null;
            if (string.IsNullOrEmpty(raw)) return (0, 0);
            var parts = TclList.Parse(raw!);
            int x = 0, y = 0;
            if (parts.Count >= 1) int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out x);
            if (parts.Count >= 2) int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out y);
            return (x, y);
        }

        // Same as LoadScriptFile but always tolerates a missing file
        // (used by split_template per-chunk — one missing chunk
        // shouldn't abort the whole campaign).
        static string? LoadScriptFileTolerant(DigglesTclContext ctx, string requested)
        {
            bool prev = ctx.TolerateMissingFiles;
            ctx.TolerateMissingFiles = true;
            try { return LoadScriptFile(ctx, requested); }
            finally { ctx.TolerateMissingFiles = prev; }
        }

        // ===== Phase 7: timer events, event dispatch, state ticks =====

        // timer_event HANDLE EVENT [-interval N] [-repeat M] [-attime T] [-userid U]
        // [-subject1/2/3 H] [-pos1/2/3 {x y z}] [-num1/2/3 N] [-text1/2 S]
        // Schedule EVENT to fire on HANDLE. Defaults per the native TimerEvent
        // (FUN_0069fef0): repeat=1 (:188), attime=now (:189), userid=0 (:191).
        // The payload flags (-userid:376-404, -subject1..3:406-514, -pos1/-pos2
        //:516-568, -num1/-num2/-text1:597-731) ride the event and are read back by
        // `event_get this -pos1` etc. in the handler — they were silently dropped
        // before, breaking every script that passes context through a timer.
        // interval == 0 forces repeat = 1 (CTimerEvtMgr add, FUN_00605f40:45-47) so a
        // `-repeat -1` without an interval can't rapid-fire forever.
        static string CmdTimerEvent(DigglesTclContext ctx, string[] args)
        {
            if (ctx.Sim == null) return "";  // no sim wired → no-op (still parses)
            if (args.Length < 3) throw new TclException("wrong # args: \"timer_event handle event ?-interval N? ?-repeat M? ?-attime T? ?-userid U? ...\"");
            var ent = ctx.ResolveEntity(args[1]);
            string evtName = args[2];
            double interval = 0, attime = ctx.Sim.GameTime;
            int repeat = 1, userid = 0;
            Dictionary<string, string>? payload = null;
            for (int k = 3; k + 1 < args.Length; k += 2)
            {
                string flag = args[k];
                string val = args[k + 1];
                switch (flag)
                {
                    case "-interval": double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out interval); break;
                    case "-repeat":   int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out repeat); break;
                    case "-attime":   double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out attime); break;
                    case "-userid":   int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out userid); break;
                    case "-subject1": case "-subject2": case "-subject3":
                    case "-pos1": case "-pos2": case "-pos3":
                    case "-num1": case "-num2": case "-num3":
                    case "-text1": case "-text2": case "-target":
                        payload ??= new Dictionary<string, string>();
                        payload[flag] = val;
                        break;
                }
            }
            if (interval == 0) repeat = 1;   // FUN_00605f40:45-47
            if (repeat == 0) repeat = 1;     // keep the registry's fire count in sync with TimerScheduler's 0→1 rule
            payload ??= new Dictionary<string, string>();
            payload[TimerRegistry.UserIdKey] = userid.ToString(CultureInfo.InvariantCulture);
            payload[TimerRegistry.SerialKey] =
                ctx.Timers.Register(ent.Handle, userid, evtName, repeat)
                    .ToString(CultureInfo.InvariantCulture);
            ctx.Sim.Scheduler.Schedule(ent.Handle, evtName, attime, interval, repeat, payload);
            return "";
        }

        // timer_unset HANDLE USERID — the original takes a numeric USERID, never an event
        // name (TCL:CTclObjMethods::TimerUnset, FUN_0069fc90:82-83: Tcl_GetIntFromObj,
        // parse failure → 0) and removes every timer on the object whose userid matches;
        // -1 is a wildcard for ALL the object's timers (FUN_00606110:24). Every original
        // call site passes an int (pilz.tcl:111 `timer_unset this 1`, z_methods.tcl:520/555
        // ids 3/4, baby.tcl:156 id 6, Lavawelt.tcl:344 id 0 — the timer_event default).
        static string CmdTimerUnset(DigglesTclContext ctx, string[] args)
        {
            if (ctx.Sim == null) return "";
            if (args.Length < 3) throw new TclException("wrong # args: \"timer_unset handle userid\"");
            var ent = ctx.ResolveEntity(args[1]);
            int userid = 0;
            int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out userid);
            var sched = ctx.Sim.Scheduler;
            int h = ent.Handle;
            ctx.Timers.Cancel(h, userid, evtName => sched.Unset(h, evtName));
            return "";   // the native sets no result
        }

        // event_get → current event name
        // event_get [this] -item KEY → payload[KEY]
        // event_get [this] -pos1 → payload["-pos1"] (and -pos2/-num1/-num2/-text1/-target/-subject1/2)
        // (A leading non-flag arg like `this` is skipped — handlers call `event_get this -pos1`.)
        static string CmdEventGet(DigglesTclContext ctx, string[] args)
        {
            if (ctx.CurrentEvent == null) return "";
            var evt = ctx.CurrentEvent.Value;
            if (args.Length == 1) return evt.Name;
            if (evt.Payload == null) return "";
            for (int k = 1; k < args.Length; k++)
            {
                if (args[k] == "-item" && k + 1 < args.Length)
                    return evt.Payload.TryGetValue(args[k + 1], out var v) ? v : "";
                if (args[k].Length > 0 && args[k][0] == '-')
                    return evt.Payload.TryGetValue(args[k], out var vf) ? vf : "";
            }
            return "";
        }

        // set_event HANDLE EVENT [-item KEY VALUE | -pos1 {x y z} | -num1 N | -target H | -text1 S |...]
        // Raises an event immediately on the entity (next sim tick drains it). The original event
        // payload fields (-pos1/-pos2/-num1/-num2/-text1/-target/-subject1/-subject2) are stored
        // under their flag name so the handler reads them via `event_get this -pos1` etc.; -item
        // KEY VALUE stores under KEY (the existing convention).
        static string CmdSetEvent(DigglesTclContext ctx, string[] args)
        {
            if (args.Length < 3) throw new TclException("wrong # args: \"set_event handle event ?-flag value ...?\"");
            var ent = ctx.ResolveEntity(args[1]);
            Dictionary<string, string>? payload = null;
            for (int k = 3; k < args.Length; )
            {
                if (args[k] == "-item" && k + 2 < args.Length)
                {
                    payload ??= new Dictionary<string, string>();
                    payload[args[k + 1]] = args[k + 2];
                    k += 3;
                }
                else if (args[k].Length > 0 && args[k][0] == '-' && k + 1 < args.Length)
                {
                    payload ??= new Dictionary<string, string>();
                    payload[args[k]] = args[k + 1];
                    k += 2;
                }
                else k++;
            }
            ent.EnqueueEvent(new EntityEvent(args[2], payload), ctx.Sim?.GameTime ?? 0d);
            return "";
        }

        // The Zwerg's own inventory capacity in UI slots (InvGetSize FUN_005cde10 base 6;
        // worn baskets ADD to it). Shared law anchor: the inventory panel's main grid size
        // (InventoryPanelController.BaseSlots), the flat-model pannier-content threshold in
        // is_wearing_pannier, and the gnome capacity gate in inv_add/inv_check all key on it.
        public const int ZwergBaseInvSlots = 6;

        // Worn-container slot bonuses (the InvGetSize FUN_005cde10 table; the binary's own
        // table misspells "Hamsterkaren" — key on the real class names, items/transport.tcl).
        // Single source for the UI basket grid AND the engine capacity law.
        public static int TransportSlotBonus(string className) => className switch
        {
            "Holzkiepe" => 4,
            "Grosse_Holzkiepe" => 8,
            "Schubkarren" => 6,
            "Hamsterkarren" => 6,
            _ => 0,
        };

        // Effective inventory capacity in SLOTS: gnomes = Zwerg base 6 + each carried
        // transport container's bonus (the container itself occupies a slot, so a Holzkiepe
        // carrier holds it + 9 other items = 10 total, matching the panel's 6+4 grid).
        // Everything else keeps its flat entity capacity — the native per-kind SIZE table
        // (FUN_005ce010:61-196) is not modelled; slot-count is the documented approximation.
        internal static int EffectiveInvCapacity(DigglesTclContext ctx, DigglesTclEntity e)
        {
            if (e.Class?.Kind != "gnome") return e.InventoryCapacity;
            int cap = ZwergBaseInvSlots;
            foreach (var h in e.Inventory)
                if (ctx.Entities.TryGetValue(h, out var it) && it.Class != null)
                    cap += TransportSlotBonus(it.Class.Name);
            return cap;
        }

        // ----- the worn-basket VISUAL law -----
        //
        // Native FUN_005cf940 (the post-mutation hook CInventory::Add and the remove path both
        // run,:221-412): every carried type-9 container is re-linked — a container WITH
        // CONTENT gets child+0x278 = carrier id, +0x27c = LINK SLOT 5 (the back;:268-275/:326-333)
        // and its VISIBLE bit set (+0x3d0|=1,:281-282); the wheelbarrow kind links at slot 0
        // (the hands,:384-391); an empty container is left unlinked (+0x278=-1) and hidden.
        // So the back-basket in the original is the carried ITEM'S OWN MODEL riding a link
        // point (that's why Grosse_Holzkiepe shows its big_holzkiepe.3db), NOT clip geometry —
        // the gehen_kiepe_* clips only supply the hunched gait.
        //
        // Port (flat inventory): "the basket has content" ⇔ carried items exceed the base
        // capacity (same threshold as is_wearing_pannier and the panel's basket grid); the
        // FIRST carried back-container is the one shown (native kind-1/kind-2 exclusivity
        // prefers the non-empty one,:117-124 — with one flat pool the first is equivalent).
        // Wheelbarrow hand visuals (slot 0) are NOT modelled yet (needs the push pose too).
        public const int BackLinkSlot = 5;

        static bool IsBackContainer(string? className)
            => className == "Holzkiepe" || className == "Grosse_Holzkiepe";

        internal static void UpdateWornContainerVisual(DigglesTclContext ctx, DigglesTclEntity carrier)
        {
            if (carrier.Class?.Kind != "gnome") return;
            DigglesTclEntity? shown = null;
            if (carrier.Inventory.Count > ZwergBaseInvSlots)
                foreach (var h in carrier.Inventory)
                    if (ctx.Entities.TryGetValue(h, out var it) && IsBackContainer(it.Class?.Name))
                    { shown = it; break; }
            foreach (var h in carrier.Inventory)
            {
                if (!ctx.Entities.TryGetValue(h, out var it) || !IsBackContainer(it.Class?.Name)) continue;
                string hs = it.Handle.ToString(CultureInfo.InvariantCulture);
                if (it == shown)
                {
                    CmdLinkObj(ctx, new[]
                    {
                        "link_obj", hs,
                        carrier.Handle.ToString(CultureInfo.InvariantCulture),
                        BackLinkSlot.ToString(CultureInfo.InvariantCulture),
                    });
                    ApplyRenderVisible(it, true);    // render only — colliders stay off while contained
                }
                else
                {
                    if (it.LinkedToHandle == carrier.Handle)
                        CmdLinkObj(ctx, new[] { "link_obj", hs });
                    ApplyRenderVisible(it, false);
                }
            }
        }

        // ----- event_generator / evtgen_attrib (the original click→context-command layer) -----

        // The internal proc name the registered generator body is callable under.
        // ("event_generator" itself stays the REGISTRAR command, so the script's own
        // `event_generator {objpar subpar pospar txtpar} {…}` form keeps working.)
        public const string EvtGenProcName = "__event_generator";

        // event_generator {params} {body} — TCL:CTclGlobalMethods::EventGenerator
        // (FUN_0063e700): exactly 2 args; the param list must hold 4 names (error
        // "param list needs 4 names",:96-101); the BODY is stored globally
        // (DAT_0077244c+0x8488,:12) with the 4 param names (:113-199) for the UI to
        // evaluate per click/hover. Port: register the body as a normal proc under
        // EvtGenProcName — EvtGenRun invokes it with (objpar subpar pospar txtpar).
        static string CmdEventGenerator(DigglesTclContext ctx, string[] a)
        {
            if (a.Length != 3)
                throw new TclException("wrong # args: should be \"event_generator {objpar subpar pospar txtpar} body\"");
            var names = TclList.Parse(a[1]);
            if (names.Count != 4) throw new TclException("param list needs 4 names");
            ctx.Interp.RegisterProc(new TclProc
            {
                Name = EvtGenProcName,
                ParamNames = names.ToArray(),
                ParamDefaults = new string?[names.Count],
                Body = a[2],
            });
            return "";
        }

        // evtgen_attrib ?-opt value …? — TCL:CTclGlobalMethods::EvtGenAttrib
        // (FUN_0063eb70): fills the global pending-event spec (DAT_00772400+0xa134…).
        // Option strings binary-verified: -evtid -object -target -subject1/2/3 -pos1/2/3
        // -num1/2/3 -delay -text1 -text2 -desc -cursor; anything else raises
        // "unknown parameter" (:686). Values are kept as raw strings (the payload
        // round-trips through event_get unchanged).
        static string CmdEvtGenAttrib(DigglesTclContext ctx, string[] a)
        {
            var s = ctx.EvtGen;
            for (int k = 1; k + 1 < a.Length; k += 2)
            {
                string v = a[k + 1];
                switch (a[k])
                {
                    case "-evtid":    s.EvtId = v; break;
                    case "-object":   int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out s.Object); break;
                    case "-target":   s.Target = v; break;
                    case "-subject1": s.Subject1 = v; break;
                    case "-subject2": s.Subject2 = v; break;
                    case "-subject3": s.Subject3 = v; break;
                    case "-pos1":     s.Pos1 = v; break;
                    case "-pos2":     s.Pos2 = v; break;
                    case "-pos3":     s.Pos3 = v; break;
                    case "-num1":     s.Num1 = v; break;
                    case "-num2":     s.Num2 = v; break;
                    case "-num3":     s.Num3 = v; break;
                    case "-delay":    s.Delay = v; break;
                    case "-text1":    s.Text1 = v; break;
                    case "-text2":    s.Text2 = v; break;
                    case "-desc":     s.Desc = v; break;
                    case "-cursor":   s.Cursor = v; break;
                    default: throw new TclException("unknown parameter");   // FUN_0063eb70:686
                }
            }
            return "";
        }

        // Evaluate the registered event_generator with (objpar, subpar, pospar, txtpar) —
        // what the native UI does per click/hover. Resets the pending spec first (each
        // evaluation builds a fresh one). Returns the script's verb/cursor word
        // ("pickup"/"use"/"dig"/"walk"/"select"/…); "" when no generator is registered
        // (eventgen.tcl not loaded — e.g. a bare test interp).
        // objpar = the SELECTED object (0 = none), subpar = the hovered/clicked object
        // (0 = none, 1 = the world), pospar = the click point in NATIVE coords,
        // txtpar = the flag list ("-command" for a real click, plus -alternate/-placement/
        // -invclick/-multiselect/asprod… as applicable).
        public static string EvtGenRun(DigglesTclContext ctx, int objpar, int subpar,
            Vector3 pospar, string txtpar)
        {
            ctx.EvtGen.Reset();
            if (!ctx.Interp.CommandExists(EvtGenProcName)) return "";
            var inv = CultureInfo.InvariantCulture;
            string cmd = EvtGenProcName + " " + objpar.ToString(inv) + " " + subpar.ToString(inv)
                + " {" + pospar.x.ToString(inv) + " " + pospar.y.ToString(inv) + " " + pospar.z.ToString(inv) + "}"
                + " {" + txtpar + "}";
            return ctx.Interp.Eval(cmd);
        }

        // Fire the pending evtgen spec: raise EvtId on the spec's -object (the generator
        // always sets it via `evtgen_attrib -object $objpar`, eventgen.tcl:43; fallbackObj
        // covers a script that didn't). Payload keys mirror set_event's flag convention so
        // the unchanged z_events.tcl handlers read them via `event_get this -subject1` etc.
        // Returns true when an event was enqueued.
        public static bool EvtGenFire(DigglesTclContext ctx, int fallbackObj)
        {
            var s = ctx.EvtGen;
            if (string.IsNullOrEmpty(s.EvtId)) return false;
            int h = s.Object != 0 ? s.Object : fallbackObj;
            if (!ctx.Entities.TryGetValue(h, out var ent)) return false;
            var payload = new Dictionary<string, string>();
            void P(string k, string v) { if (!string.IsNullOrEmpty(v)) payload[k] = v; }
            P("-target", s.Target);
            P("-subject1", s.Subject1); P("-subject2", s.Subject2); P("-subject3", s.Subject3);
            P("-pos1", s.Pos1); P("-pos2", s.Pos2); P("-pos3", s.Pos3);
            P("-num1", s.Num1); P("-num2", s.Num2); P("-num3", s.Num3);
            P("-text1", s.Text1); P("-text2", s.Text2);
            ent.EnqueueEvent(new EntityEvent(s.EvtId, payload), ctx.Sim?.GameTime ?? 0d);
            return true;
        }

        // ----- fog-of-war helpers -----

        static int ArgInt(string[] a, int idx, int def) =>
            idx < a.Length && int.TryParse(a[idx], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : def;
        static double ArgNum(string[] a, int idx, double def) =>
            idx < a.Length && double.TryParse(a[idx], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;

        // CObj::GetMaxFow (FUN_005cb3a0): follow the carrier chain (a carried item inherits the
        // carrier's visibility, native obj+0x278), then sample the object's tile rect (7 points,
        // FUN_00580ea0) with the per-player cache (obj+0x6c, invalidated every fog update via the
        // version stamp). The port's rect is the single occupied tile — the native expands by the
        // model link box, so multi-tile buildings sample slightly wider; deviation noted in
        // Docs/FOG_OF_WAR_RE.md.
        internal static int GetMaxFow(DigglesTclContext ctx, DigglesTclEntity e, int player)
        {
            var fog = ctx.FogOfWar;
            if (fog == null) return 50;
            if (player < 0) return 0xFF;
            if (player >= Diggles.Runtime.World.FogOfWarState.Players) return 0;
            for (int guard = 0; e.ContainedIn >= 0 && guard < 8
                 && ctx.Entities.TryGetValue(e.ContainedIn, out var carrier); guard++)
                e = carrier;
            e.FowCache ??= new int[Diggles.Runtime.World.FogOfWarState.Players];
            if (e.FowCacheVersion != fog.CacheVersion)
            {
                for (int i = 0; i < e.FowCache.Length; i++) e.FowCache[i] = -1;
                e.FowCacheVersion = fog.CacheVersion;
            }
            if (e.FowCache[player] >= 0) return e.FowCache[player];
            // A LINKED rider (hat / held tool — link_obj re-parents its GameObject under the
            // carrier's Model) has a stale logical Position (the sim's 2c sync covers only
            // root-parented entities): sample its LIVE transform instead — the native
            // equivalent of following the obj+0x278 carrier chain.
            int tx, ty;
            var go = e.GameObject;
            var root = ctx.SpawnParent;
            if (go != null && root != null && go.transform.parent != root)
            {
                var nat = Diggles.Runtime.RuntimeWorldSpawner.LocalToNative(
                    root.InverseTransformPoint(go.transform.position));
                tx = (int)nat.x; ty = (int)nat.y;
            }
            else
            {
                tx = (int)e.Position.x; ty = (int)e.Position.y;
            }
            // The native samples 7 points over the object's tile rect EXPANDED by its model
            // link box (FUN_005cb250 → FUN_00580ea0). The port derives the link box from the
            // renderer-bounds union, once per entity (extents are model-sized, position-free);
            // floor ±1 tile. A pivot-only probe made wall/floor-mounted deco lag the reveal
            // front and pop in mid-fade, and a cave-spanning vine stayed hidden until the
            // gnome reached its middle.
            if (e.FowExtX < 0f && go != null && root != null)
            {
                var rends = go.GetComponentsInChildren<UnityEngine.Renderer>(true);
                if (rends.Length > 0)
                {
                    var b = rends[0].bounds;
                    for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
                    var nmin = Diggles.Runtime.RuntimeWorldSpawner.LocalToNative(root.InverseTransformPoint(b.min));
                    var nmax = Diggles.Runtime.RuntimeWorldSpawner.LocalToNative(root.InverseTransformPoint(b.max));
                    e.FowExtX = System.Math.Max(1f, System.Math.Abs(nmax.x - nmin.x) * 0.5f);
                    e.FowExtY = System.Math.Max(1f, System.Math.Abs(nmax.y - nmin.y) * 0.5f);
                }
            }
            int ex = e.FowExtX < 0f ? 1 : (int)e.FowExtX;
            int ey = e.FowExtY < 0f ? 1 : (int)e.FowExtY;
            int v = fog.SampleMaxFow(tx - ex, ty - ey, tx + ex, ty + ey, player);
            e.FowCache[player] = v;
            return v;
        }

        internal static int GetMaxFowAllPlayers(DigglesTclContext ctx, DigglesTclEntity? e)
        {
            if (ctx.FogOfWar == null || e == null) return 50;
            int max = 0;
            for (int p = 0; p < Diggles.Runtime.World.FogOfWarState.Players; p++)
                max = System.Math.Max(max, GetMaxFow(ctx, e, p));
            return max;
        }

        // ----- tunnel torch helpers (Docs/TUNNEL_TORCH_LIGHTING_RE.md) -----

        // One manager pass per 10 Hz think tick (native: the CObjMgr frame pass
        // FUN_005d4250:120-303 — cadence deviation documented in the RE doc §8.1).
        // Glow drops: every DropPeriodTicks-th tick per Autolight entity owned by the
        // LOCAL player (FUN_005cbf70:81-107 — owner gate + the 16-tick stagger).
        public static void TunnelTorchTick(DigglesTclContext ctx)
        {
            var tts = ctx.TunnelTorches;
            if (tts == null) return;
            if (!ctx.Classes.ContainsKey(Diggles.Runtime.World.TunnelTorchSystem.TorchClass))
                return;                                   // native class gate (005d4250:130-138)
            try
            {
                tts.TickCounter++;
                foreach (var e in ctx.Entities.Values)
                {
                    if (e.Owner != ctx.LocalPlayer) continue;         // local-player gate (005cbf70:84-88)
                    if (e.ContainedIn >= 0) continue;                 // packed/carried never drops
                    if ((e.Handle + tts.TickCounter)
                        % Diggles.Runtime.World.TunnelTorchSystem.DropPeriodTicks != 0) continue;
                    // Autolight = obj+0x3d0 bit 0x20 (set_autolight — zwerg.tcl:156 / baby.tcl:130
                    // opt IN; native default is OFF). The port stores it as the `_autolight`
                    // entity prop var (RegisterEntityProp).
                    if (!e.VarsScope.Vars.TryGetValue("_autolight", out var al)
                        || !ParseTclBool(al)) continue;
                    tts.DropAttempt(e.Position.x, e.Position.y, e.Position.z);
                }
                tts.Tick();
                TunnelTorchSettle(ctx, tts);
            }
            catch (System.Exception ex) { Debug.LogError($"[torch] tick failed: {ex.Message}"); }
        }

        // The class_snaptowall LIVE z-settle (obj flag 0x20000 — ctor FUN_005c7b70:80-82 from
        // classdef+0x2c bit 8; law FUN_005cbf70:125-176): every 16th tick (handle-staggered,
        // (obj+0xdc ^ tick) & 0xf == 0) a snap-to-wall object's z re-snaps to
        // min(fill over the ±¼-tile cross) + 0.01 — the exposed wall FACE. x/y never move.
        // This both places a fresh torch ON the wall and makes it TRACK the wall when a later
        // dig (cave over the tunnel) recedes it (in caves, torches should sit deeper into
        // the walls). Only Misc_Light carries the flag (emitter.tcl:10).
        static void TunnelTorchSettle(DigglesTclContext ctx, Diggles.Runtime.World.TunnelTorchSystem tts)
        {
            foreach (var e in ctx.Entities.Values)
            {
                if (e.Class is not { SnapToWall: true }) continue;
                if (((e.Handle ^ tts.TickCounter) & (Diggles.Runtime.World.TunnelTorchSystem.SettlePeriod - 1)) != 0)
                    continue;
                float z = tts.SettleZAt(e.Position.x, e.Position.y);
                if (float.IsNaN(z) || System.Math.Abs(z - e.Position.z) < 1e-4f) continue;
                e.Position = new Vector3(e.Position.x, e.Position.y, z);
                if (e.GameObject != null && ctx.EntitySpawner != null)
                    ctx.EntitySpawner.ApplyPosition(e.GameObject, e.Position);
            }
        }

        // FUN_00590e60:122-165 — any live light source (class_light entities: LightSrc markers,
        // Met lights, earlier torches) with distSq(x,y) < radius + 4? The lopsided units are the
        // literal native comparison (torch r=6 ⇒ min spacing ≈ √10 ≈ 3.2 tiles).
        public static bool AnyLightNear(DigglesTclContext ctx, float x, float y)
        {
            foreach (var e in ctx.Entities.Values)
            {
                var spec = e.Class?.Light;
                if (spec == null || spec.Range <= 0f) continue;
                float dx = e.Position.x - x, dy = e.Position.y - y;
                if (dx * dx + dy * dy < spec.Range + Diggles.Runtime.World.TunnelTorchSystem.SpacingAdd)
                    return true;
            }
            return false;
        }

        // Spawn one matured glow entry as a REAL Misc_Light through the normal `new` pipeline
        // (model fackel.standard + class_light + flame particle all ride existing systems —
        // native FUN_005d4250:186-265 via FUN_00601df0). Flags per the native post-spawn
        // scrub (obj+0x3d0 &= ~0x690: viewinfog OFF — the auto torch is NOT a gray-fog ghost)
        // + fow owner -1 (+0x36c) + non-interactive.
        public static void TunnelTorchSpawn(DigglesTclContext ctx, float x, float y, float z)
        {
            if (!ctx.Classes.ContainsKey(Diggles.Runtime.World.TunnelTorchSystem.TorchClass)) return;
            try
            {
                // Spawn z = the wall FACE via the snaptowall settle law (min fill over the
                // ±¼-tile cross + 0.01, FUN_005cbf70:136-160). The native lets the 16-tick
                // settle correct whatever z the glow carried; the port applies it AT SPAWN so
                // the torch never pops (and TunnelTorchSettle keeps tracking later digs).
                // x/y stay put — the native has no side-wall snap (an earlier port-side X-hug
                // was wrong: torches belong on the BACK wall face).
                if (ctx.TunnelTorches is { } sys)
                {
                    float sz = sys.SettleZAt(x, y);
                    if (!float.IsNaN(sz)) z = sz;
                }
                var h = CmdNew(ctx, new[]
                {
                    "new", Diggles.Runtime.World.TunnelTorchSystem.TorchClass, "",
                    FormatVec3(new Vector3(x, y, z)), "0 0 0",
                });
                if (int.TryParse(h.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var hh)
                    && ctx.Entities.TryGetValue(hh, out var e))
                {
                    e.Owner = -1;                                  // +0x36c = -1 (no fow lane)
                    e.VarsScope.Vars["_selectable"] = "0";
                    e.VarsScope.Vars["_hoverable"] = "0";
                    e.VarsScope.Vars["_viewinfog"] = "0";          // native clears bit 0x80
                }
            }
            catch (System.Exception ex) { Debug.LogError($"[torch] spawn failed: {ex.Message}"); }
        }

        // ----- visibility (render flag) helpers -----

        // Save-restore write-back for the partner_info family store (the _partner_* pseudo-vars this
        // engine's partner_info command reads/writes; native CFamily block obj+0x3F0, B2 §2). The
        // {id,state} tri-state is also stored on the live entity for canonical capture. A link
        // materializes its handle var ONLY when it is state 1 (linked/alive). A never-set
        // (state 0) or deceased (state 2) link leaves no var, so partner_info get{partner,mother,
        // father} returns "" exactly as it does for a dead/never link — the port never fabricates a
        // deceased marker of its own. Dangling ids are tolerated: the handle is stored raw and the
        // ordinary revalidation (a missing target simply resolves to no entity) absorbs them, so
        // restore never fails on a stale id (the corpus has 34 live records whose ref word was -1 at
        // save time and 12 whose id is undeclared).
        internal static void RestoreFamilyLinks(
            DigglesTclEntity entity,
            int partnerId, int partnerState,
            int motherId, int motherState,
            int fatherId, int fatherState,
            IReadOnlyList<string> childrenNames,
            bool pregnancyFlag)
        {
            if (entity == null) throw new System.ArgumentNullException(nameof(entity));
            if (childrenNames == null) throw new System.ArgumentNullException(nameof(childrenNames));

            void Materialize(string role, int id, int state)
            {
                if (state == 1)
                    entity.VarsScope.Vars["_partner_" + role] =
                        id.ToString(CultureInfo.InvariantCulture);
            }

            Materialize("partner", partnerId, partnerState);
            Materialize("mother", motherId, motherState);
            Materialize("father", fatherId, fatherState);
            if (childrenNames.Count > 0)
                entity.VarsScope.Vars["_partner_children"] = TclList.Format(childrenNames);
            entity.VarsScope.Vars["_partner_preg"] = pregnancyFlag ? "1" : "0";
        }

        internal static void RestoreNativeObjectFlags(DigglesTclEntity entity, int flags)
        {
            if (entity == null) throw new System.ArgumentNullException(nameof(entity));
            static string Flag(int value, int mask) => (value & mask) != 0 ? "1" : "0";

            entity.VarsScope.Vars["_visibility"] = Flag(flags, 0x00000001);
            entity.VarsScope.Vars["_lock"] = Flag(flags, 0x00000004);
            entity.VarsScope.Vars["_storable"] = Flag(flags, 0x00000008);
            entity.VarsScope.Vars["_physic"] = Flag(flags, 0x00000010);
            // +0x3d0 bit 0x20 is AUTOLIGHT. Collision is the independent 0x80000 bit written by
            // FUN_005cd750 together with pick-target mode +0x2ac.
            entity.VarsScope.Vars["_autolight"] = Flag(flags, 0x00000020);
            entity.VarsScope.Vars["_viewinfog"] = Flag(flags, 0x00000080);
            entity.VarsScope.Vars["_selectable"] = Flag(flags, 0x00000200);
            entity.VarsScope.Vars["_hoverable"] = Flag(flags, 0x00000400);
            entity.VarsScope.Vars["_boxed"] = Flag(flags, 0x00004000);
            entity.VarsScope.Vars["_collision"] = Flag(flags, 0x00080000);
            entity.VarsScope.Vars["_cloaked"] = Flag(flags, 0x08000000);
            entity.AlternateAnimDb = (flags & 0x00002000) != 0;
            entity.Gender = (flags & 0x00008000) != 0
                ? "male"
                : (flags & 0x00010000) != 0 ? "female" : "neutral";
            entity.SequenceActiveFlag = (flags & 0x00200000) != 0;
            entity.BuildupComplete = (flags & 0x00400000) != 0;
            entity.ProdAutoSchedule = (flags & 0x00800000) == 0;
            entity.ProcessActions = (flags & 0x01000000) != 0;
            entity.InStore = (flags & 0x04000000) != 0;
        }

        internal static int CaptureNativeObjectFlags(
            DigglesTclEntity entity,
            int compatibilityBits)
        {
            if (entity == null) throw new System.ArgumentNullException(nameof(entity));
            const int liveMask =
                0x00000001 | 0x00000002 | 0x00000004 | 0x00000008 |
                 0x00000010 | 0x00000020 | 0x00000080 | 0x00000200 |
                 0x00000400 | 0x00002000 | 0x00004000 | 0x00008000 |
                 0x00010000 | 0x00080000 | 0x00200000 | 0x00400000 |
                 0x00800000 | 0x01000000 | 0x04000000 | 0x08000000;
            int result = compatibilityBits & ~liveMask;

            void AddVariableFlag(string name, int mask)
            {
                if (entity.VarsScope.Vars.TryGetValue(name, out string value) &&
                    TruthyFlagVar(value))
                    result |= mask;
            }

            // Vars with an ABSENT-means-ON port convention must capture like _visibility does:
            // absent OR truthy → bit set. The old exists-and-truthy capture dropped bit 0x200/
            // 0x400 from every fresh entity (the port never materializes "_selectable" unless a
            // script calls set_selectable 0), so restored worlds had every dwarf explicitly
            // UNSELECTABLE (+ native selection dead in the original too) — the root cause of a
            // "band box finds 0 candidates" bug. Explicit "0" (deco/sequencer spawns) still clears.
            void AddDefaultOnFlag(string name, int mask)
            {
                if (!entity.VarsScope.Vars.TryGetValue(name, out string value) ||
                    TruthyFlagVar(value))
                    result |= mask;
            }

            AddDefaultOnFlag("_visibility", 0x00000001);
            if (entity.ContainedIn >= 0) result |= 0x00000002;
            AddVariableFlag("_lock", 0x00000004);
            AddVariableFlag("_storable", 0x00000008);
            AddVariableFlag("_physic", 0x00000010);
            AddVariableFlag("_autolight", 0x00000020);
            // viewinfog is class-defaulted (Class.ViewInFog) with a per-object var override —
            // mirror the live render-gate law, not just the var.
            if (entity.VarsScope.Vars.TryGetValue("_viewinfog", out string viewInFog)
                ? TruthyFlagVar(viewInFog)
                : entity.Class?.ViewInFog == true)
                result |= 0x00000080;
            // _selectable (0x200, FUN_00680a20) / _hoverable (0x400) native defaults are
            // CLASS-SHAPED, not blanket-on. The native begining_save law (per-class flag
            // histogram): selectable = commandable units ONLY (native types 1 gnome/11 baby);
            // hoverable = every interactive type (> 0 and ≠ 12 info) — deco/light/info carry
            // neither. A blanket absent→ON hover put outlines on every decoration in the
            // original. Explicit vars still win both ways.
            if (entity.VarsScope.Vars.TryGetValue("_selectable", out string selectableVar)
                ? TruthyFlagVar(selectableVar)
                : entity.NativeObjectType is 1 or 11)
                result |= 0x00000200;
            if (entity.VarsScope.Vars.TryGetValue("_hoverable", out string hoverableVar)
                ? TruthyFlagVar(hoverableVar)
                : entity.NativeObjectType > 0 && entity.NativeObjectType != 12)
                result |= 0x00000400;
            if (entity.AlternateAnimDb) result |= 0x00002000;
            AddVariableFlag("_boxed", 0x00004000);
            AddVariableFlag("_collision", 0x00080000);
            if (string.Equals(entity.Gender, "male", System.StringComparison.Ordinal))
                result |= 0x00008000;
            else if (string.Equals(entity.Gender, "female", System.StringComparison.Ordinal))
                result |= 0x00010000;
            if (entity.SequenceActiveFlag) result |= 0x00200000;
            if (entity.BuildupComplete) result |= 0x00400000;
            if (!entity.ProdAutoSchedule) result |= 0x00800000;
            if (entity.ProcessActions) result |= 0x01000000;
            if (entity.InStore) result |= 0x04000000;
            AddVariableFlag("_cloaked", 0x08000000);
            return result;
        }

        // The +0x3d0 bit-0 visible flag, port-side: "_visibility" var = the flag (read by
        // get_visibility + obj_query's 'visible' word), renderers = the visual effect.
        // GameObject ACTIVE state is NOT touched — containment and visibility are
        // independent in the original (see the set_visibility registration comment).
        static void ApplyRenderVisible(DigglesTclEntity e, bool visible)
        {
            // Native SetVisibility (FUN_0067cca0:79-94): every REAL visibility flip stamps the
            // render-dirty latch obj+0x3B4 = 1 (never cleared) — the N35 save byte. This is
            // why aging mushrooms/hamsters carry 1 in native saves while never-toggled dwarves
            // carry 0 (byte-verified 232/103 split on save_begining_save).
            bool wasVisible = !e.VarsScope.Vars.TryGetValue("_visibility", out string previous)
                || TruthyFlagVar(previous);
            if (wasVisible != visible) e.RenderDirtyLatch = true;
            e.VarsScope.Vars["_visibility"] = visible ? "1" : "0";
            SyncRenderVisible(e);
        }

        // Fog-of-war hide half (native: objects get no render job unless GetMaxFow(local) ≥ 0x80,
        // FUN_004845a0/00580ea0). Composes with the script _visibility flag — neither side
        // clobbers the other. Called from the sim's fog pass.
        internal static void ApplyFogVisible(DigglesTclEntity e, bool fogHidden)
        {
            if (e.FowHidden == fogHidden) return;
            e.FowHidden = fogHidden;
            SyncRenderVisible(e);
        }

        internal static void SyncRenderVisible(DigglesTclEntity e)
        {
            RefreshBuildingClanFlag(e);
            bool visible = !e.FowHidden
                && (!e.VarsScope.Vars.TryGetValue("_visibility", out var v) || TruthyFlagVar(v));
            var go = e.GameObject;
            if (go == null) return;
            foreach (var r in go.GetComponentsInChildren<UnityEngine.Renderer>(true))
                r.enabled = visible;
        }

        // Containment's physics/click-world side: a packed item leaves the spatial world
        // (native InvAdd → FUN_005ebc90/FUN_005eb470 world-grid removal), so its colliders
        // must not catch rays. Kept SEPARATE from the render flag — a shelf item is
        // visible-while-contained but the click surface is governed by containment.
        internal static void SetCollidersEnabled(DigglesTclEntity e, bool enabled)
        {
            var go = e.GameObject;
            if (go == null) return;
            foreach (var c in go.GetComponentsInChildren<UnityEngine.Collider>(true))
                c.enabled = enabled;
        }

        // Called by DigglesGameSim — public sim hook. Looks up handle_event
        // body via the class chain and runs it with CurrentEvent set so
        // event_get works inside the handler.
        public static void DispatchEvent(DigglesTclContext ctx,
            DigglesTclEntity ent, EntityEvent evt)
        {
            if (ent.Class == null) return;
            var code = ctx.FindEventHandler(ent.Class, evt.Name);
            if (code == null) return;
            var prev = ctx.CurrentEvent;
            ctx.CurrentEvent = evt;
            try { RunInEntityContext(ctx, ent, code, "event " + evt.Name); }
            finally { ctx.CurrentEvent = prev; }
        }

        // Called by DigglesGameSim — the CObj::HandleStates (005cac90) think step for one entity:
        // on the FIRST think of a freshly-triggered state run its state_enter (:56-80), mark it
        // LIVE (:81 `+0x200 = +0x204`), then run the per-tick state BODY (:82-104). state_leave
        // runs in CmdStateTrigger, gated on the state having gone live (:30-55). A state set and
        // replaced between thinks (transient) therefore runs NEITHER enter nor leave — exactly
        // the native law (StateTrigger FUN_0069ec80:109 only writes the target index).
        public static void TickEntityState(DigglesTclContext ctx,
            DigglesTclEntity ent)
        {
            if (ent.Class == null || ent.CurrentState == null) return;
            if (!ent.StateWentLive)
            {
                string entering = ent.CurrentState;
                var enterCode = TryFindStateEnter(ctx, ent.Class, entering);
                if (enterCode != null) RunInEntityContext(ctx, ent, enterCode, "state_enter " + entering);
                // The enter itself may state_trigger elsewhere (CmdStateTrigger reset the flag
                // for the new target): bail — the NEW state goes live (and runs its enter) on the
                // next think. (Native nuance: 005cac90:79 re-reads +0x204 after the enter and
                // makes THAT live without running its enter — shipped data never enters-and-
                // retriggers, so the port prefers the paired behaviour.)
                if (!string.Equals(ent.CurrentState, entering, System.StringComparison.Ordinal)) return;
                ent.StateWentLive = true;   // live from here on: leaving now runs state_leave
                // Native 005cac90:81 `+0x200 = +0x204` — the live-state table index commits here.
                ent.LiveStateIndex = ent.Class.StateIndexOf(ent.CurrentState);
                ent.StateWordsDirty = true;
            }
            var code = ctx.FindState(ent.Class, ent.CurrentState);
            if (code == null) return;
            RunInEntityContext(ctx, ent, code, "state " + ent.CurrentState);
        }
    }
}
