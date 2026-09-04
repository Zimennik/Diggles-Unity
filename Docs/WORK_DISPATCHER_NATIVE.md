# WORK DISPATCHER — SYNTHESIS SPEC

> 
> **STATUS: the dispatcher is IMPLEMENTED and wired.** `Assets/Diggles/Runtime/Engine/Sim/ProdDispatcher.cs`
> (~1790 lines) ships the SP planner + PlaceLockStore; it is ticked from `DigglesGameSim.Step`
> (`Ctx.ProdDispatcher?.Tick` / `DecayCooldowns`). All the foundational primitives the "NATIVE
> PRIMITIVES" / "IMPLEMENTATION PLAN" / "RISKS" sections below describe as *stubbed / absent* are
> now REAL commands in `DigglesTclEngine.cs`: `get_walkresult` (:1219, reads
> `DigglesTclEntity.WalkResult`, set by the move executor in `EntityActions.cs`), `gnome_idle`
> (:3776), `placelock_set/check/rem` (:3838…), `get_remaining_sparetime` (:865),
> `auto_choose_workingtime` (:937). The **correction and implementation sections below are
> the authoritative, current description**; the OVERVIEW→RISKS material after them is retained as
> the original M8 planning spec + native RE ground-truth — treat every "[stub]/absent/CURRENT PORT
> STATE/IMPLEMENTATION PLAN" port-status note there as HISTORICAL (pre-implementation). The
> RE facts (native addresses/laws) in those sections remain valid.

> ## Correction (supersedes "RESOLVING THE FINDINGS' CONTRADICTION" below)
> The two dispatcher entries are NOT "two layers of one subsystem" — they are TWO SEPARATE
> allocators, MUTUALLY EXCLUSIVE on the multiplayer flag `DAT_0076837a`:
> * **single-player**: `CProdPlannerMgr::Tick` FUN_00432720:30 runs `CProdPlanner_MaintenanceTick`
> (FUN_00433a70) only while `DAT_0076837a == 0`. Jobs become per-gnome **STEP PLANS**
> (FUN_00439da0 → kind builders: 0 pack FUN_00439760, 1 unpack FUN_00439990, 2/3 workat,
> 4 haul FUN_0043c660, 5 dig inline, 6 harvest FUN_004394e0), advanced ONE STEP PER
> GNOME-IDLE round-trip (FUN_0043a460 → emit FUN_0043a8a0). A boxed-crate unpack therefore
> dispatches as `evt_change_muetze "unpack"` → `evt_autoprod_walk` → **`evt_autoprod_pickup`**
> → `evt_autoprod_walk` → `evt_autoprod_unpack` — the gnome FETCHES the crate first.
> The request collector is FUN_00436440: +0x29 pack → kind-0 @ obj pos; **+0x2a buildup AND
> +0x28 unpack BOTH → kind-1** (need 7.5 = 0x40f00000; there is NO SP buildup job kind and
> `evt_autoprod_buildup` is NEVER emitted in SP — the aufbau resume rides the unpack event's
> not-boxed branch, FUN_00439990:81-84 → prod_autounpack → prod_buildup_rep).
> * **multiplayer only**: FUN_005eed70:39 runs the 005f ProdAllocator (collect FUN_005f0850,
> greedy assign FUN_005f3600, DIRECT one-event emit FUN_005f3a50) only while
> `DAT_0076837a != 0`. The port's former direct-emit unpack/pack/buildup model was copied
> from THIS dead-in-SP path — which is why a deferred crate order walked the gnome to the
> build spot EMPTY-HANDED and died at prod_buildup_check ("dont have it",
> z_work_common.tcl:355-357) — the Play bug.
> The C# ProdDispatcher now models the SP planner (step plan for boxed-crate unpack; kind-1 for
> the buildup byte; request need 7.5). See ProdDispatcher.cs headers for the per-line citations.

> ## Material gate + kind-4 haul (implemented; full RE in ProdDispatcher.cs +
> ProdMaterialHaulTests.cs headers)
> MaintenanceTick collector order (FUN_00433a70:169-179): FUN_00434e60 (demand scan, kind-2) →
> FUN_00436440 (request bytes, kinds 0/1) → FUN_00435f50 (dig, kind-5) → **FUN_00436750
> (production, kind-3)** → FUN_00436d40 (material-demand RESOLVER — not the dig allocator as
> earlier RE claimed) → **FUN_004375c0 (haul, kind-4)**. Key laws now ported:
> * **The workat gate** (FUN_00436750:164 → FUN_00434c20): kind-3 dispatches only when every
> recipe material (global tech-tree registry ← techtreetunes; Tcl mirror = the class method
> `prod_item_materials`, MULTISET) is already in the **place's own inventory** — no world
> stock, no reachability. Missing → the workat is suppressed and a material demand is
> recorded (planner+0x44 per-pass → +0x4c persistent, source items resolved+reserved by
> FUN_00436d40/FUN_004319b0/FUN_004317a0) → **one kind-4 job per missing material**
> {pos=place, +4=place, +8=concrete source item; need = prio × 1000 = DAT_00737698 — same
> scale as kind-3}. No eligible source ⇒ no job (slot prio zeroed = backoff; escalations
> evt_task_putdown / wanted-marker list are NOT ported).
> * **Kind-4 plan** (FUN_0043c660): muetze("transport") → walk(item) → pickup(item) →
> walk(place) → transferprod(item, place); the DEST place is claimed for the whole trip
> (rec+0x1c=3 + rec+0x24,:97-102); batches nearby jobs into one trip up to capacity−2
> within weighted dist² < 40 (port: one item/trip, documented).
> * **Absorb** (FUN_00437370): a resolved source already within |Δx|<4, |Δy|<2, |Δz|<4
> (DAT_007374a0/DAT_00737330) of the place ⇒ `set_event <place> evt_prodplace_transferprod
> -subject1 <item>` — the building takes it itself, no gnome.
> * **Record-busy dedup** (FUN_00436440:31 rec+0x1c==0; FUN_00438b80 = dedupe of new jobs
> against every in-flight PLAN's place refs — NOT worker assignment): a place with an order
> in service contributes NOTHING, even with a request byte re-armed → the re-clicked build
> order can never dispatch a second builder.
> * **set_boxed side effects** (FUN_0067dc80:139-163): box ⇒ ~0x400000 (built bit) | 0x4000 +
> clears +0x3d/+0x2c/+0x29 (pack); unbox ⇒ ~0x4000 + clears +0x3d/+0x2b/+0x28 (unpack) —
> the pending order byte dies at placement (`set_boxed 0`, prod_buildup_new).
> * **Progress**: info_start_prod (FUN_0069dcf0, itemtype arg ignored natively — CProd+0x48 is
> stamped by the dispatcher emit) / info_progress_prod (FUN_0069def0: cur→CProd+0x4c,
> max→+0x50, RAW steps) / info_end_prod (FUN_0069e210: slot decrement + reset +0x48=-1,
> +0x4c/+0x50=0). The window derives pct = clamp(cur×100/max, 0, 100) (FUN_00520620:548-562)
> — the port surfaces it as BottomMenuEntry.Progress + ProgressKind Producing/Delivering.
> * **get_class_type** (FUN_0063fb30) takes a CLASS NAME — the port's handle-only resolve broke
> the BOXED_CLASSES gate: an ordered Zelt spawned BUILT instead of as a packed crate
> (feuerstelle.tcl:44-54 boxed path → prod_createproduct_inv_boxed → packtobox → set_boxed 1).
> * **Delivered materials lie VISIBLE in front, LOCKED**: the law is the
> unchanged Tcl's — prod_transfertoprod (z_work_common.tcl:51-56) inv_adds into the place
> BUT set_pos's the item to the get_place delivery spot (the front depth band z+1..z+3),
> set_visibility 1, hoverable/selectable 0; the pickup's set_lock 1 STAYS (lock_item
> z_procs.tcl:486; only the tracking var clears,:57-59). Contained+locked ⇒ inert to
> obj_query/clicks/the kind-4 source scan; the gate counts it as stock; the cook's
> prod_walk_and_consume_itemtype walks to it. RELEASE on order cancel: the native window
> follows EVERY production button with `set_event %i evt_btn_on -target %i -num1 0 -text1
> <verb> -text2 <item>` (FUN_005235a0:44 ← FUN_005242b0:351/:472) → genericprod.tcl:33-52 →
> free_unneeded_items → release_item (:566-613/:520-537 — uncontain, unlock, hoverable,
> physic, position kept). The port panel click now raises evt_btn_on after set_prod_slot_cnt.

> ## Inventions + kind-6 harvest escalation (implemented; per-line RE in
> ProdDispatcher.cs / BottomMenuController.cs / ProdMaterialHaulTests.cs headers)
> * **Invention orders**: the slot store is keyed by the PLAIN class — never "Bp<item>"
> (prod_getitemtask re-checks the STRIPPED key, genericprod.tcl:708; std_ai.tcl:1976-1981
> orders plain). The Bp prefix lives ONLY on (a) the owner attrib = the invented flag and
> (b) the workat event's -text1 marker: an ordered-but-uninvented slot dispatches as task
> code 8 → `evt_autoprod_workat … -text1 Bp%s` (FUN_0043a8a0:96; produce = code 9, plain
> class,:84) with NO material gate (the Bp branch of prod_getitemtask never calls
> prod_item_actions, genericprod.tcl:705-721). prod_invent (z_work_common.tcl:217-260)
> walks the invention dummy, plays invent_* scaled by skill (tttinvent only shortens the
> anim — there is NO exp gate anywhere), and ends in `add_owner_attrib <owner> Bp<item> 1`
> (prod_inventattribute,:8-32); the ORDER SURVIVES (slot zeroed only in switchmode,
>:28-30) and rolls into a real production on the next sweep. Port: uninvented cells click
> normally (plain-class set_prod_slot_cnt + evt_btn_on); CollectProductionJobs branches on
> the Bp attrib and emits `-text1 Bp<item>`.
> * **tttinvent shared-global clobber (the "all inventions white" bug)**: a production-site
> ITEM (Hauklotz/Steinmetz/…) is itself a CLASS whose load ends in genericprod.tcl:140
> `catch {unset … tttinvent_$classname …}` — per-interp in the original, but the port's
> shared global let the later class load DESTROY the var Feuerstelle's prod_item_attribs
> reads at call time → empty requirements → White. Non-class items (Grillhamster) kept
> theirs → Red. BottomMenuController now re-seeds the item's techtreetunes section once on
> an empty parse. Headless repro/verify: InventionDispatchTests (fresh exp = 0 ⇒
> Grillhamster/Hauklotz/Steinmetz all RED; +0.01 exp_Holz flips Hauklotz WHITE — the player's
> Play whites meant the camp had already chopped/dug, ON TOP of the clobber bug).
> * **Missing material → CHOP the resource (kind-6)**: when the resolver finds NO source ITEM
> of a missing class (FUN_004317a0:71-73, exact-class match → -1), the class maps through
> the produce registry FUN_00432690 to the RESOURCE that yields it and a harvest request is
> appended (FUN_00434e60:351-367 / FUN_00436d40:225-239 → FUN_0043ad40); FUN_0043b620
> promotes ONE request per pass to a kind-6 job (plan FUN_004394e0: muetze(harvest) → walk
> → `evt_autoprod_harvest -subject1 <res> -text1 <resClass>`, FUN_0043a8a0:70-74). Deficits
> are PER CLASS (FUN_00434e60:447-500; even the MP twin keeps Pilzhut/Pilzstamm separate,
> FUN_005f4220:247-253) — the port's old lumped produce count let a stray item of the WRONG
> class kill the chop. Port: CollectHarvestJobs = per-missing-class
> deficit over MissingMaterials + ProduceMap {Pilzstamm,Pilzhut → Pilz}.
> * **Delivering bar off-by-one**: the in-flight hauled item now credits DeliveryPct (the
> dest is claimed for the whole trip, FUN_0043c660:97-102) — the fill lagged one item and
> died at (n−1)/n when the last delivery landed.
> * **Product drop spot**: `get_place -rect … -mindist 1.5` (prod_createproduct_box*,
> z_work_prod.tcl:842/:855) — the scorer's MINDIST floor is UNCONDITIONAL
> (FUN_005a9270:29-30); only the circle cap is rect-bypassed (param_1[5]). The port skipped
> both under -rect → the finished Zelt crate landed ON the campfire.
> * **Stuck box-carry pose**: a plain `action rotate` (prod_turnfront after the drop,
> feuerstelle.tcl:62 → rotate_tofront, z_dignwalk.tcl:954) defaults its animset to 0 =
> STANDARDWALK natively (FUN_00416da0:66 → FUN_00417280:36-39 resolves the standstill pose
> THROUGH it) — the port's old -1 "keep current" left ANIMSET_WALKWITHBOX 7 (kiste_stand)
> latched forever.
> * **Buildup pacing 2×**: CAct_Anim::Init stamps the deadline synchronously (FUN_004085d0 /
> FUN_004082a0:147); the port's lazy first-Tick start cost one dead logic tick per
> prod_buildup_rep anim rep (z_work_common.tcl:473-508 — 1-frame work taps → 0.2 s instead
> of 0.1 s = the observed 2×). CmdAction now Tick(0)s a fresh anim action. Step order
> verified faithful (hat once → walk → check → hammer reps → bau stage stills).
> * **Blow particles skew**: blow_particlesource only ACCUMULATES a wind impulse
> (FUN_00675d10:147-149, emitter+0x30..0x38); the next emitter tick folds it into the
> ×0.9/frame-decaying drift and zeroes it (FUN_00498590:303-308). The port overwrote the
> source Direction — the fire stayed skewed forever after prod_blowright.

> ## Fog gates + release-path byte kill + invention bind (implemented;
> per-line RE in ProdDispatcher.cs; tests = ProdFowPackInventTests.cs)
> * **Haul sources are FOG-GATED**: the SP source-item resolver (FUN_00436d40 →
> FUN_004319b0:63 → FUN_004317a0:73) accepts an item only while `GetMaxFow(item, owner)
> > 0x8c` — (near-)currently seen; the demand scan's availability REGISTRATION
> (FUN_00434e60:114) needs only `> 0` (explored gray; same law as the Tcl obj_find_next
> family, FUN_006a2000:212-213). Port: FindHaulSource + the harvest covered-check gate at
> 0x8c; SlotBuildable (the red-icon availability surface) + the kind-6 resource pick gate
> at 0. FogOfWar == null (headless/menu) ⇒ ungated (-nofow). Fixes "gnomes fetch
> materials out of black fog".
> * **The release path KILLS the request bytes**: FUN_0043a0c0 (every plan teardown — the
> worker's next TRUE idle report via CProdPlanner::GnomeIdle FUN_00434a80/MaintenanceTick,
> and gnome_failed_work/gnome_stopped_work FUN_00676500) clears +0x28 unpack / **+0x29
> pack** / +0x2a buildup with the claim +0x24, the progress pair, and stamps the target's
> reopen +0x3e8 = 0x14. The PACK byte deliberately survives DISPATCH (prod_builddown
> re-reads it, z_work_common.tcl:443-446) — the port's missing release-clear left a
> failed/interrupted pack ARMED, and the collector marched the next gnome onto the crate
> every sweep ("pack order sends everyone"). Port: ReleaseWorkerClaim
> clears the three bytes; ReleaseGnome additionally stamps the 20-tick reopen at each
> released place (the old placelock-keyed reopen never armed for started chains —
> stop_prod's `placelock_rem $myref` had already dropped the lock). The unpack-plan
> pre-completion REARM (DropPlan) stays — documented divergence.
> * **Inventions bind rec+0x48 at the EMIT, not at collection**: the production section's
> slot pick requires FUN_005ed620 (slot item invented via the tech registry,
> FUN_00436750:100-101), so the:146-158 collection bind is unreachable for an invention;
> the kind-2 job stamps +0x48 only at the workat emit (FUN_0043a8a0:89, PLAIN class — Bp
> rides -text1 only). Port: the invention branch no longer BindCurrentItem's; Assign's
> Production branch stamps CurrentProdItem (Bp-stripped). Fixes "invention shows the blue
> in-progress bar with nobody in a work window". Invented slots keep the collection bind
> (FUN_00436750:146-158 — the regular blue "order pending" law).
> * (Same wave, other agent: the sparetime queryrect/queryrange owner+flag gate
> FUN_005f4d00:75-76 — `(flags & 0x1404002) == 0x1400000`, boxed/contained excluded —
> fixes "gnomes sleep on a boxed tent"; see CampOwnershipAndTrapTests.)

## OVERVIEW
> 📜 The remainder of this file (OVERVIEW → RISKS) is the ORIGINAL M8 planning spec. It has been
> IMPLEMENTED (see the sections above). Port-status lines below that say a
> primitive is "stubbed"/"absent" or list an "IMPLEMENTATION PLAN" reflect the pre-build state and
> are kept only for their native-RE content, which is still accurate.

The native autonomous work dispatcher is the CProdPlanner / ProdAllocator subsystem. It is driven once per server tick from CGameThread::SrvDoThink (FUN_006b4a30) and assigns open jobs (dig / harvest / production / haul) to idle gnomes of EVERY player INCLUDING the human player 0 — it is NOT gated to AI players 1-3. It does NOT raise the player-click events evt_task_dig/harvest/workprod; it raises the SYSTEM twins evt_autoprod_dig / evt_autoprod_harvest / evt_autoprod_workat / evt_autoprod_walk / evt_autoprod_pickup / evt_autoprod_pack / evt_autoprod_buildup / evt_autoprod_unpack / evt_autoprod_bringprod / evt_autoprod_transferprod / evt_autoprod_mine_new, each via `set_event <gnome> evt_autoprod_* -flags…` formatted by FUN_0043a8a0 (rich path) or FUN_005f3a50 (autoprod path) and queued into the Tcl interp by FUN_0054ea20. The unchanged Tcl handlers (z_events.tcl evt_autoprod_*_proc) set current_plan/current_worktask/current_workplace and do `state_triggerfresh this work_auto`; state_work_auto_proc (z_work_states.tcl) then translates to a tasklist (dig→dig_starttask, harvest→harvest+work_dispatch, work→prod_autobuild, bringprod→prod_transporttoprodlist, …) and work_dispatch evals each step.

RESOLVING THE FINDINGS' CONTRADICTION: the six findings describe the dispatcher at TWO distinct native addresses — (A) CProdPlanner::MaintenanceTick (FUN_00433a70), reached via CAiCentral tick (FUN_00424680) → CProdPlannerMgr::Tick (FUN_00432720); and (B) a WorkScheduler tick FUN_005eed70 → greedy assign FUN_005f3600 → emit FUN_005f3a50. These are NOT two competing dispatchers; they are two layers/passes of ONE subsystem that share the SAME event-emit bridge (FUN_0043a8a0 ↔ FUN_005f3a50 emit the identical evt_autoprod_* command strings; both call FUN_0054ea20 to queue; both use the same per-(gnome,job) path-cost scorer family FUN_004317a0/FUN_005f2380 and the same FUN_004319b0 reserve-worker). The functional invariant that matters for our faithful port is identical regardless of which layer Ghidra labeled as the entry: per amortized tick, per player, (1) rebuild open jobs from terrain dig-marks + production needs + haul/transport, (2) collect idle/ready gnomes, (3) for each idle gnome pick the best open job (highest need first, then nearest reachable gnome within a need-scaled radius), reserve the worker + the cell, and (4) raise the matching evt_autoprod_* on the gnome. The step/job TYPE→event map differs only cosmetically between the two finding sets (one numbers dig=10/harvest=12, the other dig=3/harvest=2); both map to the SAME evt_autoprod_* names, so the C# port keys off a single C# enum and emits the canonical names. I therefore specify ONE C# per-tick ProdDispatcher service.

For the human player (player 0) there is NO Tcl that raises evt_task_* / evt_autoprod_* (grep-confirmed: std_ai.tcl raises evt_task_walk/attack only for players 1-3 and PAINTS dig-marks via `dig_mark $owner …` for AI players, but never raises dig itself — the planner dispatches dig for all owners). So the native ProdDispatcher is the ONLY source of autonomous work for the human, and porting it is exactly task M8.

CURRENT PORT STATE: DigglesGameSim.Step (Assets/Diggles/Runtime/Engine/Sim/DigglesGameSim.cs) faithfully ports the SrvDoThink event-maturation + event-dispatch + active-action tick + state-body tick, but has NO dispatch phase. The event bridge (set_event→PendingEvents, handle_event→DispatchEvent, event_get), timer scheduler, and needs-decay all WORK and are faithful. The Tcl evt_autoprod_*_proc handlers and the work_auto/work_dispatch states are LOADED and correct. The dispatcher itself, the worktime/sparetime clock, and several foundational primitives (get_walkresult, placelock_*, get_place, gnome_idle, gnome_announce_dig) are absent/stubbed. The single most important consequence: get_walkresult is stubbed to "0" while the Tcl dig/work loop gates on `4 != [get_walkresult this]` (arrived) — so even the EXISTING manual dig (ClickToMoveService → evt_task_dig) never advances past the walk phase headlessly; that primitive must be wired to the MetaWalk arrival state FIRST. Human dispatch today is faked one-gnome-at-a-time by ClickToMoveService raising evt_task_dig/harvest directly on the single selected dwarf.

THE FAITHFUL C# PLAN: add a per-player ProdDispatcher service ticked once per sim Step (amortized one player per qualifying tick, like CProdPlanner). Wire gnome_idle→a per-player idle-gnome pool, gnome_announce_dig→nothing-new (the dig-mark grid already exists in DiggableTerrain — gnome_announce_dig just re-stamps the mark; we keep it as a stamp). Build open jobs: dig jobs from DiggableTerrain dig-marks, harvest jobs from obj_query Pilz/ore, production jobs from buildings with ProdSlots>0 and unmet inputs, haul jobs from production input needs vs. world stock. For each idle gnome pick the best open job and `set_event <gnome> evt_autoprod_dig -pos1 {x y}` / `evt_autoprod_harvest -subject1 <obj> -text1 <class>` / `evt_autoprod_workat -subject1 <building> -text1 <class>` / `evt_autoprod_bringprod -subject1 <building> -text1 {<class>}` — the UNCHANGED Tcl drives the rest. Also replace the get_remaining_sparetime constant with a real worktime/sparetime clock (12 hour-units/day, two 6h shifts) and raise evt_time_startwork/evt_time_startsparetime at the boundaries, so the idle state correctly chooses work_dispatch (worktime, remaining==0) vs sparetime. Favor "implement the missing primitive + let unchanged Tcl run" over re-coding behavior.

## DISPATCH TICK
ProdDispatcher.Tick(dt) — call ONCE near the end of DigglesGameSim.Step, AFTER state-body ticks (mirrors SrvDoThink: dispatch decisions are made, evt_autoprod_* queued onto gnomes, then HandleEvents delivers them — in our sim the queued events fire on the NEXT Step's phase-2 dispatch, which is the faithful one-tick-latency). Pseudocode:

```
// Amortization gate — faithful to CProdPlanner (FUN_00565a10 gate + DAT_00c3c968 &7 player counter).
_tickAccum += dt;
if (_tickAccum < DispatchInterval) return; // DispatchInterval ~0.5s (assumption; native interval DAT_007372a8 unknown)
_tickAccum = 0;
_playerCursor = (_playerCursor + 1) & 7; // one player per qualifying tick
int player = _playerCursor; // includes player 0 (human)

// ---- PHASE A: refresh the IDLE-GNOME POOL for this player ----
// Populated by the gnome_idle hook (see below), NOT scanned here — faithful: a gnome enters the
// pool when its Tcl runs `gnome_idle this 1` (state_enter_work_idle / state_enter idle). We also
// prune dead/destroyed/busy handles.
var pool = _idlePool[player];
pool.RemoveWhere(h => !Ctx.Entities.TryGetValue(h, out var e)
                      || e.Owner != player
                      || e.WorkBusyCooldown > 0      // gnome+0x3e8 cooldown (set on assign, decays each tick)
                      || !IsWorkTimeFor(e));          // worktime clock gate (see jobSources)

// ---- PHASE B: BUILD the open-job list for this player (rebuild every tick; cheap) ----
var jobs = new List<Job>;
CollectDigJobs(player, jobs); // from DiggableTerrain dig-marks (need = mark strength)
CollectHarvestJobs(player, jobs); // from obj_query Pilz / ore in the world
CollectProductionJobs(player, jobs); // buildings with ProdSlots>0 & buildup complete & invented (Bp>0)
CollectHaulJobs(player, jobs); // production input needs not yet in the building inventory

// ---- PHASE C: PRIORITY ORDER (faithful FUN_00436d40 bubble-sort by need DESC) ----
jobs.Sort((a,b) => b.Need.CompareTo(a.Need)); // highest-need job first

// ---- PHASE D: DRAIN the idle pool → ASSIGN (faithful MaintenanceTick PHASE D) ----
foreach (var job in jobs) {
    if (pool.Count == 0) break;
    // find nearest reachable free gnome within a need-scaled radius (faithful FUN_004319b0)
    float radius = job.Need*job.Need*K_score2 + K_base;     // K constants are tunable (native DATs unknown)
    DigglesTclEntity best = null; float bestCost = float.MaxValue;
    foreach (var h in pool) {
        var g = Ctx.Entities[h];
        float cost = PathCost(g.Position, job.Pos);          // NavService path-cost; ∞ if unreachable
        if (cost < 0 || cost > radius) continue;
        // carried-cargo discount for haul jobs (faithful FUN_005f2380): a gnome already holding an
        // acceptable item is strongly preferred.
        if (job.Kind == Haul && GnomeCarriesClass(g, job.WorkClass)) cost *= 0.25f;
        if (cost < bestCost) { bestCost = cost; best = g; }
    }
    if (best == null) continue;                              // no reachable gnome → leave job open
    // RESERVE the target so two gnomes don't converge (faithful placelock_set + dig-cell claim bit).
    if (!Reserve(job, best)) continue;
    best.WorkBusyCooldown = AssignCooldown;                  // gnome+0x3e8 = 2 (re-task latency)
    pool.Remove(best.Handle);
    RaiseAutoprod(best, job);                                // the evt_autoprod_* raise (below)
}
```

RaiseAutoprod(gnome, job) — faithful FUN_0043a8a0/FUN_005f3a50 step→Tcl-command map, but emitted as a real C# set_event so we don't round-trip through a string queue (we already HAVE set_event/CmdSetEvent). Run it in the gnome's object scope so the handler's `this` resolves:
```
var prev = Ctx.This; Ctx.This = gnome;
try {
  switch (job.Kind) {
    case Dig:        Interp.Eval($"set_event this evt_autoprod_dig -target {gnome.Handle} -pos1 {{{job.Tx} {job.Ty}}}"); break;
    case Harvest:    Interp.Eval($"set_event this evt_autoprod_harvest -target {gnome.Handle} -subject1 {job.TargetHandle} -text1 {job.WorkClass}"); break;
    case Production:  // produce at a building (type 9/5 → workat)
                     Interp.Eval($"set_event this evt_autoprod_workat -target {gnome.Handle} -subject1 {job.TargetHandle} -text1 {job.WorkClass}"); break;
    case Haul:       Interp.Eval($"set_event this evt_autoprod_bringprod -target {gnome.Handle} -subject1 {job.TargetHandle} -text1 {{{job.WorkClass}}}"); break;
    case Walk:       Interp.Eval($"set_event this evt_autoprod_walk -target {gnome.Handle} -pos1 {{{job.Pos.x} {job.Pos.y} {job.Pos.z}}}"); break;
  }
} finally { Ctx.This = prev; }
```
CmdSetEvent already enqueues onto gnome.PendingEvents; DigglesGameSim phase-2 next Step calls DispatchEvent → FindEventHandler(evt_autoprod_*) → RunInEntityContext → the unchanged Tcl evt_autoprod_*_proc → state_triggerfresh work_auto. No new event plumbing is needed.

gnome_idle hook (the pool entry point, faithful CProdPlanner::GnomeIdle FUN_00434a80): replace the no-op stub. `gnome_idle this 1` → if gnome.WorkBusyCooldown<=0 add gnome.Handle to _idlePool[gnome.Owner] AND immediately try a one-shot assign (faithful FUN_00434bc0 immediate re-task: build jobs for that player, pick best, RaiseAutoprod) so a freshly-idle gnome can be re-tasked the same tick, not only on the periodic sweep. `gnome_idle this 0` → remove from the pool.

WorkBusyCooldown decay: decrement each gnome's WorkBusyCooldown by 1 each Step (units = ticks; faithful gnome+0x3e8). Assumption: AssignCooldown=2.

## JOB SOURCES
- **dig**: Source = the per-player dig-mark grid, which ALREADY exists in DiggableTerrain (_digMark byte[] per tile). Native: marks are inline voxel nibbles (voxel+8, one per player) written by dig_mark FUN_006586c0 / gnome_announce_dig FUN_0067a450, consumed by the dig allocator FUN_00436d40→FUN_004319b0 which path-validates and sets a reserve bit. Port: CollectDigJobs(player) iterates DiggableTerrain marked+solid tiles (reuse FindNearestDiggableEdge / a full scan of _digMark), making one Dig job per marked tile-cluster with Pos = the dig edge (use the EXISTING get_digedge logic), Need = mark count (1..3). Assign → `set_event <g> evt_autoprod_dig -pos1 {tx ty}` → evt_autoprod_dig_proc (z_events.tcl:1514) → work_auto → dig_starttask. NOTE the dig grid is currently SINGLE-player (DiggableTerrain._digMark ignores the player arg); fine for player 0, a documented simplification for AI. AI players already PAINT marks via std_ai.tcl `dig_mark $owner …` so the dispatcher consuming them is the only missing half.
- **harvest**: Source = harvestable world objects (Pilz mushrooms, ore brocken). Native: the global raw-stock balancer FUN_005f1500 emits type-2 harvest jobs when wood/Pilzstamm stock runs low, keeping idlers gathering; per-resource jobs also exist. Port: CollectHarvestJobs(player) = obj_query for -class Pilz (and ore classes) within a radius, owner-agnostic (resources are neutral). Gate the COUNT by owner stock (OwnerAttribs[player]['Holz'] etc.) so we don't over-assign. Assign → `set_event <g> evt_autoprod_harvest -subject1 <pilz> -text1 Holz` → evt_autoprod_harvest_proc (z_events.tcl:1405) → harvest $objref + work_dispatch. obj_query is already implemented (CmdObjQuery) but must be made distance-sorted before -limit (currently dict-order) so 'nearest mushroom' is correct.
- **production**: Source = owned production buildings (Feuerstelle etc.) with a scheduled slot (ProdSlots[item]>0), BuildupComplete, and the blueprint invented (get_owner_attrib owner Bp<class> > 0 — the tech-tree gate genericprod.tcl reads). Native: ProdAllocator pass-1 FUN_00434e60 scores each building 'place' by need = Σ(recipe-demand − owner-stock), writes the top-2 wanted slots, and the assignment pass binds the nearest reachable gnome. Port: CollectProductionJobs(player) iterates Ctx.Entities where Class.Kind=='production' && Owner==player && BuildupComplete && ProdSlots has any count>0 && OwnerAttribs[player]['Bp'+item]>0.5; Need = scheduled count. Assign → `set_event <g> evt_autoprod_workat -subject1 <building> -text1 <itemClass>` → evt_autoprod_workat_proc (z_events.tcl:1127) sets current_worktask=work + work_auto → prod_autobuild. (Our M7 prod_make/prod_cook hack can stay as the actual cook payload behind start_prod until the generic prod_item_actions chain is verified.) The announce/confirm handshake (evt_zwerg_workannounce → prod_assignworker) is an AI-fairness round-trip; ASSUMPTION (flagged): skip it for player 0 and raise evt_autoprod_workat directly — prod_assignworker is already stubbed to '1' and current_workplace gating still holds, so this is the simplest faithful interpretation; add the announce later if production starves.
- **transport/haul**: Source = production buildings whose recipe-input slot is unmet AND the input item exists loose/in-store in the world. Native: FUN_005f0850→FUN_005f0e40 emits type-6 bringprod jobs, FUN_005f1cc0 computes the acceptable-input-class list (building required-input table minus building+nearby inventory) and an urgency divisor; FUN_005f2380 gives a strong distance discount to a gnome already carrying an acceptable item. Port: CollectHaulJobs(player) iterates production buildings, for each unmet recipe input emits a Haul job{TargetHandle=building, WorkClass=inputClass, Need=urgency}. Assign → `set_event <g> evt_autoprod_bringprod -subject1 <building> -text1 {<inputClass>}` → evt_autoprod_bringprod_proc (z_events.tcl) → current_worktask=bringprod → work_auto → prod_transporttoprodlist. PORT GAP to fix: there is no recipe-input model yet (set_prod_slot_cnt only stores name→count); a minimal per-class required-input table is needed (M7 had Grillpilz<-Pilzhut hardcoded). Also obj_query -flagneg {boxed contained} currently OVER-excludes (FlagMatches default-true) so the Lager store query returns nothing — fix FlagMatches to return false for unknown flags and model boxed/instore.
- **other**: WALK (evt_autoprod_walk) and PACK/UNPACK/BUILDUP (evt_autoprod_pack/unpack/buildup) and TRANSFERPROD/PICKUP/MINE are lower-priority job kinds the native planner also emits. For M8 first-light, implement dig+harvest+production+haul; stub the rest by leaving them un-emitted (the Tcl handlers exist and will work once we emit them, but they are not needed to demonstrate autonomy). Mine (evt_autoprod_mine_new) reuses the harvest path on non-Pilz targets (evt_autoprod_harvest_proc auto-redirects to evt_autoprod_mine_new_proc when the subject isn't a Pilz), so harvest covers ore for free.

## NATIVE PRIMITIVES
- **get_walkresult (FUN_0068fb80)** [stub — DigglesTclEngine.cs:1740 (bulk '0' stub). HIGHEST-PRIORITY FIX: wire to the per-entity MetaWalk arrival state (add e.WalkResult on DigglesTclEntity, set by the move executor: Walking=1, Arrived=4, Blocked=2; register a real get_walkresult reading it).]: get_walkresult this → reads cached pathfind result enum at obj+0x248. 4 = arrived/walk-succeeded; other values = still walking / failed / blocked. The Tcl dig/work loop gates on `if {4 != [get_walkresult this]} {return}` (z_dignwalk.tcl) and `get_walkresult this != 4` — a constant 0 makes the gate ALWAYS return, so the gnome never proceeds past the walk phase. MUST return 4 once the gnome's MetaWalkAction has arrived, and a non-4 (e.g. 1=walking, 2=blocked/failed) otherwise.
- **gnome_idle (FUN_00434a80 CProdPlanner::GnomeIdle)** [stub — DigglesTclEngine.cs:1756 ('0'). Implement: 1→add gnome.Handle to ProdDispatcher._idlePool[Owner] + one-shot immediate assign; 0→remove. Foundational — without it the dispatcher has no idle gnomes.]: gnome_idle this 1 → register the gnome as idle/available; if no busy cooldown, immediately try to re-task it (FUN_00434bc0). gnome_idle this 0 → unregister. This is THE availability registry the dispatcher reads; called from state_enter_work_idle_proc (z_work_states.tcl:226) and state_enter idle (zwerg.tcl:275).
- **get_remaining_sparetime / set_remaining_sparetime** [FAKE CONSTANT '300' (DigglesTclEngine.cs:528). Replace with a real worktime clock: each gnome has a worktime window [start,start+len) in hour-units (set_worktime), the day is 12 hour-units (values seen: 0.0/6.0 starts, 6.0/9.0 lengths). Compute dayPhase = (GameTime/1800) mod 12; if dayPhase in window → return 0.0 (worktime); else → return seconds to next window start. ASSUMPTION (flagged, openQuestion): hour-unit = 1800s so a worktime day = 12*1800s; our gethours already = GameTime/1800. Confirm vs Sparetime.cpp FUN_005f4a70.]: get_remaining_sparetime this → seconds of sparetime left in the current period. ==0.0 ⇒ it is WORKTIME ⇒ idle states go to work_dispatch; >0.0 ⇒ sparetime ⇒ idle anims / sparetime_dispatch. Drives whether a gnome is a dispatch candidate.
- **set_worktime / get_worktime / auto_choose_workingtime** [stub ('0', bulk list lines 1652,1741). Implement as per-entity fields WorkStart/WorkLen on DigglesTclEntity; auto_choose_workingtime alternates 0.0/6.0 by population parity. Needed for get_remaining_sparetime to be meaningful.]: set_worktime <g> START LEN sets the gnome's work window (hours). get_worktime <g> start → the start hour. auto_choose_workingtime this assigns a staggered default so the population's shifts overlap (half work while half rest). Used at zwerg.tcl:147, baby.tcl, z_methods.tcl.
- **evt_time_startwork / evt_time_startsparetime (engine-raised)** [absent (nothing raises them). Add to the worktime clock service: when a gnome crosses into/out of its window, set_event <g> evt_time_startwork / evt_time_startsparetime. Handlers already exist.]: The clock raises these on a gnome at its window boundaries; handlers (zwerg.tcl:344) set current_plan = work/sparetime. Drives the work-vs-rest plan.
- **placelock_set / placelock_check / placelock_rem (FUN_006578c0/00657e40/00657c20)** [ABSENT (not registered, not even stubbed → hits unknown-command). Add a PlaceLockStore (List<{Vector3 pos,double expiry,int id}>) on the context + the 3 commands. now = Sim.GameTime. CRITICAL for multi-gnome anti-collision; also called inside a faithful get_place.]: A flat list of {pos[3], expiry, id, tag} reservations. placelock_set {pos} dur [id] adds/updates a timed lock (expiry = now+dur). placelock_check {pos} radius [exceptId] → 1 if any non-excepted live lock is within radius (XY + Z*scale), GC-ing expired locks first; true ⇒ occupied. placelock_rem id removes all locks with that id. Tcl idiom: `if {![placelock_check $sp 0.7 $myref]} {placelock_set $sp 1 $myref; walk}` — prevents two gnomes targeting the same spot. id space = the gnome handle (+65536 = a 2nd lock).
- **get_place / get_place_long (FUN_00656560 GetPlace → FUN_005a9700 FindPlace)** [stub '-1 0 0' sentinel (DigglesTclEngine.cs:825) — deliberately fires Tcl fallbacks. For M8 the '-1 0 0' fallback is acceptable for dig/harvest (the dig loop uses vector_fixdig/get_digedge instead); a real get_place (NavGraph candidate scan + placelock) is needed for production worker-spot selection. Implement after dig+harvest land.]: Rasterizes floor reachability into a candidate grid, scores cells by distance + reachability + placelock occupancy + mindist/walldist/rimdist, returns the best standable world {x y z} (or {-1 -1 -1}), and place-locks the chosen cell. Used to pick an approach spot for work/placement.
- **gnome_announce_dig (FUN_0067a450)** [no-op stub (DigglesTclEngine.cs:1756). Implement as a re-stamp of the DiggableTerrain dig mark at the tile (keeps the mark alive while digging). Low-risk — the mark grid already exists; mainly makes the dispatcher's dig-job discovery consistent with what the loop is actively digging.]: gnome_announce_dig this $pos writes/refreshes the dig mark (x,y,strength=50) into the per-player dig-mark manager that the dig allocator consumes. Called repeatedly inside dig_starttask/dig_continue/dig_execute.
- **gnome_failed_work / gnome_stopped_work** [no-op stub (line 1756). Implement to: release the gnome's placelock/dig-cell reservation + clear WorkBusyCooldown so the gnome re-enters the idle pool. Without it a failed job's gnome stays 'busy'.]: Report task outcome back so the planner can re-plan (release reservation, re-open job). Called at the top of every evt_*_proc and on dig/harvest failure.
- **obj_query (FUN_005de500) — distance sort** [IMPLEMENTED but returns dict-order then truncates at -limit (CmdObjQuery, DigglesTclEngine.cs:2635). FIX: sort results by distance to queryPos before -limit. Also fix FlagMatches (line 2725) to return false for unknown flags so -flagneg {boxed contained} doesn't over-exclude the Lager store query.]: Returns matching object handles; native sorts by -sorting (byrange = nearest-first) BEFORE applying -limit. Our harvest/haul discovery needs 'nearest item' correctness.
- **prod_assignworker / prod_valid / get_prodautoschedule** [hardcoded '1' (DigglesTclEngine.cs:826-828). Acceptable for first-light: '1' lets the handshake/idle gating pass. Make get_prodautoschedule per-entity later (a gnome on strike / hand-ordered shouldn't auto-dispatch).]: prod_assignworker this $workplace = the gnome ACCEPTING a workprod job (binds + returns 1). get_prodautoschedule this = is auto-dispatch ON for this gnome (gates idle→work). prod_valid = is the workplace usable.
- **prod_gnome_state** [stub (line 1694). Safe as no-op for dispatch; revisit if the dispatcher needs occupation info (it reads our idle pool instead).]: prod_gnome_state this idle|dig|harvest|fight … = HUD/state bookkeeping the work_idle/dig states call; native may also feed the planner's gnome-occupation read.
- **set_event / event_get / DispatchEvent (the raise bridge)** [HAVE — faithful (DigglesTclEngine.cs:3531 CmdSetEvent,:3559 DispatchEvent,:3510 CmdEventGet). The C# dispatcher reuses it directly; no change needed.]: set_event <h> <evt> -flags queues an event; DispatchEvent runs the handle_event body with event_get returning the payload. The dispatcher's ONLY interface to the gnome.

## DATA MODEL
All NEW state lives in a single C# service class plus a few fields added to existing types — faithful to the native layout without copying its byte offsets.

1) ProdDispatcher (new, Assets/Diggles/Runtime/Engine/Sim/ProdDispatcher.cs) — the C# analog of CProdPlannerMgr + the 8 CProdPlanner per-player planners (native &DAT_00c4ecd0, 8×0x3094 bytes). One service instance; per-player state held in arrays indexed [0..7]:
   - readonly HashSet<int>[] _idlePool = new HashSet<int>[8]; // CProdPlanner+0x2c/+0x30 idle-gnome pool; entered via gnome_idle
   - double _tickAccum; int _playerCursor; // amortization (FUN_00565a10 gate + DAT_00c3c968 &7)
   - readonly DigglesTclContext Ctx; (+ Interp, NavService, Terrain refs)
   - struct Job { JobKind Kind; int TargetHandle; string WorkClass; Vector3 Pos; int Tx,Ty; float Need; } // = native JobDescriptor/CObjTask (0x2c/0x28-byte record): Kind=type@+8, TargetHandle=targetObj@+0xc, Pos=+0x14..0x1c, Need=seedScore/+0x04
   - enum JobKind { Dig, Harvest, Production, Haul, Walk } // collapses the native type bytes (dig=10/3, harvest=12/2, workat=9/5, bringprod=6, walk=14/2) to the canonical evt_autoprod_* names
   - readonly List<Job> _jobs = new; // rebuilt each tick (CProdPlanner+0x6c/+0x70 work-step list)
   - PlaceLockStore for reservations (or share one on the context).

2) DigglesTclEntity (existing, add fields):
   - public int WalkResult; // obj+0x248 — set by MetaWalkAction (1=walking,2=blocked,4=arrived); read by get_walkresult. THE key blocker fix.
   - public int WorkBusyCooldown; // obj+0x3e8 busy/cooldown counter; set to AssignCooldown on assign, decremented per Step.
   - public float WorkStart = 0f, WorkLen = 6f; // worktime window in hour-units (set_worktime); auto_choose_workingtime staggers WorkStart.
   - (existing ProdSlots, BuildupComplete, Inventory, ContainedIn, Owner already present and reused as-is.)

3) DiggableTerrain (existing) — the dig-mark grid (_digMark byte[]) IS the native per-player voxel dig nibble store; reused unchanged for dig-job discovery. gnome_announce_dig re-stamps it. (Single-player simplification stands.)

4) PlaceLockStore (new, small): List<(Vector3 pos, double expiry, int id)>; Set(pos,dur,id) add/replace by id; Check(pos,radius,exceptId) GC-expired-then-test; Rem(id). now = Ctx.Sim.GameTime. = native PlaceFinder+0x62/+0x66 lock array (0x18-byte {pos[3],expiry,id,tag} entries).

5) WorktimeClock (can fold into ProdDispatcher): per-Step, for each gnome compute dayPhase=(GameTime/1800)%12, compare to [WorkStart,WorkStart+WorkLen); on the edge raise evt_time_startwork/evt_time_startsparetime; get_remaining_sparetime reads from this. = native Sparetime.cpp singleton DAT_0077246c.

Owner stock (OwnerAttribs, existing on the context) is the need denominator (demand − stock) for production/haul need scoring — reused as-is.

## INTEGRATION POINTS
- **Assets/Diggles/Runtime/Engine/Sim/DigglesGameSim.cs**: After the phase-3 state-body tick loop (before/around the phase-4 Ctx.Camera?.Tick block, ~line 148), add `Ctx.ProdDispatcher?.Tick(dt);` — the dispatch decision pass (faithful: decisions made this tick, evt_autoprod_* queued onto gnomes, delivered on the next Step's phase-2). Also in the existing phase-2b/3 loops, decrement each entity's WorkBusyCooldown once per Step (a single pass).
- **Assets/Diggles/Runtime/Tcl/Engine/DigglesTclContext.cs**: Add `public ProdDispatcher? ProdDispatcher;` and `public PlaceLockStore PlaceLocks = new;`. Created in DigglesTclEngine.Register (like Camera/Particles) so headless tests get it too.
- **Assets/Diggles/Runtime/Tcl/Engine/DigglesTclEntity.cs**: Add fields: int WalkResult; int WorkBusyCooldown; float WorkStart=0f, WorkLen=6f. (No removals.)
- **Assets/Diggles/Runtime/Tcl/Engine/DigglesTclEngine.cs**: De-stub: (1) get_walkresult → return ent.WalkResult.ToString. (2) gnome_idle → 1: ctx.ProdDispatcher?.OnGnomeIdle(ent); 0: ctx.ProdDispatcher?.OnGnomeBusy(ent). (3) get_remaining_sparetime → ctx.ProdDispatcher.RemainingSparetime(ent). (4) set_worktime → ent.WorkStart/WorkLen; get_worktime → ent.WorkStart; auto_choose_workingtime → stagger by population. (5) gnome_announce_dig → re-stamp DiggableTerrain mark; gnome_failed_work/gnome_stopped_work → ctx.ProdDispatcher?.ReleaseGnome(ent). (6) Register placelock_set/check/rem against ctx.PlaceLocks. (7) In CmdObjQuery: sort results by distance to queryPos before -limit; fix FlagMatches default to return false. REMOVE these names from GetBulkStubNames so the real impls win (get_walkresult, gnome_idle, gnome_announce_dig, gnome_failed_work, gnome_stopped_work, get_worktime, auto_choose_workingtime). Register all AFTER RegisterStubs (existing pattern, like get_hmap).
- **Assets/Diggles/Runtime/Engine/MetaWalkAction.cs (or the active nav executor)**: Set the owning entity's WalkResult: =1 while walking, =4 on arrival, =2 on blocked/failed. This is what unblocks the dig/work loop's `get_walkresult==4` gate. (Find the actual move-executor file via the nav pivot.)
- **Assets/Diggles/Runtime/Engine/ClickToMoveService.cs**: Optional/later: the manual evt_task_dig/harvest path can remain for explicit user orders, but once the dispatcher works the shift-click dig becomes redundant for autonomy; keep it as the player-override path (evt_task_*) distinct from the autonomous evt_autoprod_* path.

## IMPLEMENTATION PLAN
1. **1. Wire get_walkresult to MetaWalk arrival** -- Add DigglesTclEntity.WalkResult; set it in the nav executor (1 walking / 4 arrived / 2 blocked); register a real get_walkresult returning it and REMOVE it from GetBulkStubNames. This is the prerequisite for ANY work loop (dig/harvest) to advance past walking — even the existing manual dig is blocked by the constant 0. _(test: Headless WorkLoopTests: in EditMode walks degrade to WaitAction, so on action-finish set WalkResult=4. Add a test that raises evt_task_dig, steps the sim, and asserts the dig loop advances (dig_continue runs, ApplyDig is called / get_hmap drops) instead of stalling at the walk gate. Verifiable headlessly.)_
2. **2. Implement gnome_idle → idle pool + ProdDispatcher skeleton** -- Create ProdDispatcher with _idlePool[8] and the amortized Tick gate (no job logic yet). De-stub gnome_idle (1=add, 0=remove) and add the WorkBusyCooldown decay in DigglesGameSim. Create it in Register; call Ctx.ProdDispatcher?.Tick(dt) in DigglesGameSim.Step. _(test: Headless: spawn a Zwerg, step until it enters work_idle/idle (which runs gnome_idle this 1), assert the dispatcher's _idlePool[0] contains the gnome handle; trigger a state leave and assert removal. Verifiable headlessly.)_
3. **3. Dig-job discovery + assign (first autonomous job)** -- CollectDigJobs(player) from DiggableTerrain marks (reuse get_digedge for Pos, mark count for Need). Implement PHASE C/D drain: for each dig job pick the nearest idle gnome (NavService path-cost; in tests degrade to Euclidean), reserve via placelock, RaiseAutoprod → set_event evt_autoprod_dig. Implement placelock_set/check/rem against ctx.PlaceLocks first. _(test: Headless WorkLoopTests (extend Dig_DriveLoop): paint a dig mark, DON'T raise evt_task_dig manually, just step the sim; assert the IDLE gnome autonomously receives evt_autoprod_dig (current_worktask=='dig', tasklist contains dig_starttask) within N steps. The whole event→state→tasklist chain is headlessly verifiable.)_
4. **4. Real worktime/sparetime clock** -- Add WorkStart/WorkLen + set_worktime/get_worktime/auto_choose_workingtime; implement get_remaining_sparetime from dayPhase=(GameTime/1800)%12 vs the window; raise evt_time_startwork/startsparetime on the edge. Remove the '300' constant and the bulk stubs for these. Gate the dispatcher's idle-pool eligibility on IsWorkTime. _(test: Headless: set_worktime g 0 6; step GameTime across the 0..6 boundary and across 6..12; assert get_remaining_sparetime flips 0.0↔>0 and the gnome's current_plan flips work↔sparetime; assert a sparetime gnome is NOT dispatched a job. Verifiable headlessly.)_
5. **5. Harvest-job discovery + assign** -- CollectHarvestJobs(player) via obj_query -class Pilz (after adding distance-sort to obj_query). RaiseAutoprod → set_event evt_autoprod_harvest -subject1 <pilz> -text1 Holz. Gate count by owner stock. _(test: Headless (extend Harvest_DriveLoop): spawn gnome + Pilz, step WITHOUT a manual event; assert the gnome autonomously gets evt_autoprod_harvest and enters the harvest flow (current_worktask=='harvest'). Verifiable headlessly; the actual walk-to-mushroom is Play-only but the dispatch+state flow is headless.)_
6. **6. Production-job discovery + assign** -- CollectProductionJobs(player): buildings with ProdSlots>0 + BuildupComplete + Bp invented. RaiseAutoprod → evt_autoprod_workat -subject1 <building> -text1 <item>. Keep prod_make/prod_cook as the cook payload behind start_prod for now (M7 reuse). Need = scheduled count − owner stock. _(test: Headless: spawn a Feuerstelle (owner 0), set ProdSlots[Grillpilz]=1, set OwnerAttrib BpGrillpilz 1, spawn an idle gnome; step; assert the gnome gets evt_autoprod_workat for the building (current_worktask=='work', current_workplace==building). The downstream cook is Play-verified.)_
7. **7. Haul-job discovery + recipe-input model + obj_query flag fix** -- Add a minimal per-class required-input table (extend the M7 Grillpilz<-Pilzhut into a small recipe registry). CollectHaulJobs emits evt_autoprod_bringprod for unmet inputs. Fix FlagMatches to return false on unknown flags + model boxed/instore so -flagneg {boxed contained} (lager store query) works. Add carried-cargo discount in the gnome-match cost. _(test: Headless: building needs Pilzhut, a loose Pilzhut exists; idle gnome; step; assert evt_autoprod_bringprod raised with -subject1 building -text1 {Pilzhut}; assert obj_query -flagneg {boxed contained} no longer returns empty when a free Lager exists. Verifiable headlessly.)_
8. **8. Multi-gnome anti-collision + immediate re-task + failure release** -- Verify placelock prevents two gnomes converging (the assign loop reserves before raising). Implement the gnome_idle one-shot immediate re-task (FUN_00434bc0). De-stub gnome_failed_work/gnome_stopped_work to release reservations + cooldown. _(test: Headless: two idle gnomes + two dig marks; step; assert each gnome gets a DIFFERENT mark (placelock_check excludes the reserved one). Assert a failed dig releases the gnome back to the pool. Verifiable headlessly.)_
9. **9. Play-mode end-to-end + tuning** -- In urw_6a/runtime scene: paint dig marks, leave gnomes idle, confirm they autonomously walk + dig + carve + re-mesh; confirm harvest + a production chain run hands-off. Tune DispatchInterval, AssignCooldown, the need-scaled radius K constants, and worktime stagger to feel right (native DAT constants unknown). _(test: Play-only (movement, re-mesh, visuals). Use the existing scene + HUD; observe idle dwarves picking up painted digs and mushrooms without clicks.)_

## TEST STRATEGY
HEADLESSLY VERIFIABLE (EditMode WorkLoopTests, no GameObjects — walks degrade to WaitAction but the event→state→tasklist→primitive LOGIC fully runs): (1) get_walkresult round-trip: set ent.WalkResult=4 and assert the dig loop advances past `4 != [get_walkresult this]`. (2) gnome_idle → pool membership: assert _idlePool[Owner] gains/loses the handle on idle enter/leave. (3) AUTONOMOUS DIG: paint a dig mark, step the sim with NO manual event, assert the idle gnome receives evt_autoprod_dig (current_worktask=='dig' in VarsScope, tasklist contains 'dig_starttask') — this is the core M8 proof and is fully headless. (4) AUTONOMOUS HARVEST: spawn Pilz + idle gnome, step, assert evt_autoprod_harvest + current_worktask=='harvest'. (5) AUTONOMOUS PRODUCTION: schedule a slot + invent Bp, assert evt_autoprod_workat. (6) AUTONOMOUS HAUL: unmet input + loose item, assert evt_autoprod_bringprod -text1 {class}. (7) WORKTIME CLOCK: step GameTime across hour-unit boundaries, assert get_remaining_sparetime flips 0.0↔>0 and current_plan flips work↔sparetime and a sparetime gnome is NOT dispatched. (8) ANTI-COLLISION: two gnomes + two marks → different targets via placelock; failed work releases the gnome. (9) obj_query distance-sort: assert -limit 1 returns the NEAREST. (10) placelock_check semantics: set/check/rem with expiry. Concrete assertions to add to WorkLoopTests: extend Dig_DriveLoop and Harvest_DriveLoop to drop the manual set_event and instead assert the DISPATCHER raises the autoprod event; add DispatchTests for pool membership, worktime gating, placelock anti-collision, and obj_query nearest. The existing RealHost harness (loads ~300 classes, the unchanged Tcl) is exactly the right substrate — assert on VarsScope.Vars['current_worktask']/['current_plan'], TaskList contents, and the dispatcher's pool/job lists.

PLAY-ONLY (cannot be headlessly verified — needs the nav executor + transforms + re-mesh): actual gnome MOVEMENT to the dig edge / mushroom / building, the carve + chunk re-mesh, hauling an item into a building, and the visual feel/tuning of DispatchInterval/radius/worktime stagger. These are verified in the runtime scene by leaving gnomes idle with painted marks and observing hands-off work.

## RISKS
- get_walkresult is the silent foundational blocker: until WalkResult is wired to MetaWalk arrival, NO work loop (autonomous or manual) advances past walking — even though the rest of the chain looks correct. Fix it first or every later test will mislead. In EditMode there is no movement, so the test must synthesize WalkResult=4 on action-finish, or dig/harvest tests will appear to stall for the wrong reason.
- The native tuning constants are unknown (DispatchInterval DAT_007372a8, the need-scaled radius DAT_00737578/00737680/00737010/00737700, the worktime day length, AssignCooldown units). The control flow is faithful but the NUMBERS are guesses; behavior (which gnome takes which job, re-task latency, work/rest ratio) will need Play-mode tuning and may not match the original feel. Flagged as assumptions throughout.
- ASSUMPTION: skipping the evt_zwerg_workannounce→prod_assignworker handshake for player 0 and raising evt_autoprod_workat directly. The announce round-trip may be load-bearing for current_workplace gating or AI fairness; if production starves or double-assigns, restore the announce. prod_assignworker is currently a constant '1' which papers over this.
- ASSUMPTION: worktime hour-unit = 1800s (12-unit day = 12*1800s), matching our gethours=GameTime/1800. If Sparetime.cpp uses a different day length (the openQuestion flags 1800s-as-a-DAY vs as-an-HOUR), get_remaining_sparetime and the work/rest cycle will be wrong; verify against FUN_005f4a70 before shipping the clock.
- Single-player dig-mark grid: DiggableTerrain._digMark ignores the player arg, so AI players (1-3) painting digs would collide with player 0's marks. Fine for the human-only M8 first-light; a real per-player nibble grid is needed before AI economy digging.
- No recipe-input model yet: production/haul discovery needs a per-class required-input table. The M7 hardcoded Grillpilz<-Pilzhut must be generalized (read prod_items / techtreetunes) or haul jobs will be empty / wrong.
- obj_query -flagneg {boxed contained} currently over-excludes (FlagMatches default-true). The Lager store query returns nothing → hauled items can't be stored. Must fix FlagMatches AND model boxed/instore, or haul-then-store breaks.
- get_place is still the '-1 0 0' fallback sentinel; production worker-spot selection relies on it. Acceptable for dig/harvest (which use get_digedge/vector_fixdig) but production placement may be imprecise until a real NavGraph-based get_place + placelock lands.
- Dispatch latency: the C# raise queues onto PendingEvents and fires on the NEXT Step's phase-2 (one-tick latency, faithful to SrvDoThink's dispatch-then-HandleEvents order). If the dispatcher runs in the SAME phase that drains events, ordering bugs (a gnome assigned then immediately re-pooled) can appear; keep the Tick at the END of Step.
- Per-player amortization (one player per qualifying tick) means player 0's gnomes are re-dispatched only every ~8 qualifying ticks; combined with the immediate-re-task on gnome_idle this is faithful, but if the immediate re-task is omitted, idle gnomes will feel sluggish to pick up new work.
