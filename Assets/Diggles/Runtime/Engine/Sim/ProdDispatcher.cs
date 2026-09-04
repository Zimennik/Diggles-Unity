#nullable enable

namespace Diggles.Runtime.Sim
{
    using System.Collections.Generic;
    using Diggles.Runtime.Tcl.Engine;
    using UnityEngine;

    // Native PlaceFinder reservation list (Map/PlaceFinder.cpp +0x62 lock array). A flat set of timed
    // {pos,id} reservations so two gnomes don't converge on the same spot. Positions are native
    // (X,Y horizontal, Z height), so proximity is horizontal. The Tcl idiom:
    // if {![placelock_check $sp 0.7 $myref]} {placelock_set $sp 1 $myref; walk}
    // id = the gnome handle (a gnome may hold a 2nd lock at handle+65536). `now` = Sim.GameTime.
    public sealed class PlaceLockStore
    {
        struct Lock { public Vector3 Pos; public double Expiry; public int Id; }
        readonly List<Lock> _locks = new();

        public void Set(Vector3 pos, double dur, int id, double now)
        {
            for (int k = 0; k < _locks.Count; k++)
                if (_locks[k].Id == id) { _locks[k] = new Lock { Pos = pos, Expiry = now + dur, Id = id }; return; }
            _locks.Add(new Lock { Pos = pos, Expiry = now + dur, Id = id });
        }

        // True if any non-excepted, unexpired lock is within `radius` (horizontal) of pos. GCs expired.
        public bool Check(Vector3 pos, float radius, int exceptId, double now)
            => Check(pos, radius, exceptId, exceptId, now);

        // Two-except form: get_place honours both the asking entity's own locks and an explicit
        // `-placelockidexcept <id>` (native CPlaceFinder option, Tcl GetPlace FUN_00656560:995-1032)
        // — e.g. sparetime_slp_start excepts $myref while standing on its own walk-target lock
        // (z_spare_procs.tcl:519,529) and walk_down_from_wall does the same (z_dignwalk.tcl:869).
        public bool Check(Vector3 pos, float radius, int exceptIdA, int exceptIdB, double now)
        {
            float r2 = radius * radius;
            bool hit = false;
            for (int k = _locks.Count - 1; k >= 0; k--)
            {
                if (_locks[k].Expiry <= now) { _locks.RemoveAt(k); continue; }
                if (hit || _locks[k].Id == exceptIdA || _locks[k].Id == exceptIdB) continue;
                float dx = _locks[k].Pos.x - pos.x, dy = _locks[k].Pos.y - pos.y;
                if (dx * dx + dy * dy <= r2) hit = true;
            }
            return hit;
        }

        public void Rem(int id) => _locks.RemoveAll(l => l.Id == id);
        public void Clear() => _locks.Clear();

        // Position of the (unexpired-or-not) lock held by `id`, if any — lets the dispatcher
        // recover which job a failing gnome had reserved when gnome_failed_work releases it.
        public bool TryGet(int id, out Vector3 pos)
        {
            for (int k = 0; k < _locks.Count; k++)
                if (_locks[k].Id == id) { pos = _locks[k].Pos; return true; }
            pos = default;
            return false;
        }
    }

    // C# port of the native CProdPlanner work dispatcher (AI/AiCentral.cpp, Map/DigMark.cpp,
    // Objects/TransportLogic.cpp). Ticked once per sim Step. For every player including the human
    // (player 0) it: (1) holds an idle-gnome pool entered via the gnome_idle hook, (2) builds the
    // open-job list (dig/harvest/production/pack/unpack; haul next), and (3) dispatches one job
    // per maintenance pass by raising the matching evt_autoprod_* event onto the gnome — the
    // unchanged Tcl evt_autoprod_*_proc handlers then drive work_auto → work_dispatch. It raises
    // evt_autoprod_*, not evt_task_* (that is the direct user-click path).
    //
    // Two native allocators exist and are mutually exclusive on the multiplayer flag DAT_0076837a:
    // • single-player: CProdPlannerMgr::Tick FUN_00432720:30 runs CProdPlanner_MaintenanceTick
    // (FUN_00433a70) only while DAT_0076837a == 0 — jobs become per-gnome step plans
    // (FUN_00439da0 builders → steps emitted one per gnome-idle round-trip via FUN_0043a460 →
    // FUN_0043a8a0). A boxed-crate unpack therefore dispatches as
    // muetze → walk → pickup(crate) → walk → unpack (FUN_00439990).
    // • multiplayer: FUN_005eed70:39 runs the 005f ProdAllocator (collect FUN_005f0850, greedy
    // assign FUN_005f3600, direct one-event emit FUN_005f3a50) only while DAT_0076837a != 0.
    // The port models the single-player planner; 005f citations below are kept where both agree
    // (request bytes, contained gate). Full spec: Docs/WORK_DISPATCHER_NATIVE.md.
    public sealed class ProdDispatcher
    {
        public enum JobKind { Dig, Harvest, Production, Pack, Unpack, Haul }

        struct Job
        {
            public JobKind Kind;
            public int TargetHandle;   // object the work is on (Pilz/building); -1 for dig
            public string WorkClass;   // resource/item class (-text1)
            public int Tx, Ty;         // dig tile
            public Vector3 Pos;        // native job position (for matching + reservation)
            public float Need;         // priority — higher dispatched first
            public float PreferenceScore;
            public bool AsBox;         // unpack only: place as bare box (rec+0x2d → job+0x24 → -num1)
            public int ItemHandle;     // haul only: the concrete source item (kind-4 job+0x08, FUN_004375c0:165)
            public string ForItem;     // haul only: the product the fetched material serves (UI surface)
        }

        // ---- per-gnome step plans (native CProdPlanner plan array, planner+0x6c) ----------------
        // Kind-4 transport and kind-1 boxed-crate unpack jobs expand into step plans, dispatched
        // one step per gnome-idle round-trip (MaintenanceTick FUN_00433a70:355-407 → step advance
        // FUN_0043a460 → Tcl emit FUN_0043a8a0):
        // haul (builder FUN_0043c660): change_muetze "transport" → walk → pickup(item) →
        // walk → transferprod(item, place);
        // unpack (builder FUN_00439990): change_muetze "unpack" → [boxed: walk → pickup(crate),
        //:81-113] → walk → unpack(crate, orderpos, asbox) (:114-141). A not-boxed
        // target (aufbau resume / repair) skips straight to the unpack step with the
        // object's own position (:81-84).
        // Our faithful plan keeps the muetze step plus the state-carrying steps (pickup, then the
        // final event); the walk legs are the live Tcl procs' own (pickup → walk_near_item,
        // z_procs.tcl:477; prod_bringtoprod → walk_near_item, z_work_common.tcl:92-95;
        // prod_autounpack → walk_pos, z_work_common.tcl:328-336). The boxed-unpack muetze step
        // ('evt_change_muetze -text1 unpack', FUN_0043a8a0:44-48 → evt_change_muetze_proc →
        // prod_change_muetze unpack → Dummy_Muetze_dienstleistung, z_work_common.tcl:823) is
        // kept — dropping it loses the carrier hat on the fetch leg. The haul plan keeps its own
        // muetze step too ('evt_change_muetze -text1 transport', FUN_0043c660:189 →
        // FUN_00431f20("transport")). Documented port divergences: the not-boxed kind-1
        // (resume/repair) skips its muetze step — evt_autoprod_unpack_proc re-hats immediately
        // (prod_change_muetze service, z_events.tcl:1326) so nothing is visibly lost; the native
        // batches further nearby haul items into one carrier trip up to inventory-capacity−2
        // within weighted item/place dist² < 40 = DAT_007375bc (FUN_0043c660:89-92,:121-188) —
        // we deliver one item per trip.
        sealed class StepPlan
        {
            public JobKind Kind;       // Haul or Unpack
            public int ItemHandle;     // haul: the primary item (Items[0]); unpack: the crate
            public int DestHandle;     // haul only: the primary destination (Dests[0])
            public string ItemClass = "";
            public string ForItem = "";// haul only: the product the material serves (UI surface)
            public Vector3 OrderPos;   // unpack only: the deferred-order position (rec+0x30)
            public bool AsBox;         // unpack only: the asbox byte (rec+0x2d)
            // Unpack stages: 0 = muetze emitted (the plan's first step, FUN_00439990:72-80),
            // 1 = pickup emitted, 2 = the final unpack emitted.
            // Haul stages (the batched kind-4 plan, FUN_0043c660): 0 = muetze emitted; stages
            // 1..N = pickup k emitted (:195-198 primary,:237-244 each batch rider); stages
            // N+1..2N = transferprod k emitted (:258-273 primary,:282+ riders). N = Items.Count.
            public int Stage;
            // ---- kind-4 batch (FUN_0043c660:121-188): the carrier serves multiple kind-4
            // jobs in one trip — parallel item/dest/class/forItem lists, [0] = the primary.
            public readonly List<int> Items = new();
            public readonly List<int> Dests = new();
            public readonly List<string> Classes = new();
            public readonly List<string> ForItems = new();
        }
        readonly Dictionary<int, StepPlan> _plans = new();   // gnome handle → active plan

        // Test/diagnostic: does this gnome hold an active step plan?
        public bool HasPlan(int gnomeHandle) => _plans.ContainsKey(gnomeHandle);

        public void ResetTransientState()
        {
            _plans.Clear();
            _failBreather.Clear();
            _jobReopen.Clear();
            for (int player = 0; player < Players; player++)
            {
                _idle[player].Clear();
                _digRequests[player].Clear();
                _digScanX[player] = 0;
                _digScanY[player] = 0;
            }
            _accum = 0.0;
            _cursor = 0;
            // Documented save deviation: the native serializes the slot priority (CProd
            // slot+0x14) in the.sav record (FUN_005ed160:52) — the port drops the mirror on
            // reset/load and re-earns the ramp (≤ 200 s to the cap; the invention-vs-request
            // crossover re-arms from zero). Flagged for the save lane.
            _slotPrio.Clear();
            _logicTick = 0;
        }

        // Recipe cache: "<placeClass>\n<itemType>" → material class list (the native global recipe
        // table FUN_0043e4d0 is fed from the same techtreetunes data genericprod.tcl compiles into
        // each class's prod_item_materials method — we read it through the live Tcl method).
        readonly Dictionary<string, List<string>> _materialsCache = new();

        const int Players = 8;
        const float DispatchInterval = 0.5f;   // amortized sweep cadence (native DAT_007372a8 = 1.0 dbl; ours finer)
        const int AssignCooldown = 2;          // gnome+0x3e8 re-task latency, in ticks
        // FUN_00438210 constants decoded from the retail executable's.rdata.
        private const float PreferredWorkplaceDistanceScale = 0.125f;
        private const float OtherWorkplaceDistanceScale = 10f;
        private const float PreferredWorkplaceScoreBonus = 8f;
        private const float ProductionDistanceScoreDivisor = 5f;

        // ---- failure breathers (anti-thrash, decompile-verified) -------------------------------
        // All cooldowns below are expressed in logic ticks = 0.1 s units, decremented once per
        // DecayCooldowns call (today: once per sim Step; soon: the dedicated 10 Hz logic tick).
        //
        // FailRetaskDelayTicks — the native Tcl GnomeFailedWork/GnomeStoppedWork handler
        // (FUN_00676500) stamps the failing gnome's planner record +0x48 = 0x40a00000 = 5.0f, and
        // the allocator's gnome-match scan FUN_00438210:35-38 skips the gnome until
        // (gametime − idle-since(rec+0x38)) ≥ that delay. 5.0 s = 50 logic ticks.
        // Port-side divergence (documented anti-thrash): the native one-shot re-task FUN_00434bc0
        // zeroes rec+0x48 on the next gnome_idle — it can afford to, because it only takes jobs
        // the ~1 Hz ProdAllocator already planned (and the failed job's own +0x3e8=0x14 target
        // cooldown bridges the gap). Our OnGnomeIdle re-plans the job list at think-tick rate, so
        // an early clear would resurrect the assign→fail→reassign storm; we hold the embargo for
        // the full 5 s while the gnome stays idle, and clear it when it leaves the pool for real
        // work (OnGnomeBusy — the same moment the native clear fires).
        const int FailRetaskDelayTicks = 50;
        //
        // JobReopenTicks — the job-release path FUN_0043a0c0 (called by FUN_00676500) stamps the
        // released job target object's +0x3e8 = 0x14 = 20 ticks (and CObj::Tick's ProdGnome block
        // keeps it pinned ≥ 0x14 while the job is worked), so the planner cannot immediately
        // re-dispatch the very job that just failed — to this gnome or anyone else. The native
        // tick unit for +0x3e8 is the same counter our AssignCooldown=2 already uses; ported
        // 1:1 as 20 logic ticks = 2.0 s.
        const int JobReopenTicks = 20;
        // Reservation/job-identity radius (same scale the Reserve placelock check uses).
        const float JobReopenRadius = 0.8f;
        //
        // ---- dig request funnel (FUN_0043bdd0 scan + FUN_00435f50 job builder) --
        // The native dig pipeline is not per-marked-tile jobs: each player's planner keeps a
        // persistent request list {x, y, timer, age} (planner+0x3074, stride 0x10) fed by an
        // incremental ~2000-cells/pass scan of the dig-mark bbox (FUN_0043bdd0:22). A scan
        // hit is skipped while a request exists within Manhattan<8 (:333) and a new request is
        // rejected while one exists within Manhattan<20 (:270-271) — so a whole 10×10 marked
        // rect funnels into one request → at most one kind-5 job → one digger. The announce
        // (gnome_announce_dig → FUN_0043bc90:41-51, fired from dig_starttask z_dignwalk.tcl:143,
        // re-stamped every dig cycle:236/:313 and at job assignment, FUN_0043a460:55-72)
        // refreshes timer=10.0 on every request within Manhattan<20; the job builder forces the
        // request's job need to 0 while timer>0 (FUN_00435f50:142-156) and deletes need-0 kind-5
        // jobs (:163-203) — the native "no second digger on my area" mechanism. Note: the
        // strength-50/decay-1.0 announce list in FUN_0067a450:90-117 is dead multiplayer code
        // (gated on DAT_0076837a≠0,:90); single-player takes the:133-135 branch into
        // FUN_0043bc90 — the timer model below. All float constants byte-verified against
        // Diggles.exe.rdata.
        const int DigScanBudget = 2000;                 // FUN_0043bdd0:206
        const int DigScanSkipManhattan = 8;             //:333 — scan point near a live request
        const int DigRequestMergeManhattan = 20;        // 0x14 (:270-271 + FUN_0043bc90:45)
        const int DigDiggerRefreshManhattan = 5;        //:100 — an active digger refreshes its request
        const float DigAnnounceTimer = 10f;             // 0x41200000 (FUN_0043bc90:50, FUN_0043bdd0:104)
        const float DigRequestInitialTimer = 1f;        // 0x3f800000 (:132/:305)
        const float DigNeedExisting = 7.5f;             // 0x40f00000 (FUN_00435f50:137)
        const float DigJobZ = 14f;                      // 0x41600000 = DAT_00737550 (FUN_00435f50:113)
        // Kind-5 gnome-match extras (FUN_00438210:139-185,.rdata-verified):
        const float DigMatchMaxCost = 120f;             // DAT_007683a4 — hard gate on the weighted dist²
        const float DigCrowdNearDist2 = 15f;            // DAT_00737568 — horiz dist² of another active digger
        const float DigCrowdFarDist2 = 30f;             // DAT_007375a4
        const float DigCrowdPenalty = 1.5f;             // DAT_007371c0 (both gates hit ⇒ ×2.25)
        //
        // ---- harvest emit cadence (FUN_005f1500 / FUN_005f4220) --------------------------------
        // Both native harvest emitters run on a ≥5 s period (DAT_007373a8 = 5.0 dbl, 005f1500:53 /
        // 005f4220:56), emit AT most one kind-2 job per pass, and push their next pass another
        // +10 s out after emitting (_DAT_007373f0 = 10.0 dbl, 005f1500:159 / 005f4220:279) —
        // i.e. ~15 s between harvest dispatches per player. Port divergence (documented): our job
        // list is rebuilt per sweep (no persistent array), so the cooldown is armed on assignment
        // and ReleaseGnome clamps it down to the job-reopen delay — a failed dispatch retries on
        // the native 20-tick reopen schedule instead of burning the full 15 s.
        const int HarvestEmitCooldownTicks = 150;       // (5 + 10) s × 10 Hz
        // Harvest job weight = deficit × DAT_00737054 = 0.2 (005f1500:146, 005f4220:255).
        const float HarvestNeedScale = 0.2f;

        // Kind-4 batch distance gate: weighted dist² threshold for folding a further haul job
        // into the same carrier trip (DAT_007375bc = 40.0f, FUN_0043c660:168).
        const float HaulBatchDist2 = 40f;

        // ---- native slot priority (the CProd slot record +0x14 float, 0..1) --------------------
        // "CObj::Tick Prod" (CObj_Tick FUN_005cc600:291 → FUN_005edea0:49-85) ramps every
        // queued production slot once per 10 Hz logic tick: count==0 ⇒ priority resets to 0
        // (:59-60,:82); count>0 ⇒ priority += DAT_00736fb0 = 0.0005 (finite order) or
        // DAT_00736fac = 0.0003 (count==10 — the panel's ∞ order, same marker FUN_0069e210:83
        // keys the no-decrement branch on), clamped at DAT_00737138 = 1.0 (:71-79). The planner
        // consumes it as the job need:
        // kind-2 invention need = priority × DAT_00737534 = 10.0 (FUN_00434e60:646-647)
        // kind-3 production need = priority × DAT_00737698 = 1000.0 (FUN_00436750:26,
        //:10 also selects the slot with the max priority)
        // versus the request jobs' constant 7.5 (0x40f00000, FUN_00436440:87/:118). So a queued
        // invention starts at need ≈ 0 and needs ≥ 1500 unserviced logic ticks (~150 s; 2500 at
        // the ∞ ramp) before it can outrank a user pack/buildup order — the queue count never
        // multiplies the need (an earlier revision need = count+2 let a 6+ queue beat the pack
        // instantly). A completed item decays the slot ×DAT_00737054 = 0.2
        // (finite) / ×DAT_00737034 = 0.1 (∞) (FUN_0069e210:83-96) — invention slots never reach
        // that path (completion flips them invented and out of the kind-2 scan). All constants
        // byte-read from Diggles.exe.rdata.
        const float SlotPrioRampFinite = 0.0005f;     // DAT_00736fb0 (FUN_005edea0:71-75)
        const float SlotPrioRampInfinite = 0.0003f;   // DAT_00736fac (count==10, FUN_005edea0:66-73)
        const float SlotPrioMax = 1f;                 // DAT_00737138 (FUN_005edea0:76-79)
        const float InventionNeedScale = 10f;         // DAT_00737534 (FUN_00434e60:646-647)
        const int InfiniteOrderCount = 10;            // FUN_005edea0:66 / FUN_0069e210:83

        // Per-(place,slot) mirror of the CProd slot+0x14 priority. The native ramps it inside
        // each building's own 10 Hz object tick; the port advances it lazily by the logic ticks
        // elapsed since the last read (same aggregate ramp for a steady count; a count 0↔N flap
        // between reads quantizes to the read cadence — documented shortcut). Divergences kept
        // small and documented: (a) the native persists slot+0x14 in the.sav CProd record
        // (FUN_005ed160:52 serializes it) — the port re-earns the ramp after load
        // (ResetTransientState clears the mirror); (b) the FUN_00434e60:587-596 backoff (zero
        // the priority when the slot's availability flag +0x19 never got stamped this pass) is
        // not modelled —:573-576 stamps +0x19 for every examined gnome, so under the shipped
        // data the flag is only clear for a slot with no qualification scan at all, and an
        // unqualifiable-for-now invention keeps accruing urgency exactly like a native slot
        // whose gnomes were examined and rejected at match time (FUN_00438210:88-102).
        struct SlotPrio { public float Value; public long Tick; }
        readonly Dictionary<(int place, string item), SlotPrio> _slotPrio = new();
        long _logicTick;   // advanced once per DecayCooldowns — the port's 10 Hz logic-tick surface

        // Advance-and-read the slot's priority (FUN_005edea0:49-85; see the block above).
        float SlotPriority(int place, string item, int count)
        {
            var key = (place, item);
            if (count <= 0) { _slotPrio.Remove(key); return 0f; }   // count==0 ⇒ reset (:59-60,:82)
            float rate = count == InfiniteOrderCount ? SlotPrioRampInfinite : SlotPrioRampFinite;
            if (!_slotPrio.TryGetValue(key, out var p))
                // First observation seeds one tick of ramp: the order was queued strictly
                // before this pass, and the building's own 10 Hz CProd tick has always lifted
                // a queued slot past 0 by the time any native maintenance pass reads it — a
                // fresh order is need>0 (dispatchable when nothing outranks it) but still
                // three orders of magnitude under the 7.5 request weight.
                p = new SlotPrio { Value = rate, Tick = _logicTick };
            long dt = _logicTick - p.Tick;
            if (dt > 0) p.Value = Mathf.Min(SlotPrioMax, p.Value + rate * dt);
            p.Tick = _logicTick;
            _slotPrio[key] = p;
            return p.Value;
        }

        // The worktime clock (shared with gettime/gethours/get_worktime/get_remaining_sparetime and
        // the worktime gate). RE-proven, binary-verified model (DAT_00737630 = 150.0f): gettime
        // advances in real seconds (factor 1.0), and every
        // native consumer divides by 150 to get clock-hours — GetWorkTime FUN_0068b790:110,
        // GetRemainingSpareTime FUN_006955d0:59, ViWinClock draw FUN_0050ab10:669. One clock-hour
        // = 150 s; one day = 12 h = 1800 s (the scripts' `N*1800` age constants are days — the
        // earlier "60 s hour" tune ran the dial 2.5× fast and starved the Tcl's per-second
        // needs-decay pacing of day-cycles).
        public const double WorktimeHourSeconds = 150.0;
        public const double HourSec = WorktimeHourSeconds;   // alias used by the P6 clock code
        public const double DayHours = 12.0;

        readonly DigglesTclContext _ctx;
        readonly HashSet<int>[] _idle = new HashSet<int>[Players];
        double _accum;
        int _cursor;

        // Failure-breather state (see the constants above). The gnome stays in the idle pool while
        // breathing (native: rec+0x0c==2/+0x45 keep it poolable; only the match scan skips it,
        // FUN_00438210:29-38) — it just can't be matched to a job until the delay runs out.
        readonly Dictionary<int, int> _failBreather = new();          // gnome handle → ticks left
        struct ReopenLock { public Vector3 Pos; public int Ticks; }
        readonly List<ReopenLock> _jobReopen = new();                 // failed-job positions cooling down

        // The persistent per-player dig-request list (planner+0x3074, FUN_0043bdd0 — see the
        // constants block). Timers decay 1.0 in the job-build phase (FUN_00435f50:53-54) plus
        // 1.0 in the scan phase (FUN_0043bdd0:149-150) = 2.0 per player pass ≈ 0.5/s on the
        // 4 s/player round-robin cadence.
        sealed class DigRequest
        {
            public int X, Y;          // +0x00/+0x04 — the request tile (rounded)
            public float Timer;       // +0x08 — announce/closure timer; >0 ⇒ job need forced 0
            public float Age;         // +0x0c — passes spent open; need = age*0.5+1.0
            public bool HadJob;       // port stand-in for "a kind-5 job already exists" (:88-93)
        }
        readonly List<DigRequest>[] _digRequests = new List<DigRequest>[Players];
        readonly int[] _digScanX = new int[Players];   // +0x307c — the incremental scan cursor
        readonly int[] _digScanY = new int[Players];   // +0x3080

        // gnome_announce_dig — refresh timer=10.0 on every request within Manhattan<20 of the
        // announced point (FUN_0067a450:133-135 → FUN_0043bc90:41-51; round, not floor). Applied
        // to all players' lists (only the local player's is ever populated — single dig grid).
        public void AnnounceDig(int tx, int ty)
        {
            for (int p = 0; p < Players; p++)
            {
                var reqs = _digRequests[p];
                for (int k = 0; k < reqs.Count; k++)
                    if (Manhattan(reqs[k].X - tx, reqs[k].Y - ty) < DigRequestMergeManhattan)
                        reqs[k].Timer = DigAnnounceTimer;
            }
        }

        // True while a request near the tile is announce-closed (FUN_00435f50:142-156 need=0).
        public bool DigAnnounced(int tx, int ty)
        {
            for (int p = 0; p < Players; p++)
                foreach (var r in _digRequests[p])
                    if (r.Timer > 0f && Manhattan(r.X - tx, r.Y - ty) < DigRequestMergeManhattan)
                        return true;
            return false;
        }

        // Test/diagnostic: live request count for a player.
        public int DigRequestCount(int player) => _digRequests[ClampPlayer(player)].Count;

        static int Manhattan(int dx, int dy) => (dx < 0 ? -dx : dx) + (dy < 0 ? -dy : dy);

        // Per-player harvest emit cooldown, in logic ticks (FUN_005f1500/005f4220 cadence).
        readonly int[] _harvestCooldown = new int[Players];

        public ProdDispatcher(DigglesTclContext ctx)
        {
            _ctx = ctx;
            for (int i = 0; i < Players; i++)
            {
                _idle[i] = new HashSet<int>();
                _digRequests[i] = new List<DigRequest>();
            }
        }

        static int ClampPlayer(int owner) => (owner >= 0 && owner < Players) ? owner : 0;

        // Test/diagnostic: is this gnome currently registered idle/available?
        public bool IsPooled(int handle, int owner) => _idle[ClampPlayer(owner)].Contains(handle);

        // Save-restore seam: re-seed idle-pool membership for a gnome whose saved planner
        // idle-announce flag (native planner sub+0x34 == 1) had it offering itself to the auto-work
        // pool at save time. This adds only the pool membership — unlike OnGnomeIdle it does
        // not dissolve worker claims, advance a step plan, or run any work-window / auto-schedule
        // gate, and it never ticks the dispatcher. The transient re-task cooldown and idle-since are
        // re-earned on the first ordinary post-load maintenance pass.
        public void RestoreIdlePoolMembership(DigglesTclEntity? g)
        {
            if (g == null) return;
            _idle[ClampPlayer(g.Owner)].Add(g.Handle);
            g.PlannerIdleAnnounceFlag = 1;
        }

        // ---- gnome_idle hook (native CProdPlanner::GnomeIdle FUN_00434a80) ----
        // `gnome_idle this 1` → register available. The native one-shot re-task FUN_00434bc0:12-21
        // only advances an existing plan (FUN_0043a050 finds the gnome's current plan; -1 → plain
        // release FUN_0043a0c0) — it never matches new jobs. New work waits for the periodic
        // MaintenanceTick sweep (FUN_00433a70:408-450). The former port one-shot job match here
        // re-planned the whole job list on every idle report at think-tick rate and has been
        // removed.
        public void OnGnomeIdle(DigglesTclEntity? g)
        {
            if (g == null || g.WorkBusyCooldown > 0) return;
            int p = ClampPlayer(g.Owner);
            if (_idle[p].Add(g.Handle))
                g.PlannerIdleSince = (float)(_ctx.Sim?.GameTime ?? 0.0);
            g.PlannerIdleAnnounceFlag = 1;
            // Reporting idle dissolves any prodplace claim this gnome still holds — the native
            // plan teardown zeroes the place's gnome ref rec+0x24 when the planner state returns
            // to idle (FUN_0043a0c0:31). Without this, a builder/worker that finished normally
            // would keep its building claimed forever and block the one-worker production gate
            // (ProductionPlaceAvailable) for that place. not while a step plan is active: the plan's
            // idle reports are the step advances, and the native job dedup FUN_00438b80 keeps a
            // place untouchable while any in-flight plan references it — the claim is the port's
            // equivalent, so it must outlive the plan's intermediate idles (else a mid-haul
            // building re-collected a second haul/workat every sweep).
            if (!_plans.ContainsKey(g.Handle))
            {
                g.PlannerCurrentWorkplaceId = -1;
                foreach (var e in _ctx.Entities.Values)
                    if (e.CurrentWorkerHandle == g.Handle) ReleaseWorkerClaim(e);
            }
            // A gnome mid-plan advances its step plan — the surviving faithful piece of the
            // one-shot (FUN_00434bc0:12-21 → FUN_0043a050 finds the existing plan →
            // FUN_0043a460 steps it). The advance runs before (and regardless of) the
            // work-window/auto-schedule gate below — those gate pool entry for new work
            // (FUN_00433a70:112), while the one-shot advances an existing plan on every
            // gnome_idle. The former ordering (gate first) wedged any plan whose gnome
            // idle-reported outside its work window: the plan could neither advance nor drop,
            // every claimed destination stayed claimed forever and the workplace went dead.
            if (_plans.TryGetValue(g.Handle, out var plan)) { AdvancePlan(g, plan); return; }
            // Auto-schedule gate: the dispatcher only auto-acts while the gnome's flag is on —
            // CProdPlanner_MaintenanceTick FUN_00433a70:112 `+0x35d != 0 && (obj+0x3d0 & 0x800000)==0`
            // gates pool entry, so a manual-schedule gnome never receives evt_autoprod_*.
            if (!IsWorkTime(g) || !g.ProdAutoSchedule) return;
        }

        // Release a place a worker held: the native job-release FUN_0043a0c0 (called on every
        // plan teardown — the worker's next true idle report via CProdPlanner::GnomeIdle /
        // MaintenanceTick, and the gnome_failed_work/gnome_stopped_work handler FUN_00676500)
        // zeroes the claim rec+0x24 (:31/:53), returns the record to idle (+0x1c = 0,:39/:61),
        // invalidates the production progress pair (+0x4c/+0x50 = 0xffffffff,:37-38/:59-60) —
        // the over-building bar falls back to the blue "order pending" state instead of freezing
        // at the dead worker's last progress — and kills the unpack/buildup request bytes
        // (+0x28/:34/:56, +0x2a/:36/:58).
        //
        // Deliberate port deviation: the pack byte (+0x29) survives the release. The native
        // clears it here too (FUN_0043a0c0:35/:57) — a diverted/failed/dead packer cancels the
        // order and the player must re-click. Instead, an executor-less pack order is picked up
        // by the next free gnome: with the byte armed, the collector re-collects the kind-0 job
        // and the next sweep dispatches another gnome — one at a time, held by the rec+0x24
        // claim + the live step plan (the record-busy gate). The "everyone packs" storm this
        // byte-clear once suppressed cannot return: its true enabler was the set_buildupstate
        // claim-drop (uncited port glue — see ValidatePlansAndClaims / PackFanoutReproTests);
        // the byte-clear had been treating the symptom. The byte still dies at (a) boxing
        // (set_boxed side effects,
        // FUN_0067dc80:157-161) and (b) the explicit Cancel button (PlacementController.
        // CancelPack) — the only two order-terminating acts now. rec+0x48 (the bound current
        // item) is not touched — the order surface stays bound until info_end_prod / the count
        // gate hides the bar.
        void ReleaseWorkerClaim(DigglesTclEntity place)
        {
            _ctx.Energy.Release(place);
            place.CurrentWorkerHandle = -1;
            place.ProdProgressCur = 0;
            place.ProdProgressMax = 0;
            place.UnpackRequested = false;    // +0x28 (:34/:56)
            place.BuildupRequested = false;   // +0x2a (:36/:58)
        }

        // Step-advance of an active plan on the gnome's idle report — the native steps a
        // planner-state gnome's plan from the same idle collection pass (MaintenanceTick
        // FUN_00433a70:328-350 collects,:355-407 advances via FUN_0043a460, Tcl emit
        // FUN_0043a8a0) before any new job match.
        // unpack stage 0 → 1: the muetze leg (hat anims via the task state) ended — emit the
        // pickup (evt_autoprod_pickup / _store by the instore bit obj+0x3d0 &
        // 0x4000000, FUN_0043a8a0:49-59); requires the crate alive and not carried
        // by someone else (the native step-validity gate FUN_0043a290).
        // stage 1 → 2: the pickup leg ended — with the item in the inventory emit the final
        // event:
        // haul → evt_autoprod_transferprod -subject1 place -subject2 item (the payload
        // evt_autoprod_transferprod_proc reads, z_events.tcl:1357-1377);
        // unpack → evt_autoprod_unpack -subject1 crate -text1 class -pos1 {order pos}
        // -num1 asbox (the step-7 emit, FUN_0043a8a0:113-118) — prod_autounpack's
        // boxed branch then walks the carried crate to the spot and places it
        // (z_work_common.tcl:333: walk_pos → prod_buildup_check → pickup(no-op,
        // already held) → unpack_pos_unfixed).
        // stage 2: the final event was emitted — the trip is over → drop the plan.
        // A dead crate — or a pickup leg that ended without the item in the inventory — drops
        // the plan; a pre-completion (stage < 2) unpack drop re-arms the crate's request byte so
        // the order retries (the SP planner never cleared it — FUN_00436440 re-collects when
        // the record returns to idle).
        void AdvancePlan(DigglesTclEntity g, StepPlan plan)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (plan.Kind == JobKind.Haul) { AdvanceHaulPlan(g, plan); return; }
            // ---- kind-0 pack plan (FUN_00439760: muetze → walk → pack step code 6) ----
            if (plan.Kind == JobKind.Pack)
            {
                // Stage 0 → 1: the muetze leg ended → emit the pack step (FUN_0043a8a0:
                // 11). Stand-down gates: the place must still exist and the order byte
                // must still be armed — the byte is the native cancel channel (the Tcl chain
                // re-reads it mid-flight the same way, prod_builddown z_work_common.tcl:443-446).
                if (plan.Stage == 0
                    && _ctx.Entities.TryGetValue(plan.ItemHandle, out var packPlace)
                    && packPlace.PackRequested)
                {
                    plan.Stage = 1;
                    g.WorkBusyCooldown = AssignCooldown;   // FUN_00434bc0:18
                    EvalAsGnome(g, "set_event this evt_autoprod_pack -subject1 " + plan.ItemHandle,
                        "pack step");
                    return;
                }
                // Stage 1 (the pack event went out — the Tcl owns the builddown from here) or a
                // cancelled/dead order: drop the plan. The place claim deliberately survives the
                // drop (rec+0x24 carries through the working leg) and releases on the packer's
                // next planless idle report / failure release — the same lifecycle the workat
                // claim rides.
                DropPlan(g.Handle, plan, rearm: false);
                return;
            }
            // Stage 0 → 1: the muetze leg ended → emit the pickup (FUN_00439990:81-113) —
            // the crate must be alive and either free in the world, shelved (pickup_store),
            // or already held by this gnome (the native step-validity gate FUN_0043a290).
            if (plan.Stage == 0
                && _ctx.Entities.TryGetValue(plan.ItemHandle, out var crate)
                && (crate.ContainedIn < 0 || crate.ContainedIn == g.Handle || crate.InStore))
            {
                plan.Stage = 1;
                // Every plan advance stamps gnome+0x3e8 = 2 (FUN_00434bc0:18 / the MaintenanceTick
                // advances at FUN_00433a70:369-370/:440) — the queued step event has a one-tick
                // dispatch latency, and without this cooldown the state machine's next idle
                // report (idle → work_dispatch → work_idle re-entry fires gnome_idle 1 again
                // within the same think ticks) re-advanced the plan before the step's handler
                // ever ran: stage 1 saw the crate not-yet-held → drop+rearm → the ~4 s
                // re-dispatch's tasklist_clear wiped the in-flight fetch — a perpetual
                // muetze-hat loop.
                g.WorkBusyCooldown = AssignCooldown;
                EvalAsGnome(g, "set_event this "
                    + (crate.InStore ? "evt_autoprod_pickup_store" : "evt_autoprod_pickup")
                    + " -subject1 " + plan.ItemHandle + " -text1 {" + plan.ItemClass + "}",
                    "pickup step");
                return;
            }
            if (plan.Stage == 1
                && _ctx.Entities.TryGetValue(plan.ItemHandle, out var item) && item.ContainedIn == g.Handle)
            {
                plan.Stage = 2;
                g.WorkBusyCooldown = AssignCooldown;   // FUN_00434bc0:18 — see above
                EvalAsGnome(g, "set_event this evt_autoprod_unpack -subject1 " + plan.ItemHandle
                    + " -text1 {" + plan.ItemClass + "}"
                    + " -pos1 {" + plan.OrderPos.x.ToString("0.0##", inv) + " "
                    + plan.OrderPos.y.ToString("0.0##", inv) + " "
                    + plan.OrderPos.z.ToString("0.0##", inv) + "} -num1 " + (plan.AsBox ? "1" : "0"),
                    "unpack step");
                return;
            }
            DropPlan(g.Handle, plan, rearm: plan.Stage < 2);
        }

        // The batched kind-4 step machine (FUN_0043c660): muetze → pickup(item0) →
        // pickup(item1) … → transferprod(item0 → dest0) → transferprod(item1 → dest1) …
        // Each step advances on one gnome-idle round-trip (the pickup legs' walking lives in
        // the unchanged Tcl handlers — pickup → walk_near_item, z_procs.tcl:477; the transfer
        // leg walks to the place, prod_bringtoprod z_work_common.tcl:92-95). Step validity:
        // pickup k — the previous pickup landed in the inventory and item k is alive and
        // free/shelved/held (FUN_0043a290); a dead/taken batch rider is simply
        // dropped from the lists (its job re-collects next pass) instead of
        // killing the whole trip;
        // transfer k — item k is still held and dest k alive; a dead entry is skipped.
        void AdvanceHaulPlan(DigglesTclEntity g, StepPlan plan)
        {
            int n = plan.Items.Count;
            // ---- pickup phase: stages 0..n-1 → emit pickup for Items[plan.Stage] ----
            while (plan.Stage < n)
            {
                int k = plan.Stage;
                // previous pickup must have landed (stage 0 has no predecessor)
                if (k > 0 && (!_ctx.Entities.TryGetValue(plan.Items[k - 1], out var prev)
                              || prev.ContainedIn != g.Handle))
                { DropPlan(g.Handle, plan, rearm: false); return; }
                if (!_ctx.Entities.TryGetValue(plan.Items[k], out var it)
                    || !(it.ContainedIn < 0 || it.ContainedIn == g.Handle || it.InStore))
                {
                    // a dead/taken rider: primary kills the trip, a rider is dropped in place
                    if (k == 0) { DropPlan(g.Handle, plan, rearm: false); return; }
                    RemoveBatchEntry(g.Handle, plan, k);
                    n = plan.Items.Count;
                    continue;
                }
                plan.Stage = k + 1;
                g.WorkBusyCooldown = AssignCooldown;   // FUN_00434bc0:18
                EvalAsGnome(g, "set_event this "
                    + (it.InStore ? "evt_autoprod_pickup_store" : "evt_autoprod_pickup")
                    + " -subject1 " + plan.Items[k] + " -text1 {" + plan.Classes[k] + "}",
                    "pickup step");
                return;
            }
            // ---- transfer phase: stages n..2n-1 → emit transferprod for entry Stage-n ----
            while (plan.Stage < 2 * n)
            {
                int k = plan.Stage - n;
                if (!_ctx.Entities.TryGetValue(plan.Items[k], out var it) || it.ContainedIn != g.Handle
                    || !_ctx.Entities.ContainsKey(plan.Dests[k]))
                {
                    if (k == 0 && plan.Stage == n) { DropPlan(g.Handle, plan, rearm: false); return; }
                    RemoveBatchEntry(g.Handle, plan, k);
                    n = plan.Items.Count;
                    plan.Stage = n + k;                // re-point at the same slot post-removal
                    continue;
                }
                plan.Stage++;
                g.WorkBusyCooldown = AssignCooldown;
                EvalAsGnome(g, "set_event this evt_autoprod_transferprod -subject1 " + plan.Dests[k]
                    + " -subject2 " + plan.Items[k] + " -text1 {" + plan.Classes[k] + "}",
                    "haul transfer");
                return;
            }
            DropPlan(g.Handle, plan, rearm: false);    // trip over
        }

        // Drop one batch rider from a haul plan (its dest claim releases; the job re-collects
        // on a later pass). Index 0 (the primary) is never removed here.
        void RemoveBatchEntry(int gnomeHandle, StepPlan plan, int k)
        {
            _ctx.Entities.TryGetValue(plan.Dests[k], out var dest);
            plan.Items.RemoveAt(k);
            plan.Dests.RemoveAt(k);
            plan.Classes.RemoveAt(k);
            plan.ForItems.RemoveAt(k);
            // release after removal so a dest still served by another entry keeps its claim
            if (dest != null && dest.CurrentWorkerHandle == gnomeHandle && !plan.Dests.Contains(dest.Handle))
                ReleaseWorkerClaim(dest);
        }

        // Drop a plan; a pre-completion unpack plan (muetze/pickup leg died) re-arms the crate's
        // deferred order so the next maintenance pass retries it with another gnome.
        void DropPlan(int gnomeHandle, StepPlan plan, bool rearm)
        {
            Debug.Log($"[ProdDispatcher] plan drop: gnome#{gnomeHandle} {plan.Kind} stage={plan.Stage} rearm={rearm}");
            _plans.Remove(gnomeHandle);
            if (_ctx.Entities.TryGetValue(gnomeHandle, out var gnome))
                gnome.PlannerCurrentWorkplaceId = -1;
            // Plan teardown releases every destination the trip claimed (FUN_0043a0c0:31/:53
            // zeroes rec+0x24 wherever this gnome held it; the batched kind-4 trip claimed each
            // rider's dest at:235-236) — the dests re-open for the next trip/workat.
            if (plan.DestHandle != 0 && _ctx.Entities.TryGetValue(plan.DestHandle, out var dest)
                && dest.CurrentWorkerHandle == gnomeHandle)
                ReleaseWorkerClaim(dest);
            foreach (var dh in plan.Dests)
                if (_ctx.Entities.TryGetValue(dh, out var d) && d.CurrentWorkerHandle == gnomeHandle)
                    ReleaseWorkerClaim(d);
            if (rearm && plan.Kind == JobKind.Unpack
                && _ctx.Entities.TryGetValue(plan.ItemHandle, out var crate))
            {
                crate.UnpackRequested = true;
                crate.OrderPos = plan.OrderPos;
                crate.OrderAsBox = plan.AsBox;
                if (crate.CurrentWorkerHandle == gnomeHandle) crate.CurrentWorkerHandle = -1;
            }
        }

        // Run a set_event in the gnome's object scope (the native FUN_0043a630 evals the built
        // command string the same way).
        void EvalAsGnome(DigglesTclEntity g, string cmd, string what)
        {
            Debug.Log($"[ProdDispatcher] step emit gnome#{g.Handle}: {cmd}");
            var prev = _ctx.This;
            _ctx.This = g;
            try { _ctx.Interp.Eval(cmd); }
            catch (System.Exception e) { Debug.LogWarning($"[ProdDispatcher] {what}: {e.Message}"); }
            finally { _ctx.This = prev; }
        }

        // `gnome_idle this 0` → no longer available (left work_idle, took a job, died).
        public void OnGnomeBusy(DigglesTclEntity? g)
        {
            if (g == null) return;
            _idle[ClampPlayer(g.Owner)].Remove(g.Handle);
            g.PlannerIdleAnnounceFlag = 0;
            // Leaving the pool for real work clears the failure embargo — the native one-shot
            // re-task FUN_00434bc0 zeroes rec+0x48 unconditionally the moment the gnome is
            // re-tasked. This matters for the evt_task_* user-order procs, which call
            // gnome_failed_work as a drop-the-planner-job cleanup at their top (z_events.tcl:346,
            // 415,446,…): the order itself runs Tcl-driven, and once the gnome leaves idle for it
            // (state_leave idle → gnome_idle this 0, zwerg.tcl:269-270) the embargo is moot. A
            // failing gnome never gets here while breathing — it cannot be auto-assigned, and a
            // genuine failure drops it straight back into idle without leaving the pool.
            _failBreather.Remove(g.Handle);
        }

        // gnome_failed_work / gnome_stopped_work → release the reservation so the job re-opens,
        // with the native anti-thrash breathers (FUN_00676500 + FUN_0043a0c0):
        // • the gnome keeps its assign cooldown (the old `WorkBusyCooldown = 0` is not what the
        // native does — FUN_00676500 never touches gnome+0x3e8) and additionally gets the
        // 5.0 s allocator embargo (rec+0x48 = 5.0f, read by FUN_00438210:35-38);
        // • the job it had reserved gets the 20-tick reopen cooldown (target+0x3e8 = 0x14,
        // FUN_0043a0c0) so the same failing job isn't instantly re-dispatched to anyone.
        // Without these, an instantly-failing walk (e.g. FindPath NULL) recycled assign→fail→
        // reassign at the think-tick rate — the post-cutscene work-assignment storm.
        public void ReleaseGnome(DigglesTclEntity? g)
        {
            if (g == null) return;
            _failBreather[g.Handle] = FailRetaskDelayTicks;
            if (_ctx.PlaceLocks.TryGet(g.Handle, out var jobPos))
                _jobReopen.Add(new ReopenLock { Pos = jobPos, Ticks = JobReopenTicks });
            _ctx.PlaceLocks.Rem(g.Handle);
            // A failed/stopped harvest must retry on the native 20-tick job-reopen schedule, not
            // sit out the full 15 s emit cadence (the native job persists in the planner array
            // across a failure — only new emissions pay the cadence, 005f1500:46-55).
            int p0 = ClampPlayer(g.Owner);
            if (_harvestCooldown[p0] > JobReopenTicks) _harvestCooldown[p0] = JobReopenTicks;
            // FUN_0043a0c0:31 — releasing a gnome also drops the prodplace's current-gnome ref
            // (+0x24 = 0, plus the request bytes), so a half-built building re-opens for the next
            // worker (after the reopen cooldown above) instead of staying claimed forever. The
            // same path invalidates the place's progress pair (+0x4c/+0x50 = -1,:37-38) — a
            // cancelled order's evt_zwerg_break lands here via gnome_stopped_work and the bar
            // must not freeze at the dead worker's progress.
            foreach (var e in _ctx.Entities.Values)
                if (e.CurrentWorkerHandle == g.Handle)
                {
                    // FUN_0043a0c0:32 companion stamp — the released target object gets
                    // +0x3e8 = 0x14 (20 logic ticks) so the just-failed job cannot re-dispatch
                    // to anyone for 2 s. The placelock-keyed reopen above misses every chain
                    // that already started: the evt proc's stop_prod runs `placelock_rem
                    // $myref` (z_procs.tcl:1245-1246) long before a mid-chain failure lands
                    // here, so TryGet finds nothing and the reopen never armed.
                    _jobReopen.Add(new ReopenLock { Pos = e.Position, Ticks = JobReopenTicks });
                    ReleaseWorkerClaim(e);
                }
            // A failing gnome's step plan dies with the release (the native gnome_failed_work →
            // FUN_0043a0c0 destroys the plan). A pre-completion unpack plan (muetze/pickup leg
            // failed) re-arms the crate's request: the SP planner never cleared the request
            // bytes at dispatch — the Tcl chain clears them on completion/cancel
            // (set_prod_unpack, z_work_common.tcl:363) — so a failed fetch natively re-collects
            // and retries after the reopen cooldown. A stage-2 failure (mid-carry) is not
            // re-armed: from evt_autoprod_unpack_proc on, the Tcl owns the order, and its own
            // failure cleanup cancels carried production items' orders (stop_prod's inv_list
            // loop → set_prod_unpack 0, z_procs.tcl:1238-1243).
            if (_plans.TryGetValue(g.Handle, out var plan))
                DropPlan(g.Handle, plan, rearm: plan.Stage < 2);
            g.PlannerCurrentWorkplaceId = -1;
        }

        // True while the gnome sits out its post-failure re-task embargo (FUN_00438210:35-38).
        public bool IsBreathing(int handle)
            => _failBreather.TryGetValue(handle, out var t) && t > 0;

        // True while a failed job at (or within JobReopenRadius of) `pos` is still cooling down
        // (the target+0x3e8 = 0x14 reopen cooldown, FUN_0043a0c0). Horizontal+depth proximity in
        // native coords, matching the placelock identity the reservation used.
        bool JobOnCooldown(Vector3 pos)
        {
            float r2 = JobReopenRadius * JobReopenRadius;
            for (int k = 0; k < _jobReopen.Count; k++)
            {
                if (_jobReopen[k].Ticks <= 0) continue;
                float dx = _jobReopen[k].Pos.x - pos.x, dy = _jobReopen[k].Pos.y - pos.y;
                if (dx * dx + dy * dy <= r2) return true;
            }
            return false;
        }

        // ---- per-Step bookkeeping (called from DigglesGameSim) ----
        // Note: an earlier revision raised invented evt_time_startwork/evt_time_startsparetime edge events
        // here. Deleted — no raiser exists in the original: the exe interns the (different)
        // evt_worktime_start/end names once (FUN_005c7410) and never raises them, the zwerg.tcl:344-354
        // handlers are dead code, and plan switching is pure polling of get_remaining_sparetime
        // (zwerg.tcl:299-311, z_work_states.tcl:256).
        public void DecayCooldowns()
        {
            // The logic-tick counter the lazy CProd slot-priority mirror ramps against
            // (FUN_005edea0 runs once per object logic tick — "CObj::Tick Prod",
            // CObj_Tick FUN_005cc600:291).
            _logicTick++;
            // Failure breathers (logic-tick units — see the constants block). Key snapshot only
            // when someone is actually breathing (the dictionary is empty in the common case).
            if (_failBreather.Count > 0)
            {
                var keys = new List<int>(_failBreather.Keys);
                foreach (var h in keys)
                {
                    int t = _failBreather[h] - 1;
                    if (t <= 0) _failBreather.Remove(h);
                    else _failBreather[h] = t;
                }
            }
            for (int k = _jobReopen.Count - 1; k >= 0; k--)
            {
                var l = _jobReopen[k];
                l.Ticks--;
                if (l.Ticks <= 0) _jobReopen.RemoveAt(k);
                else _jobReopen[k] = l;
            }
            // (Dig request/announce timers decay inside the per-player maintenance pass — the
            // job builder and the request scan each subtract 1.0 per pass, FUN_00435f50:53-54 +
            // FUN_0043bdd0:149-150 — not here on the logic tick.)
            for (int p = 0; p < Players; p++)
                if (_harvestCooldown[p] > 0) _harvestCooldown[p]--;

            foreach (var e in _ctx.Entities.Values)
            {
                if (e.WorkBusyCooldown > 0) e.WorkBusyCooldown--;
                // Per-tick half-hour snap of the work window (FUN_005cb760:24-29, called from the
                // per-object pre-tick FUN_005cbf70:121-123 for planner-tracked objects):
                // x = floor(2x + 0.05) * 0.5 (DAT_00737188 = 0.05 dbl, DAT_00737230 = 0.5 dbl).
                // Native gates on the planner record obj+0x3e4 — gnomes/babies; port: class kind.
                if (e.Class != null && (e.Class.Kind == "gnome" || e.Class.Kind == "baby"))
                {
                    e.WorkStart = Mathf.Floor(e.WorkStart * 2f + 0.05f) * 0.5f;
                    e.WorkLen   = Mathf.Floor(e.WorkLen   * 2f + 0.05f) * 0.5f;
                }
            }
        }

        // ---- the periodic dispatch sweep (one player per qualifying tick, faithful amortization) ----
        public void Tick(double dt)
        {
            _accum += dt;
            if (_accum < DispatchInterval) return;
            _accum = 0;
            _cursor = (_cursor + 1) & (Players - 1);
            Sweep(_cursor);
        }

        // Test seam: run one full per-player maintenance pass on demand (the exact body Tick's
        // round-robin invokes) — EditMode tests drive dispatch through this instead of waiting
        // out the 4 s/player cadence.
        public void SweepNow(int player) => Sweep(ClampPlayer(player));

        void Sweep(int player)
        {
            // The validation pass runs first — the native MaintenanceTick re-validates every
            // tracked gnome's plan at its very top (FUN_00433a70:71-135), before any job build.
            ValidatePlansAndClaims();
            // Faithful phase order inside CProdPlanner::MaintenanceTick: the dig-job builder
            // FUN_00435f50 runs at:172 (inside BuildJobs→CollectDigJobs), the request scan
            // FUN_0043bdd0 at:217 — so a request inserted this pass gets its job on the next
            // pass. Both run even with an empty idle pool (timers must keep decaying).
            var jobs = BuildJobs(player);
            MaintainDigRequests(player);

            var pool = _idle[player];
            // FUN_00433a70:112,:329 — work-window flag and auto-schedule bit clear, else not dispatchable.
            pool.RemoveWhere(h => !_ctx.Entities.TryGetValue(h, out var e) || e.Owner != player
                || e.WorkBusyCooldown > 0 || !IsWorkTime(e) || !e.ProdAutoSchedule);
            if (pool.Count == 0 || jobs.Count == 0) return;
            CollectPreferredWorkplaces(player, out Dictionary<int, int>? preferredWorkplaces,
                out HashSet<int>? claimedWorkplaces);
            if (preferredWorkplaces == null)
            {
                jobs.Sort((a, b) => b.Need.CompareTo(a.Need));
            }
            else
            {
                for (int index = 0; index < jobs.Count; index++)
                {
                    Job scored = jobs[index];
                    scored.PreferenceScore = scored.Kind == JobKind.Production
                        ? BestProductionScore(scored, pool, preferredWorkplaces, claimedWorkplaces!)
                        : scored.Need;
                    jobs[index] = scored;
                }
                jobs.Sort((a, b) => b.PreferenceScore.CompareTo(a.PreferenceScore));
            }

            List<DigglesTclEntity>? diggers = null;          // lazy active-digger list (crowding)
            foreach (var job in jobs)
            {
                if (JobOnCooldown(job.Pos)) continue;   // failed-job reopen delay (FUN_0043a0c0 +0x3e8=0x14)
                // Note on the native need>0 dispatch gate (FUN_00433a70:418 — only records with
                // 0.0 < rec+0x18 are served): the port needs no counterpart. The only candidate
                // for need 0 is a kind-2 invention, and the slot-priority mirror seeds a fresh
                // observation with one CProd tick of ramp (see SlotPriority) — matching the
                // native, where the building's 10 Hz CProd tick has always lifted a queued slot
                // past 0 before any maintenance pass reads it. Every job the port builds
                // therefore carries need > 0.
                // nearest reachable free gnome for this job (native FUN_004319b0 / FUN_00438210)
                DigglesTclEntity? best = null;
                float bestD = float.MaxValue;
                float bestScore = 0f;
                bool usePreferenceScore = preferredWorkplaces != null &&
                    job.Kind == JobKind.Production;
                foreach (var h in pool)
                {
                    if (IsBreathing(h)) continue;       // post-failure embargo (FUN_00438210:35-38)
                    if (_plans.ContainsKey(h)) continue;   // mid-plan: the plan advances in OnGnomeIdle, never re-matches
                    var g = _ctx.Entities[h];
                    if (!Match(g, job, preferredWorkplaces, claimedWorkplaces)) continue;
                    if (usePreferenceScore)
                    {
                        float score = ProductionScore(g, job, preferredWorkplaces!);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = g;
                        }
                        continue;
                    }
                    float d = MatchCost(g, job, player, ref diggers);
                    if (d < bestD) { bestD = d; best = g; }
                }
                if (best == null || !Reserve(job, best)) continue;
                // Kind-4 batching (FUN_0043c660:89-92,:121-188): before the trip opens, fold
                // further kind-4 jobs from this pass into it — capacity = invMax − 2 − held
                // (:92; invMax base = 6 for a gnome, FUN_005cde10 — the container-item bonus
                // table DAT_00772478 is a documented open item), rider qualifies when both its
                // item lies within weighted dist² < 40 of the primary item and its dest within
                // 40 of the primary dest (:157-168, DAT_007375bc); a consumed rider's job need
                // zeroes (:178) — here: it simply rides the one dispatched plan.
                List<Job>? batch = null;
                if (job.Kind == JobKind.Haul)
                {
                    int capacity = 6 - 2 - best.Inventory.Count;
                    if (capacity > 0 && _ctx.Entities.TryGetValue(job.ItemHandle, out var prim))
                    {
                        foreach (var other in jobs)
                        {
                            if (capacity <= 0) break;
                            if (other.Kind != JobKind.Haul || other.ItemHandle == job.ItemHandle) continue;
                            if (JobOnCooldown(other.Pos)) continue;
                            if (!_ctx.Entities.TryGetValue(other.ItemHandle, out var oit)) continue;
                            if (Dist2(oit.Position, prim.Position) >= HaulBatchDist2) continue;   // item near item
                            if (Dist2(other.Pos, job.Pos) >= HaulBatchDist2) continue;            // dest near dest
                            (batch ??= new List<Job>()).Add(other);
                            capacity--;
                        }
                    }
                }
                if (!Assign(best, job, player, batch))
                {
                    _ctx.PlaceLocks.Rem(best.Handle);
                    continue;
                }
                pool.Remove(best.Handle);
                // One dispatch per maintenance pass: the native tail walks the matched pairs,
                // dispatches the first need>0 one and breaks (FUN_00433a70:409-450). The cap is
                // global across job kinds — further work waits for the next pass (~4 s/player on
                // the 0.5 s × 8-player round-robin, the native per-player cadence).
                break;
            }
        }

        // ---- the MaintenanceTick validation pass (FUN_00433a70:110-131) ------------------------
        // The native re-validates every tracked gnome's active plan each pass, before any job
        // build: FUN_0043a050 finds the plan, FUN_0043a290 checks that the current step's
        // referenced object handles still resolve to live objects (step+0x24 via FUN_00431e50,
        // step+0x20 via FUN_00431de0 — FUN_0043a290:28-29), and a failed check tears the plan
        // down on the spot: FUN_0043d4b0 (plan removal) + FUN_0043a0c0 (claim + request bytes +
        // progress pair released, target +0x3e8 = 0x14 reopen stamp). Assignments are not
        // persisted on faith across passes — every pass re-earns them.
        //
        // The port must also validate the plan owner: the native keys plans on the live gnome
        // object pointer, and the death chain always releases first (proc destroy → stop_prod +
        // `gnome_failed_work this` + `del this`, z_procs.tcl:1884/:1895) — but a port-side
        // delete that skips the Tcl destroy (direct `del`, a script error mid-chain, C#-side
        // teardown) left the plan wedged forever: TryGetHaulFor painted the blue delivery bar
        // with nobody hauling, PlanTargetsItem locked the source item out of every re-collect,
        // the destination stayed record-busy — a production freeze that only rebuilding the
        // place recovered. The same pass scrubs claims held by
        // no-longer-existing gnomes (the FUN_0043a0c0:49-62 by-gnome loop's observable law:
        // no dead gnome can hold rec+0x24 — with it the progress pair and request bytes reset,
        // so the over-building bar falls back to the true order state).
        void ValidatePlansAndClaims()
        {
            List<int>? doomed = null;
            foreach (var kv in _plans)
                if (!_ctx.Entities.ContainsKey(kv.Key) || !PlanCurrentStepAlive(kv.Value))
                    (doomed ??= new List<int>()).Add(kv.Key);
            if (doomed != null)
                foreach (var h in doomed)
                {
                    var plan = _plans[h];
                    bool gnomeAlive = _ctx.Entities.ContainsKey(h);
                    Debug.Log($"[ProdDispatcher] validation teardown: gnome#{h} {plan.Kind} stage={plan.Stage} gnomeAlive={gnomeAlive}");
                    // every target this gnome had claimed gets the reopen stamp
                    // (+0x3e8 = 0x14, FUN_0043a0c0:30) before the claims dissolve
                    foreach (var e in _ctx.Entities.Values)
                        if (e.CurrentWorkerHandle == h)
                            _jobReopen.Add(new ReopenLock { Pos = e.Position, Ticks = JobReopenTicks });
                    DropPlan(h, plan, rearm: plan.Stage < 2);
                    if (!gnomeAlive)
                    {
                        // the dead carrier's residual planner state dies with it — its
                        // reservation locks (Reserve's 6 s placelock would otherwise pin the
                        // job spot), its pool entries, its failure breather, and every claim
                        // DropPlan's dest list did not cover
                        _ctx.PlaceLocks.Rem(h);
                        _failBreather.Remove(h);
                        for (int p = 0; p < Players; p++) _idle[p].Remove(h);
                        foreach (var e in _ctx.Entities.Values)
                            if (e.CurrentWorkerHandle == h) ReleaseWorkerClaim(e);
                    }
                }
            // Dead-worker claim scrub: kind-3 workat / kind-0 pack claims carry no step plan to
            // die with — a claimed worker deleted without the Tcl death cleanup held the place
            // record-busy forever ("order exists but nobody ever starts").
            foreach (var e in _ctx.Entities.Values)
                if (e.CurrentWorkerHandle >= 0 && !_ctx.Entities.ContainsKey(e.CurrentWorkerHandle))
                {
                    _jobReopen.Add(new ReopenLock { Pos = e.Position, Ticks = JobReopenTicks });
                    _ctx.PlaceLocks.Rem(e.CurrentWorkerHandle);
                    ReleaseWorkerClaim(e);
                }
        }

        // The FUN_0043a290 step-validity reading for the port's coarsened step plans: the
        // current step's referenced objects must still exist. The kind-1 unpack plan references
        // its crate on every state-carrying step (FUN_00439990:81-141); the kind-4 haul's first
        // pending step references the primary item — later pickup/transfer steps re-validate at
        // each idle-report advance, where AdvanceHaulPlan prunes dead riders entry-by-entry
        // (the native, too, only fails the current step here; later steps fail when they
        // become current, FUN_0043a460).
        bool PlanCurrentStepAlive(StepPlan plan)
        {
            // kind-1 unpack: the crate rides every step; kind-0 pack: the place does.
            if (plan.Kind == JobKind.Unpack || plan.Kind == JobKind.Pack)
                return _ctx.Entities.ContainsKey(plan.ItemHandle);
            if (plan.Stage <= 0)
                return plan.Items.Count > 0 && _ctx.Entities.ContainsKey(plan.Items[0]);
            return true;
        }

        // The kind-aware gnome-match cost (FUN_00438210). Base = the 3-D weighted dist² (see
        // Dist2). Kind-5 dig extras (:139-185): a hard gate at weighted dist² > 120
        // (DAT_007683a4) and a crowding penalty — for each other gnome of this player actively
        // digging, horizontal dist² to the job < 15 ⇒ ×1.5 and < 30 ⇒ ×1.5 (both ⇒ ×2.25) —
        // idle dwarfs spread to other dig fronts instead of clustering. (The native also scales
        // an idle-time radius local_c = idle·5+5 capped to FLT_MAX past 75 — the cap makes it
        // moot for any gnome idle >14 s; not ported, documented simplification.)
        float MatchCost(DigglesTclEntity g, Job job, int player, ref List<DigglesTclEntity>? diggers)
        {
            float d = Dist2(g.Position, job.Pos);
            if (job.Kind != JobKind.Dig) return d;
            if (d > DigMatchMaxCost) return float.PositiveInfinity;   //:149 hard gate
            diggers ??= CollectActiveDiggers(player);
            foreach (var o in diggers)
            {
                if (o.Handle == g.Handle) continue;
                var dp = DigPosOf(o);
                float dx = dp.x - job.Pos.x, dy = dp.y - job.Pos.y;
                float h2 = dx * dx + dy * dy;                          //:169-171 — raw horizontal
                if (h2 < DigCrowdNearDist2) d *= DigCrowdPenalty;      //:173-174
                if (h2 < DigCrowdFarDist2) d *= DigCrowdPenalty;       //:176-178
            }
            // (The native:181-183 re-check compares vs the idle-time radius local_c, which is
            // FLT_MAX for any gnome idle >14 s — for the pooled common case the crowding penalty
            // is ranking-only, which is what we keep.)
            return d;
        }

        // FUN_00438210 kind-2/kind-3 scoring and FUN_00433710 site exclusivity. Only live,
        // same-player targets participate in dispatch; native-save dangling ids remain stored but
        // are behaviorally inert. The scan covers non-idle gnomes too because a claimed site is
        // exclusive even while its preferred gnome is not currently available for matching.
        private void CollectPreferredWorkplaces(
            int player,
            out Dictionary<int, int>? preferredWorkplaces,
            out HashSet<int>? claimedWorkplaces)
        {
            preferredWorkplaces = null;
            claimedWorkplaces = null;
            foreach (DigglesTclEntity gnome in _ctx.Entities.Values)
            {
                if (gnome.Owner != player || gnome.NativeObjectType != 1 ||
                    gnome.PreferredWorkplaceHandle <= 0 ||
                    !_ctx.Entities.TryGetValue(gnome.PreferredWorkplaceHandle, out var workplace) ||
                    workplace.Owner != player)
                {
                    continue;
                }

                preferredWorkplaces ??= new Dictionary<int, int>();
                claimedWorkplaces ??= new HashSet<int>();
                preferredWorkplaces[gnome.Handle] = workplace.Handle;
                claimedWorkplaces.Add(workplace.Handle);
            }
        }

        private float BestProductionScore(
            Job job,
            HashSet<int> pool,
            Dictionary<int, int> preferredWorkplaces,
            HashSet<int> claimedWorkplaces)
        {
            float bestScore = 0f;
            foreach (int handle in pool)
            {
                if (IsBreathing(handle) || _plans.ContainsKey(handle)) continue;
                DigglesTclEntity gnome = _ctx.Entities[handle];
                if (!Match(gnome, job, preferredWorkplaces, claimedWorkplaces)) continue;
                float score = ProductionScore(gnome, job, preferredWorkplaces);
                if (score > bestScore) bestScore = score;
            }
            return bestScore;
        }

        private static float ProductionScore(
            DigglesTclEntity gnome,
            Job job,
            Dictionary<int, int> preferredWorkplaces)
        {
            bool hasPreference = preferredWorkplaces.TryGetValue(gnome.Handle,
                out int preferredHandle);
            bool isPreferred = hasPreference && preferredHandle == job.TargetHandle;
            float distance = Mathf.Sqrt(Dist2(gnome.Position, job.Pos));
            bool invention = job.WorkClass.StartsWith("Bp", System.StringComparison.Ordinal);
            if (!invention && hasPreference)
            {
                distance *= isPreferred
                    ? PreferredWorkplaceDistanceScale
                    : OtherWorkplaceDistanceScale;
            }

            float score = job.Need /
                Mathf.Max(1f, distance / ProductionDistanceScoreDivisor);
            return isPreferred ? score * PreferredWorkplaceScoreBonus : score;
        }

        // ---- job sources ----
        List<Job> BuildJobs(int player)
        {
            var jobs = new List<Job>();
            CollectDigJobs(player, jobs);
            CollectHarvestJobs(player, jobs);
            CollectPlaceJobs(player, jobs);   // production section emits kind-4 hauls too
            return jobs;
        }

        // Request jobs carry the fixed need 7.5 = 0x40f00000 (FUN_00436440:87/:118) — above the
        // dig/harvest weights, so an explicit user order out-prioritizes the ambient work.
        const float RequestNeed = 7.5f;

        // Per-prodplace job collection — the single-player collector FUN_00436440, an if/else-if
        // chain (a place contributes at most one job per pass; the record-idle gate rec+0x1c==0
        // at:31 keeps a mid-plan place out — port equivalent: the bytes clear at dispatch):
        //
        // +0x29 pack → kind-0 job, pos = the building, need 7.5 (:89-118)
        // +0x2a buildup → kind-1 job (the same unpack kind — SP has no separate buildup job;
        // the resume rides the unpack event's not-boxed branch), pos =
        // rec+0x30, job+0x08 asbox = 0 (:33-52)
        // +0x28 unpack → kind-1 job, pos = the rec+0x30 order pos, job+0x08 = the asbox
        // byte +0x2d (:54-87)
        // else the production-slot section (kind-3 workat, FUN_00436750).
        //
        // contained gate: the MP collector FUN_005f0850:22/:56/:90 skips objects with obj+0x3d0
        // bit 2 (set by inv_add FUN_005ce010:166/:204, cleared by inv_rem FUN_005ce930:139) — a
        // crate riding in a gnome's inventory is invisible. The SP collector has no explicit
        // gate, but its plan would die at the Tcl `pickup` (is_contained → false,
        // z_procs.tcl:462); the port keeps the explicit gate (documented, saves the dead trip).
        //
        // The request bytes are orders with explicit writers and nothing else (exhaustive
        // decompile search): buildup = the window's aufbau button
        // (FUN_005242b0:342) / the deferred re-place tick (FUN_005edea0:136) / Tcl
        // set_prod_buildup (never called by shipped scripts); unpack = the placement confirm's
        // deferred-order branch (FUN_00531310:316-331 via FUN_005ee250) / ai_setbuildup
        // (FUN_0042d9b0:85-91); pack = the window's pack button (FUN_005242b0:304-330). There is
        // no auto-arm scan for placed-unbuilt buildings — the former CollectBuildupJobs re-arm
        // fabricated that fan-out and has been deleted.
        void CollectPlaceJobs(int player, List<Job> jobs)
        {
            foreach (var e in _ctx.Entities.Values)
            {
                if (e.Owner != player || e.ContainedIn >= 0) continue;   // contained gate (see above)
                // record-busy gate (FUN_00436440:31 `rec+0x1c == 0`): a place whose order is
                // already in service — claimed by a live worker or referenced by an in-flight
                // step plan — contributes nothing this pass, even with a request byte re-armed.
                // Without it, re-clicking the pending build button re-armed UnpackRequested and
                // dispatched a second gnome onto the same crate — two builders hammering one
                // building.
                if ((e.CurrentWorkerHandle >= 0 && _ctx.Entities.ContainsKey(e.CurrentWorkerHandle))
                    || PlanTargetsItem(e.Handle))
                {
                    continue;
                }
                // Research is a separate live-slot pass in the native planner
                // (MaintenanceTick FUN_00433a70:169 -> FUN_00434e60), not the fallback arm of
                // the request/regular-production chain below. Collect it before that chain so
                // every currently queued uninvented slot remains visible on every sweep.
                bool productionAvailable = ProductionPlaceAvailable(e);
                if (productionAvailable) CollectInventionJobs(e, player, jobs);
                if (e.PackRequested)                                      // FUN_00436440:89-118
                {
                    jobs.Add(new Job
                    {
                        Kind = JobKind.Pack, TargetHandle = e.Handle, WorkClass = "",
                        Tx = 0, Ty = 0, Pos = e.Position, Need = RequestNeed,
                    });
                    continue;
                }
                if (e.BuildupRequested)                                   //:33-52 — kind-1, asbox 0
                {
                    // Job/event pos: the native copies the (possibly stale) rec+0x30, but the
                    // kind-1 plan builder overrides both the walk and the emitted -pos1 with the
                    // object's own position for a not-boxed target (FUN_00439990:81-84) — the
                    // port uses the object position directly (a resume target stands where it
                    // was placed).
                    jobs.Add(new Job
                    {
                        Kind = JobKind.Unpack, TargetHandle = e.Handle, WorkClass = "",
                        Tx = 0, Ty = 0, Pos = e.Position, Need = RequestNeed, AsBox = false,
                    });
                    continue;
                }
                if (e.UnpackRequested)                                    //:54-87
                {
                    jobs.Add(new Job
                    {
                        Kind = JobKind.Unpack, TargetHandle = e.Handle, WorkClass = "",
                        Tx = 0, Ty = 0, Pos = e.OrderPos, Need = RequestNeed, AsBox = e.OrderAsBox,
                    });
                    continue;
                }
                if (productionAvailable) CollectProductionJobs(e, player, jobs);
            }
        }

        // Missing-material → resource escalation: the SP kind-6 harvest chain. The earlier
        // "wanted buildings vs lumped Pilzhut+Pilzstamm world stock" model let a stray item of
        // the wrong class kill the deficit — missing Pilzhut with a Pilzstamm lying around never
        // chopped. Native ground truth (decompile-verified):
        // • When the demand scan / material resolver finds no source item of a missing recipe
        // class (FUN_004317a0:71-73 matches by exact class → -1), the class maps through the
        // produce registry FUN_00432690 (resource classes register what they yield,
        // FUN_00434e60:116-120/:387-408) to the resource that produces it, and a harvest
        // request {resourceClass, x, y, weight} is appended (FUN_00434e60:351-367 /
        // FUN_00436d40:225-239 → FUN_0043ad40:53-78, deduped:33-47).
        // • The request maintainer FUN_0043b620 (MaintenanceTick:219) promotes the best request
        // to one kind-6 job per pass; its plan (FUN_004394e0) = muetze("harvest") →
        // walk(resource) → step 0xc = `set_event this evt_autoprod_harvest -subject1 <res>
        // -text1 <resourceClass>` (FUN_0043a8a0:70-74).
        // • Deficits are per class (FUN_00434e60:447-500 keys owner stock per class; even the
        // MP emitter keeps Pilzhut and Pilzstamm separate, FUN_005f4220:247-253) with need
        // scaled ×0.2 (DAT_00737054), on the 5 s/+10 s cadence (constants above).
        void CollectHarvestJobs(int player, List<Job> jobs)
        {
            if (_harvestCooldown[player] > 0) return;            // 5 s pass / +10 s post-emit cadence
            foreach (var e in _ctx.Entities.Values)
            {
                if (e.Owner != player || e.ContainedIn >= 0 || !e.BuildupComplete
                    || !e.ProdAutoSchedule || e.ProdSlots.Count == 0) continue;
                if (IsBoxed(e)) continue;                            // a crate produces nothing
                if (e.VarsScope.Vars.TryGetValue("_prod_enabled", out var pev) && pev == "0") continue;
                // Materialneed 0 (rec+0x41 — the demand scan gates on it too, FUN_00434e60:301):
                // a self-supplying place (farm) generates no material deficit → no chops for it.
                if (e.VarsScope.Vars.TryGetValue("_materialneed", out var mnb) && mnb == "0") continue;
                foreach (var kv in e.ProdSlots)
                {
                    if (kv.Value <= 0) continue;
                    if (GetOwnerAttrib(player, "Bp" + kv.Key) <= 0.5) continue;   // inventions eat no materials
                    var missing = MissingMaterials(e, kv.Key);
                    if (missing.Count == 0) continue;
                    // per-class deficit of this recipe's missing entries (multiset counts)
                    var deficit = new Dictionary<string, int>();
                    foreach (var cls in missing)
                        deficit[cls] = deficit.TryGetValue(cls, out var n) ? n + 1 : 1;
                    foreach (var kv2 in deficit)
                    {
                        // The escalation fires only on the resolver's exact failure — no source
                        // item of the class exists at all (FUN_004317a0:71-73 matches by exact
                        // class, unlocked). Cooldown/plan-reserved items still count as existing
                        // (the kind-4 haul owns them), and a haul already delivering this class
                        // into this place suppresses the chop for the pass.
                        bool covered = false;
                        foreach (var p in _plans.Values)
                        {
                            if (p.Kind != JobKind.Haul) continue;
                            for (int k = 0; k < p.Dests.Count && !covered; k++)   // incl. batch riders
                                if (p.Dests[k] == e.Handle && p.Classes[k] == kv2.Key) covered = true;
                            if (covered) break;
                        }
                        if (!covered)
                            foreach (var it in _ctx.Entities.Values)
                            {
                                if (it.Class == null || it.Class.Name != kv2.Key) continue;
                                if (it.ContainedIn >= 0 && !it.InStore) continue;
                                if (it.Owner != player && it.Owner >= 0) continue;
                                if (IsLocked(it)) continue;
                                // A fog-hidden item does not cover the deficit — the escalation
                                // fires exactly when the resolver finds nothing, and the
                                // resolver's own gate is GetMaxFow > 0x8c (FUN_004317a0:73).
                                if (!FowEligible(it, player, SourceResolveFowMin)) continue;
                                covered = true; break;
                            }
                        if (covered) continue;
                        if (!ProduceMap.TryGetValue(kv2.Key, out var resCls)) continue;
                        // Nearest eligible resource of the producing class. Registration rejects
                        // foreign resources and black fog (FUN_00434e60:14); promotion then
                        // resolves the concrete target through FUN_0043bae0:33 -> FUN_004317a0,
                        // whose player mask rejects foreign ownership (FUN_004315a0:56-91;
                        // FUN_004317a0:46-55) and whose final fog test requires > 0x8c
                        // (FUN_004317a0:71-74). Locked resources fail that same predicate.
                        DigglesTclEntity? best = null; float bestD = float.MaxValue;
                        foreach (var r in _ctx.Entities.Values)
                        {
                            if (r.ContainedIn >= 0 || r.Class == null || r.Class.Name != resCls) continue;
                            if (r.Owner != player && r.Owner != -1) continue;
                            if (IsLocked(r) || r.WorkBusyCooldown > 0) continue;
                            if (!FowEligible(r, player, SourceResolveFowMin)) continue;
                            float d = Dist2(r.Position, e.Position);
                            if (d < bestD) { bestD = d; best = r; }
                        }
                        if (best == null) continue;   // no ripe resource → starve (FUN_0043b620 validity gate)
                        jobs.Add(new Job
                        {
                            Kind = JobKind.Harvest, TargetHandle = best.Handle, WorkClass = resCls,
                            Tx = 0, Ty = 0, Pos = best.Position, Need = kv2.Value * HarvestNeedScale,
                        });
                        return;   // one kind-6 job per pass (FUN_0043b620 promotes one request)
                    }
                }
            }
        }

        // The produce map — the FUN_00432690 registry equivalent (resource class → what it
        // yields, inverted to "missing class → resource to harvest"). The native builds it
        // data-driven from resource class defs; the port table covers the shipped Pilz line —
        // the only kind-6 harvestable in the start campaign (extend as further resources land).
        static readonly Dictionary<string, string> ProduceMap = new()
        {
            ["Pilzstamm"] = "Pilz",
            ["Pilzhut"] = "Pilz",
        };

        // The kind-5 dig-job builder — the faithful FUN_00435f50 over the persistent request
        // list (replaces the old one-job-per-marked-tile collector, which is what marched the
        // whole camp onto a single dig order):
        // • every request's timer decays 1.0 per pass (:53-54);
        // • timer > 0 (announced/just-created) ⇒ the job's need is forced 0 (:142-156) and
        // need-0 kind-5 jobs are deleted (:163-203) — modelled as "emit nothing";
        // • timer ≤ 0 ⇒ one job per request: need = age·0.5+1.0 for a fresh job (:100-130,
        // DAT_0073708c/DAT_00737138), 7.5 when the job already existed (:137);
        // • job pos = {x, y, 14.0} (:111-113, z = DAT_00737550).
        void CollectDigJobs(int player, List<Job> jobs)
        {
            if (player != _ctx.LocalPlayer) return;   // single-player dig grid (documented)
            var reqs = _digRequests[player];
            for (int k = 0; k < reqs.Count; k++)
            {
                var r = reqs[k];
                r.Timer -= 1f;                                       //:53-54
                if (r.Timer > 0f) { r.HadJob = false; continue; }    //:142-156 → pass-3 delete
                float need = r.HadJob ? DigNeedExisting : r.Age * 0.5f + 1f;
                // The standoff refinement (FUN_0043bdd0:245): the native scan hit is refined
                // through FUN_005981d0 (marked-region BFS → nearest open/explored frontier tile)
                // before it becomes a request, and the kind-5 job carries
                // those refined coords ({x, y, 14}, FUN_00435f50:111-113). The port keeps the
                // request at the marked tile (phase-B validation reads the dig nibble there)
                // and refines at job-build time instead. Without this the match cost used the
                // buried mark row: the ×5 vertical weight (FUN_00438210:141, DAT_007374cc)
                // blew every candidate past the 120 hard gate (DAT_007683a4,:149) the moment
                // the marks sat ≥3 rows below the walk line — marquee marks never dispatched
                // (DigPlayReproTests repro: reqs=1, pooled=True, 0 assigns).
                // A mark with no reachable standoff emits no job — the native rejects such
                // scan hits outright ({-1,-1} → no request at all).
                if (!TryRefineDigStandoff(r.X, r.Y, out float jx, out float jy)) continue;
                r.HadJob = true;
                jobs.Add(new Job
                {
                    Kind = JobKind.Dig, TargetHandle = -1, WorkClass = "",
                    Tx = r.X, Ty = r.Y, Pos = new Vector3(jx, jy, DigJobZ), Need = need,
                });
            }
        }

        // The animator caller passes seed==reference to FUN_005981d0 (FUN_0043bdd0:241-245).
        // The worker returns an open, explored tile on the marked-region frontier.
        bool TryRefineDigStandoff(int tx, int ty, out float jx, out float jy)
        {
            jx = tx; jy = ty;
            var terrain = _ctx.Terrain;
            if (terrain == null) return false;
            // Animator passes the same integer tile coordinate as both seed and reference.
            if (!terrain.TryFindDigEdge(
                    tx,
                    ty,
                    _ctx.LocalPlayer,
                    tx,
                    ty,
                    out int edgeTileX,
                    out int edgeTileY,
                    _ctx.FogOfWar))
            {
                return false;
            }
            jx = edgeTileX;
            jy = edgeTileY;
            return true;
        }

        // The per-player request maintenance — the faithful FUN_0043bdd0 (runs right after the
        // job build, MaintenanceTick:217). Three phases over the player's dig-mark bbox:
        // A. active diggers (:58-140): a gnome currently digging refreshes the first request
        // within Manhattan<5 of its dig point to timer=10.0 (:100-105); with none nearby it
        // seeds a new request {round(pos), timer=1.0, age=0} (:113-138) — the request list
        // follows the dig front.
        // B. validate/age (:141-205): timer −= 1.0; requests with timer ≤ 0 whose tile no
        // longer carries the player's dig nibble are deleted (:170-193); surviving open
        // requests age += 1.0 (:195-199) — need grows while a dig area waits for a worker.
        // C. scan (:22): ~2000 cells/pass walk of the mark bbox; a scan point near a live
        // request (Manhattan<8) is skipped (:314-339); a marked hit (nibble≠0,:240) becomes
        // a new request only if none exists within Manhattan<20 (:251-277) — a 10×10 marked
        // rect = one request. (The native refines the hit through FUN_005981d0 — a
        // dig-start-point search; port stand-in = the scan hit itself, documented: the
        // row-major scan reaches the topmost marked row first, which is the dig front.)
        void MaintainDigRequests(int player)
        {
            var reqs = _digRequests[player];
            var T = _ctx.Terrain;
            if (T == null || player != _ctx.LocalPlayer)
            {
                if (reqs.Count > 0) reqs.Clear();
                return;
            }
            // FUN_0043bdd0:53 — the whole pass is gated on a non-empty mark rect.
            if (!T.TryGetDigMarkBounds(player, out int bx0, out int by0, out int bx1, out int by1)) return;

            // ---- A. active diggers refresh/seed ----
            foreach (var e in _ctx.Entities.Values)
            {
                if (e.Owner != player || !IsActiveDigger(e, player)) continue;
                var dp = DigPosOf(e);
                int dx = Mathf.RoundToInt(dp.x), dy = Mathf.RoundToInt(dp.y);
                bool near = false;
                for (int k = 0; k < reqs.Count; k++)
                    if (Manhattan(reqs[k].X - dx, reqs[k].Y - dy) < DigDiggerRefreshManhattan)
                    { reqs[k].Timer = DigAnnounceTimer; near = true; break; }   // first match only (:100-106)
                if (!near) reqs.Add(new DigRequest { X = dx, Y = dy, Timer = DigRequestInitialTimer, Age = 0f });
            }

            // ---- B. decay / validate / age ----
            for (int k = reqs.Count - 1; k >= 0; k--)
            {
                var r = reqs[k];
                r.Timer -= 1f;                                       //:149-150
                if (r.Timer > 0f) continue;
                if (T.RawDigNibble(r.X, r.Y, player) == 0) { reqs.RemoveAt(k); continue; }   //:170-193
                r.Age += 1f;                                         //:195-199
            }

            // ---- C. incremental bbox scan ----
            int ex1 = bx1 + 1, ey1 = by1 + 1;                        // exclusive-upper walk like the native
            for (int n = 0; n < DigScanBudget; n++)
            {
                _digScanX[player]++;
                if (_digScanX[player] < bx0 || _digScanX[player] >= ex1) { _digScanY[player]++; _digScanX[player] = bx0; }
                if (_digScanY[player] < by0 || _digScanY[player] >= ey1) _digScanY[player] = by0;
                int sx = _digScanX[player], sy = _digScanY[player];
                if (NearRequest(reqs, sx, sy, DigScanSkipManhattan)) continue;        //:314-339
                if (T.RawDigNibble(sx, sy, player) == 0) continue;                    //:240
                if (NearRequest(reqs, sx, sy, DigRequestMergeManhattan)) continue;    //:251-277
                reqs.Add(new DigRequest { X = sx, Y = sy, Timer = DigRequestInitialTimer, Age = 0f });   //:279-309
            }
        }

        static bool NearRequest(List<DigRequest> reqs, int x, int y, int manhattan)
        {
            for (int k = 0; k < reqs.Count; k++)
                if (Manhattan(reqs[k].X - x, reqs[k].Y - y) < manhattan) return true;
            return false;
        }

        // "Actively digging" — the native planner-state 0xe gate (FUN_0043bdd0:75-76,
        // FUN_00438210:167-168). Port detection: a busy (un-pooled) gnome whose live Tcl
        // worktask is "dig" (evt_task_dig_proc / evt_autoprod_dig_proc both set
        // current_worktask "dig", z_events.tcl:1019/:1514).
        bool IsActiveDigger(DigglesTclEntity e, int player)
        {
            if (e.Class == null || (e.Class.Kind != "gnome" && e.Class.Kind != "baby")) return false;
            if (_idle[player].Contains(e.Handle)) return false;
            return e.VarsScope.Vars.TryGetValue("current_worktask", out var wt) && wt == "dig";
        }

        // The lazy active-digger list MatchCost's crowding pass shares across one Sweep
        // (FUN_00438210:167-168 walks the player's planner records once per match).
        List<DigglesTclEntity> CollectActiveDiggers(int player)
        {
            var list = new List<DigglesTclEntity>();
            foreach (var e in _ctx.Entities.Values)
                if (e.Owner == player && IsActiveDigger(e, player)) list.Add(e);
            return list;
        }

        // The digger's current dig point — the native reads the plan record's step position
        // (rec+0x1c/+0x20, FUN_0043bdd0:77-79); port equivalent = the Tcl current_digpos the dig
        // event handler stored (falls back to the gnome's own position).
        static Vector3 DigPosOf(DigglesTclEntity e)
        {
            if (e.VarsScope.Vars.TryGetValue("current_digpos", out var s) && !string.IsNullOrEmpty(s))
            {
                var parts = s.Split(new[] { ' ', '\t', '{', '}' }, System.StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2
                    && float.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float x)
                    && float.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float y))
                    return new Vector3(x, y, parts.Length > 2 && float.TryParse(parts[2],
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float z) ? z : e.Position.z);
            }
            return e.Position;
        }

        bool ProductionPlaceAvailable(DigglesTclEntity e)
        {
            if (!e.BuildupComplete || e.ProdSlots.Count == 0) return false;
            // A boxed place contributes no production job: the whole prod chain is gated on
            // get_boxed (genericprod.tcl:19 obj_init /:569 prod work loop) — a crate produces
            // nothing until unpacked.
            if (e.VarsScope.Vars.TryGetValue("_boxed", out var bxv) && (bxv == "1" || bxv == "true")) return false;
            // FUN_00436750:69 gates the whole section on the place's enabled byte (prodComp+0x64,
            // set_prod_enabled — the Lager toggles it) and the auto-schedule flag
            // ((obj+0x3d0 & 0x800000) == 0 — a manual-schedule place emits no ambient jobs).
            if (!e.ProdAutoSchedule) return false;
            if (e.VarsScope.Vars.TryGetValue("_prod_enabled", out var pev) && pev == "0") return false;
            // rec+0x43 (set_prod_schedule, FUN_00696670:73-82): the SP production section runs
            // only while it is set (FUN_00436750:70; the invention emission checks it too,
            // FUN_00434e60:608). Default on; the farm's event0 tick drives it 0/1 every 3 s
            // (farm.tcl:158/:166 — 1 = "send me a gnome", 0 = breeding timer running) — this
            // is the native fetch/visit cadence for self-supplying places.
            if (e.VarsScope.Vars.TryGetValue("_prod_schedule", out var psv) && psv == "0") return false;
            // Native demand scan FUN_00434e60 marks every slot unavailable when the place cannot
            // find a built, charged source in range. This gates inventions, material requests, and
            // normal production alike; acquisition happens only after a worker is assigned.
            if (!_ctx.Energy.Check(e)) return false;
            // One worker per building: the workat dispatch claims the prodplace for the gnome
            // (rec+0x24 — the same FUN_005ede50:17 claim the request dispatches write; cleared on
            // idle/failure). While a live claim stands, the place's slot section contributes no
            // second job — else every sweep re-collected the same building and marched the whole
            // camp to one campfire. The kind-4 haul
            // dispatch claims the place the same way (FUN_0043c660:97-102 rec+0x1c=3 + rec+0x24),
            // so material trips serialize with the production itself.
            return e.CurrentWorkerHandle < 0 || !_ctx.Entities.ContainsKey(e.CurrentWorkerHandle);
        }

        // The native research allocator is its own MaintenanceTick builder: FUN_00433a70:169 calls
        // FUN_00434e60 before the request and kind-3 production builders (:170/:174). That builder
        // walks every registered place's current slot table (:26/:269-300), and every live
        // positive, uninvented, qualified slot appends a kind-2 candidate (:437-447/:580-651).
        // There is no "first production slot" break and no building re-registration trigger.
        // Keeping this scan separate prevents a preceding invented-but-starved slot (or an
        // unqualified invention) from hiding a later queued invention until pack/unpack clears
        // the slot table. Bp is only the port's kind-2 marker; the order remains keyed by the
        // plain class in ProdSlots, matching genericprod.tcl:705-718.
        void CollectInventionJobs(DigglesTclEntity e, int player, List<Job> jobs)
        {
            foreach (var kv in e.ProdSlots)
            {
                // Advance the slot's CProd priority mirror first — count==0 resets it, any
                // positive count ramps it (FUN_005edea0:49-85), whether or not this pass
                // emits a job for it.
                int count = (int)kv.Value;
                float prio = SlotPriority(e.Handle, kv.Key, count);
                if (count <= 0) continue;
                if (GetOwnerAttrib(player, "Bp" + kv.Key) > 0.5) continue;
                // FUN_00434e60:529-578 finds whether any own gnome meets every requirement;
                // Match repeats the same predicate for the particular assignee.
                if (!AnyGnomeQualifiesToInvent(e, kv.Key, player)) continue;
                // Kind-2 need = slot priority × 10.0 (rec+0x18 = slot[+0x14] × DAT_00737534,
                // FUN_00434e60:646-647) — not a function of the queue count. A fresh order
                // sits at need ≈ 0 (it sorts last, dispatching only when nothing else wants
                // the pass — see the FUN_00433a70:418 note in Sweep) and only outranks a user
                // pack/buildup request (7.5, FUN_00436440:87/:118) after ~150 s unserviced.
                jobs.Add(new Job
                {
                    Kind = JobKind.Production, TargetHandle = e.Handle, WorkClass = "Bp" + kv.Key,
                    Tx = 0, Ty = 0, Pos = e.Position, Need = prio * InventionNeedScale,
                });
            }
        }

        // One owned production building with a scheduled, invented item → a workat job (the SP
        // kind-3 section FUN_00436750). This is deliberately separate from CollectInventionJobs,
        // just as MaintenanceTick calls FUN_00434e60 and FUN_00436750 as separate builders.
        // The kind-3 workat is gated on FUN_00434c20: every recipe material must already be in
        // the place inventory; otherwise kind-4 hauling is collected, and missing + unreachable
        // leaves this selected regular-production slot starved for the pass.
        void CollectProductionJobs(DigglesTclEntity e, int player, List<Job> jobs)
        {
            foreach (var kv in e.ProdSlots)
            {
                if (kv.Value <= 0) continue;
                // FUN_00436750:100-101 only admits slots for which FUN_005ed620 says the item is
                // invented. Uninvented slots belong exclusively to the independent kind-2 scan.
                if (GetOwnerAttrib(player, "Bp" + kv.Key) <= 0.5) continue;
                // Documented deviation (kind-3 need): the native ranks the workat by the same
                // CProd slot priority — need = slot[+0x14] × DAT_00737698 = 1000.0
                // (FUN_00436750:26;:10 picks the max-priority slot) — reaching 1000
                // where dig/harvest/request weights top out at 7.5. The port keeps its count+2
                // heuristic here: the request-vs-production interlock the huge scale would
                // reorder is carried natively by gates the port already models differently
                // (the kind-3 section skips a place whose pack/unpack byte is armed,
                // FUN_00436750:71, and the record-busy/claim gates serialize the rest), and
                // recalibrating kind-3 against every other pinned weight is out of scope here.
                // Only the kind-2 invention need above is switched to the native math.
                float need = (float)kv.Value + 2f;
                // materialneed byte (rec+0x41, written by set_prod_materialneed
                // FUN_00696920:73-82): the recipe check FUN_00434c20 gates its whole walk on it
                // (:22) — materialneed 0 ⇒ "complete", the workat dispatches with no material
                // gate and no kind-4 hauls. The farm runs this way (farm.tcl:118 init 0; its
                // event0 tick flips it 1 only while breeding parents are missing,:156/:232-234)
                // — ignoring the byte made the port keep fetching hamsters forever. Default
                // (var unset) = 1: the native record byte
                // inits to need-materials and ordinary workshops never touch it.
                bool materialNeed = !e.VarsScope.Vars.TryGetValue("_materialneed", out var mn) || mn != "0";
                var missing = materialNeed ? MissingMaterials(e, kv.Key) : new List<string>();
                if (missing.Count == 0)
                {
                    BindCurrentItem(e, kv.Key);
                    jobs.Add(new Job
                    {
                        Kind = JobKind.Production, TargetHandle = e.Handle, WorkClass = kv.Key,
                        Tx = 0, Ty = 0, Pos = e.Position, Need = need,
                    });
                    break;   // one worker job per building
                }
                // Missing materials → absorb what already lies at the place, then one kind-4
                // haul job per missing entry with a distinct source item (FUN_004375c0 emits a
                // job per missing entry {pos=place, +4=place, +8=source item}). The Sweep-side
                // batching (FUN_0043c660:121-188) folds nearby ones into a single carrier
                // trip; non-batched leftovers serialize on the one-dispatch-per-pass cap + the
                // place claim, one trip at a time.
                var used = new HashSet<int>();
                bool anyJob = false;
                foreach (var cls in missing)
                {
                    var src = FindHaulSource(e, cls, player, used);
                    if (src == null) continue;                    // unreachable → starve, no job
                    if (WithinAbsorbBox(src.Position, e.Position))
                    {
                        // FUN_00437370: the place takes the nearby item itself (its unchanged
                        // evt_prodplace_transferprod handler runs take_item, genericprod.tcl:57).
                        src.WorkBusyCooldown = AssignCooldown;    // don't re-pick before it lands
                        EvalAsPlace(e, "set_event " + e.Handle + " evt_prodplace_transferprod -target "
                            + e.Handle + " -subject1 " + src.Handle, "absorb nearby material");
                        continue;
                    }
                    used.Add(src.Handle);
                    anyJob = true;
                    jobs.Add(new Job
                    {
                        Kind = JobKind.Haul, TargetHandle = e.Handle, WorkClass = cls,
                        Tx = 0, Ty = 0, Pos = e.Position, Need = need,
                        ItemHandle = src.Handle, ForItem = kv.Key,
                    });
                }
                if (anyJob) BindCurrentItem(e, kv.Key);
                break;   // one slot per building per pass (native picks the single best slot)
            }
        }

        // Bind the place's current item when the collector picks its slot — the native writes
        // rec+0x48 = the slot's item id the first time the slot is serviced (FUN_00436750:
        // 146-158, `if (rec+0x48 == -1) rec+0x48 = slotItem`), before any haul/workat runs.
        // The over-building bar keys its cell on this binding (FUN_00520620:483), so the blue
        // "order pending / delivering" bar exists from the first service pass, not only once
        // a worker starts. info_end_prod clears it (CurrentProdItem = "").
        static void BindCurrentItem(DigglesTclEntity place, string item)
        {
            if (place.CurrentProdItem == item) return;
            // First-wins while the bound order lives. A binding whose own slot count fell to 0
            // (cancelled order) is dead: the native record would be re-stamped by the next
            // workat emit anyway (FUN_0043a8a0:77 writes rec+0x48 unconditionally), but the
            // haul phase precedes any emit — the stale bind kept the new order's
            // delivering-blue bar off and the place looked dead until a different order was
            // placed.
            if (!string.IsNullOrEmpty(place.CurrentProdItem)
                && place.ProdSlots.TryGetValue(place.CurrentProdItem, out var c) && c > 0) return;
            place.CurrentProdItem = item;
        }

        // ---- invention qualification (the FUN_00434e60:529-564 / FUN_00437a40 predicate) ----
        // Requirements = the class method prod_item_attribs <item> (tttinvent_* pairs,
        // genericprod.tcl:119-122) — the same data the native recipe record serves at
        // FUN_0043e4d0 +0x40/+0x44. A gnome qualifies iff every pair's value ≤ his own attrib
        // (native reads obj+0x21c; port reads get_attrib — the exp value store). Empty
        // requirements (genuinely requirement-free tttinvent) qualify anyone — the native
        // treats a missing/empty pair list as a pass (FUN_00437a40's loop body never rejects).
        readonly Dictionary<string, List<(string attr, float val)>> _inventReqCache = new();
        readonly HashSet<string> _healedInvents = new();   // one tttinvent re-seed try per item

        List<(string attr, float val)> InventRequirements(DigglesTclEntity place, string item)
        {
            string key = (place.Class?.Name ?? "") + "\n" + item;
            if (_inventReqCache.TryGetValue(key, out var cached)) return cached;
            var reqs = ParseInventPairs(place, item);
            if (reqs.Count == 0 && _healedInvents.Add(item))
            {
                // Heal the shared-global tttinvent clobber (same law as BottomMenuController.
                // InventionStateFor): an item that is itself a production class destroys its own
                // tttinvent_$item at class load (genericprod.tcl:140 `catch {unset …}`) because
                // the port's class loads share one global scope. Re-running the item's
                // techtreetunes section restores the canonical values.
                try
                {
                    _ctx.Interp.Eval("set tttsection_tocall {" + item + "}");
                    _ctx.Interp.Eval("call scripts/misc/techtreetunes.tcl");
                }
                catch { /* partial data setups */ }
                reqs = ParseInventPairs(place, item);
            }
            _inventReqCache[key] = reqs;
            return reqs;
        }

        List<(string attr, float val)> ParseInventPairs(DigglesTclEntity place, string item)
        {
            var reqs = new List<(string, float)>();
            if (place.Class == null) return reqs;
            try
            {
                // tttinvent_* is a list of sublists: {{exp_Nahrung 0.039}} — flatten.
                var raw = _ctx.Interp.Eval(
                    "call_method_static " + place.Class.Name + " prod_item_attribs {" + item + "}");
                var tokens = new List<string>();
                foreach (var el in Diggles.Runtime.Tcl.TclList.Parse(raw))
                    tokens.AddRange(Diggles.Runtime.Tcl.TclList.Parse(el));
                for (int i = 0; i + 1 < tokens.Count; i += 2)
                    if (float.TryParse(tokens[i + 1], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var v))
                        reqs.Add((tokens[i], v));
            }
            catch { /* no method → requirement-free */ }
            return reqs;
        }

        bool GnomeQualifiesToInvent(DigglesTclEntity g, List<(string attr, float val)> reqs)
        {
            foreach (var (attr, val) in reqs)
            {
                float have = 0f;
                try
                {
                    float.TryParse(_ctx.Interp.Eval("get_attrib " + g.Handle + " " + attr),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out have);
                }
                catch { }
                if (have < val) return false;   // FUN_00437a40: reject on any unmet pair
            }
            return true;
        }

        // The emission gate FUN_00434e60:529-564/:587 — any own gnome qualifies (the scan walks
        // all the owner's gnomes, not just the idle pool).
        bool AnyGnomeQualifiesToInvent(DigglesTclEntity place, string item, int player)
        {
            var reqs = InventRequirements(place, item);
            if (reqs.Count == 0) return true;
            foreach (var g in _ctx.Entities.Values)
            {
                if (g.Owner != player || g.Class == null
                    || (g.Class.Kind != "gnome" && g.Class.Kind != "baby")) continue;
                if (GnomeQualifiesToInvent(g, reqs)) return true;
            }
            return false;
        }

        // Shared native invention predicate for CAIManager's ai_getpossibleinventions.
        // Keeping the query on the planner guarantees that opponent policy and work emission
        // read the same prod_item_attribs requirements and the same per-gnome stat values.
        public bool OwnerCanInventAt(DigglesTclEntity place, string item, int player) =>
            AnyGnomeQualifiesToInvent(place, item, player);

        // ---- fog-of-war eligibility (Docs/FOG_OF_WAR_RE.md §2.8) --------------------------------
        // Two different native thresholds gate the planner's world scans on the owner's fog lane
        // (both via CObj::GetMaxFow FUN_005cb3a0):
        // • the source resolver feeding kind-4 hauls (FUN_00436d40 → FUN_004319b0:63) and
        // kind-6 harvest promotion (FUN_0043b620:162-178 → FUN_0043bae0:33) both end in
        // FUN_004317a0:71-74 and accept a target only while GetMaxFow(target, owner) > 0x8c —
        // the target must be (near-)currently seen, not merely explored;
        // • the demand scan's availability registration (FUN_00434e60:114) counts an object
        // while GetMaxFow(item, owner) > 0 — explored gray suffices (same law as the Tcl
        // obj_find_next family, FUN_006a2000:212-213).
        // A fog-hidden item is therefore invisible to hauls (gnomes must not fetch construction
        // materials out of black fog); a class with no resolver-visible source still
        // escalates to the kind-6 chop — that is the resolver's exact-failure law. FogOfWar ==
        // null (headless tests / menu boot) ⇒ no gate, the native -nofow full reveal.
        // The native resolver also requires the requester's bit in a per-object visibility mask
        // byte (entry+8 of the 0xc-stride availability array, FUN_004317a0:46/:55 — checked
        // before the live:73 fow test). Decode of the stamping side:
        // CAiCentral's tick visits only 150 object slots per pass (FUN_00424680:57-73; cursor
        // DAT_00c3c964 wraps at 0x2000 ⇒ each object is re-stamped every ~5.5 s), and each visit
        // clears the byte (FUN_004315a0:55) then re-stamps it — neutral objects: one bit per
        // player lane whose fow > 200 (:61-78) plus the single best lane > 0x80 (:93-102);
        // owned objects: the owner's bit at fow > 0x80 (:83-91; the > 200 branch:88-90 ORs
        // the owner bit via FUN_00431c20, a 0xc-stride accessor over the same table shape —
        // its this-pointer is decompiler-lost, so same-entry aliasing is unproven; either way
        // the byte is re-derived on every cursor revisit). The mask is therefore a
        // ≤5.5-s-stale periodic snapshot, not a forever-sticky "ever seen > 200" latch — the
        // bit decays as soon as the amortized cursor revisits a faded object.
        // Documented port deviation (decision: not modelled): the port applies only the live
        // > 0x8c test. Residual deltas vs native: (a) an object revealed moments ago resolves
        // immediately (native waits ≤5.5 s for the amortized stamp); (b) a neutral object whose
        // best fow lane belongs to another player resolves for us at live fow ∈ (0x8c, 200]
        // where the native demands > 200 for a non-best lane. Both are edge-of-vision timing /
        // multi-lane nuances invisible to the gray (≤0x80) and fully-visible cases the tests
        // pin; a faithful model needs the full amortized per-object stamp loop (a plain sticky
        // bitset would implement the wrong law), which is disproportionate here.
        private const int SourceResolveFowMin = 0x8c;   // FUN_004317a0:73 (the FOW doc's "AI-detect" threshold)

        bool FowEligible(DigglesTclEntity it, int player, int min)
            => _ctx.FogOfWar == null || DigglesTclEngine.GetMaxFow(_ctx, it, player) > min;

        // ---- recipe / material availability (the FUN_00434c20 model) ----------------------------

        // The FUN_00437370 "material already lies at the workplace" box: |Δx|<4, |Δz|<4 (the two
        // horizontal-ish axes, DAT_007374a0) and |Δy|<2 (the gameplay-vertical axis, DAT_00737330).
        const float AbsorbXZ = 4f;
        const float AbsorbY = 2f;

        static bool WithinAbsorbBox(Vector3 item, Vector3 place)
            => Mathf.Abs(item.x - place.x) < AbsorbXZ
               && Mathf.Abs(item.y - place.y) < AbsorbY
               && Mathf.Abs(item.z - place.z) < AbsorbXZ;

        // The recipe = the class's prod_item_materials method (techtreetunes tttmaterial_* baked
        // per class by genericprod.tcl:103-115 — the same data the native recipe table FUN_0043e4d0
        // serves). Cached per (placeClass, item); an eval failure or no method reads as an empty
        // recipe, which — like the native's missing recipe entry (FUN_00434c20:29-33) — leaves the
        // workat ungated.
        public List<string> MaterialsFor(DigglesTclEntity place, string item)
        {
            string key = (place.Class?.Name ?? "") + "\n" + item;
            if (_materialsCache.TryGetValue(key, out var cached)) return cached;
            var mats = new List<string>();
            try
            {
                var res = _ctx.Interp.Eval("call_method " + place.Handle + " prod_item_materials {" + item + "}");
                foreach (var m in Diggles.Runtime.Tcl.TclList.Parse(res))
                    if (!string.IsNullOrEmpty(m) && m != "0") mats.Add(m);
            }
            catch { /* no recipe method → ungated, like the native's missing table entry */ }
            _materialsCache[key] = mats;
            return mats;
        }

        // Recipe minus the place inventory, multiset semantics (each inventory item satisfies at
        // most one recipe entry — FUN_00434c20:83-95 removes one need entry per matching item).
        public List<string> MissingMaterials(DigglesTclEntity place, string item)
        {
            var mats = MaterialsFor(place, item);
            if (mats.Count == 0) return mats;
            var missing = new List<string>(mats);
            foreach (var h in place.Inventory)
            {
                if (!_ctx.Entities.TryGetValue(h, out var it) || it.Class == null) continue;
                int idx = missing.IndexOf(it.Class.Name);
                if (idx >= 0) missing.RemoveAt(idx);
            }
            return missing;
        }

        // Nearest eligible world source of a material class (the native source-assignment feeding
        // kind-4 job+0x08). Eligibility mirrors the planner's resource scans (FUN_00434e60:110-114):
        // unlocked (obj+0x3d0 bit 4 clear), owner == player or -1, and either free in the world or
        // shelved in a Lager (pickup_store). A mid-plan item (another gnome is fetching it) or one
        // still cooling down (item+0x3e8 = 2, FUN_0043c660:88) is skipped.
        DigglesTclEntity? FindHaulSource(DigglesTclEntity place, string cls, int player,
                                         HashSet<int>? exclude = null)
        {
            DigglesTclEntity? best = null; float bestD = float.MaxValue;
            foreach (var it in _ctx.Entities.Values)
            {
                if (it.Class == null || it.Class.Name != cls) continue;
                if (it.ContainedIn >= 0 && !it.InStore) continue;    // packed away (in a gnome / the place)
                if (it.Owner != player && it.Owner >= 0) continue;   // someone else's stock
                if (IsLocked(it) || it.WorkBusyCooldown > 0) continue;
                if (!FowEligible(it, player, SourceResolveFowMin)) continue;   // hidden in fog (FUN_004317a0:73)
                if (PlanTargetsItem(it.Handle)) continue;            // already being fetched
                if (exclude != null && exclude.Contains(it.Handle)) continue;   // picked this pass
                float d = Dist2(it.Position, place.Position);
                if (d < bestD) { bestD = d; best = it; }
            }
            return best;
        }

        bool PlanTargetsItem(int handle)
        {
            foreach (var p in _plans.Values)
            {
                if (p.ItemHandle == handle) return true;
                if (p.Items.Contains(handle)) return true;   // batched kind-4 riders
            }
            return false;
        }

        // ---- UI seams (BottomMenuController / get_prod_slot_buildable) --------------------------

        // The active kind-4 haul delivering into this building, if any (drives the Delivering bar).
        public bool TryGetHaulFor(int destHandle, out string forItem)
        {
            foreach (var p in _plans.Values)
            {
                if (p.Kind != JobKind.Haul) continue;
                if (p.DestHandle == destHandle) { forItem = p.ForItem; return true; }
                for (int k = 0; k < p.Dests.Count; k++)
                    if (p.Dests[k] == destHandle) { forItem = p.ForItems[k]; return true; }
            }
            forItem = "";
            return false;
        }

        // The "worker is AT the place" gate the bar model's green phase keys on — the
        // FUN_00437370 workplace box (|Δx|<4, |Δy|<2, |Δz|<4, DAT_007374a0/DAT_00737330), the
        // port stand-in for the native kind-2/3 plan's completed walk leg (the workat event —
        // and with it the Tcl progress feed — only fires post-arrival in the native,
        // FUN_00438e80:91→:108 / FUN_004391f0:83→:100). (The former partial-blue DeliveryPct
        // fill is deleted — the native blue bar is always the solid full-width back,
        // FUN_00520620:492-524.)
        public static bool AtWorkplace(Vector3 gnome, Vector3 place)
            => WithinAbsorbBox(gnome, place);

        // "Ingredients available" for the production window's Red<icon> gate (get_prod_slot_
        // buildable): every recipe entry is either already in the place inventory or has an
        // eligible source in the world. Port-pragmatic reading of the native per-slot material
        // byte the demand scan maintains (FUN_00434e60 slot+0x18/0x19).
        public bool SlotBuildable(DigglesTclEntity place, string item)
        {
            var missing = MissingMaterials(place, item);
            if (missing.Count == 0) return true;
            // per-class deficit vs eligible world stock
            var needByClass = new Dictionary<string, int>();
            foreach (var cls in missing)
                needByClass[cls] = needByClass.TryGetValue(cls, out var n) ? n + 1 : 1;
            int player = ClampPlayer(place.Owner);
            foreach (var kv in needByClass)
            {
                int avail = 0;
                foreach (var it in _ctx.Entities.Values)
                {
                    if (it.Class == null || it.Class.Name != kv.Key) continue;
                    if (it.ContainedIn >= 0 && !it.InStore) continue;
                    if (it.Owner != player && it.Owner >= 0) continue;
                    if (IsLocked(it)) continue;
                    // World stock counts while explored (the demand-scan registration gate
                    // GetMaxFow > 0, FUN_00434e60:114 — gray suffices for the availability
                    // surface; only the haul resolver needs the stricter 0x8c).
                    if (!FowEligible(it, player, 0)) continue;
                    if (++avail >= kv.Value) break;
                }
                if (avail < kv.Value) return false;
            }
            return true;
        }

        // Run a set_event in the place's object scope (the native FUN_00437370 queues the command
        // through the same Tcl bridge FUN_0054ea20).
        void EvalAsPlace(DigglesTclEntity place, string cmd, string what)
        {
            Debug.Log($"[ProdDispatcher] absorb emit place#{place.Handle}: {cmd}");
            var prev = _ctx.This;
            _ctx.This = place;
            try { _ctx.Interp.Eval(cmd); }
            catch (System.Exception e) { Debug.LogWarning($"[ProdDispatcher] {what}: {e.Message}"); }
            finally { _ctx.This = prev; }
        }

        // Unset names read the def_attrib registry default (the native's per-owner slot table is
        // registry-shaped and initialized from def_attrib, FUN_0062c9b0/FUN_005da2f0) — that is
        // how preinvented Bp items (def_attrib Bp$x 0 1 1, genericprod.tcl:147-156) gate open
        // with no explicit seeding. Mirrors get_owner_attrib in DigglesTclEngine.
        double GetOwnerAttrib(int player, string name)
            => _ctx.OwnerAttribs.TryGetValue(player, out var d) && d.TryGetValue(name, out var v)
                ? v : _ctx.Stats.Registry.DefaultOf(name);

        // Whether a gnome may take a job. Invention jobs (kind 2, our Production job carrying
        // the Bp<item> marker) accept only a qualified gnome: the native kind-2 match runs
        // FUN_00437a40 (FUN_00438210:88-102), which walks the item's tttinvent requirement
        // pairs against the gnome's own attribs and rejects on any unmet one (without this,
        // any free gnome could invent regardless of qualification). Other kinds: anyone
        // (tool gating later).
        private bool Match(
            DigglesTclEntity g,
            Job job,
            Dictionary<int, int>? preferredWorkplaces,
            HashSet<int>? claimedWorkplaces)
        {
            if (job.Kind != JobKind.Production) return true;
            bool isPreferred = preferredWorkplaces != null &&
                preferredWorkplaces.TryGetValue(g.Handle, out int preferredHandle) &&
                preferredHandle == job.TargetHandle;
            if (claimedWorkplaces != null && claimedWorkplaces.Contains(job.TargetHandle) &&
                !isPreferred)
            {
                return false;
            }
            if (job.WorkClass.StartsWith("Bp", System.StringComparison.Ordinal) &&
                _ctx.Entities.TryGetValue(job.TargetHandle, out var place))
            {
                return GnomeQualifiesToInvent(g, InventRequirements(place, job.WorkClass.Substring(2)));
            }
            return true;
        }

        // Reserve the job target so two gnomes don't converge; fails if already reserved by another.
        bool Reserve(Job job, DigglesTclEntity g)
        {
            double now = _ctx.Sim?.GameTime ?? 0.0;
            if (_ctx.PlaceLocks.Check(job.Pos, 0.8f, g.Handle, now)) return false;
            _ctx.PlaceLocks.Set(job.Pos, 6.0, g.Handle, now);
            return true;
        }

        bool Assign(DigglesTclEntity g, Job job, int player, List<Job>? haulBatch = null)
        {
            if (job.Kind == JobKind.Production
                && _ctx.Entities.TryGetValue(job.TargetHandle, out var energyConsumer)
                && !_ctx.Energy.Acquire(energyConsumer))
                return false;
            g.WorkBusyCooldown = AssignCooldown;
            _failBreather.Remove(g.Handle);   // FUN_00434bc0 zeroes rec+0x48 when a job is taken
            _idle[player].Remove(g.Handle);
            g.PlannerIdleAnnounceFlag = 0;
            g.PlannerCurrentWorkplaceId = job.TargetHandle > 0 ? job.TargetHandle : -1;
            // One harvester at a time: the next harvest emission waits out the 5 s pass + 10 s
            // post-emit cadence (FUN_005f1500:53/:159, FUN_005f4220:56/:279). Armed on assignment
            // (our job list is rebuilt per sweep — see the constant block); a failure clamps it
            // back down to the 20-tick reopen in ReleaseGnome.
            if (job.Kind == JobKind.Harvest) _harvestCooldown[player] = HarvestEmitCooldownTicks;
            // A dig assignment re-announces the request (the MaintenanceTick dispatch tail
            // advances the fresh plan via FUN_0043a460, whose dig-step scan re-stamps every
            // request within Manhattan<20 to timer=10,:55-72 → FUN_0043bc90) — the taken dig
            // area closes to further dispatch the moment a digger is en route.
            if (job.Kind == JobKind.Dig) AnnounceDig(job.Tx, job.Ty);
            // Place-request dispatch is one-shot in the port: the bytes clear and the prodplace
            // is claimed for the gnome (rec+0x24, FUN_005ede50:17) at dispatch. (The MP emitter
            // clears the bytes itself, FUN_005f3a50:65-69/:79-84; the SP planner instead gates
            // re-collection on the record leaving idle, FUN_00436440:31 — same observable. A
            // dying unpack plan re-arms the byte, see DropPlan.)
            if ((job.Kind == JobKind.Pack || job.Kind == JobKind.Unpack)
                && _ctx.Entities.TryGetValue(job.TargetHandle, out var place))
            {
                place.BuildupRequested = false;
                // The pack byte (+0x29) must survive the dispatch: the Tcl chain re-reads it
                // mid-chain — prod_builddown aborts silently when it is gone (eventuell ist das
                // Abbauen inzwischen gecancelt, z_work_common.tcl:444 `if {![get_prod_pack
                // $item]} { return }`) — that is the native cancel mechanism (clearing the byte
                // makes the arriving packer stand down). The native never clears it at dispatch:
                // the window button arms it (FUN_005242b0 case 2:315-318, +0x29 u16 = 1) and it
                // dies only when the building actually boxes (set_boxed side effects,
                // FUN_0067dc80:157-161). Clearing it here made every pack order a no-op — the
                // gnome walked over and prod_builddown returned without doing anything.
                // Re-dispatch while the packer works is gated by the CurrentWorkerHandle claim,
                // not the byte.
                if (job.Kind != JobKind.Pack) place.PackRequested = false;
                place.UnpackRequested = false;
                place.CurrentWorkerHandle = g.Handle;
            }
            // The production workat dispatch claims the place the same way (rec+0x24 via the
            // FUN_005ede50:17 claim helper) — the one-worker gate in ProductionPlaceAvailable reads
            // it; OnGnomeIdle/ReleaseGnome dissolve it. The workat emit also re-invalidates the
            // progress pair (rec+0x4c/+0x50 = 0xffffffff — step code 9 FUN_0043a8a0:79-80,
            // code 8:91-92): a stale pair from an interrupted previous cycle must not paint the
            // bar green while the new worker is still walking.
            else if (job.Kind == JobKind.Production && _ctx.Entities.TryGetValue(job.TargetHandle, out var pp))
            {
                pp.CurrentWorkerHandle = g.Handle;
                // The workat emit stamps the record's current item rec+0x48 = the job's item
                // id (produce = step code 9, FUN_0043a8a0:77; invent = code 8,:89 — the Bp
                // marker lives only on the -text1 string; +0x48 gets the plain class both
                // ways). For an invented slot the collector already bound it
                // (FUN_00436750:146-158); for an invention order this emit is the first
                // bind — the blue in-progress bar may only appear once a worker is assigned,
                // never for a slot with nobody in a work window.
                pp.CurrentProdItem = job.WorkClass.StartsWith("Bp", System.StringComparison.Ordinal)
                    ? job.WorkClass.Substring(2) : job.WorkClass;
                pp.ProdProgressCur = 0;
                pp.ProdProgressMax = 0;
            }
            // The kind-4 haul dispatch claims the destination place (FUN_0043c660:97-102 —
            // rec+0x1c=3 + rec+0x24=gnome) and stamps the source item's cooldown (item+0x3e8=2,
            //:87-88) so no second collector picks it while the plan opens. Each batched
            // rider's dest is claimed the same way (:235-236 rec+0x1c=3 + FUN_005ede50).
            else if (job.Kind == JobKind.Haul)
            {
                if (_ctx.Entities.TryGetValue(job.TargetHandle, out var dest)) dest.CurrentWorkerHandle = g.Handle;
                if (_ctx.Entities.TryGetValue(job.ItemHandle, out var item)) item.WorkBusyCooldown = AssignCooldown;
                if (haulBatch != null)
                    foreach (var b in haulBatch)
                    {
                        if (_ctx.Entities.TryGetValue(b.TargetHandle, out var bd)) bd.CurrentWorkerHandle = g.Handle;
                        if (_ctx.Entities.TryGetValue(b.ItemHandle, out var bi)) bi.WorkBusyCooldown = AssignCooldown;
                    }
            }
            RaiseAutoprod(g, job, haulBatch);
            return true;
        }

        // The native FUN_0043a8a0 step→Tcl-command map, emitted as a real set_event (we already have the
        // raise bridge). Runs in the gnome's object scope so the handler's `this` resolves. The queued
        // event fires on the next Step's phase-2 dispatch (faithful one-tick latency).
        void RaiseAutoprod(DigglesTclEntity g, Job job, List<Job>? haulBatch = null)
        {
            var interp = _ctx.Interp;
            var prev = _ctx.This;
            _ctx.This = g;
            try
            {
                switch (job.Kind)
                {
                    case JobKind.Dig:
                        // 3-float world pos (0043a8a0:101) — evt_autoprod_dig_proc stores it as
                        // current_digpos and the dig chain (vector_fixdig etc.) expects a real
                        // position, not bare tile ints (dispatcher-reconcile.md gap 2).
                        var inv = System.Globalization.CultureInfo.InvariantCulture;
                        interp.Eval("set_event this evt_autoprod_dig -pos1 {"
                            + job.Pos.x.ToString("0.0##", inv) + " "
                            + job.Pos.y.ToString("0.0##", inv) + " "
                            + job.Pos.z.ToString("0.0##", inv) + "}");
                        break;
                    case JobKind.Harvest:
                        interp.Eval("set_event this evt_autoprod_harvest -subject1 " + job.TargetHandle + " -text1 " + job.WorkClass);
                        break;
                    case JobKind.Production:
                        interp.Eval("set_event this evt_autoprod_workat -subject1 " + job.TargetHandle + " -text1 " + job.WorkClass);
                        break;
                    case JobKind.Pack:
                        // The kind-0 plan (FUN_00439760): muetze step first (:58 — FUN_00431f20
                        // with the "pack" category string; evt_change_muetze_proc carries a
                        // literal "pack" case that no shipped Tcl ever sends — only this native
                        // emit — and get_nameofmuetze_proc maps pack → the service hat
                        // Dummy_Muetze_dienstleistung, z_work_common.tcl:822), then the walk
                        // (:66), then the pack step (code 6,:84 → FUN_0043a8a0:11
                        // 'evt_autoprod_pack') — evt_autoprod_pack_proc (z_events.tcl:1240)
                        // sets current_worktask "pack" → prod_autopack → pack {place} →
                        // prod_builddown(_rep) steps the bau anims down to packtobox
                        // (z_work_common.tcl:439-539, gated mid-chain on get_prod_pack:444).
                        // The port opens the step plan and emits the muetze leg; the pack event
                        // follows on the next idle report (AdvancePlan). The old direct
                        // evt_autoprod_pack emit lost the service hat and left the order with
                        // no plan guard.
                        _plans[g.Handle] = new StepPlan
                        {
                            Kind = JobKind.Pack, ItemHandle = job.TargetHandle,
                        };
                        interp.Eval("set_event this evt_change_muetze -text1 pack");
                        break;
                    case JobKind.Unpack:
                        // The kind-1 plan (FUN_00439990). Boxed target → the crate must be
                        // fetched first: the plan is muetze → walk → pickup(crate) → walk →
                        // unpack, so the port opens a step plan and emits the muetze step
                        // ('evt_change_muetze -text1 unpack', FUN_00439990:72-80 →
                        // FUN_0043a8a0:44-48 — the carrier hat goes on before the fetch); the
                        // pickup and unpack steps follow on the next idle reports (AdvancePlan).
                        // Without the pickup leg the Tcl chain walked to the spot empty-handed
                        // and prod_buildup_check cancelled the order ("dont have it",
                        // z_work_common.tcl:355-357).
                        // Not-boxed target (aufbau resume / repair) → straight to the unpack
                        // step at the object's position (FUN_00439990:81-84):
                        // evt_autoprod_unpack_proc (z_events.tcl:1291) redirects to "repair"
                        // when hitpoints < 0.99 (:1314-1321), else prod_autounpack's not-boxed
                        // branch resumes prod_buildup_rep (z_work_common.tcl:335); its muetze
                        // step is skipped — the proc re-hats immediately (prod_change_muetze
                        // service, z_events.tcl:1326).
                        var invu = System.Globalization.CultureInfo.InvariantCulture;
                        string cls = _ctx.Entities.TryGetValue(job.TargetHandle, out var crate0)
                            && crate0.Class != null ? crate0.Class.Name : "";
                        if (crate0 != null && IsBoxed(crate0))
                        {
                            _plans[g.Handle] = new StepPlan
                            {
                                Kind = JobKind.Unpack, ItemHandle = job.TargetHandle,
                                ItemClass = cls, OrderPos = job.Pos, AsBox = job.AsBox,
                            };
                            interp.Eval("set_event this evt_change_muetze -text1 unpack");
                        }
                        else
                        {
                            interp.Eval("set_event this evt_autoprod_unpack -subject1 " + job.TargetHandle
                                + " -text1 {" + cls + "}"
                                + " -pos1 {" + job.Pos.x.ToString("0.0##", invu) + " "
                                + job.Pos.y.ToString("0.0##", invu) + " "
                                + job.Pos.z.ToString("0.0##", invu) + "} -num1 " + (job.AsBox ? "1" : "0"));
                        }
                        break;
                    case JobKind.Haul:
                        // The kind-4 plan (FUN_0043c660): muetze("transport") → walk(item) →
                        // pickup(item) → walk(place) → transferprod(item, place) (:189-198 —
                        // FUN_00431f20("transport") / FUN_00431f90 walk / FUN_004321a0 pickup /
                        // FUN_00432220 transferprod). The muetze emit ('evt_change_muetze -text1
                        // transport' → evt_change_muetze_proc's literal "transport" case,
                        // z_events.tcl:2139 → prod_change_muetze transport — the carrier hat)
                        // opens the step plan; pickup and transferprod follow on the next idle
                        // reports (AdvancePlan). Not evt_autoprod_bringprod: its Tcl chain
                        // (prod_transporttoprodlist) is commented out in the shipped scripts
                        // (z_work_common.tcl:122-195) — the SP planner never uses it.
                        // Batch riders (FUN_0043c660:121-188) ride the same plan: their pickups
                        // interleave after the primary's, all transfers follow (:237-252/:282+).
                        var hp = new StepPlan
                        {
                            Kind = JobKind.Haul, ItemHandle = job.ItemHandle,
                            DestHandle = job.TargetHandle, ItemClass = job.WorkClass,
                            ForItem = job.ForItem,
                        };
                        hp.Items.Add(job.ItemHandle);
                        hp.Dests.Add(job.TargetHandle);
                        hp.Classes.Add(job.WorkClass);
                        hp.ForItems.Add(job.ForItem);
                        if (haulBatch != null)
                            foreach (var b in haulBatch)
                            {
                                hp.Items.Add(b.ItemHandle);
                                hp.Dests.Add(b.TargetHandle);
                                hp.Classes.Add(b.WorkClass);
                                hp.ForItems.Add(b.ForItem);
                            }
                        _plans[g.Handle] = hp;
                        interp.Eval("set_event this evt_change_muetze -text1 transport");
                        break;
                }
            }
            catch (System.Exception e) { Debug.LogWarning($"[ProdDispatcher] raise {job.Kind}: {e.Message}"); }
            finally { _ctx.This = prev; }
        }

        // The native lock flag, obj+0x3d0 bit 4 (set_lock). Port backing = the "_lock" entity var
        // the set_lock prop setter stores (same source obj_query's "locked" flag word reads).
        static bool IsLocked(DigglesTclEntity e)
            => e.VarsScope.Vars.TryGetValue("_lock", out var v) && (v == "1" || v == "true");

        // The native boxed flag, obj+0x3d0 bit 0x4000 (set_boxed) — the branch the kind-1 plan
        // builder takes (FUN_00439990:81). Port backing = the "_boxed" entity var.
        static bool IsBoxed(DigglesTclEntity e)
            => e.VarsScope.Vars.TryGetValue("_boxed", out var v) && (v == "1" || v == "true");

        // Mirror of get_remaining_sparetime: is the day phase inside the gnome's [WorkStart,+WorkLen)?
        bool IsWorkTime(DigglesTclEntity e)
        {
            double t = _ctx.Sim?.GameTime ?? 0.0;
            double phase = (t / HourSec) % DayHours;
            double rel = phase - e.WorkStart; if (rel < 0) rel += DayHours;
            return rel < e.WorkLen;
        }

        // The gnome-match cost (FUN_004317a0:75-78): a 3-D weighted squared distance in native
        // coords — the gameplay-vertical axis (climbing) ×5.0 (DAT_007374cc), the slab-depth axis
        // ×0.25 (DAT_00737058); both extracted from.rdata. The old 2-D Dist2 dropped
        // the depth term and the weights (dispatcher-reconcile.md gap 1). Note: the native also
        // computes a need-scaled radius (need²·500+20) but immediately clamps it to FLT_MAX
        // (DAT_00736f78=0 threshold always passes, 00436d40:141-143) — the radius gate is dead
        // code in the shipped binary, so no distance reject is ported (faithful).
        static float Dist2(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dy = (a.y - b.y) * 5f, dz = (a.z - b.z) * 0.25f;
            return dx * dx + dy * dy + dz * dz;
        }
    }
}
