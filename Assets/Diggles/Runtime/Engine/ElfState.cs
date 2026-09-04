#nullable enable

namespace Diggles.Runtime
{
    using UnityEngine;

    // The tutorial/narrator elf — a faithful port of the native CElf singleton (DAT_0076a274).
    // She has no Tcl class and is not a CObj: one native struct + one global Tcl command `elf`
    // (FUN_00649640), ticked at a 200 Hz fixed step (FUN_00424560 drains an accumulator of
    // DAT_007687f8 = 0.005 s and calls CElf::Tick = FUN_00427e30 per step, only while visible).
    //
    // This class is the pure-logic core (headless-testable): the struct fields, the mode machine
    // 0–5, FlyToward (FUN_00428b80), the anim registry + phase machine (ctor 0x424cc0 /
    // FUN_00428630 AdvanceAnim), lean math and lockview write-back. Everything that needs a real
    // camera/screen/scene goes through the host callbacks below (ElfPresenter wires them in Play;
    // EditMode tests mock them). No UnityEngine.Camera/Time dependencies — Vector3/Mathf only.
    public sealed class ElfState
    {
        // Native fixed tick step (DAT_007687f8 = 0.005 s → 200 Hz).
        public const float FixedStep = 0.005f;

        [System.Serializable]
        public sealed class NativeSaveState
        {
            public int TextureVariant;
            public int CurrentRegisteredAnimation;
            public int RequestedRegisteredAnimation;
            public int NativeModelDatabaseId;
            public int NativeMeshId;
            public float FrameAlpha;
            public int LookAtObjectId;
            public Vector3 Position;
            public float LeanA;
            public float LeanB;
            public int NativeState64Bits;
            public Vector3 Target;
            public int Mode;
            public bool DrawOverUi;
            public bool AnimationHasMoreFrames;
            public int FirstParticleSourceHandle;
            public int SecondParticleSourceHandle;
            public string? ResolvedModelAnimation;
            public int ResolvedModelAnimationFrame;
            public int ResolvedModelAnimationPhase;
        }

        // ---------------- CElf struct (byte offsets per the RE) ----------------
        public int AnimPhase;                   // +0x00: 0=start(intro) 1=loop 2=end(outro)
        public string? ModelAnim;               // +0x04 modelAnimId — current 3db clip (null = -1)
        public int FrameInAnim;                 // +0x08
        public Vector3 Velocity;                // +0x14
        public bool LockView;                   // +0x20
        public bool Visible;                    // +0x21 (set by ANY `elf` cmd — dispatcher:103)
        public int FinishObjId = -1;            // +0x24 (-1 = none; -2 = port sentinel: no liveness check)
        public string? FinishCode;              // +0x28
        public int TexVariant;                  // +0x34 (renderer variant list {texvar, 0}: body slot only)
        public int CurAnimIdx = -1;             // +0x38 registered anim playing
        public int ReqAnimIdx = -1;             // +0x3c registered anim requested
        public float FrameAlpha;                // +0x48 (+0.05/tick; frame flip at >=0.99 => 10 fps)
        public int LookatObjId = -1;            // +0x4c
        public Vector3 Position;                // +0x50 (native: x=east, y=screen-down, z=depth; standard z=16)
        public float LeanA;                     // +0x5c (from delta-y / camera pan) — radians-ish, clamped
        public float LeanB;                     // +0x60 (from delta-x)
        public Vector3 Target;                  // +0x68
        public int Mode;                        // +0x74: 0=hold 1=screen-follow 2=move 3=hide 4=crash 5=path
        public bool DrawOverUI;                 // +0x7c
        public bool AnimHasMoreFrames;          // +0x7d

        // Port-only: bumped whenever a model clipstarts, so the presenter re-issues the clip
        // to DigglesAnimator (loop = the native "restart the loop clip at every clip end").
        public int ClipSerial;

        // Hover-bob phase accumulator (native DAT_00c3c960 — a global, but only the elf reads it).
        float _bobT;
        // 200 Hz fixed-step accumulator (native FUN_00565940 state DAT_007687f0).
        double _accum;

        int _nativeModelDatabaseId = -1;
        int _nativeMeshId = -1;
        int _nativeState64Bits;
        int _firstParticleSourceHandle = -1;
        int _secondParticleSourceHandle = -1;

        // ---------------- host callbacks (presenter wires real ones; tests mock) ----------------
        // FUN_00428a70: project nativePos through the view-proj matrix; true if within the screen
        // rect expanded by marginPx on every side. Default (unwired/headless): nothing is on screen
        // — FlyToward then teleports, hide completes immediately (sane no-screen degradation).
        public System.Func<Vector3, int, bool>? IsOnScreen;
        // FUN_00479150: screen point (real pixels, native top-left origin) onto the world plane at
        // native z = depthZ. Default: identity passthrough (x,y,depthZ) for headless determinism.
        public System.Func<float, float, float, Vector3>? UnprojectScreenToNative;
        // Real screen size in pixels (native GameUI +0x2830/+0x2834). Default = the 800x600 reference.
        public System.Func<Vector2>? ScreenSize;
        // Live view focus (native view +0x154/+0x158 — the camera ground target) for lockview's
        // per-tick lerp and the no-arg `movescreen` target.
        public System.Func<Vector2>? GetViewFocus;
        public System.Action<Vector2>? SetViewFocus;
        // Live view pan velocities (native view +0x160/+0x164): read by the no-lookat lean follow,
        // decayed x0.9/tick by lockview. The port camera has no pan-velocity state — unwired => 0.
        public System.Func<Vector2>? GetViewPanVel;
        public System.Action<Vector2>? SetViewPanVel;
        // Lookat target's native position; null = object gone (native clears lookat then).
        public System.Func<int, Vector3?>? ResolveObjPos;
        // Finishcode liveness check (FUN_004279f0 fires only while the stored obj is still alive).
        public System.Func<int, bool>? IsObjAlive;
        // Frame count of an elfe.3db clip (model anim table lookup in FUN_00427e30). Default 10.
        public System.Func<string, int>? GetClipFrameCount;

        System.Action<string>? _eval;
        public void SetEvalCallback(System.Action<string> f) => _eval = f;

        public NativeSaveState CaptureNativeSaveState() => new()
        {
            TextureVariant = TexVariant,
            CurrentRegisteredAnimation = CurAnimIdx,
            RequestedRegisteredAnimation = ReqAnimIdx,
            NativeModelDatabaseId = _nativeModelDatabaseId,
            NativeMeshId = _nativeMeshId,
            FrameAlpha = FrameAlpha,
            LookAtObjectId = LookatObjId,
            Position = Position,
            LeanA = LeanA,
            LeanB = LeanB,
            NativeState64Bits = _nativeState64Bits,
            Target = Target,
            Mode = Mode,
            DrawOverUi = DrawOverUI,
            AnimationHasMoreFrames = AnimHasMoreFrames,
            FirstParticleSourceHandle = _firstParticleSourceHandle,
            SecondParticleSourceHandle = _secondParticleSourceHandle,
            ResolvedModelAnimation = ModelAnim,
            ResolvedModelAnimationFrame = FrameInAnim,
            ResolvedModelAnimationPhase = AnimPhase,
        };

        public void RestoreNativeSaveState(NativeSaveState state)
        {
            if (state == null) throw new System.ArgumentNullException(nameof(state));
            if (state.CurrentRegisteredAnimation < -1 ||
                state.CurrentRegisteredAnimation >= Anims.Length ||
                state.RequestedRegisteredAnimation < -1 ||
                state.RequestedRegisteredAnimation >= Anims.Length)
            {
                throw new System.ArgumentException(
                    "Saved CElf registered animation index is invalid.", nameof(state));
            }
            if (state.Mode < 0 || state.Mode > 5)
                throw new System.ArgumentException("Saved CElf mode is invalid.", nameof(state));

            TexVariant = state.TextureVariant;
            CurAnimIdx = state.CurrentRegisteredAnimation;
            ReqAnimIdx = state.RequestedRegisteredAnimation;
            _nativeModelDatabaseId = state.NativeModelDatabaseId;
            _nativeMeshId = state.NativeMeshId;
            FrameAlpha = state.FrameAlpha;
            LookatObjId = state.LookAtObjectId;
            Position = state.Position;
            LeanA = state.LeanA;
            LeanB = state.LeanB;
            _nativeState64Bits = state.NativeState64Bits;
            Target = state.Target;
            Mode = state.Mode;
            DrawOverUI = state.DrawOverUi;
            AnimHasMoreFrames = state.AnimationHasMoreFrames;
            _firstParticleSourceHandle = state.FirstParticleSourceHandle;
            _secondParticleSourceHandle = state.SecondParticleSourceHandle;
            ModelAnim = state.ResolvedModelAnimation;
            FrameInAnim = Mathf.Max(0, state.ResolvedModelAnimationFrame);
            AnimPhase = Mathf.Clamp(state.ResolvedModelAnimationPhase, 0, 2);
            Visible = state.NativeMeshId >= 0;
            Velocity = Vector3.zero;
            LockView = false;
            FinishObjId = -1;
            FinishCode = null;
            _bobT = 0f;
            _accum = 0d;
            if (ModelAnim != null) ClipSerial++;
        }

        // ---------------- anim registry (ctor FUN_00424cc0, AddAnim FUN_004277f0) ----------------
        // {name, startClip, loopClip, endClip, oneShot} resolved against elfe.3db; 34 entries.
        public sealed class AnimDef
        {
            public readonly string Name;
            public readonly string? Start, Loop, End;
            public readonly bool OneShot;
            public AnimDef(string name, string? start, string? loop, string? end, bool oneShot)
            { Name = name; Start = start; Loop = loop; End = end; OneShot = oneShot; }
        }

        static AnimDef Os(string name) => new(name, null, name, null, true);   // one-shot: name = loop clip

        public static readonly AnimDef[] Anims =
        {
 /* 0 */ new("standard", null, "standanim", null, false),
 /* 1 */ new("move_u", "fliegen_hoch_norm_start", "fliegen_hoch_norm_loop", "fliegen_hoch_norm_end", false),
 /* 2 */ new("move_d", null, "standanim", null, false),
 /* 3 */ new("move_l", "fliegen_links_norm_start", "fliegen_links_norm_loop", "fliegen_links_norm_end", false),
 /* 4 */ new("move_r", "fliegen_rechts_norm_start", "fliegen_rechts_norm_loop", "fliegen_rechts_norm_end", false),
            // idx 5: the fast-up flight; registered name unrecovered from the ctor disasm (nothing in
            // the shipped scripts calls it and no code path selects it) — placeholder name, real clips.
 /* 5 */ new("move_fast_u", "fliegen_hoch_schnell_start", "fliegen_hoch_schnell_loop", "fliegen_hoch_schnell_end", false),
 /* 6 */ new("idle", "rumhaengen_start", "rumhaengen_loop", "rumhaengen_end", false),
 /* 7 */ new("sleep", "schlafen_start", "schlafen_loop", "schlafen_end", false),
 /* 8 */ Os("ablehnen"),
 /* 9 */ Os("anfeuern"),
 /* 10 */ Os("auffordern"),
 /* 11 */ Os("beleidigen"),
 /* 12 */ Os("drehen_links_90grad"),
 /* 13 */ Os("drehen_rechts_90grad"),
 /* 14 */ Os("gucken_links"),
 /* 15 */ Os("gucken_rechts"),
            // idx 16 = kichern (the giggle): the ctor-disasm name was unrecovered ("nothing calls
            // it"), but urw_6a:159 does — `do_elf text 006ah {kichern} Haha` (the elf's laugh that
            // never played). The 8..24 block is alphabetical and kichern slots
            // exactly between gucken_rechts (15) and koketieren (17); elfe.3db ships the clip.
            // Index 25 (abstuerzen_fall, addressed by number) is unaffected.
 /* 16 */ Os("kichern"),
 /* 17 */ Os("koketieren"),
 /* 18 */ Os("pirouette"),
 /* 19 */ Os("radschlagen"),
 /* 20 */ Os("salto"),
 /* 21 */ Os("schmollen"),
 /* 22 */ Os("verzweifeln"),
 /* 23 */ Os("warnen"),
 /* 24 */ Os("zustimmen"),
            // 25: crash fall loop — mode 4 sets cur=req=25 directly (dispatcher), so the one-shot
            // flag never triggers for it and the fall clip loops while falling.
 /* 25 */ new("abstuerzen_fall", null, "abstuerzen_fall", null, true),
            // 26: impact — plays once as the start clip, then the pending req=0 returns to standard
            // (the registered standanim loop never plays: AdvanceAnim switches at the clip end).
 /* 26 */ new("abstuerzen_aufprall", "abstuerzen_aufprall", "standanim", null, true),
 /* 27 */ Os("zeigen_rechts"),
 /* 28 */ Os("zeigen_links"),
 /* 29 */ Os("reden_a"),
 /* 30 */ Os("reden_b"),
 /* 31 */ Os("kopf_schuetteln"),
 /* 32 */ Os("kusshand"),
 /* 33 */ Os("meckern"),
        };

        // FUN_00427950: linear name scan; -1 when unknown (callers clamp to 0 = standard).
        public static int FindAnim(string name)
        {
            for (int i = 0; i < Anims.Length; i++)
                if (Anims[i].Name == name) return i;
            return -1;
        }

        static int ClampAnim(int idx) => Mathf.Min(idx, Anims.Length - 1);

        // ---------------- command surface (`elf <verb>`, FUN_00649640) ----------------
        // EVERY `elf` command makes her visible BEFORE the verb dispatch (FUN_00649640 line
        // `*(DAT_0076a274+0x21)=1`), even pure getters like get_pos.
        public void MarkVisible() => Visible = true;

        // action <name> / anim <name> / sleep / idle / standard — unknown name clamps to 0 (standard).
        public void CmdAction(string name)
        {
            MarkVisible();
            int idx = FindAnim(name);
            ReqAnimIdx = ClampAnim(idx < 0 ? 0 : idx);
        }

        public int CmdTexVar(int? set)
        {
            MarkVisible();
            if (set.HasValue) TexVariant = set.Value;
            return TexVariant;
        }

        // finishcode <obj> <code>: stores the obj id + code; fired by arrivals / one-shot-enter,
        // only while the obj is still alive (FUN_004279f0). objId -2 = port sentinel (always fire).
        public void CmdFinishcode(int objId, string code)
        {
            MarkVisible();
            FinishObjId = objId;
            FinishCode = code;
        }

        public void CmdLookat(int objId) { MarkVisible(); LookatObjId = objId; }   // -1 = clear

        // stop: mode=0, reqAnim=0 (dispatcher tail branch).
        public void CmdStop() { MarkVisible(); Mode = 0; ReqAnimIdx = ClampAnim(0); }

        public bool CmdDrawOverUi(bool? set)
        {
            MarkVisible();
            if (set.HasValue) DrawOverUI = set.Value;
            return DrawOverUI;
        }

        // hide: keeps flying to the current target until off-screen, then Reset(keep texvar) +
        // invisible + finishcode (mode-3 tick). The dispatcher only clears lookat/lockview here.
        public void CmdHide() { MarkVisible(); LookatObjId = -1; LockView = false; Mode = 3; }

        public void CmdMove(Vector3 v)
        {
            MarkVisible();
            ReqAnimIdx = ClampAnim(0);
            Target = v;
            Mode = 2;
        }

        // path {p1} {p2} s (FUN_004288c0): quantized velocity — stepLen = dist/round(dist/s)
        // (s < 0.01 => 1.0), velocity = dir * stepLen * 0.05/tick => cruise speed ~ stepLen*10 u/s.
        public void CmdPath(Vector3 p1, Vector3 p2, float speed)
        {
            MarkVisible();
            Vector3 d = p2 - p1;
            float dist = d.magnitude;
            float s = speed;
            if (s < 0.01f) s = 1.0f;                                  // DAT_00737148 / DAT_00737138
            float q = dist / s;
            float steps = Mathf.Floor(q);
            if (q - steps >= 0.5f) steps = Mathf.Ceil(q);             // round-half-up (DAT_0073708c)
            if (steps < 1f) steps = 1f;   // port guard: native divides by round unguarded (degenerate dist < s/2)
            float stepLen = dist / steps;
            Vector3 dir = dist > 0f ? d / dist : Vector3.zero;
            Velocity = dir * (stepLen * 0.05f);
            Position = p1;
            Target = p2;
            ReqAnimIdx = ClampAnim(0);
            Mode = 5;
        }

        // crash {v}: spawn 15 above (y-down frame: y-15, DAT_00737568), fall anim 25, mode 4.
        // The dispatcher also clears modelAnimId and sets curAnimIdx directly to 25 (so the next
        // AdvanceAnim restarts the fall loop rather than treating 25 as a one-shot entry).
        public void CmdCrash(Vector3 v)
        {
            MarkVisible();
            ModelAnim = null;
            int idx = ClampAnim(25);
            ReqAnimIdx = idx;
            CurAnimIdx = idx;
            Position = new Vector3(v.x, v.y - 15f, v.z);
            Target = v;
            Mode = 4;
        }

        // movescreen {x y z}: screen-anchored mode 1; the x/y/z screen semantics (negatives,
        // 800x600-reference scaling when z<0) are resolved every tick in FixedTick (tracks camera).
        public void CmdMoveScreen(Vector3 v)
        {
            MarkVisible();
            ReqAnimIdx = ClampAnim(0);
            Target = v;
            Mode = 1;
        }

        // movescreen (no arg): a WORLD move (mode 2) to (view.FocusX, view.FocusY, 16).
        public void CmdMoveScreenNoArg()
        {
            MarkVisible();
            Vector2 f = GetViewFocus?.Invoke() ?? Vector2.zero;
            Target = new Vector3(f.x, f.y, 16f);
            ReqAnimIdx = ClampAnim(0);
            Mode = 2;
        }

        public void CmdLockView(bool on) { MarkVisible(); LockView = on; }

        // followview {v}: same screen-follow mode as movescreen-with-vector. The vector is
        // required natively (`elf followview` without args is a Tcl arg-count error).
        public void CmdFollowView(Vector3 v) { MarkVisible(); Target = v; Mode = 1; }

        // unfollowview: mode=0 only (holds position; reqAnim untouched).
        public void CmdUnfollowView() { MarkVisible(); Mode = 0; }

        // set_pos: teleport — pos AND target (so a later mode switch doesn't chase a stale target).
        public void CmdSetPos(Vector3 v) { MarkVisible(); Position = v; Target = v; }

        public Vector3 CmdGetPos() { MarkVisible(); return Position; }

        // ---------------- Reset (FUN_00427610) ----------------
        // New-game/shutdown calls Reset(true); hide-completion calls Reset(false) (texvar kept).
        // pos -> origin re-arms FlyToward's |pos|^2 < 0.01 screen-edge fly-in.
        public void Reset(bool resetTexvar)
        {
            if (resetTexvar) TexVariant = 0;
            LookatObjId = -1;
            FinishObjId = -1;          // code string itself is not cleared natively — just disarmed
            Position = Vector3.zero;
            CurAnimIdx = -1;
            ReqAnimIdx = -1;
            Target = Vector3.zero;
            LockView = false;
            Visible = false;           // the native write is 2 bytes wide: +0x20 AND +0x21
            Mode = 0;
            AnimHasMoreFrames = false;
            // Port: drop the playing clip now (natively curAnimIdx=-1 makes the next AdvanceAnim
            // clear modelAnimId; we won't tick while invisible, so clear it here).
            ModelAnim = null;
            AnimPhase = 0;
            FrameInAnim = 0;
            FrameAlpha = 0f;
            Velocity = Vector3.zero;
        }

        // ---------------- fixed-step pump (FUN_00424560 TickAll) ----------------
        // Called from DigglesGameSim.Step with the sim dt; drains 0.005 s steps. While invisible
        // the native drains the accumulator without ticking (no burst on re-show) — same here.
        public void Tick(double dt)
        {
            _accum += dt;
            if (!Visible) { _accum = 0.0; return; }
            if (_accum > 1.0) _accum = 1.0;        // port safety: cap a hitch burst at 200 ticks
            while (_accum + 1e-9 >= FixedStep)
            {
                _accum -= FixedStep;
                FixedTick();
                if (!Visible) { _accum = 0.0; return; }   // hide completed mid-drain
            }
        }

        // ---------------- CElf::Tick (FUN_00427e30), one 5 ms step ----------------
        void FixedTick()
        {
            // 1. Lean update. No lookat: follow the camera pan velocities (decay to 0). Lookat:
            // slow clamped lean toward the target — she never yaw-rotates; these two small
            // angles are her whole orientation (flight direction is conveyed by clips).
            if (LookatObjId == -1)
            {
                Vector2 pan = GetViewPanVel?.Invoke() ?? Vector2.zero;
                LeanA = pan.x * 0.05f + LeanA * 0.95f;
                LeanB = pan.y * 0.05f + LeanB * 0.95f;
            }
            else if (ResolveObjPos != null)
            {
                Vector3? op = ResolveObjPos(LookatObjId);
                if (op == null)
                {
                    LookatObjId = -1;             // object gone: clear, skip the lean this tick
                }
                else
                {
                    float a = (Position.x - op.Value.x) * 0.2f;
                    float b = -(Position.y - op.Value.y) * 0.2f;
                    if (a > 0f) a = 2.5f - a;                          // reflect (DAT_007373d8)
                    else if (a < 0f) a = a - 2.5f;
                    b = Mathf.Clamp(b, -1.7f, 1.7f);
                    LeanA = b * 0.02f + LeanA * 0.98f;
                    LeanB = Mathf.Clamp(a, -3f, 3f) * 0.02f + LeanB * 0.98f;
                }
            }
            // (unwired ResolveObjPos: hold the lookat + leans — headless tolerance)

            // 2. Mode machine.
            switch (Mode)
            {
                case 1:   // screen-follow: re-resolve the screen target every tick => tracks camera
                {
                    Vector2 scr = ScreenSize?.Invoke() ?? new Vector2(800f, 600f);
                    float x = Target.x, y = Target.y, z = Target.z;
                    // Decompile order matters: negatives add the real screen dims first, then the
                    // z<0 branch applies the 800x600-reference scale (FUN_00427e30 case 1).
                    if (x < 0f) x += scr.x;
                    if (y < 0f) y += scr.y;
                    if (z < 0f)
                    {
                        z = -z;
                        x *= scr.x / 800f;                             // DAT_0073768c
                        y *= scr.y / 600f;                             // DAT_00737688
                    }
                    Vector3 t = UnprojectScreenToNative?.Invoke(x, y, z) ?? new Vector3(x, y, z);
                    FlyToward(t);
                    break;
                }
                case 2:   // move-to-world
                {
                    if ((Position - Target).sqrMagnitude >= 0.04f) FlyToward(Target);   // DAT_0073700c
                    else { Mode = 0; RunFinishCode(); }
                    break;
                }
                case 3:   // hide: fly until off-screen, then finishcode + Reset(keep texvar) + invisible
                {
                    // FUN_00427e30 case 3 — the decompiler lost the IsOnScreen margin register
                    // arg; 0 px (the plain screen rect) is the natural reading. Worst case the
                    // native used a small margin => she vanishes a few px later.
                    if (!OnScreen(Position, 0))
                    {
                        Mode = 0;
                        RunFinishCode();
                        Reset(false);
                    }
                    // Native calls FlyToward even on the reset tick (harmless: pos==target==0).
                    FlyToward(Target);
                    break;
                }
                case 4:   // crash: fall straight down (y-down frame) 0.05/tick into the -3.0 band
                {
                    if (Position.y < Target.y - 3.0f)                  // DAT_00737428
                    {
                        Velocity = new Vector3(0f, 0.05f, 0f);
                        float ny = Position.y + 0.05f;
                        float nx = Position.x, nz = Position.z;
                        if (Target.y < ny) { nx = Target.x; nz = Target.z; ny = Target.y; }   // native dead code (kept verbatim)
                        ReqAnimIdx = ClampAnim(25);                    // abstuerzen_fall
                        Position = new Vector3(nx, ny, nz);
                    }
                    else
                    {
                        ReqAnimIdx = ClampAnim(26);                    // abstuerzen_aufprall
                        Mode = 0;
                        RunFinishCode();
                    }
                    break;
                }
                case 5:   // path: unguided quantized velocity, anim from the velocity quadrant
                {
                    if ((Position - Target).sqrMagnitude >= 0.0625f)   // DAT_0073701c
                    {
                        Position += Velocity;
                        ReqAnimIdx = ClampAnim(DirAnim(Velocity.x, Velocity.y));
                    }
                    else
                    {
                        Mode = 0;
                        ReqAnimIdx = ClampAnim(0);
                        RunFinishCode();
                    }
                    break;
                }
            }

            // 3. lockview camera write-back: view center lerps to the elf, pan velocities decay.
            if (LockView)
            {
                if (GetViewFocus != null && SetViewFocus != null)
                {
                    Vector2 f = GetViewFocus();
                    SetViewFocus(new Vector2(Position.x * 0.02f + f.x * 0.98f,
                                             Position.y * 0.02f + f.y * 0.98f));
                }
                if (GetViewPanVel != null && SetViewPanVel != null)
                    SetViewPanVel(GetViewPanVel() * 0.9f);             // DAT_00737108
            }

            // 4. (the two link-point sparkle lights, FUN_00427a70 — presenter polish, not ported)

            // 5. Anim frame advance + hover bob + clip-end handoff.
            AnimFrameTick();
        }

        // FUN_004290a0: directional anim from velocity — |vx|*1.5 beats |vy| => horizontal.
        // 1=move_u 2=move_d 3=move_l 4=move_r (y-down frame: vy>0 = moving down the screen).
        public static int DirAnim(float vx, float vy) =>
            Mathf.Abs(vy) < Mathf.Abs(vx) * 1.5f ? (vx > 0f ? 4 : 3)
                                                 : (vy > 0f ? 2 : 1);

        // ---------------- FlyToward (FUN_00428b80) ----------------
        void FlyToward(Vector3 target)
        {
            Vector3 d = target - Position;
            float hdist2 = d.x * d.x + d.y * d.y;
            float dist = Mathf.Sqrt(hdist2 + d.z * d.z);

            // Off-screen teleport: both endpoints far outside (400 px margin) => just appear there.
            if (!OnScreen(Position, 400) && !OnScreen(target, 400) && dist > 0.1f)
            {
                Position = target;
                return;
            }

            // Fresh/reset elf (pos ~ origin): ENTER FROM THE SCREEN EDGE — walk outward from the
            // target along (pos-target) until off-screen (50 px), stand one step inside.
            if (Position.sqrMagnitude < 0.01f)                          // DAT_00737148
            {
                Vector3 dir = Position - target;
                float dl = dir.magnitude;
                if (dl > 0f) dir /= dl;
                if (dir.magnitude > 0.1f)                               // degenerate target==origin: fall through
                {
                    Vector3 p = target;
                    // Native loop is unbounded (the projection always leaves a finite screen);
                    // capped here purely as a defensive guard for misbehaving mocks.
                    for (int guard = 0; guard < 4096 && OnScreen(p, 50); guard++) p += dir;
                    Position = p - dir;
                    return;
                }
            }

            Velocity *= 0.75f;                                          // damping (DAT_007370c0)

            if (dist >= 1.5f)                                           // cruise (DAT_007371c0)
            {
                Vector3 n = d / dist;
                if (dist > 0.8f) Velocity += n * 0.15f;                 // accel (DAT_0073704c)
                if (Velocity.sqrMagnitude > 25f)                        // speed clamp (DAT_0073758c)
                    Velocity *= 5f / Velocity.magnitude;

                bool directional = false;
                int req;
                if (Mathf.Sqrt(hdist2) <= 1.0f) req = ClampAnim(0);     // nearly vertical: hover clip
                else { req = ClampAnim(DirAnim(Velocity.x, Velocity.y)); directional = true; }
                ReqAnimIdx = req;

                // MOVE GATE: only advance while the right directional LOOP is playing (waits for
                // the _start turn anim), or when hovering with the standard clip requested.
                if ((AnimPhase == 1 && directional && CurAnimIdx == req) || (req == 0 && !directional))
                    Position += Velocity * 0.05f;
                return;
            }

            if (dist < 0.2f)                                            // arrive (DAT_00737054)
            {
                Position = target;
                ReqAnimIdx = ClampAnim(0);
                RunFinishCode();
                return;
            }

            // Ease-out band [0.2, 1.5): glide 10%/tick + play the current anim's _end outro.
            Position += d * 0.1f;
            ReqAnimIdx = ClampAnim(0);
            StartOutro(CurAnimIdx);
        }

        bool OnScreen(Vector3 p, int marginPx) => IsOnScreen?.Invoke(p, marginPx) ?? false;

        // ---------------- anim machinery ----------------

        int ClipFrames(string clip) => GetClipFrameCount?.Invoke(clip) ?? 10;

        // Tail of FUN_00427e30: frameAlpha += 0.05, frame flip at >=0.99 (=> 10 fps), hover bob
        // while playing registered anim 0, AdvanceAnim at the clip end (or when nothing plays).
        void AnimFrameTick()
        {
            if (CurAnimIdx != -1 && ModelAnim != null)
            {
                int frameCount = ClipFrames(ModelAnim);
                FrameAlpha += 0.05f;
                if (FrameAlpha >= 0.99f) { FrameInAnim++; FrameAlpha = 0f; }   // DAT_00737128
                if (FrameInAnim > frameCount) FrameInAnim = frameCount;
                if (FrameInAnim < frameCount)
                {
                    AnimHasMoreFrames = FrameInAnim < frameCount - 1;
                    if (CurAnimIdx != 0) return;
                    // Hover bob — only while the standard hover plays (sin coefficients §D.5).
                    _bobT += 0.05f;
                    Position = new Vector3(
                        Position.x + Mathf.Sin(_bobT * 0.12f) * 0.0002f,
                        Position.y + Mathf.Sin(_bobT * 0.31f + 0.1f) * 0.0008f,
                        Position.z);
                    return;
                }
                FrameAlpha = 0f;
            }
            AdvanceAnim();
        }

        // FUN_00428630: clip-end / no-clip handoff. Switching away first plays the _end outro;
        // ENTERING a one-shot immediately re-requests standard and fires/clears the finishcode;
        // an unchanged request restarts the LOOP clip.
        void AdvanceAnim()
        {
            int cur = CurAnimIdx, req = ReqAnimIdx;
            if (cur != req)
            {
                if (cur == -1)
                {
                    // Fresh start (the native also creates her two sparkle lights here). Note: this
                    // path does NOT check oneShot — faithful (FUN_00428630 cur==-1 branch).
                    CurAnimIdx = req;
                    if (req != -1) StartAnim(req);
                }
                else
                {
                    if (!StartOutro(cur)) { /* outro started — wait for it to finish */ }
                    else
                    {
                        CurAnimIdx = req;
                        if (req >= 0 && Anims[req].OneShot)
                        {
                            ReqAnimIdx = ClampAnim(0);    // auto-return to standard
                            RunFinishCode();              // fired on one-shot ENTER
                        }
                        if (CurAnimIdx != -1) StartAnim(CurAnimIdx);
                    }
                }
            }
            else if (cur != -1)
            {
                // Clip finished with no pending switch:start the LOOP phase.
                AnimPhase = 1;
                SetModelAnim(Anims[cur].Loop);
            }
            if (CurAnimIdx == -1) ModelAnim = null;
        }

        // FUN_004284d0: start phase 0 with the _start clip; no start clip => straight to the loop.
        void StartAnim(int idx)
        {
            var def = Anims[idx];
            if (def.Start != null) { AnimPhase = 0; SetModelAnim(def.Start); }
            else { AnimPhase = 1; SetModelAnim(def.Loop); }
        }

        // FUN_00428580: returns false when an outro just STARTED (caller waits); true when no
        // outro is needed (none registered / already in the outro phase) and the switch may proceed.
        bool StartOutro(int idx)
        {
            if (AnimPhase != 2 && idx >= 0 && idx < Anims.Length && Anims[idx].End != null)
            {
                AnimPhase = 2;
                SetModelAnim(Anims[idx].End);
                return false;
            }
            return true;
        }

        void SetModelAnim(string? clip)
        {
            ModelAnim = clip;
            FrameInAnim = 0;
            FrameAlpha = 0f;
            if (clip != null) ClipSerial++;       // presenter re-issues the clip (incl. loop restarts)
        }

        // FUN_004279f0: fire only while the stored obj is still alive AND a code is set; the
        // registration is consumed either way. Eval happens BEFORE the clear is observable to the
        // caller’s next registration (native frees the obj after eval).
        public void RunFinishCode()
        {
            if (FinishObjId == -1) return;
            bool alive = FinishObjId == -2 || IsObjAlive == null || IsObjAlive(FinishObjId);
            string? code = FinishCode;
            if (alive && !string.IsNullOrEmpty(code)) _eval?.Invoke(code!);
            FinishCode = null;
            FinishObjId = -1;
        }

        // ---------------- legacy compatibility shims ----------------
        // DigglesTclEngine's current `elf` command (and two older tests) still call the pre-rewrite
        // API. These shims keep it compiling/working until the engine-side rewiring task lands —
        // see that task for the exact old->new verb mapping. DO NOT use in new code.

        // Old "is the elf world-flying" flag (mode 1 was the old ScreenMode, not Moving).
        public bool Moving => Mode >= 2;

        // Old presenter/tests read a CLIP-ish name; map the requested registered anim to its name
        // (idx 0 reports the old default "standanim" since no "standard" clip exists in elfe.3db).
        public string CurrentAnim
        {
            get
            {
                int idx = ReqAnimIdx != -1 ? ReqAnimIdx : CurAnimIdx;
                return idx <= 0 ? "standanim" : Anims[idx].Name;
            }
        }

        // Dead flag the old engine wiring sets (native has no followview boolean — followview is a
        // screen-anchor MODE requiring a vector). Kept settable for compile-compat only.
        public bool FollowView = true;

        // Old screen-pin flag => the faithful mode-1 state.
        public bool ScreenMode
        {
            get => Mode == 1;
            set { if (value) Mode = 1; else if (Mode == 1) Mode = 0; }
        }

        public int LookatHandle => LookatObjId;
        public string? Finishcode => FinishCode;

        public void MoveTo(Vector3 pos, float speed = -1f) => CmdMove(pos);   // old speed arg: ignored (native has none)
        public void SetPos(Vector3 pos) => CmdSetPos(pos);
        public void MoveScreen(Vector3 screenPos) => CmdMoveScreen(screenPos);
        public void Anim(string name) => CmdAction(name);
        public void Lookat(int handle) => CmdLookat(handle);
        // Old finishcode had no obj: -2 sentinel skips the liveness check.
        public void SetFinishcode(string code) => CmdFinishcode(-2, code);
        // Old hide = instant invisible. Kept (NOT the faithful CmdHide) so the pre-rewire engine +
        // its tests keep today's behavior; the engine rewiring replaces this call with CmdHide.
        public void Hide() => Visible = false;
    }
}
