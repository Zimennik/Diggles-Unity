# B7 — Runtime tick laws for CAct_Crawl (4), CAct_AttackBallistic (7), CAct_Lore (9)

## Status

All three fixture-absent actions are now dynamically reverse-engineered from the decompile
(the per-function decompile of `Diggles.exe`), with every serialized payload field bound to its
runtime role. The shared action contract is proven: `CObj_Tick` (`005cc600`, profiler string
`"void CObj::Tick Action"`) calls action vtable slot `+0x14` once per sim tick; a `0` return
means finished — the engine then calls slot `+0x0C` (completion hook), clears `owner+0x3f8`, and
schedules the FinishCode (`action+0x10`) for Tcl evaluation. Abort/replace instead calls slot
`+0x10` and evaluates BreakCode (`action+0x14`) (`FUN_005d0c20`, `FUN_00671fd0` =
`CTclObjMethods::Action`). Headline discoveries: **Lore is the mine-cart/rail ride** (waypoint
spline + gravity/friction + trailer train, global per-object track registry), **the mysterious
"auxiliary records" after sentinel `0x626C6133` in the action table are exactly that global Lore
track registry** (so Lore progress/speed IS save-restorable), **AttackBallistic is the ranged-weapon
attack produced natively by the fight engine** (damage precomputed at launch; the "projectile" is a
tracer-point trail; hit resolves when the trail dies if the target stayed within 0.5 units of the
aim point), and **Crawl is a scripted-verb slow terrain-hugging wander** (random quadrant heading,
20 ticks/sec countdown) with **no shipped-script or native producer** — effectively dead content.

## Shared action vtable layout (context for all findings)

PROVEN from `save_action_vtables_dump.txt` + per-slot reads:
`+0x00` deleting dtor, `+0x04` class-name getter, `+0x08` Init-from-Tcl-args,
`+0x0C` completion hook (most: `FUN_00406700` = clear `+0xc`; Lore: `FUN_00414a50`),
`+0x10` abort hook (`FUN_00406710` no-op ret 1), `+0x14` per-tick (ret 1 = keep running, 0 =
finished; Wait/Anim: `FUN_00408640` = `now <= deadline`), `+0x18` dual read/write serializer
(mode arg `2` = write; `FUN_00406730` dispatches).

Common object layout (PROVEN via `FUN_004179d0` Wait init + serializers): `+0x8` Argument TclObj,
`+0xc` owner ptr, `+0x10` FinishCode TclObj, `+0x14` BreakCode TclObj, `+0x18` status/opaque.
Verb strings for the Tcl `action` command factory `FUN_00405e10`: `wait, slide, anim, crawl,
rotate, attack, attackballistic, fly, lore, walk, fall, move` (strings read from exe.rdata;
`fall` exists as a verb but has no save type).

## Findings

### 1. CAct_Crawl (type 4) — slow terrain-hugging wander

1.1 **Producer** — PROVEN-negative for scripts: no `action … crawl` anywhere under the Steam
`data/Scripts` or `data/GameScripts` (grep). The only "crawl" hits are animation names
(`raupe.tcl set_class_anim crawl`, `baby.tcl` animset 2 "krabbeln" — those drive CActWalk animsets,
not CAct_Crawl). PROVEN-negative for natives: the only constructors of `CAct_Crawl::vftable` are
the verb factory `FUN_00405e10` (Tcl `action` command / MP dispatcher) and the save loader
`FUN_00406730`. Corpus count 0 is consistent. Crawl is reachable only by issuing the Tcl verb
`action <obj> crawl <seconds> <finish> <break>` manually.

1.2 **Init** (`FUN_0040c300` + `FUN_0040c100`) — PROVEN:
- `+0x34 RemainingTicks = (int)(argSeconds * 20.0)` (`DAT_00737578` = 20.0).
- `+0x38 = (rand % 4) * π/2` (`FUN_006d46ea` = rand; `DAT_007371e0` = 1.5707964). The doc's
  `RandomYawOrPhaseBits` is therefore a **float angle in {0, π/2, π, 3π/2}** — a per-action random
  quadrant heading parameter. PROVEN.
- Two 12-byte anim slots resolved by name from the owner's anim DB (`FUN_004613d0`):
  `+0x28/+0x2c` = `"crawl"` anim (db id, anim index), `+0x1c/+0x20` = `"standstill"`
  (string `s_standstill_0071cf60`; the "crawl" bytes at `0x71cc74` double as the factory verb).
  If owner flag `+0x3d0 & 0x2000` (female/alternate-set) both anim indices are +1. These slots are
  **not serialized**; the native loader ctor re-resolves them. PROVEN.
- Init immediately plays the crawl anim: `FUN_006b49b0(owner, +0x28, +0x2c)` (=
  `FUN_005cdae0` set anim on render object). PROVEN.

1.3 **Tick** (`FUN_0040c460`, vtable +0x14) — law:
- Reads owner pos `+0xc/+0x10/+0x14` and rot `+0x40/+0x44/+0x48`; NaN-guards and normalizes each
  rot component into (−π, π] (constants π/2π at `0x737440/0x7374f8`). PROVEN.
- Computes a horizontal step from the owner yaw and the `+0x38` angle via `_CIsin/_CIcos`
  (`FUN_004089d0/FUN_004089f0` wrap `FUN_006f6ad0/FUN_006f5ae0`), magnitude divided by 150.0
  (`DAT_00737630`) → ≈0.007–0.02 units/tick. Exact operand binding is lost to FPU-arg decompiler
  mangling — step ≈ `(sin·A/150, cos·A/150)` — LIKELY on the exact formula, PROVEN that the step
  is tiny, yaw-derived, scaled by `+0x38/150`.
- Samples terrain height at the stepped position with the byte sub-grid sampler (8 cells/unit,
  bilinear; `FUN_005aae70` → `FUN_004093f0`); if height > 14.0 (`DAT_00737550`) the tick returns 0
  (immediate finish — off walkable ground). New Z = sampled floor height (crawlers glue to the
  floor). PROVEN mechanism, PROVEN threshold.
- Aligns pitch/roll to the local slope: neighboring height samples (`FUN_005ab050/FUN_005ab130`)
  produce target angles; current rot eases toward them by ±0.02 rad/tick (`DAT_00736ff0`), with 2π
  renormalization. LIKELY (slope-alignment intent), PROVEN easing constant.
- If position changed → `FUN_005cd8e0` SetPos + render refresh (`FUN_005ebc90/FUN_005eb470`);
  always `FUN_005cd840` SetRot. PROVEN.
- `--RemainingTicks (+0x34)`; while >0 → return 1. At ≤0: plays `"standstill"`
  (`FUN_006b49b0(owner, +0x1c, +0x20, 0, 2)`) and returns 0 → engine fires FinishCode. PROVEN.

1.4 **Field → law binding**: `RemainingTicks` = literal ticks left (20 Hz-scaled seconds);
`RandomYawOrPhaseBits` = the fixed random heading angle chosen at init (restoring it preserves the
wander direction without consuming RNG). PROVEN.

### 2. CAct_AttackBallistic (type 7) — ranged weapon attack

2.1 **Producers** — PROVEN:
- Native fight engine `FUN_005c2910` (the round resolver our `FightResolver` ports: computes
  damage via `FUN_005bdab0`, trains `exp_F_Defense`, emits result verbs `"rotate"`,
  `"defender_is_dead"`, `"production"`, `"miss"` (string at `0x72b4d8`), `"attack %i"` through
  `FUN_006bb930`): creates via factory verb `"attackballistic"`, configures via `FUN_00409730`,
  then virtual-inits with the shooter. Reached from `fight_dispatch` (`FUN_005bf660`) and from the
  Tcl command `fight_setactions_ballistic` (used by `z_procs.tcl::shoot_anim` — the slingshot
  hamster hunt).
- MP/replay opcode 9 in `FUN_005b59f0` (same configure + init sequence).
- No shipped script uses `action … attackballistic` directly (grep). Scripts trigger it via
  `fight_dispatch`/`fight_setactions_ballistic`; z_dignwalk maps pose `"ballistic"` to animsets.

2.2 **Configure** (`FUN_00409730`) — PROVEN field map (binds the serialized payload):
- `+0x1c TargetObjectId` = defender object.
- `+0x2c/+0x30` = shoot-anim db id / anim index (from the weapon table `FUN_005bbb20`, record
  stride 0x40; these are the first "2 × Raw32" in the payload).
- `+0x40` = **damage** (float; precomputed by the fight math at launch — the second Raw32 pair's
  second value).
- `+0x44` byte = flag from `FUN_005bebc0(attacker, defender, …, damage)` — gates a defender
  render-object channel write on hit (`FUN_005d8660(1,5)`), LIKELY the knockdown/hit-reaction cue.
- `+0x24/+0x28 schedule` = copy of the weapon's fire-time array (`weaponRec+0x38`). Sentinel: if
  `schedule[0] == -1`, the schedule is cleared and `+0x20 Mode = 1` (straight-line/beam shot);
  otherwise `Mode = 0` (arc). PROVEN.

2.3 **Init** (`FUN_0040abc0`, vtable +0x08) — PROVEN:
- Plays the shoot anim (`FUN_006b49b0(owner, +0x2c, +0x30)`; female flag `0x2000` bumps anim id).
- `+0x60 AbsoluteDeadline = now + Δ`; Δ arrives in an FPU register the decompiler lost — LIKELY
  the shoot-anim duration in seconds.
- **`owner+0x24c = 0`** — resets the ballistic result; Tcl getter =
  `CTclObjMethods::GetBallisticResult` (`FUN_0068fd40`, command `get_ballisticresult`, consumed
  by `z_procs.tcl:557` — result 1 ⇒ hit ⇒ paralyze/pickup the hamster). PROVEN.
- Each schedule entry += `round(now * 10.0)` (`_DAT_007373f0` = 10.0 double): relative fire ticks
  become **absolute 10 Hz tick stamps**. PROVEN.
- `+0x58 TargetVerticalOffset = (modelMaxY − modelMinY) * 0.005 * 0.5` from the target's graphics
  record (stride 0xe8, fields +0x48/+0x54; scale `DAT_00736fcc` = 0.005) — aim at body middle.
  Class specials: target class `"Krake"` (string `0x71ce84`) sets `+0x55 = 1`
  (trail ignores terrain collision); `"Plattmachfalle"` forces `+0x58 = −1.8`. PROVEN.
- Rotates the shooter toward the target (`FUN_0040a590`). LIKELY (facing intent).

2.4 **Tick** (`FUN_0040afe0`, vtable +0x14) — law, PROVEN:
- Guard: target ptr non-null, readable, magic `+8 == 0x616e4f62`; else return 0 (finish; FinishCode
  still runs via CObj_Tick).
- Runs while `now <= deadline (+0x60)` **or** live trail handles exist (`+0x38 != 0`); else
  return 0. `+0x3c` increments every tick (tick counter; opaque in save).
- **Fire phase**: for each pending schedule entry `> 0` with `entry <= now*10`: spawn a tracer
  trail `FUN_00409830(start = owner pos with y − 0.5, end = target pos with y + 0x58 offset)`,
  record aim point `+0x48/+0x4c/+0x50 LastTarget = target pos`, mark entry `= -1` (consumed).
- **Trail** (`FUN_00409830`): allocates a render trail slot (manager `FUN_00492250` /
  `FUN_004954a0`), pushes its handle onto `+0x34/+0x38`, sets `+0x54 = 1`. Mode 1 = straight line
  of points (step 0.125 units, `DAT_0073703c`; starts 8 steps = 1.0 unit out); Mode 0 = the same
  line with a sine-shaped vertical arc (π phase over the flight, steeper for near-vertical shots).
  Each step tests terrain occlusion `FUN_004097b0` (height-map byte compare) unless `+0x55`; if
  terrain blocks, the trail stops early and `+0x54 = 0` (**miss marker**). PROVEN.
- **Hit phase**: for each live handle, when the render manager reports the trail finished
  (`FUN_0049dc20` false): compute `dist²(target now, LastTarget)`; if `< 0.25` (`DAT_00737058`,
  i.e. target moved < 0.5 units since aim — the dodge rule) **and** `+0x54` (trail completed):
  ensure the defender's attribute container (`+0x21c`, class 0x14a), apply the `+0x40` damage to
  `atr_Hitpoints` (`FUN_005dac00/FUN_005dae40`), if `+0x44` fire the defender hit-reaction channel
  write (`FUN_005d8660(1,5)`), and set **`owner+0x24c = 1`** (ballistic result = hit). Remove the
  handle from the vector. PROVEN.
- No Tcl is evaluated inside the tick; the only Tcl surface is FinishCode/BreakCode via the
  CObj_Tick law plus the `get_ballisticresult` flag.

2.5 **Serialized-field verdicts**: `TargetObjectId`(+0x1c) stable ref PROVEN; `Mode`(+0x20)
straight-vs-arc PROVEN; schedule = absolute 10 Hz fire stamps, `-1` = already fired, PROVEN;
first Raw32 pair = shoot anim db/index PROVEN; transient handles (+0x34/+0x38) = render trail slot
indices — meaningless post-load (native reloads them verbatim and the first tick resolves them as
"trail dead" ⇒ in-flight shots settle immediately after load) PROVEN-mechanism/LIKELY-consequence;
second Raw32 pair = tick counter (+0x3c, opaque) and damage (+0x40) PROVEN; byte(+0x44)
hit-reaction flag PROVEN-gate/LIKELY-meaning; `LastTarget`(+0x48..0x50) = aim snapshot for the
dodge test PROVEN; bytes +0x54/+0x55 = trail-completed / pierce-terrain PROVEN;
`TargetVerticalOffset`(+0x58) PROVEN; `AbsoluteDeadline`(+0x60) PROVEN-role/LIKELY-source.

### 3. CAct_Lore (type 9) — mine-cart / rail ride ("Lore" = German for mine cart)

3.1 **Producers** — PROVEN (shipped scripts):
- `Scripts/classes/story/Lorenbahn.tcl:180` — the story mine-cart railway:
  `action this lore "-waypoints { … } -trailers { $waggonlist } -gravity 1 -friction 0.05"
  {…; call_method this go} {}` — FinishCode re-issues the next track segment (`$position` is a
  script-side object variable, not engine-set).
- `Scripts/classes/characters/riesenelfe.tcl` (347, 349-358, 711) — the giant elf uses single- and
  two-waypoint lore actions (with min=max speed) as a generic "float this object along a line"
  mover for herself and her wing anchors.
- `sequences/Lava/unq_ende.tcl` (commented-out usage).

3.2 **Argument grammar** (`FUN_00412840` option parser) — PROVEN:
`-waypoints { { {x y z} {rx ry rz} [minSpeed [maxSpeed]] } … }` → action-owned array `+0x2c`
(ptr) / `+0x30` (count), stride 0x20 = the serialized 8 Raw32 per waypoint:
pos(3 float), rot(3 float), `+0x18` minSpeed, `+0x1c` maxSpeed (missing scalars default to the
sentinel −1.0). `-trailers { {obj offset} … }` → `+0x24/+0x28` pairs {object ptr (serialized as
id), float **distance behind the head along the track**} (parse: element 0 = object ref via
`FUN_005c8310`, element 1 = double). `-gravity` → `+0x1c` float, `-friction` → `+0x20` float.

3.3 **Global track registry** — PROVEN (`FUN_00413570` "start", `FUN_00414e40` lookup):
`DAT_007735e0` table of 0x18-byte records keyed by **owner object id**:
`{+0x00 ownerId, +0x04 waypointArray(0x20 stride), +0x08 count, +0x0c progress, +0x10 totalLength,
+0x14 speed}`. Init **appends** the action's waypoints onto the owner's existing record (creating
it at zero if absent): each appended node adds its segment length (z-component scaled by 0.5,
`DAT_0073708c`) to totalLength; nodes whose minSpeed is the −1.0 sentinel inherit the previous
node's min/max (defaults 0.5 / 10.0). This is why Lorenbahn can chain `go` calls: **the track
persists across actions**. `FUN_007137c0` frees the table on game reset.

3.4 **Tick** (`FUN_00414930` "LORE-TICK" → `CAct_Lore_SetLorePos` `004143d0`,
math in `CAct_Lore_GetPosRotSpeed` `00413ea0`) — law, PROVEN structure:
1. Find the owner's track record; take `speed`.
2. Head placement: walk the waypoint list to the segment containing `progress`; `t = local
   progress / segment length`; pos = lerp(node[i].pos, node[i+1].pos, t); rot = component-wise
   angle interpolation (`FUN_00413c70`); interpolated node min/max speeds come back as clamps.
   Off-track (progress ≥ totalLength) reports "done".
3. **Gravity**: while placing the head, `speed += (segmentVerticalDrop / segLen · gravity(+0x1c) ·
   0.1) / (1 + trailerCount)` — downhill accelerates, uphill brakes, the whole train's "mass"
   (1 + trailers) divides the acceleration. PROVEN formula (constant 0.1 = `DAT_00737034`).
4. Apply head pos/rot to the owner (`FUN_005cd8e0/FUN_005cd840` + render refresh).
5. **Trailers**: each trailer object is placed at `progress − offset` with the same law; its rot
   components are sanity-clamped (|v| ≥ 10000 → 0), flags `+0x3d8 |= 2` and byte `+0x402 = 1` are
   set (externally-driven/carried marks), and a render object is ensured. PROVEN.
6. **Friction**: `speed −= friction(+0x20) · 0.1`; clamp to the interpolated node min/max.
7. `track.speed = speed; track.progress += speed · 0.1`; tick returns
   `newProgress < totalLength` — i.e. the action finishes when the head reaches the end of the
   accumulated track (FinishCode then runs via CObj_Tick).
8. **Completion hook** (vtable +0x0C = `FUN_00414a50` "LORE-SHUT"): trims the consumed part of the
   global track — drops fully-passed waypoints while keeping `progress − lastTrailerOffset` of
   track behind the head (so the trailers never dangle off the trimmed end), rebases progress and
   totalLength, shrinks the array. PROVEN.

3.5 **⭐ The action-table "auxiliary records" are the Lore track registry** — PROVEN
(`FUN_00406fe0:110-134`): after sentinel `0x626C6133` the serializer writes
`count`, then per record `Int32 ownerId`, `Int32 waypointCount + waypointCount × 8 Raw32`
(`FUN_004153b0`), then `Raw32 progress, Raw32 totalLength, Raw32 speed`, then sentinel
`0x626C6134`. This exactly matches the doc's "value, nestedCount, nested*8, 3*Int32" and means the
ride's progress and speed **are** in the save — the action payload alone (gravity/friction/
trailers/own-waypoint-copy) is not the whole state.

3.6 The Lore action's own serialized waypoint list (`+0x2c/+0x30`) is its contribution to the
track (used again by LORE-SHUT trailer-offset math); the authoritative runtime geometry lives in
the global record. PROVEN.

### 4. Tcl surface to reproduce on completion/abort (all three actions)

PROVEN, uniform: none of the three ticks evaluates Tcl directly. The engine law is:
- normal completion (tick returns 0) → completion hook (+0x0C) → clear `owner+0x3f8` → schedule
  **FinishCode** eval on the owner (CObj_Tick `005cc600:387-414`);
- abort/replace (`FUN_005d0c20`, or CmdAction installing a new action `FUN_00671fd0`) → abort hook
  (+0x10, no-op) → schedule **BreakCode** eval; CmdAction also pings
  `AchievementManager::CheckTriggerName` with the replaced action's name.
- Ballistic additionally publishes `owner+0x24c` for `get_ballisticresult` (0 at start, 1 on any
  landed hit), and its *producer* (`FUN_005c2910`, native fight engine) — not the action — emits
  the fight-dispatch result verbs (`attack %i`/`miss`/`rotate`/`defender_is_dead`/`production`).

## Port mapping recommendation

### Crawl (`NativeCrawlActionSaveState`)
- `RemainingTicks` → port state, drive as a literal per-tick countdown. **Retain.**
- `RandomYawOrPhaseBits` → reinterpret as float heading angle (`(rand%4)·π/2` domain); port state.
  **Retain; do not re-roll RNG on load.**
- Anim slots → derivable-on-load: resolve `"crawl"`/`"standstill"` from the owner's animset (the
  port's anim-controller name lookup), +1 index shift for the female flag equivalent.
- Continuation implementation: a tiny dedicated integrator (not MetaWalkAction — crawl never
  paths): per tick, step ~`1/150`-scale along facing modulated by the saved heading, snap to the
  DiggableTerrain floor probe (the same live floor-support probe MetaWalkAction uses), abort→finish
  when off walkable floor, ease pitch/roll to slope, play `crawl` loop, `standstill` at the end,
  then let `DigglesGameSim`'s existing post-tick finish law run FinishCode. Given zero shipped
  producers and zero corpus records, the current compatibility shell (countdown without movement)
  is already behaviorally adequate for every real save; full dynamics are polish, not a blocker.

### AttackBallistic (`RestoredNativeAttackBallisticAction` source fields)
- `TargetObjectId` → port state (stable id, already validated). **Retain.**
- `Mode` → port state (0 arc / 1 straight). **Retain.**
- `Schedule` → port state as **absolute 10 Hz fire stamps; −1 = already fired**; on continuation,
  fire remaining entries when `gameTime*10 ≥ entry`. **Retain.**
- First Raw32 pair → shoot anim db/index: compatibility-retain for byte-identical rewrite; the port
  should re-resolve the clip from its animset (`"ballistic"` pose clips per z_dignwalk).
- `TransientHandles` → compatibility-retain only (native semantics: trail render slots). On load,
  treat every saved handle as a **completed trail** and resolve its hit test on the first ordinary
  post-commit tick (this mirrors what the original effectively does with stale handles).
- Second Raw32 pair → tick counter (opaque, retain) and **Damage (float)** → port state; apply via
  the existing `TimedImpactAction` value object (attacker id, defender id, damage) claimed once at
  trail completion — exactly the port's post-commit terminal-tick contract. Do **not** re-run
  `FightResolver` math on load; the damage was fixed at launch.
- Byte `+0x44` → retain; map to the port's hit-reaction/knockdown cue if/when ported.
- `LastTarget` → port state: the dodge test needs it (`hit iff dist(targetNow, LastTarget) < 0.5`).
  **Retain.**
- Bytes `+0x54/+0x55` → port state: trail-completed (miss if 0) / pierce-terrain (Krake).
  **Retain.**
- `TargetVerticalOffset` → derivable-on-load from the target model bounds, but **retain** (cheap,
  covers Plattmachfalle's forced −1.8).
- `AbsoluteDeadline` → port state; continue as `max(0, deadline − restoredGameTime)`, and keep the
  action alive while any restored in-flight shot is unresolved. **Retain.**
- Owner `+0x24c` ballistic result: add to the owner entity save surface (or reset to 0 on load —
  native resets it per action init; the shipped consumer reads it immediately after the finish
  code, so restoring mid-action with 0 and letting the continuation set it is faithful). Implement
  the `get_ballisticresult` Tcl command against this field.
- Visuals: drive the trail through the CPU particle/tracer layer (straight or sine-arc point
  chain, 0.125-unit steps, terrain occlusion via the height sampler); gameplay must not depend on
  the visual's lifetime — use flight-time ≈ points/steps at the manager's decay rate, or resolve
  the hit on a fixed short delay (TimedImpact) to stay corpus-compatible.

### Lore (`RestoredNativeLoreAction` state)
- `Gravity`, `Friction` → port state (floats). **Retain.**
- Trailer pairs → port state: {stable object id, float distance-behind-head}. **Retain.**
- Waypoints (8 floats each) → name them in the port model: pos xyz, rot xyz, minSpeed, maxSpeed
  (−1.0 = inherit-previous sentinel). **Retain, now safely typed.**
- **New requirement**: parse the action-table auxiliary block as `LoreTrackRecord { ownerId,
  waypoints[8 floats], progress, totalLength, speed }` into a port-global Lore track registry
  (mirrors `DAT_007735e0`). This block is currently "lossless auxiliary" — it must become typed
  state, because continuation position/speed live here, not in the action payload.
- Continuation implementation: a dedicated `LoreTrackSystem` (new small system, not MoveAction):
  per 0.1 s tick do gravity → place head+trailers by arc-length lerp → friction → node min/max
  clamp → advance progress; finish when progress ≥ totalLength; completion trims the track behind
  the head by the largest trailer offset. Drive owner/trailer transforms directly (the native sets
  pos/rot, flags them carried, and bypasses navigation entirely — riesenelfe uses it as a generic
  tween). FinishCode chaining (`Lorenbahn::go`) then works for free through the existing
  post-commit finish law.

## Open questions

1. Ballistic `AbsoluteDeadline` addend: in-register FPU value at `FUN_0040abc0:56` — LIKELY the
   shoot-anim duration; confirm via live dump (compare `+0x60 − saveTime` against the weapon anim
   length) before relying on it for re-encoding.
2. Crawl step formula operand binding (`sin/cos` of yaw vs of the `+0x38` angle) is decompiler-
   mangled; magnitude/scale (÷150) and inputs are certain, the exact trig product is not. A 2-min
   live-dump watch of a console-issued `action … crawl` would settle it.
3. `FUN_005d8660(1,5)` writes render-channel field `+0x5c = 5` on the defender — LIKELY the pain/
   knockdown visual cue; name it properly if the animation-channel table gets RE'd.
4. Crawl's 20 ticks/sec constant vs the 10 Hz schedule stamps of Ballistic: if `CObj::Tick` runs at
   10 Hz, crawl actions last half the requested seconds. Worth one original-exe stopwatch test
   before choosing the port's tick base for restored `RemainingTicks`.
5. Whether the Lore auxiliary registry can ever contain records for owners with **no** active Lore
   action (LORE-SHUT trims but does not delete records): PROVEN-possible from code; the port
   registry should therefore load/save records independently of action existence.
