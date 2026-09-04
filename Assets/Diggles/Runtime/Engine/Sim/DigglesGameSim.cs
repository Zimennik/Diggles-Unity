#nullable enable

namespace Diggles.Runtime.Sim
{
    using System.Collections.Generic;
    using Diggles.Runtime.Tcl.Engine;
    using UnityEngine;

    // The fixed-step simulator. Mirrors the inner work of
    // CGameThread::SrvDoThink (re/decompiled/Diggles/_global/006b4a30_
    // CGameThread_SrvDoThink.c) — see Docs/ENGINE_ARCHITECTURE.md § 2.1
    // for the original phase breakdown.
    //
    // Each Step:
    // 1. Advances GameTime.
    // 2. Matures TimerScheduler entries into per-entity queues.
    // 3. Drains each entity's PendingEvents, evals handle_event body.
    // 4. Ticks each entity's current state body (if any).
    //
    // The driver MonoBehaviour (GameSimDriver) calls Step at a fixed
    // rate; the sim itself has zero Unity-Time dependency, so it can be
    // driven manually from tests too.
    public sealed class DigglesGameSim
    {
        public readonly DigglesTclContext Ctx;
        public readonly TimerScheduler Scheduler = new();

        // Simulation time in seconds. Independent of Unity's Time.time —
        // advances only via Step(dt). Tcl scripts read it via `gametime`.
        public double GameTime;

        // `gametime factor F` (FUN_0061ece0 writes gameThread+0xa090). The native clock advance
        // (FUN_005653f0) does `accum += factor * realDt`, and CGameThread::SrvDoThink's logic loop
        // is paced by that scaled accumulator (`while (nextThink <= gameTime) …`, FUN_006b4a30) —
        // so the factor scales the whole game thread: waits, timers,
        // actions, anim cadence, walk speed. Port equivalent: scale the dt every Step consumes.
        // start_001 uses 0.3 for the Matrix-jump slow-mo; scripts also pause with 0.
        public double TimeFactor = 1.0;

        // `gametime start/stop` (FUN_0061ece0: byte gameThread+0xa098 gates FUN_005653f0).
        public bool ClockRunning = true;

        // Snapshot buffers, reused across ticks to avoid per-frame alloc.
        readonly List<DigglesTclEntity> _snapshot = new();
        readonly List<DigglesTclEntity> _presentationCullSnapshot = new();

        // 10 Hz logic-tick accumulator (the original's pacing): CGameThread::SrvDoThink runs the
        // whole logic pass — planner, per-object event drain, CObj_HandleStates — inside
        // `while (nextThink <= gameTime) { … nextThink += DAT_007371a8 /*0.1*/ }` (006b4a30:129-153,
        // max 10 catch-ups). State thinks therefore fire at most 10/s of game time. The port keeps
        // movement/actions at the fine Step rate (their dt-summed durations are rate-independent and
        // the executors move transforms smoothly), but gates phase 3 + the dispatcher cooldown decay
        // on this accumulator — without it every script branch that returns without state_disable
        // re-ran at 40 Hz (4× the original; the post-cutscene churn amplifier).
        const double LogicTick = 0.1;   // DAT_007371a8
        double _thinkAccum;
        int _fowPhase;                  // native (game+0x6c)&3 — FoW runs on every 4th think

        public void CaptureCadence(out double thinkAccumulator, out int fogPhase)
        {
            thinkAccumulator = _thinkAccum;
            fogPhase = _fowPhase;
        }

        public void RestoreCadence(double thinkAccumulator, int fogPhase)
        {
            _thinkAccum = System.Math.Max(0.0, thinkAccumulator);
            _fowPhase = fogPhase & 3;
        }

        public DigglesGameSim(DigglesTclContext ctx)
        {
            Ctx = ctx;
            ctx.Sim = this;
        }

        // NOTE — no "boot window" here, by design. An earlier version ran the original's
        // pre-intro gap (Trigger_Urw_006 arms at [gettime]+5, Trigger_Urwald.tcl:928) as 50
        // instant steps before PlayCutscene. That contradicts the original's observable timeline:
        // those 5 seconds are live, on-screen play — the player watches the camp, the founders'
        // t=3 gender/clothes init flips visibly (zwerg.tcl:183 → z_methods init), and only then
        // the urw_3 proximity trigger fires. Compressing them into one frame (a) made the flip
        // look instant and (b) gave the AI 50 hidden ticks to issue walks, so the intro opened on
        // scattered, mid-stride gnomes. The faithful pre-roll now lives in
        // DigglesWorldRuntime (intro staged at GameTime+5, fired from Update while the sim runs
        // at real time). Sequence-freeze semantics in Step below are unchanged and faithful.

        // Step phase profiling. Section totals in ms; the
        // diag test flips ProfileStep and prints the table.
        public static bool ProfileStep;
        public static readonly System.Collections.Generic.Dictionary<string, double> ProfileStepMs = new();

        private static System.Diagnostics.Stopwatch? PhaseStart() =>
            ProfileStep ? System.Diagnostics.Stopwatch.StartNew() : null;

        private static void PhaseAdd(string label, System.Diagnostics.Stopwatch? clock)
        {
            if (clock == null) return;
            ProfileStepMs.TryGetValue(label, out double total);
            ProfileStepMs[label] = total + clock.Elapsed.TotalMilliseconds;
        }

        public void Step(double dt)
        {
            // Watchdog-visible section (never self-logs — the contextual slow log below owns
            // that): a freeze inside a step is named live by the background watchdog.
            using var __perfSec = Diggles.Runtime.Diagnostics.PerfTrace.Section("sim.step", double.MaxValue);
            long __perf = Diggles.Runtime.Diagnostics.PerfTrace.Now();
            var __phase = PhaseStart();
            DrainDeferredScripts();
            PhaseAdd("step.drainDeferred", __phase);
            // Sim time advance = realDt × factor (FUN_005653f0: accum += factor*dt, gated by the
            // running byte). At factor 0 (or stop) nothing below runs — the original's SrvDoThink
            // loop never reaches nextThink — except the camera, which the native render loop ticks
            // on real time (kept unscaled at the bottom).
            double sdt = ClockRunning ? dt * TimeFactor : 0.0;
            // NOTE: the anim time scale is published by the active frame-loop driver
            // (EngineTickService/GameSimDriver → DigglesAnimator.PublishTimeScale), never from
            // here — any stepped sim (stale scope, EditMode fixture, paused tool) would stomp
            // the global every step and freeze/slow every animator.
            if (sdt <= 0.0)
            {
                Ctx.Camera?.Tick(dt);
                return;
            }
            GameTime += sdt;

            // 1. Mature scheduled timer events → entity queues.
            __phase = PhaseStart();
            Scheduler.Mature(GameTime, OnEventMatured);
            PhaseAdd("step.mature", __phase);
            __phase = PhaseStart();

            // 2. Dispatch events. Snapshot first since handlers may
            // spawn / destroy entities; iterating the live dict
            // would throw.
            SnapshotEntities();
            foreach (var ent in _snapshot)
            {
                if (MaybeDead(ent)) continue;  // destroyed
                // Global cutscene freeze — event drain half. While sequence mode is on
                // (set_sequence 1, FUN_00649080 → DAT_00772400+0x8c), CGameThread::SrvDoThink
                // drains an object's event ring only if its sequenceactive bit is set:
                // `(flags & 0x1000000) && (!seqMode || (flags & 0x200000)) && classEnabled`
                // (FUN_005d38c0:138 +:185-187, the gate wrapping FUN_005cabe0 + HandleStates).
                // Events are not dropped — they stay queued and burst after set_sequence 0,
                // exactly like the native ring buffer. This is what keeps a mid-sequence summon
                // inert: the troll's obj_init timer evt_timer0 (troll.tcl:98 → troll_init +
                // state_enable) never delivers while the backdrop plays, so its wander AI can't
                // override the script. The trigger itself (init_sequencer `set_sequenceactive
                // this 1`, sequencer.tcl:530) and ambient life (Fisch/hamster/…) stay live.
                if (Ctx.SequenceActive && !ent.SequenceActiveFlag) continue;
                while (ent.PendingEvents.Count > 0)
                {
                    var evt = ent.DequeueEvent();
                    DigglesTclEngine.DispatchEvent(Ctx, ent, evt);
                }
            }

            PhaseAdd("step.dispatchEvents", __phase);
            __phase = PhaseStart();
            // 2b. Tick the active engine action on each entity; when it
            // finishes naturally, eval its Tcl finishcode (the completion
            // contract idle chains / walk callbacks rely on).
            SnapshotEntities();
            foreach (var ent in _snapshot)
            {
                if (MaybeDead(ent)) continue;
                if (ent.ActiveAction == null) continue;
                bool running;
                try { running = ent.ActiveAction.Tick(sdt); }
                catch (System.Exception e)
                {
                    UnityEngine.Debug.LogWarning($"action tick failed: {e.Message}");
                    running = false;
                }
                if (ent.ActiveAction.DrivesTransform) ent.PositionDirty = true;   // movers → 2c reads pos back; wait/anim don't move
                if (running) continue;
                var finish = ent.ActionFinish;
                var chainBreak = ent.ActionBreak;   // inherited by an action this finishcode chains into
                DigglesTclEngine.RestoreActionPlaybackSpeed(ent);
                ent.ActiveAction = null;
                ent.ActionFinish = null;
                ent.ActionBreak = null;
                if (!string.IsNullOrEmpty(finish))
                {
                    // Diagnostic ([FINTRC], armed with the anim trace): every action finishcode with
                    // its owner — catches stale followaction_list replays (a seq_enable popping a
                    // fight-anim `do_action` left over from another cutscene) and double finishes.
                    if (Diggles.Runtime.DigglesAnimController.TracePlayClip)
                        UnityEngine.Debug.LogWarning($"[FINTRC] {ent.Class?.Name}#{ent.Handle} finishcode: " +
                            $"{(finish!.Length > 160 ? finish.Substring(0, 160) + "…" : finish)}");
                    // finishcode runs in the owning entity's object scope (this + per-object
                    // `global` binding), so callbacks like `action this anim X {seq_idle}` resolve
                    // and a called proc's object globals bind per-object (e.g. create_muetze's
                    // current_muetze_ref/hatcolor — else cutscene hats cross-delete each other).
                    var prevChain = Ctx.ChainBreak;
                    Ctx.ChainBreak = chainBreak;
                    DigglesTclEngine.RunInEntityContext(Ctx, ent, finish!, "finishcode");
                    Ctx.ChainBreak = prevChain;
                }
            }

            PhaseAdd("step.actions", __phase);
            __phase = PhaseStart();
            // 2b2. Engine velocity (set_vel — CTclObjMethods::SetVel FUN_00685ec0 writes obj+0x28
            // vec3; the object tick integrates it into the position each game-time step).
            // Drives start_004's disco moonwalk slide and st009_end_2's sinking corpse.
            // Headless-safe: native Position always advances; a bound GameObject is posed via
            // the spawner (same conversion as set_pos). Units = native units per game-second
            // (scale pinned by the start_004 choreography staying on the menu meadow).
            foreach (var ent in _snapshot)
            {
                if (MaybeDead(ent)) continue;
                var vel = ent.Velocity;
                if (vel.sqrMagnitude <= 0f) continue;
                ent.Position += vel * (float)sdt;
                if (ent.GameObject != null && Ctx.EntitySpawner != null)
                    Ctx.EntitySpawner.ApplyPosition(ent.GameObject, ent.Position);
            }

            // 2c. Sync each entity's logical Position back from its GameObject transform. The move
            // executors (MetaWalk/WalkClimb) only drive the transform; without this, get_pos
            // returns the pre-move (e.g. beam/spawn) coords, so everything the sequencer derives
            // from live positions — rotate facing, formation pens, elf look/move targets — was
            // computed from stale positions (dwarves faced the wrong way, elf flew to old spots).
            // Only entities parented directly under the world root (skip linked hats/items that
            // ride a parent); the native↔world round-trip is exact, so static entities are
            // unchanged.
            var root = Ctx.SpawnParent;
            if (root != null)
            {
                foreach (var ent in _snapshot)
                {
                    // Only entities whose ActiveAction ticked this step drove the transform without
                    // updating Position (MetaWalk/climb do that). Idle entities, set_vel movers (2b2
                    // writes Position directly) and set_pos don't — so skipping them here avoids a
                    // native transform read + InverseTransformPoint per idle entity every step, the
                    // biggest per-step cost at fast-forward (up to 40 steps/frame). Cleared after read.
                    if (!ent.PositionDirty) continue;
                    ent.PositionDirty = false;
                    var go = ent.GameObject;
                    if (go == null || go.transform.parent != root) continue;
                    ent.Position = Diggles.Runtime.RuntimeWorldSpawner.LocalToNative(
                        root.InverseTransformPoint(go.transform.position));
                    // Facing read-back (the rotation half of this sync). The authoritative live
                    // yaw is DigglesObject.CurrentNativeYaw — movement rotates the outer
                    // transform; the inner Model keeps the constant spawn base pose. The first
                    // version of this sync read the Model child and serialized the same −π/2
                    // base yaw for every dwarf, later proven identical in both engines from the
                    // file bytes. Normalized to (−π,π] to match the native N06 rotation
                    // specimens. Movers only — untouched entities keep their archived rotation
                    // bits.
                    var facingSource = go.GetComponent<Diggles.Runtime.DigglesObject>();
                    if (facingSource != null &&
                        UnityEngine.Quaternion.Angle(
                            go.transform.rotation, facingSource.SpawnRotation) > 0.5f)
                    {
                        // Only when the mover actually turned from its spawn pose — an
                        // untouched entity keeps its spawn/native rotation words (a virgin
                        // CurrentNativeYaw would overwrite them with the constant basis yaw).
                        float nativeYaw = facingSource.CurrentNativeYaw();
                        if (nativeYaw > UnityEngine.Mathf.PI)
                            nativeYaw -= 2f * UnityEngine.Mathf.PI;
                        ent.Rotation = new UnityEngine.Vector3(
                            ent.Rotation.x, nativeYaw, ent.Rotation.z);
                    }
                }
            }

            PhaseAdd("step.velocityAndPosSync", __phase);
            __phase = PhaseStart();
            foreach (var ent in _snapshot)
            {
                if (MaybeDead(ent)) continue;
                ent.CaptureNativeCoreState();
            }
            PhaseAdd("step.captureCoreState", __phase);
            __phase = PhaseStart();

            // Native CTransportLogic door callbacks run alongside movement/path processing.
            // Re-evaluate conditional doors after transform-to-native position sync so their
            // blocker and opening animation follow the actors currently approaching them.
            DigglesTclEngine.TickTransportLogic(Ctx, sdt);

            // 3. State body ticks — on the 10 Hz logic tick only (see _thinkAccum above). Headless
            // tests stepping 0.1 get exactly one think per Step (faithful); the 40 Hz Play loop
            // thinks every 4th step. Catch-up capped at one logic tick per Step (the native's
            // 10-cap only matters after multi-second hitches; one Step is ≤0.1 s anyway).
            PhaseAdd("step.transportLogic", __phase);
            this._thinkAccum += sdt;
            bool thinkTick = this._thinkAccum >= LogicTick - 1e-9;
            if (thinkTick) this._thinkAccum -= LogicTick;
            if (this._thinkAccum > LogicTick) this._thinkAccum = LogicTick;   // drop backlog

            // 2d. Fog of war — native CFogOfWar::UpdateFoW runs from CGameThread::SrvDoThink on
            // every 4th think ((game+0x6c)&3==3, 006b4a30:393-421); the port mirrors that as
            // every 4th 10 Hz logic tick. Full RE: Docs/FOG_OF_WAR_RE.md. Fades all 8 owner
            // lanes, then every owned entity with set_fogofwar sizes reveals around its live
            // position (radial raycast with 3-tile rock penetration; negative sizes = the
            // no-collision box). Runs during cutscenes too (faithful — the native has no
            // sequence gate here); only the render is revealed while a sequence plays.
            __phase = PhaseStart();
            if (thinkTick && Ctx.FogOfWar != null && (++_fowPhase & 3) == 3)
            {
                var fog = Ctx.FogOfWar;
                fog.BeginUpdate(Ctx.LocalPlayer);
                SnapshotEntities();
                foreach (var ent in _snapshot)
                {
                    if (MaybeDead(ent)) continue;
                    if ((uint)ent.Owner >= Diggles.Runtime.World.FogOfWarState.Players) continue;
                    if (ent.ContainedIn >= 0) continue;   // packed items don't reveal
                    // Linked riders (hats/held tools) have a stale logical Position (2c sync
                    // covers only root-parented entities) — skip them as revealers; their
                    // carrier reveals the same area (gnome 14×8 ⊇ any hand-tool radius).
                    if (ent.GameObject != null && Ctx.SpawnParent != null
                        && ent.GameObject.transform.parent != Ctx.SpawnParent) continue;
                    // Native class-type-5 gate (0057f5c0:154-155): a production building reveals
                    // only once built — obj+0x3d0 bit 0x400000 is the port's BuildupComplete.
                    if (ent.Class?.Kind == "production" && !ent.BuildupComplete) continue;
                    float lx = ent.FowLastX, ly = ent.FowLastY;
                    int fogTransport = -1;
                    float fogTransportLength = 0f;
                    if (ent.TransportLogic?.Elevator is { } elevator)
                    {
                        fogTransport = 0;
                        fogTransportLength = elevator.ProjectedSpan;
                    }
                    else if (ent.TransportLogic?.Ladder is { } ladder)
                    {
                        fogTransport = 1;
                        fogTransportLength = ladder.ProjectedSpan;
                    }
                    fog.UpdateRevealer(ent.Position.x, ent.Position.y, ref lx, ref ly,
                                       ent.FowSizeX, ent.FowSizeY, ent.Owner, ent.Handle,
                                       fogTransport, fogTransportLength);
                    ent.FowLastX = lx;
                    ent.FowLastY = ly;
                    ent.FowLastZ = ent.Position.z;
                }
                fog.EndUpdate();

                // Object visibility (native render-job gate FUN_004845a0: no job unless
                // GetMaxFow(local) ≥ 0x80). viewinfog objects stand in for the native
                // CDummyObjMgr last-seen ghosts: static things stay drawn in explored gray
                // (byte ≥ 1). Fog stays live during cutscenes (faithful — the reveal-in-cutscene
                // extension was wrong; story scripts open their own stages via
                // remove_black_fog / FogRemover objects).
                bool fogHides = fog.Enabled && fog.ShowFog;
                var fowRoot = Ctx.SpawnParent;
                Diggles.Runtime.World.MapDecoPresentationCull.FillCullSet(
                    _snapshot,
                    Ctx.MapDecoSeeds,
                    _presentationCullSnapshot);
                foreach (var ent in _presentationCullSnapshot)
                {
                    bool saveSeed = ent.MapDecoSaveSeed;
                    if (!saveSeed && MaybeDead(ent)) continue;
                    var go = ent.GameObject;
                    if (go == null && !saveSeed) continue;
                    // Link children (hats / held tools — link_obj re-parents them under the
                    // carrier's Model via DigglesLinkAttachment): their logical Position is
                    // stale (the 2c sync only covers root-parented entities), and the native
                    // hides carried items with the carrier anyway (GetMaxFow walks the
                    // obj+0x278 chain; the carrier's renderer sweep includes its children).
                    // The carrier's sweep governs their renderers — only reset the flag here
                    // (not ApplyFogVisible: re-enabling a hat whose carrier is fog-hidden
                    // would float it in the dark). Using their stale (black-tile) Position here
                    // previously made carried items invisible.
                    if (go != null && fowRoot != null && go.transform.parent != fowRoot)
                    {
                        ent.FowHidden = false;
                        continue;
                    }
                    bool hidden = false;
                    if (fogHides)
                    {
                        int fow = DigglesTclEngine.GetMaxFow(Ctx, ent, fog.LocalPlayer);
                        bool viewInFog = ent.Class?.ViewInFog ?? false;
                        if (ent.VarsScope.Vars.TryGetValue("_viewinfog", out var vf))
                            viewInFog = vf == "1" || vf == "true";
                        hidden = viewInFog ? fow < 1 : fow < 0x80;
                    }
                    if (saveSeed)
                    {
                        byte lodTier = ent.MapDecoSeedRecord?.LodTier ?? 0;
                        Vector2 focus = Ctx.Camera?.Focus ?? Vector2.zero;
                        hidden |= !Diggles.Runtime.World.MapDecoPresentationCull.ShouldMaterialize(
                            ent.Position,
                            lodTier,
                            Diggles.Runtime.World.MapDecoPresentationCull.ActiveDetail,
                            focus,
                            false);
                    }
                    DigglesTclEngine.ApplyFogVisible(ent, hidden);
                }
            }
            PhaseAdd("step.fogOfWar", __phase);
            __phase = PhaseStart();
            // Native EnergyMgr load records count down once per SrvDoThink logic pass.
            if (thinkTick) Ctx.Energy.Tick();
            // Native CFluid advances from the same 10 Hz SrvDoThink pass.
            if (thinkTick) Ctx.Fluids.Tick();
            if (thinkTick) GnomeWaterReaction.Synchronize(Ctx);
            PhaseAdd("step.energyFluids", __phase);
            __phase = PhaseStart();
            // Snapshot only on think ticks — the loop below is a no-op on the other 3-of-4
            // steps (it breaks immediately), so rebuilding the O(N) snapshot for them was
            // pure waste. Think steps must re-snapshot: 2b finish-
            // codes can spawn/destroy entities.
            if (thinkTick) SnapshotEntities();
            foreach (var ent in _snapshot)
            {
                if (!thinkTick) break;
                if (MaybeDead(ent)) continue;
                if (ent.CurrentState == null) continue;
                // Frozen by a cutscene: `state_disable` (preset actor → count<0),
                // `park_obj_actions` (ambient object → Parked), or the global sequence freeze —
                // state-think half of the SrvDoThink gate (FUN_005d38c0:185-187: during
                // set_sequence 1, CObj_HandleStates runs only for objects with the
                // sequenceactive bit 0x200000). Covers mid-sequence summons, which neither
                // park_obj_actions (ran before they existed) nor add_actor's state_disable
                // (props are never actors) touches. The state machine stops thinking — only the
                // sequence's direct eactions drive it (phase 2b still ticks the active action) —
                // so summoned-class AI doesn't fight the cutscene. shut_sequencer's
                // state_enable / restore_obj_actions / set_sequence 0 un-freezes.
                if (ent.StateEnabledCount < 0 || ent.Parked
                    || (Ctx.SequenceActive && !ent.SequenceActiveFlag))
                {
                    // While a cutscene has frozen this actor (preset actors → state_disable, count<0;
                    // ambient objects → park, Parked), keep the auto face-mood off: the dwarf's
                    // blink/mood (actors.tcl/z_events.tcl, gated on `auto_fanim_state`) otherwise
                    // re-enables via a timer between the sequencer's explicit `sq_actor eyes/mouth`
                    // calls and overwrites them with a single-frame mood pose, so the scripted facial
                    // anim never sticks. The explicit calls aren't gated by this flag, so they play.
                    ent.VarsScope.Vars["auto_fanim_state"] = "0";
                    continue;
                }
                // State-think pacing — the faithful rule (no action gate at all). Ground truth:
                // • CObj::HandleStates (005cac90:27-29) gates on exactly three things: the per-object
                // think-frequency counter (+0x218 == 0; ctor interval 0 ⇒ thinks every tick),
                // state-enabled (+0x208 > 0) and has-a-state. There is no ActiveAction term.
                // • CGameThread::SrvDoThink (005d38c0:251 "… Active Objs States") calls HandleStates
                // unconditionally per active object, right after that object's event drain —
                // actions tick in their own independent pass.
                // • Pacing is the scripts' own job via the state_disable discipline: idle ends with
                // `set_idle_anim; state_disable this; action this wait 1 {state_enable this}`
                // (zwerg.tcl:316-318); work waits do the same (z_work_states.tcl:202-203,299-300);
                // every walk proc disables first and re-enables in its finish/break code
                // (z_dignwalk.tcl:594,610,624,652,680,805,818,834 + rotate procs:951-954).
                // The old `ActiveAction != null && TaskList.Count == 0` skip was an admitted band-aid:
                // with tasks queued it let a non-disabled state body re-enter every 0.025s step while
                // its action was in flight (a 3s walk ⇒ ~120 re-entries; play_anim restarted per tick —
                // the "anims x100 / hats snap instantly" Play bug), and with no tasks it masked
                // commanded walks that skip the state_disable discipline (those callers must adopt it,
                // as the original's click→Tcl walk_pos path does).
                if (ProfileStep)
                {
                    string stateLabel =
                        "state." + (ent.Class?.Name ?? "?") + "." + (ent.CurrentState ?? "?");
                    var stateClock = System.Diagnostics.Stopwatch.StartNew();
                    DigglesTclEngine.TickEntityState(Ctx, ent);
                    PhaseAdd(stateLabel, stateClock);
                }
                else
                {
                    DigglesTclEngine.TickEntityState(Ctx, ent);
                }
            }

            PhaseAdd("step.stateThink", __phase);
            __phase = PhaseStart();
            // 3a2. Native trigger scan (Objects/Trigger.cpp FUN_0060df40 ← CGameThread::SrvDoThink
            // 006b4a30:391): once per 10 Hz logic tick; each record self-gates on its own
            // checktimer, and the whole scan is off in sequence mode (FUN_0060df40:24 reads
            // the set_sequence byte DAT_00772400+0x8c — gate lives inside Scan).
            if (thinkTick) Ctx.TriggerService?.Scan();

            // 3a3. Map-deco pending tick (CMapBeautifier FUN_0058d370 ← CGameThread::SrvDoThink
            // 005d4250:87): one queued during-dig deco entry (tunnel beams/treasure) per
            // logic tick — spawns once its local dig is finished and the floor fits.
            if (thinkTick) DigglesTclEngine.MapDecoTick(Ctx);

            // 3a4. Tunnel torches (native glow-drop → Misc_Light auto-spawn: CObjMgr pass
            // FUN_005d4250:120-303 + the Autolight drop FUN_005cbf70:81-107 —
            // Docs/TUNNEL_TORCH_LIGHTING_RE.md): Autolight gnomes of the local player
            // drop glow points; matured points spawn wall torches. 10 Hz stand-in for
            // the native per-frame pass (cadence deviation: RE doc §8.1).
            if (thinkTick) DigglesTclEngine.TunnelTorchTick(Ctx);

            // Native AI owners are staggered across the 10 Hz game-thread counter and each run
            // once every 50 logic ticks. AiSystem preserves that cadence and isolated Tcl state.
            if (thinkTick) Ctx.Ai?.Tick(GameTime);

            // 3b. Autonomous work dispatch (native CProdPlanner). After state bodies have run (so
            // freshly-idle gnomes have registered via gnome_idle), assign idle gnomes to open jobs by
            // queuing evt_autoprod_* onto them — delivered on the next Step's phase-2 (faithful
            // SrvDoThink dispatch-then-HandleEvents order). Cooldowns are in logic ticks — decay
            // only on the 10 Hz tick (AssignCooldown=2 must last 0.2 s, not 0.05 s at 40 Hz).
            PhaseAdd("step.triggersDecoTorchAi", __phase);
            __phase = PhaseStart();
            if (thinkTick) Ctx.ProdDispatcher?.DecayCooldowns();
            Ctx.ProdDispatcher?.Tick(sdt);
            PhaseAdd("step.prodDispatcher", __phase);
            __phase = PhaseStart();

            // 4. Interpolate the cinematic camera toward its target view / followed object,
            // and count down dialogue/subtitle timers. All game-thread mirrors consume the
            // scaled dt (slow-mo slows subtitles/voice windows/particles/elf too); the camera
            // alone stays on real time — the native camera ticks in the render loop.
            Ctx.Camera?.Tick(dt);
            Ctx.Dialogue?.Tick(sdt);
            Ctx.NewsTicker.Tick(sdt);
            Ctx.Audio?.Tick(sdt);
            Ctx.Particles?.Tick(sdt);
            Ctx.ScreenFx?.Tick(sdt);
            Ctx.Elf?.Tick(sdt);

            PhaseAdd("step.uiTickers", __phase);
            // Hitch labeller: a single sim Step normally costs microseconds; >100 ms means a synchronous
            // burst happened inside it — the cutscene-skip sequencer drain, or a dig-triggered worldgen
            // generation running on the StoryMgr tick. Labels the Play freeze so the fix targets the right
            // op. The context string is composed only on the slow path (the old eager $"gt=…" interpolation
            // allocated on every step — 40 allocs/s of pure garbage on the hot path).
            double __ms = Diggles.Runtime.Diagnostics.PerfTrace.MsSince(__perf);
            if (__ms >= 100 && Diggles.Runtime.Diagnostics.PerfTrace.Enabled)
                UnityEngine.Debug.LogWarning($"[perf] sim.step {__ms:F0} ms | gt={GameTime:F1} ents={Ctx.Entities.Count}");
        }

        void DrainDeferredScripts()
        {
            int count = Ctx.DeferredScripts.Count;
            for (int i = 0; i < count; i++)
                Ctx.Interp.Eval(Ctx.DeferredScripts.Dequeue());
        }

        void OnEventMatured(int handle, EntityEvent evt)
        {
            // timer_unset by user ID (FUN_0069fc90/FUN_00606110): when several timers share an
            // event name on one handle the scheduler entry can't be removed selectively, so
            // the cancelled fire is dropped here at delivery instead (TimerRegistry tracks
            // the per-timer serial in the payload). Non-timer events pass straight through.
            if (evt.Payload != null && Ctx.Timers.FilterDelivery(evt.Payload))
                return;
            if (Ctx.Entities.TryGetValue(handle, out var ent))
                ent.EnqueueEvent(evt, GameTime);
            // else: entity was destroyed before its timer matured —
            // drop silently (matches Diggles which just GCs the timer).
        }

        // Version of Ctx.Entities the current _snapshot was taken at. While the registry is
        // unchanged (the common case), the per-entity "was it destroyed mid-loop?" ContainsKey
        // probes are skipped — any spawn/destroy bumps Ctx.EntitiesVersion and re-enables them.
        int _snapshotVersion;

        // True when `ent` (from the current snapshot) may have been destroyed since the
        // snapshot. Registry unchanged ⇒ provably still alive ⇒ no dictionary probe.
        bool MaybeDead(DigglesTclEntity ent)
            => Ctx.EntitiesVersion != _snapshotVersion && !Ctx.Entities.ContainsKey(ent.Handle);

        void SnapshotEntities()
        {
            _snapshot.Clear();
            foreach (var kv in Ctx.Entities) _snapshot.Add(kv.Value);
            _snapshotVersion = Ctx.EntitiesVersion;
        }
    }
}
