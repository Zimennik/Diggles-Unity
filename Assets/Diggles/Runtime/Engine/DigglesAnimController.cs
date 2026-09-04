#nullable enable

namespace Diggles.Runtime
{
    using System;
    using System.Collections.Generic;
    using UnityEngine;

    // Mid-layer animation controller — the single API both the Tcl bridge and
    // the native (standalone) systems drive. It sits on top of DigglesAnimator
    // (raw clip playback) and uses DigglesModelLibrary (lazy clip loading).
    //
    // Responsibilities the engine puts here rather than in the raw animator:
    // * resolve a logical state ("walkloop", "digfront") to a concrete clip
    // via the active animset, falling back to the class base map;
    // * sequence one-shot → loop → one-shot chains (the engine never auto-
    // chains start/loop/stop);
    // * building construction: map an integer build step to the right
    // single-frame `bau_*` mesh and freeze it (Still mode).
    //
    // State tables come from the Tcl commands set_class_anim / set_class_animset
    // (stored on DigglesClass) or can be set directly for native code / tests.
    public sealed class DigglesAnimController : MonoBehaviour
    {
        public DigglesAnimator? Animator;

        // Object name used to load clips by ("obj","clip") from the library,
        // e.g. "mann", "saegewerk". The state map values may name a different
        // obj (the "<obj>.<anim>" prefix wins when present).
        public string ObjName = "";

        DigglesModelLibrary? _lib;
        Dictionary<string, string> _animMap = new();          // state -> "obj.anim"
        Dictionary<int, Dictionary<string, string>> _animsets = new();
        int _activeAnimset = -1;                                // -1 = none (base map only)

        // Single in-flight timed continuation (covers start→loop chaining).
        Action? _pending;
        double _pendingAt = -1;
        // Game-scaled controller clock for the chain timer. A start→loop handoff must stretch
        // with `gametime factor` exactly like the start clip itself (DigglesAnimator's clock) —
        // on real time, slow-mo cut every start clip over to its loop at 30% played.
        double _chainClock;

        public int ActiveAnimset => _activeAnimset;
        public string CurrentClip => Animator != null ? Animator.CurrentAnimName : "";

        // Construction build-step letters: a,b,c,d,e,f,g,h,i,k,l — "j" is skipped
        // (genericprod.tcl:786-799).
        public static readonly string[] BauLetters =
            { "a", "b", "c", "d", "e", "f", "g", "h", "i", "k", "l" };

        // "bau_c" → stage index 2; -1 when the clip is not a construction stage. The
        // inverse of the genericprod.tcl:787-799 step→letter switch. Used by the engine's
        // set_anim hook to mirror the building's stage and route it through SetBuildupStep.
        public static int BauStepIndex(string bareClip)
        {
            if (string.IsNullOrEmpty(bareClip) || bareClip.Length != 5
                || !bareClip.StartsWith("bau_", StringComparison.Ordinal))
                return -1;
            for (int i = 0; i < BauLetters.Length; i++)
                if (BauLetters[i][0] == bareClip[4]) return i;
            return -1;
        }

        // ----- configuration ------------------------------------------------

        public void Configure(DigglesModelLibrary lib, DigglesAnimator animator, string objName)
        {
            _lib = lib;
            Animator = animator;
            ObjName = objName ?? "";
        }

        public void SetTables(
            Dictionary<string, string>? animMap,
            Dictionary<int, Dictionary<string, string>>? animsets)
        {
            _animMap = animMap ?? new Dictionary<string, string>();
            _animsets = animsets ?? new Dictionary<int, Dictionary<string, string>>();
        }

        public void SetActiveAnimset(int id) => _activeAnimset = id;

        // Alternate animation DB (set_alternateanimdb): a female dwarf resolves clips from
        // `frau` instead of `mann`. The engine's "+1 db" trick — the same clip names resolve
        // to the alternate db. It must remap both bare clips (ObjName-based) and
        // explicitly-prefixed ones (`mann.hut_auf_kopf` → `frau.hut_auf_kopf`), else any
        // prefixed clip would revert the model to mann. See SplitObjAnim.
        public bool UseAlternate;
        public string AlternateObjName = "";

        public void SetAlternate(bool useAlt, string altObjName)
        {
            if (Animator == null) return;
            altObjName ??= "";
            if (UseAlternate == useAlt && AlternateObjName == altObjName) return;
            UseAlternate = useAlt;
            AlternateObjName = altObjName;
            // Clips are cached by bare name, so the old db's clips would shadow the new —
            // drop them and replay the current clip (now resolved through the alternate db).
            string current = Animator.CurrentAnimName;
            var mode = Animator.CurrentMode;
            Animator.ClearClips();
            CancelPending();
            bool ok = !string.IsNullOrEmpty(current) && PlayClip(current, mode);
            if (!ok) PlayClip("standard", DigglesAnimator.AnimMode.Loop);
        }

        // ----- low-level playback ------------------------------------------

        // Diagnostic: trace every PlayClip resolution to the Console ([ANIMTRC]). Armed from
        // DigglesWorldRuntime's Debug section — answers which clip an actor actually got.
        public static bool TracePlayClip = false;

        // Ensure the clip is loaded into the animator (lazily, via the library),
        // then play it. clipName is a bare anim name on ObjName unless it already
        // contains a "." (then the prefix selects the source object).
        public bool PlayClip(string clipName, DigglesAnimator.AnimMode mode, int startFrame = 0)
        {
            if (Animator == null || string.IsNullOrEmpty(clipName)) return false;
            SplitObjAnim(clipName, out var obj, out var clip);
            // A clip from a different object than this controller's own (e.g. a building showing
            // the crate "kiste.standard", or a carried box switching kiste_offen→kiste_tragen via
            // change_look) must be keyed by its qualified name — the animator caches clips by bare
            // name, so otherwise "standard" would resolve to the model's own standard.
            string baseObj = (UseAlternate && !string.IsNullOrEmpty(AlternateObjName)) ? AlternateObjName : ObjName;
            bool crossObj = !string.IsNullOrEmpty(obj) && !string.Equals(obj, baseObj, StringComparison.Ordinal);
            string key = crossObj ? obj + "." + clip : clip;
            if (!Animator.HasClip(key))
            {
                if (_lib == null) return false;
                var built = _lib.GetClip(obj, clip);
                if (built == null)
                {
                    if (TracePlayClip)
                        Debug.LogWarning($"[ANIMTRC] {transform.parent?.name ?? name}: PlayClip('{clipName}') FAILED — " +
                                         $"obj='{obj}' clip='{clip}' (base='{baseObj}' alt={UseAlternate}:{AlternateObjName}) not built");
                    return false;
                }
                Animator.AddClip(built, crossObj ? key : null);
            }
            CancelPending();
            Animator.SetAnim(key, startFrame, mode);
            if (TracePlayClip)
                Debug.LogWarning($"[ANIMTRC] {transform.parent?.name ?? name}: PlayClip('{clipName}') -> key='{key}' " +
                                 $"start={startFrame} mode={mode} alt={UseAlternate}:{AlternateObjName}");
            return true;
        }

        // ----- logical states ----------------------------------------------

        // Resolve a logical state to its "obj.anim" via the active animset first, then
        // animset 0, then the class base map. The set-0 fallback is the bank-inheritance
        // law the scripts author against: a Tcl `set_class_animset N {…}` overrides only
        // the slots it lists — baby.tcl:61 defines bank 1 as nothing but
        // `walk_stop = laufen_fallen_sitzen`, so its walk_start/walk_loop must come from
        // bank 0. Without the fallback a `-animsets 1` walk resolves no loop clip at all
        // and the baby slides across the ground in its last pose.
        public string? ResolveState(string state)
        {
            if (_activeAnimset >= 0 &&
                _animsets.TryGetValue(_activeAnimset, out var set) &&
                set.TryGetValue(state, out var fromSet))
                return fromSet;
            if (_activeAnimset > 0 &&
                _animsets.TryGetValue(0, out var set0) &&
                set0.TryGetValue(state, out var fromSet0))
                return fromSet0;
            return _animMap.TryGetValue(state, out var fromMap) ? fromMap : null;
        }

        // Root-motion locomotion speed (world units/s) for a logical state's
        // clip: |MotionOffset| × modelScale / (frames/10) × multiplier. The engine's
        // frame-coupled speed — the body travels exactly the clip's authored stride
        // (no foot-skating), so different clips/phases come out at proportional speeds.
        // Returns 0 if the clip is unknown; multiplier scales all locomotion uniformly
        // (1 = faithful/no-skate; higher = faster but skates).
        public float LocomotionSpeed(string state, float multiplier = 1f)
        {
            var anim = ResolveState(state) ?? state;
            SplitObjAnim(anim, out var obj, out var clip);
            var c = _lib != null ? _lib.GetClip(obj, clip) : null;
            if (c == null || c.Frames == null || c.Frames.Length == 0) return 0f;
            float dur = c.Frames.Length / 10f;
            float scale = transform.lossyScale.y;   // uniform model scale to world
            return c.MotionOffset.magnitude * scale / Mathf.Max(0.0001f, dur) * multiplier;
        }

        // Native root-motion rate (units/s in model space) of a state resolved in a
        // specific animset (falling back to the base map). Gives the faithful gait
        // ratio, e.g. run:walk = laufen 34.29 / gehen 16.22 ≈ 2.11. Model scale
        // cancels in the ratio, so it is omitted here.
        public float LocomotionRate(int animset, string state)
        {
            string? anim = null;
            if (animset >= 0 && _animsets.TryGetValue(animset, out var set))
                set.TryGetValue(state, out anim);
            if (anim == null) _animMap.TryGetValue(state, out anim);
            if (anim == null) return 0f;
            SplitObjAnim(anim, out var obj, out var clip);
            var c = _lib != null ? _lib.GetClip(obj, clip) : null;
            if (c == null || c.Frames == null || c.Frames.Length == 0) return 0f;
            return c.MotionOffset.magnitude / (c.Frames.Length / 10f);
        }

        // Raw |VecA| of a state's clip (model units, no duration normalization) — the value the
        // native slot table caches as the slot's root motion and the movers consume directly:
        // floor advance per tick = budget × rm(active slot) (FUN_00418280:59-62); climb advance
        // per tick = budget × 0.25 × Σ rm(slots 9/0xa/0xb/0xc) (FUN_00418db0:74-82, DAT_00737058).
        // Resolves through the given animset → base map → the animator's own clip list (mock rigs).
        public float ClipMotionMagnitude(int animset, string state)
        {
            string? anim = null;
            if (animset >= 0 && _animsets.TryGetValue(animset, out var set))
                set.TryGetValue(state, out anim);
            if (anim == null) _animMap.TryGetValue(state, out anim);
            if (anim == null) return 0f;
            SplitObjAnim(anim, out var obj, out var clip);
            var c = _lib != null ? _lib.GetClip(obj, clip) : null;
            if (c != null) return c.MotionOffset.magnitude;
            if (Animator != null)                       // library-less rigs (tests/tools)
                foreach (var ac in Animator.Clips)
                    if (ac.Name == anim || ac.Name == clip) return ac.MotionOffset.magnitude;
            return 0f;
        }

        // ===== native frame-coupled locomotion speed =====
        //
        // The original's movement is frame-coupled: per logic tick (0.1 s, one anim frame at the
        // native 10 fps) the body consumes a fixed fraction of the active clip's root motion as
        // arc length over the baked waypoint list:
        //
        // floor advance/tick = 0.1 × rm(active walk slot) FUN_00418280:59-62
        // (slot 6 walk_start / 7 walk_loop / 8 walk_stop — the slot ids
        // are FUN_00401010's registration order; the 0.1 fraction is
        // obj[+0x3c], reset each tick at FUN_00419c60:46)
        // climb advance/tick = 0.1 × 0.25 × Σ rm(slots 9,0xa,0xb,0xc) FUN_00418db0:74-82
        // (9..12 = climb_up/climb_down/climb_right/climb_left;
        // DAT_00737058 = 0.25)
        //
        // rm = the slot record's cached cruise read at record[+8] (FUN_00423270, disasm:
        // `movss xmm0, [eax + esi + 8]` over the +0x798-bank × 0x14-stride record table).
        //
        // Scale, from a live memory dump: the slot records of a live dwarf (owner obj
        // @0x2d07d14c) cache rm = |VecA| × 0.05, in native tiles/second, not the raw.3db value:
        // bank 0 (walk): slots 6/7/8 all = 0.811087 = 16.2218 (gehen_loop |VecA|) × 0.05
        // bank 1 (run): slots 6/7/8 all = 1.714336 = 34.2867 (laufen_schnell) × 0.05
        // climb slots 9..12 = 0.417855/0.414571/0.415327/0.414413 = klettern |VecA|s × 0.05
        // (records @0x2d07d1d0 bank 0, @0x2d07d3b4 bank 1)
        // ⇒ cruise (world units/s at native cadence) = |VecA| × 0.05 × playbackSpeed
        // walk 0.811 wu/s · run 1.714 wu/s · climb 0.25×Σ = 0.4155 wu/s (1 tile = 1 wu).
        // Bank uniformity: all three walk-bank slots cache the loop clip's value — the
        // start/stop phases cruise at the loop speed (NativeLocomotionSpeed reads walk_loop).
        public const float NativeArcToWorld = 0.05f;   // slot-rm scale: |VecA| → native tiles/s

        // World-units/s cruise of a walk-bank state under the native law (0 when the clip is
        // unknown — callers fall back). `cadence` = anim playback speed (frame-coupled: the
        // per-FRAME advance is constant, so speed scales with the frame rate).
        // Bank uniformity (see NativeArcToWorld): the native slot table caches the loop clip's
        // cruise in the start/stop slots too — every walk-bank state resolves through walk_loop,
        // so the start/stop phases never move at their own clips' |VecA|.
        public float NativeLocomotionSpeed(int animset, string state, float cadence = 1f)
        {
            float rm = ClipMotionMagnitude(animset, "walk_loop");
            if (rm <= 0.0001f) rm = ClipMotionMagnitude(animset, state);   // loopless rigs
            return rm * NativeArcToWorld * Mathf.Max(0f, cadence);
        }

        // World-units/s climb cruise: 0.1 × 0.25 × Σ rm(climb_up/down/right/left) per tick
        // (FUN_00418db0:74-82) — the same full arc every tick regardless of the leg's heading,
        // so up/down/lateral legs all climb at one uniform speed (mann: 0.4155 wu/s).
        public float NativeClimbSpeed(int animset, float cadence = 1f)
        {
            float sum = ClipMotionMagnitude(animset, "climb_up")
                      + ClipMotionMagnitude(animset, "climb_down")
                      + ClipMotionMagnitude(animset, "climb_right")
                      + ClipMotionMagnitude(animset, "climb_left");
            return 0.25f * sum * NativeArcToWorld * Mathf.Max(0f, cadence);
        }

        // Vertical-ladder UP cruise is its own transport-mover law, not CAct_Climb's quarter-sum:
        // FUN_0060bc70 reads slot 0x0e (ladder_climb_up) and advances by slotRm × 0.1 each
        // 0.1-second tick. The live slot-table scale is |VecA| × 0.05, so the per-second speed is
        // simply |VecA|(ladder_climb_up) × 0.05 (mann: 28.2918701 × 0.05 = 1.4145935 wu/s).
        public float NativeLadderUpSpeed(int animset)
        {
            float up = ClipMotionMagnitude(animset, "ladder_climb_up");
            return up * NativeArcToWorld;
        }

        // Play a logical state. Mode is inferred from the name when not given
        // (names ending in start/stop/end → Once, otherwise Loop).
        public bool PlayState(string state, DigglesAnimator.AnimMode? mode = null)
        {
            var anim = ResolveState(state);
            if (anim == null) return false;
            return PlayClip(anim, mode ?? InferMode(state));
        }

        // start→loop: play the start clip once, then switch to the loop clip when
        // the start clip's length elapses (frames/10 s).
        public bool PlayStartLoop(string startState, string loopState)
        {
            var startAnim = ResolveState(startState) ?? startState;
            var loopAnim = ResolveState(loopState) ?? loopState;
            if (!PlayClip(startAnim, DigglesAnimator.AnimMode.Once)) return false;
            double dur = ClipDuration();
            ScheduleAfter(dur, () => PlayClip(loopAnim, DigglesAnimator.AnimMode.Loop));
            return true;
        }

        // One-shot then settle to an idle state (e.g. walk_stop → standstill).
        public bool PlayOnceThen(string onceState, string idleState)
        {
            var onceAnim = ResolveState(onceState) ?? onceState;
            var idleAnim = ResolveState(idleState) ?? idleState;
            if (!PlayClip(onceAnim, DigglesAnimator.AnimMode.Once)) return false;
            double dur = ClipDuration();
            ScheduleAfter(dur, () => PlayClip(idleAnim, DigglesAnimator.AnimMode.Loop));
            return true;
        }

        public bool Idle(string idleState = "standstill")
        {
            // Try the mapped idle state first, else the bare clip name, else the
            // universal "standard" pose.
            return PlayState(idleState, DigglesAnimator.AnimMode.Loop)
                || PlayClip("standard", DigglesAnimator.AnimMode.Loop);
        }

        // ----- buildings ----------------------------------------------------

        // Show a building's construction stage. step in [0..maxStep]; step beyond
        // maxStep shows the finished "standard". Each stage is a separate single-
        // frame anim frozen on frame 0.
        public bool SetBuildupStep(int step, int maxStep)
        {
            string clip;
            if (step < 0) clip = BauLetters.Length > 0 ? "bau_" + BauLetters[0] : "standard";
            else if (step >= maxStep || step >= BauLetters.Length) clip = "standard";
            else clip = "bau_" + BauLetters[step];
            return PlayClip(clip, DigglesAnimator.AnimMode.Still);
        }

        public bool SetDestroyed()
        {
            return PlayClip("zerstoert", DigglesAnimator.AnimMode.Once)
                || PlayClip("kaputt", DigglesAnimator.AnimMode.Once);
        }

        // ----- internals ----------------------------------------------------

        void OnEnable() => DigglesFrameDriver.Register(this);    // central per-frame tick
        void OnDisable() => DigglesFrameDriver.Unregister(this);

        // Advance the chain clock (`deltaTime` in unscaled seconds; the game-time scale applies
        // here) and fire the pending continuation when due. Public so headless tests can drive
        // it with a fake clock — Update feeds Time.deltaTime.
        public void TickChain(float deltaTime)
        {
            // Nothing scheduled → skip. The chain clock is only read while a continuation is
            // pending, and ScheduleAfter re-bases _pendingAt on the current _chainClock, so
            // pausing its advancement between schedules is timing-neutral. Keeps the idle
            // per-entity tick down to a single null check.
            if (_pending == null) return;
            _chainClock += deltaTime * DigglesAnimator.GlobalTimeScale;
            if (_chainClock >= _pendingAt)
            {
                var cb = _pending;
                _pending = null;
                _pendingAt = -1;
                cb?.Invoke();
            }
        }

        void ScheduleAfter(double seconds, Action cb)
        {
            _pending = cb;
            _pendingAt = _chainClock + Math.Max(0.0, seconds);
        }

        void CancelPending()
        {
            _pending = null;
            _pendingAt = -1;
        }

        double ClipDuration()
        {
            int n = Animator != null ? Animator.CurrentFrameCount : 0;
            return n > 0 ? n / 10.0 : 0.0;
        }

        // Loop unless the clip name reads as a transition (…start/…stop/…end). Public so the
        // action layer can play cutscene anims in their natural mode instead of forcing Once
        // (which freezes a loop/idle/talk clip on its last frame between sequencer commands).
        public static DigglesAnimator.AnimMode InferMode(string name)
        {
            if (name.EndsWith("start", StringComparison.Ordinal) ||
                name.EndsWith("stop", StringComparison.Ordinal) ||
                name.EndsWith("end", StringComparison.Ordinal))
                return DigglesAnimator.AnimMode.Once;
            return DigglesAnimator.AnimMode.Loop;
        }

        void SplitObjAnim(string value, out string obj, out string clip)
        {
            int dot = value.IndexOf('.');
            if (dot > 0)
            {
                obj = value.Substring(0, dot);
                clip = value.Substring(dot + 1);
            }
            else
            {
                obj = ObjName;
                clip = value;
            }
            // Alternate db remap (set_alternateanimdb): the primary model object (e.g. the
            // dwarf "mann", whether bare or "mann."-prefixed) resolves from the alternate
            // ("frau"). Other objects (props, etc.) are untouched.
            if (UseAlternate && !string.IsNullOrEmpty(AlternateObjName) && obj == ObjName)
                obj = AlternateObjName;
        }
    }
}
