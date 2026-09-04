#nullable enable

namespace Diggles.Runtime.Tcl.Engine
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using Diggles.Runtime.Tcl;

    // ===================================================================================
    // The native trigger engine — faithful port of wiggles/Wiggles/Objects/Trigger.cpp
    // (source attribution: Diggles.source_attribution.tsv).
    //
    // • `trigger <verb> <obj> …` Tcl command = FUN_0060e920 (the dispatcher).
    // • per-logic-tick scan = FUN_0060df40, called from
    // CGameThread::SrvDoThink (006b4a30:391) — i.e. once per 10 Hz logic tick; each
    // record additionally self-gates on its own checktimer (FUN_0060df40:49).
    // The whole scan is suppressed in sequence mode: FUN_0060df40:24 returns unless
    // the set_sequence byte DAT_00772400+0x8c is clear.
    // • record constructor / defaults = FUN_0060da90 (constants extracted from
    // Diggles.exe.data — see each field).
    // • firing = FUN_0060db80: evaluate the record's fire
    // script (rec+0x10, the 5th `create` word) in the owner object's own Tcl interp
    // (FUN_005c9cc0 interp lookup + FUN_00555a00 eval). There is no event raise — the
    // consumers' scripts (`trigger create this any_object "sequencer_activate"`,
    // Trigger_Urwald.tcl:105/925-940) run their handler proc directly.
    // • actuators (rec+0x68) = Tcl list of the object ids that tripped the trigger,
    // formatted by itoa(obj+0xdc) (FUN_005caf90) — `trigger get_actuators this`
    // (sequencer.tcl:397 add_actor loop, z_work_prod.tcl:1437 guard attack).
    //
    // Trigger kinds (rec+0x04; create's 4th word, FUN_0060e920:127-145):
    // 1 single_timer fires once when gametime ≥ deadline (FUN_0060df40:58-63)
    // 2 repeat_timer pulses: activates+fires each period, deactivates on the next
    // check inside the new period (FUN_0060df40:65-80)
    // 3 any_object proximity: any filter-matched object within range of the owner
    // (FUN_0060e410)
    // 4 unique_object proximity to one specific object (set_target_ref, FUN_0060e780)
    // 5 callback boolean Tcl condition polled per check (FUN_0060df40:88-130)
    // 0 dead reaped by the next scan pass (FUN_0060df40:132-143)
    //
    // Execution modes (rec+0x08; set_execution, FUN_0060e920:235-375 — string table with
    // the native's own "when_inavtive" typo at:346, preserved verbatim):
    // 1 never state toggles but nothing ever executes
    // 2 on_activate fire script runs on the inactive→active edge (ctor default)
    // 3 on_inactivate fire script runs on the active→inactive edge
    // 4 when_active fire script runs every check while active (FUN_0060df40:168-187)
    // 5 when_inavtive fire script runs every check while inactive (FUN_0060df40:190-208)
    // 6 always fire script runs every check (FUN_0060df40:151-167;
    // string "always" = DAT_00722d14, exe-verified)
    // ===================================================================================

    public enum TriggerKind
    {
        Dead = 0,          // reaped by the scan's default case (FUN_0060df40:132-143)
        SingleTimer = 1,   // "single_timer" (FUN_0060e920:127-128)
        RepeatTimer = 2,   // "repeat_timer" (:129-131)
        AnyObject = 3,     // "any_object" (:133-135)
        UniqueObject = 4,  // "unique_object" (:137-139)
        Callback = 5,      // "callback" (:141-143)
    }

    // One trigger record — native layout in comments (record built by FUN_0060da90,
    // serialized by FUN_0060f7b0). All time fields are game seconds (DAT_00772400+0xa088).
    [System.Serializable]
    public sealed class TriggerRecord
    {
        public int Index;                       // +0x00 — slot index (create returns it)
        public TriggerKind Kind;                // +0x04
        public int Execution = 2;               // +0x08 — ctor default 2 = on_activate (FUN_0060da90:22)
        public int OwnerHandle = -1;            // +0x0c — owner object id (create: obj+0xdc, FUN_0060e920:199)
        public string FireScript = "";          // +0x10 — create's 5th word; evaluated by FUN_0060db80
        public double LastCheck;                // +0x18 — ctor: now − 4.9 (DAT_007373a0, exe-verified
                                                // 4.9000001) so the first check is due at once
        public double CheckTimer = 3.0;         // +0x20 — current poll interval (DAT_00737910 = 3.0)
        public double CheckIdle = 3.0;          // +0x28 — poll interval with no candidates (DAT_00737918 = 3.0)
        public double CheckNear = 0.5;          // +0x30 — poll interval with candidates/near (DAT_00737230 = 0.5)
        public bool Active;                     // +0x38 — the fired/condition state (ctor 0)
        public float TargetRange = 5f;          // +0x3c — ctor 0x40a00000 = 5.0 ("Umkreis standard (= 5 Zm)",
                                                // Trigger_Urwald.tcl:160)
        public double FireTime;                 // +0x40 — timer deadline (ctor 0)
        public double Interval = 1.0;           // +0x48 — repeat period (ctor DAT_007372a8 = 1.0)
        public int TargetRef = -1;              // +0x50 — unique_object / callback target (ctor −1)
        public string? TargetClass;             // +0x54 — native stores the class ID (FUN_0060e920:580);
                                                // the port keeps the name, matched inheritance-aware
        public List<string>? TargetTypes;       // +0x58 — native ORs 1<<typeId per word (FUN_0060e920:496-547);
                                                // the port matches DigglesClass.Kind words directly
        public string? TargetOwnerSpec;         // +0x5c — raw owner spec, re-parsed each scan (set_target_owner
                                                // stores the Tcl obj — diplomacy masks change at runtime)
        public int TargetCount;                 // +0x60 — candidate-count gate (ctor 0)
        public string CallbackScript = "";      // +0x64 — the condition script (kind=callback only)
        public List<string> Actuators = new();  // +0x68 — object-id strings of the objects that tripped it
    }

    // The trigger registry + scanner. One per DigglesTclContext (created in
    // DigglesTclEngine.Register); Scan is called by DigglesGameSim.Step on the
    // 10 Hz logic tick — the port's CGameThread::SrvDoThink equivalent.
    public sealed class TriggerService
    {
        readonly DigglesTclContext _ctx;
        // Sparse slot array — create reuses the first null slot, else appends
        // (FUN_0060e920:155-181); the scan truncates trailing nulls (FUN_0060df40:216-241).
        readonly List<TriggerRecord?> _records = new();
        // obj+0x2a4 mirror: each object owns at most one trigger (piVar4[0xa9]); `create`
        // on an object that already has one deletes the old record first (FUN_0060e920:111-113).
        readonly Dictionary<int, int> _byOwner = new();

        // ---- intro-compatibility guard (port-only, no native counterpart) ----
        // One-shot: when DigglesWorldRuntime's staged intro fallback fires PlayCutscene
        // itself, any real trigger staged on the same sequencescript must be consumed —
        // whether it is already armed (SuppressSequence sweep) or gets armed later (its
        // arming evt_timer0 may sit queued behind the cutscene's event freeze). Checked
        // at fire time; cleared once consumed.
        public string? SuppressedSequence;

        public TriggerService(DigglesTclContext ctx) => _ctx = ctx;

        double Now => _ctx.Sim?.GameTime ?? 0.0;

        public int LiveCount
        {
            get { int n = 0; foreach (var r in _records) if (r != null && r.Kind != TriggerKind.Dead) n++; return n; }
        }

        public TriggerRecord?[] CaptureRecords()
        {
            var result = new TriggerRecord?[_records.Count];
            for (int i = 0; i < _records.Count; i++)
                result[i] = CloneRecord(_records[i]);
            return result;
        }

        public void RestoreRecords(IEnumerable<TriggerRecord?> records)
        {
            ValidateRecords(records);
            _records.Clear();
            _byOwner.Clear();
            foreach (var source in records)
            {
                var record = CloneRecord(source);
                int index = _records.Count;
                if (record != null)
                {
                    record.Index = index;
                    if (record.OwnerHandle >= 0) _byOwner[record.OwnerHandle] = index;
                }
                _records.Add(record);
            }
        }

        public static void ValidateRecords(IEnumerable<TriggerRecord?>? records)
        {
            if (records == null) throw new ArgumentNullException(nameof(records));
            var owners = new HashSet<int>();
            int index = 0;
            foreach (TriggerRecord? record in records)
            {
                if (record == null)
                {
                    index++;
                    continue;
                }
                if (record.Index != index)
                    throw new ArgumentException("A trigger record has an invalid sparse-slot index.", nameof(records));
                if (!Enum.IsDefined(typeof(TriggerKind), record.Kind))
                    throw new ArgumentException("A trigger record has an invalid kind.", nameof(records));
                if (record.Execution is < 1 or > 6 || record.TargetCount < 0)
                    throw new ArgumentException("A trigger record has invalid execution state.", nameof(records));
                if (record.OwnerHandle < -1 || record.TargetRef < -1)
                    throw new ArgumentException("A trigger record has an invalid object handle.", nameof(records));
                if (record.OwnerHandle >= 0 && !owners.Add(record.OwnerHandle))
                    throw new ArgumentException("An object owns more than one trigger record.", nameof(records));
                if (record.FireScript == null || record.CallbackScript == null || record.Actuators == null)
                    throw new ArgumentException("A trigger record contains null script state.", nameof(records));
                if (record.TargetTypes != null)
                {
                    foreach (string? targetType in record.TargetTypes)
                        if (targetType == null)
                            throw new ArgumentException("A trigger target type is null.", nameof(records));
                }
                foreach (string? actuator in record.Actuators)
                    if (actuator == null)
                        throw new ArgumentException("A trigger actuator is null.", nameof(records));
                if (!Finite(record.LastCheck) || !Finite(record.CheckTimer) ||
                    !Finite(record.CheckIdle) || !Finite(record.CheckNear) ||
                    !Finite(record.FireTime) || !Finite(record.Interval) ||
                    !Finite(record.TargetRange) || record.CheckTimer < 0d ||
                    record.CheckIdle < 0d || record.CheckNear < 0d ||
                    record.Interval < 0d || record.TargetRange < 0f)
                {
                    throw new ArgumentException("A trigger record has invalid timing or range state.", nameof(records));
                }
                index++;
            }
        }

        static bool Finite(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value);

        static bool Finite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);

        static TriggerRecord? CloneRecord(TriggerRecord? source)
        {
            if (source == null) return null;
            return new TriggerRecord
            {
                Index = source.Index,
                Kind = source.Kind,
                Execution = source.Execution,
                OwnerHandle = source.OwnerHandle,
                FireScript = source.FireScript,
                LastCheck = source.LastCheck,
                CheckTimer = source.CheckTimer,
                CheckIdle = source.CheckIdle,
                CheckNear = source.CheckNear,
                Active = source.Active,
                TargetRange = source.TargetRange,
                FireTime = source.FireTime,
                Interval = source.Interval,
                TargetRef = source.TargetRef,
                TargetClass = source.TargetClass,
                TargetTypes = source.TargetTypes != null ? new List<string>(source.TargetTypes) : null,
                TargetOwnerSpec = source.TargetOwnerSpec,
                TargetCount = source.TargetCount,
                CallbackScript = source.CallbackScript,
                Actuators = new List<string>(source.Actuators),
            };
        }

        public TriggerRecord? FindByOwner(int handle)
            => _byOwner.TryGetValue(handle, out var idx) && idx >= 0 && idx < _records.Count
               ? _records[idx] : null;

        // =====================================================================
        // The `trigger` Tcl command — FUN_0060e920. argv: [trigger, verb, obj, …].
        // Verbs are matched case-insensitively (__stricmp,:108 etc.); the
        // set_execution mode strings are matched exactly (inlined byte compares,
        //:235-365). Errors mirror the native messages verbatim.
        // =====================================================================
        public string Command(string[] a)
        {
            if (a.Length < 3) throw new TclException("wrong # args");   // Obj_GetObjSafe(argv[2]) (:54)
            string verb = a[1];
            var owner = _ctx.ResolveEntity(a[2]);

            if (Eq(verb, "create"))
            {
                if (a.Length != 5) throw new TclException("wrong # args");   // param_3 == 5 (:110)
                // An object owns one trigger — re-create deletes the old record (:111-113).
                if (_byOwner.TryGetValue(owner.Handle, out var old)) RemoveRecord(old);
                TriggerKind kind;                                            //:127-145
                if (Eq(a[3], "single_timer")) kind = TriggerKind.SingleTimer;
                else if (Eq(a[3], "repeat_timer")) kind = TriggerKind.RepeatTimer;
                else if (Eq(a[3], "any_object")) kind = TriggerKind.AnyObject;
                else if (Eq(a[3], "unique_object")) kind = TriggerKind.UniqueObject;
                else if (Eq(a[3], "callback")) kind = TriggerKind.Callback;
                else throw new TclException("invalid trigger type");         //:151
                int slot = AllocSlot();                                      //:155-181 first free slot
                var rec = new TriggerRecord                                  // ctor FUN_0060da90
                {
                    Index = slot,
                    Kind = kind,
                    OwnerHandle = owner.Handle,                              //:199 (obj+0xdc)
                    FireScript = a[4],                                       //:201-202 (rec+0x10)
                    LastCheck = Now - 4.9,                                   // FUN_0060da90:43-45 (DAT_007373a0)
                };
                _records[slot] = rec;
                _byOwner[owner.Handle] = slot;                               //:197 (obj+0x2a4)
                return slot.ToString(CultureInfo.InvariantCulture);          //:203-206 Tcl_NewIntObj(index)
            }

            if (Eq(verb, "delete"))                                          //:215-221 (no argc check)
            {
                if (_byOwner.TryGetValue(owner.Handle, out var idx)) RemoveRecord(idx);
                return "";
            }

            // Every remaining verb operates on the object's existing trigger (:415-419).
            TriggerRecord Rec()
            {
                var r = FindByOwner(owner.Handle);
                if (r == null) throw new TclException("object has no trigger");
                return r;
            }

            if (Eq(verb, "set_execution"))                                   //:224-379
            {
                Need(a, 4);
                var r = Rec();
                int mode = a[3] switch                                       // exact compares:235-365
                {
                    "on_activate" => 2,                                      //:235-258
                    "on_inactivate" => 3,                                    //:254-278
                    "never" => 1,                                            //:280-300
                    "always" => 6,                                           //:302-322 (DAT_00722d14, exe-verified)
                    "when_active" => 4,                                      //:324-345
                    "when_inavtive" => 5,                                    //:346-366 — the native's own typo;
                                                                             // "when_inactive" errors below, like the exe
                    _ => 0,
                };
                if (mode == 0) throw new TclException("invalid trigger execution");   //:367-374
                r.Execution = mode;                                          //:376-378 (rec+0x08)
                return "";
            }

            if (Eq(verb, "set_timer"))                                       //:384-409
            {
                Need(a, 4);
                var r = Rec();
                double t = Dbl(a[3]);
                // checktimer is clamped to ≤ 1.0 s (DAT_007372a8, exe-verified) so a timer
                // deadline is never missed by more than a second (:389-394), and the idle
                // interval follows it (:395-398, rec+0x28 ← rec+0x20).
                if (r.CheckTimer > 1.0) r.CheckTimer = 1.0;
                r.CheckIdle = r.CheckTimer;
                r.FireTime = Now + t;                                        //:399-403 (rec+0x40 = arg + gametime)
                r.Interval = t;                                              //:404-408 (rec+0x48; the arg is parsed a
                                                                             // second time — the ctor default 1.0 proves
                                                                             // +0x48 is a duration, not a timestamp)
                return "";
            }

            if (Eq(verb, "set_checktimer"))                                  //:428-447
            { Need(a, 4); Rec().CheckTimer = Dbl(a[3]); return ""; }

            if (Eq(verb, "set_target_range"))                                //:450-462 (rec+0x3c, float)
            { Need(a, 4); Rec().TargetRange = (float)Dbl(a[3]); return ""; }

            if (Eq(verb, "set_target_ref"))                                  //:465-493
            {
                Need(a, 4);
                var r = Rec();
                var tgt = _ctx.GetEntity(a[3]);
                if (tgt == null) throw new TclException("invalid object");   //:485
                r.TargetRef = tgt.Handle;                                    //:490 (rec+0x50 ← obj+0xdc)
                return "";
            }

            if (Eq(verb, "set_target_type"))                                 //:496-552
            {
                Need(a, 4);
                // Native: each list word → type id via FUN_005fffe0, OR'd into the +0x58 mask
                // (unknown word → "invalid type"). The port stores the words and matches them
                // against DigglesClass.Kind at scan time; no word validation (the type-id
                // table is not RE'd — scripts only pass gnome/monster, z_work_prod.tcl:1430).
                Rec().TargetTypes = TclList.Parse(a[3]);
                return "";
            }

            if (Eq(verb, "set_target_class"))                                //:555-583
            {
                Need(a, 4);
                var r = Rec();
                if (!_ctx.Classes.ContainsKey(a[3]))
                    throw new TclException("invalid class");                 //:576 (FUN_00601f90 == 0)
                r.TargetClass = a[3];                                        //:580 (rec+0x54 ← class id)
                return "";
            }

            if (Eq(verb, "set_target_count"))                                //:586-597
            { Need(a, 4); Rec().TargetCount = (int)Dbl(a[3]); return ""; }

            if (Eq(verb, "set_target_owner"))                                //:600,660-679
            {
                Need(a, 4);
                // Native validates the spec via the owner-mask parser (FUN_005e2330 → "invalid
                // owner") but stores the raw Tcl obj for per-scan re-parse (rec+0x5c) — the
                // enemy/friend masks are diplomacy-dependent. Scripts pass numerics
                // (0/-1/1/3/6), "enemy" (z_work_prod.tcl:1431) or [get_owner …].
                Rec().TargetOwnerSpec = a[3];
                return "";
            }

            if (Eq(verb, "set_callback"))                                    //:602-613 (rec+0x64)
            { Need(a, 4); Rec().CallbackScript = a[3]; return ""; }

            if (Eq(verb, "set_active"))                                      //:615-625 (rec+0x38 = 1)
            { Need(a, 3); Rec().Active = true; return ""; }

            if (Eq(verb, "set_inactive"))                                    //:627-637 (rec+0x38 = 0)
            { Need(a, 3); Rec().Active = false; return ""; }

            if (Eq(verb, "get_actuators"))                                   //:639-654 (returns rec+0x68)
            { Need(a, 3); return TclList.Format(Rec().Actuators); }

            throw new TclException("unknown sub-command");                   //:641-645
        }

        // =====================================================================
        // The per-tick scan — FUN_0060df40 (← CGameThread::SrvDoThink 006b4a30:391).
        // =====================================================================
        public void Scan()
        {
            // Sequence mode suppresses the whole scan (FUN_0060df40:24 — the set_sequence
            // byte DAT_00772400+0x8c). Checked once at entry, like the native: a fire that
            // starts a cutscene does not abort the rest of this pass.
            if (_ctx.SequenceActive) return;
            double now = Now;
            int count = _records.Count;                                      //:25 (count cached before the loop)
            for (int i = 0; i < count; i++)
            {
                var rec = _records[i];
                if (rec == null) continue;
                if (now - rec.LastCheck < rec.CheckTimer) continue;          //:49 per-record checktimer gate
                rec.LastCheck = now;                                         //:56

                switch (rec.Kind)                                            //:57
                {
                    case TriggerKind.SingleTimer:                            //:58-63
                        if (rec.FireTime <= now && !rec.Active)
                        {
                            rec.Active = true;
                            if (rec.Execution == 2) Fire(rec);
                        }
                        break;

                    case TriggerKind.RepeatTimer:                            //:65-80
                        if (now < rec.FireTime)
                        {
                            // inside the period → the pulse falls (fires on_inactivate)
                            if (rec.Active)
                            {
                                rec.Active = false;
                                if (rec.Execution == 3) Fire(rec);
                            }
                        }
                        else
                        {
                            // deadline reached → pulse rises + period advances (:74-79; the
                            // deadline is re-read after the fire — the script may set_timer)
                            if (!rec.Active)
                            {
                                rec.Active = true;
                                if (rec.Execution == 2) Fire(rec);
                            }
                            rec.FireTime += rec.Interval;
                        }
                        break;

                    case TriggerKind.AnyObject:                              //:83 → FUN_0060e410
                        EvalAnyObject(rec);
                        break;

                    case TriggerKind.UniqueObject:                           //:86 → FUN_0060e780
                        EvalUniqueObject(rec);
                        break;

                    case TriggerKind.Callback:                               //:88-130
                        EvalCallback(rec);
                        break;

                    default:                                                 //:132-143 — dead record reaped
                        RemoveRecord(i);
                        break;
                }

                // Post-switch executors — the slot is re-read (:148; the fire script may
                // have deleted/recreated the trigger, e.g. Trigger_Urwald.tcl:564
                // `trigger delete this; call_method …`).
                rec = i < _records.Count ? _records[i] : null;
                if (rec == null) continue;
                if (rec.Execution == 6) FireOrKill(rec);                     //:151-167 "always"
                if (rec.Active)
                {
                    if (rec.Execution == 4) FireOrKill(rec);                 //:168-185 "when_active"
                    if (rec.Active) continue;                                //:187
                }
                if (rec.Execution == 5) FireOrKill(rec);                     //:190-208 "when_inavtive"
            }
            // Trailing compaction (:216-241): truncate the tail of null slots.
            int last = _records.Count;
            while (last > 0 && _records[last - 1] == null) last--;
            if (last < _records.Count) _records.RemoveRange(last, _records.Count - last);
        }

        // ---- any_object proximity — FUN_0060e410 ----
        void EvalAnyObject(TriggerRecord rec)
        {
            if (!_ctx.Entities.TryGetValue(rec.OwnerHandle, out var owner))
            { rec.Kind = TriggerKind.Dead; return; }                         //:46-49 owner gone → dead

            var cands = Candidates(rec, owner);
            bool anyCandidates = cands.Count > 0;                            //:58 (local_15)
            // Count gate: proceed only when the filtered population reaches target_count
            // (:62 — `if (rec+0x60 <= count)`); firing then needs ≥1 candidate in range.
            if (rec.TargetCount <= cands.Count)
            {
                rec.Actuators = new List<string>();                          //:63-79 list replaced
                bool found = false;
                float r2 = rec.TargetRange * rec.TargetRange;
                foreach (var c in cands)
                {
                    // Native-frame 3D distance: obj+0xc/0x10/0x14 floats vs the owner's
                    // (:94-98) — DigglesTclEntity.Position is exactly that triple.
                    if ((c.Position - owner.Position).sqrMagnitude <= r2)
                    {
                        rec.Actuators.Add(c.Handle.ToString(CultureInfo.InvariantCulture));  //:99-111 itoa(obj+0xdc)
                        found = true;
                    }
                }
                if (found)
                {
                    // Edge-fire; Active stays set afterwards — any_object is one-shot until
                    // a script re-arms it via set_inactive (:116-119; nothing in FUN_0060e410
                    // ever clears +0x38).
                    if (!rec.Active)
                    {
                        rec.Active = true;
                        if (rec.Execution == 2) Fire(rec);
                    }
                    return;                                                  //:120 (skips the interval switch)
                }
            }
            // Adaptive polling (:123-129): candidates exist → fast poll, none → idle poll.
            rec.CheckTimer = anyCandidates ? rec.CheckNear : rec.CheckIdle;
        }

        // ---- unique_object proximity — FUN_0060e780 ----
        void EvalUniqueObject(TriggerRecord rec)
        {
            if (!_ctx.Entities.TryGetValue(rec.OwnerHandle, out var owner))
            { rec.Kind = TriggerKind.Dead; return; }                         //:29-31 owner gone → dead
            if (!_ctx.Entities.TryGetValue(rec.TargetRef, out var target))
            { rec.Kind = TriggerKind.Dead; return; }                         // port divergence: the native derefs the
                                                                             // target slot unchecked (:39) — a vanished
                                                                             // target would crash it; we kill the record
                                                                             // (it could never fire again anyway)
            float d2 = (owner.Position - target.Position).sqrMagnitude;      //:40-44 native-frame 3D
            float r = rec.TargetRange;
            if (d2 > r * r)
            {
                // Outside: hysteresis band of +5.0 native units (DAT_007374cc, exe-verified)
                // selects the fast poll; beyond it the idle poll (:45-51).
                rec.CheckTimer = d2 <= (r + 5f) * (r + 5f) ? rec.CheckNear : rec.CheckIdle;
            }
            else
            {
                rec.Actuators = new List<string>                             //:53-78 list ← [target id]
                { target.Handle.ToString(CultureInfo.InvariantCulture) };
                if (!rec.Active)                                             //:82-85 edge-fire, stays active
                {
                    rec.Active = true;
                    if (rec.Execution == 2) Fire(rec);
                }
            }
        }

        // ---- callback condition — FUN_0060df40:88-130 ----
        void EvalCallback(TriggerRecord rec)
        {
            // Condition target: set_target_ref'd object, else the owner (:89-93).
            int h = rec.TargetRef != -1 ? rec.TargetRef : rec.OwnerHandle;
            if (!_ctx.Entities.TryGetValue(h, out var target))
            { rec.Kind = TriggerKind.Dead; return; }                         //:100-101
            if (string.IsNullOrEmpty(rec.CallbackScript)) return;            //:104-105
            // Evaluated in the target object's interp; the interp result is read as a Tcl
            // boolean (:14 — FUN_005c9cc0 + FUN_00555a00 + FUN_006baf10). A failed eval
            // leaves the state untouched (:107 `if (cVar3 != 0)`).
            bool? b = EvalBoolInEntity(target, rec.CallbackScript);
            if (b == null) return;
            if (!b.Value)
            {
                if (rec.Active)                                              //:118-123 falling edge
                {
                    rec.Active = false;
                    if (rec.Execution == 3) Fire(rec);
                }
            }
            else
            {
                if (!rec.Active)                                             //:124-127 rising edge
                {
                    rec.Active = true;
                    if (rec.Execution == 2) Fire(rec);
                }
            }
        }

        // ---- candidate filter for any_object ----
        // The native enumerates the object population through the target filters: class id
        // (rec+0x54), type mask (rec+0x58) and the owner mask re-parsed from the raw spec
        // (rec+0x5c via FUN_005e2330: numeric n → 1<<n, −1/"world" → the 0x10000 world bit,
        // enemy/friend → diplomacy-table bits). The port mirrors obj_query's matching
        // (same helpers): inheritance-aware class match, Class.Kind word match, and
        // OwnerMatches with the trigger owner's player as the diplomacy subject
        // (FUN_005e2330 reads *subject+0x36c). Contained (packed/swallowed) objects are
        // off the native spatial grid → excluded, like CmdObjQuery; the owner itself
        // never actuates its own trigger.
        List<DigglesTclEntity> Candidates(TriggerRecord rec, DigglesTclEntity owner)
        {
            var outl = new List<DigglesTclEntity>();
            foreach (var e in _ctx.Entities.Values)
            {
                if (e == owner) continue;
                if (e.ContainedIn >= 0) continue;
                if (rec.TargetClass != null
                    && (e.Class == null || !DigglesTclEngine.ClassIsA(_ctx, e.Class, rec.TargetClass))) continue;
                if (rec.TargetTypes is { Count: > 0 } && !rec.TargetTypes.Contains(e.Class?.Kind ?? "")) continue;
                if (rec.TargetOwnerSpec != null
                    && !DigglesTclEngine.OwnerMatches(_ctx, e.Owner, rec.TargetOwnerSpec, owner.Owner)) continue;
                outl.Add(e);
            }
            return outl;
        }

        // ---- firing — FUN_0060db80 ----
        // Evaluate the fire script (rec+0x10) in the owner object's Tcl scope (the native
        // resolves the owner's per-object interp, FUN_005c9cc0; our per-object scope is
        // RunInEntityContext — `this`, object globals and per-class procs all bind to the
        // owner, so `sequencer_activate` / `remove_fow` resolve like in the original).
        void Fire(TriggerRecord rec)
        {
            if (!_ctx.Entities.TryGetValue(rec.OwnerHandle, out var owner))
            { rec.Kind = TriggerKind.Dead; return; }                         // FUN_0060db80:19-21

            // Port-only intro guard (see SuppressedSequence): the staged C# intro already
            // played this sequence — consume the edge silently instead of replaying it.
            if (SuppressedSequence != null
                && owner.VarsScope.Vars.TryGetValue("sequencescript", out var seq)
                && seq == SuppressedSequence)
            {
                UnityEngine.Debug.Log($"[TriggerService] suppressed late-armed trigger for '{seq}' " +
                                      "(the staged intro fallback already played it)");
                SuppressedSequence = null;
                return;
            }
            DigglesTclEngine.RunInEntityContext(_ctx, owner, rec.FireScript, "trigger-fire");
        }

        // Post-switch executors (modes 4/5/6) share the owner-lookup + dead-marking shape
        // of the fire path (FUN_0060df40:151-167 each re-resolve objects[rec+0xc]).
        void FireOrKill(TriggerRecord rec) => Fire(rec);

        // ---- intro-compatibility sweep (port-only) ----
        // Consume every armed trigger whose owner is staged on `seq` (object-scope
        // `sequencescript` var): set Active so the on_activate edge can never fire — the
        // exact effect of the native `trigger set_active` (FUN_0060e920:615-625), which the
        // edge tests skip (+0x38 already 1). Also arms the one-shot late guard for a
        // trigger that gets created after this point. Returns how many were consumed.
        public int SuppressSequence(string seq)
        {
            int n = 0;
            foreach (var rec in _records)
            {
                if (rec == null || rec.Active || rec.Kind == TriggerKind.Dead) continue;
                if (_ctx.Entities.TryGetValue(rec.OwnerHandle, out var owner)
                    && owner.VarsScope.Vars.TryGetValue("sequencescript", out var s) && s == seq)
                {
                    rec.Active = true;
                    n++;
                }
            }
            SuppressedSequence = seq;
            return n;
        }

        // ---- helpers ----

        int AllocSlot()
        {
            for (int i = 0; i < _records.Count; i++)
                if (_records[i] == null) return i;                           // FUN_0060e920:158-167
            _records.Add(null);                                              //:169-180 append
            return _records.Count - 1;
        }

        void RemoveRecord(int idx)
        {
            if (idx < 0 || idx >= _records.Count) return;
            var rec = _records[idx];
            _records[idx] = null;
            if (rec != null && _byOwner.TryGetValue(rec.OwnerHandle, out var cur) && cur == idx)
                _byOwner.Remove(rec.OwnerHandle);                            // FUN_0060e920:217-219 (obj+0x2a4 = −1)
        }

        // Eval `code` in the entity's object scope and read the result as a Tcl boolean.
        // null = eval failed or non-boolean result (native: FUN_00555a00 returned 0 /
        // Tcl_GetBooleanFromObj failed → no state change). Same scope discipline as
        // DigglesTclEngine.RunInEntityContext, but the result is captured.
        bool? EvalBoolInEntity(DigglesTclEntity ent, string code)
        {
            var prevThis = _ctx.This;
            var prevGlobal = _ctx.Interp.ActiveGlobalOverride;
            _ctx.This = ent;
            _ctx.Interp.Scopes.Add(ent.VarsScope);
            _ctx.Interp.ActiveGlobalOverride = ent.VarsScope;
            string? res;
            try { res = _ctx.Interp.Eval(code); }
            catch (TclReturnException r) { res = r.Value; }                  // bare `return 1` in a condition body
            catch (System.Exception) { res = null; }
            finally
            {
                _ctx.Interp.ActiveGlobalOverride = prevGlobal;
                _ctx.Interp.Scopes.RemoveAt(_ctx.Interp.Scopes.Count - 1);
                _ctx.This = prevThis;
            }
            if (res == null) return null;
            res = res.Trim();
            switch (res.ToLowerInvariant())
            {
                case "true": case "yes": case "on": return true;
                case "false": case "no": case "off": case "": return false;
            }
            if (double.TryParse(res, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                return d != 0.0;
            return null;
        }

        static bool Eq(string s, string t) => string.Equals(s, t, System.StringComparison.OrdinalIgnoreCase);

        static void Need(string[] a, int argc)
        {
            if (a.Length != argc) throw new TclException("wrong # args");    // FUN_006bc8f0
        }

        static double Dbl(string s)
            => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0.0;
    }
}
