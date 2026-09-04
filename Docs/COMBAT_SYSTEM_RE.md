# Combat system — reverse-engineering and implementation status

> Status: native loop re-derived and the fight-loop repair implemented.
> EditMode coverage is authored; live Play-mode verification is still pending. This document
> separates native evidence, landed behavior, and remaining integration work.

## Scope and sources

The original executable owns combat in `CFightActionMgr`; the shipped Tcl scripts configure
and orchestrate it. The port keeps the original `Scripts/init/fight.tcl` and
`Scripts/misc/genericfight.tcl` as the gameplay definition/orchestration layer while replacing
the executable implementation with clean C#.

Primary evidence:

- `re/decompiled/Diggles/_global/005c5010_FUN_005c5010.c` — inventory weapon/shield scan,
  experience eligibility, unarmed fallback, ballistic range filter, and four-element result.
- `005c3880_FUN_005c3880.c` — attack-position query and the larger-of-both-class radii law.
- `005c4730_FUN_005c4730.c` / `005c4c10_FUN_005c4c10.c` — class/owner HP ratio and escape
  lookup, including the `all` owner fallback.
- `005c3c90_FUN_005c3c90.c` — weapon-combination registration.
- `006142b0_FUN_006142b0.c` — `class_fightdist` at class offset `+0xcf8`; the class constructor
  initializes it to `1.0` (`00601250_FUN_00601250.c`).
- `00689b80`, `006897a0`, `006899c0`, `0068a5a0`, `0068a7c0`, `0068fd40`, `006988a0`, and
  `00698cb0` — weapon/shield ids and per-object fight flags/results.
- `005bf660_FUN_005bf660.c` — coordinated melee selection, the 15-degree facing gate,
  range/result protocol, damage scheduling, and attack-slot lifecycle.
- `005bf300_FUN_005bf300.c`, `005bdd20_FUN_005bdd20.c`, `005beaf0_FUN_005beaf0.c`, and
  `005be480_FUN_005be480.c` — free-defender selection plus the exact attack/reaction row filters.
- `005bf080_FUN_005bf080.c` — floor and wall horizontal-direction masks.
- `00408ac0_FUN_00408ac0.c`, `00408d00_FUN_00408d00.c`, `00408e50_FUN_00408e50.c`, and
  `00408fc0_FUN_00408fc0.c` — the dedicated `CAct_Attack` lifetime and completion hand-off.
- `005c2910_FUN_005c2910.c` — ballistic selection, the 30-degree facing gate, production
  scaling, projectile result, and `defender_is_dead` behavior.
- `005c0ef0_FUN_005c0ef0.c` / `005c1140_FUN_005c1140.c` — training and area-strike action
  creation. Strikeback performs no owner/diplomacy read while enumerating targets.
- `00677420`, `006778d0`, `00677d80`, and `006780e0` — Tcl argument and callback contracts
  for the four action commands.
- `Scripts/init/fight.tcl` — 377 action rows, 52 weapon rows, five combinations, and 29 HP
  rules in the English Steam 2.1.1.10 data.
- `Scripts/misc/genericfight.tcl` — turn/result protocol consumed by the class state machines.
- `Scripts/classes/zwerg/z_events.tcl`, `Scripts/ai/std_ai.tcl`, and
  `Scripts/init/eventgen.tcl` — defend-event, opponent-cadence, and attack-order eligibility laws.

## Landed foundation

`Runtime/Tcl/Engine/FightSystem.cs` is a context-owned replacement for the original global
fight registry. It currently provides:

- `fight_action`, `fight_weapon`, `fight_weapon_kombi`, `fight_map_obj2db`, and
  `fight_map_class2db` registration;
- `def_hp_ratio` plus exact-owner, `all`, and native default (`ratio=1.0`, `escape=0.1`)
  lookup;
- `class_fightdist`, with the native `1.0` class default;
- `get_best_weapon`, melee/ballistic filtering, minimum-experience checks, unarmed fallback,
  two-handed shield exclusion, and complete-combination multipliers. Native attack evaluation
  is `sqrt(attack * combination multiplier)`; the fourth result element is the selected
  shield's combination multiplier rather than its final defense score;
- `get_weapon_id`, `get_weapon_pose`, `get_weapon_range`, and `chack_weapon_exp`;
- `get_attack_pos`, using the larger class radius or the explicit override and retaining the
  defender height;
- live weapon/shield ids, attack-in-progress, ballistic-result, died-in-fight, climbability,
  and velocity-component object queries.

The shipped file contains control-byte debris after the meaningful fields on several hit-action
rows (`fight.tcl:133-148`). The registry intentionally consumes the fixed meaningful prefix and
ignores trailing fields, matching the tolerant behavior required to load the original data
without modifying it.

`FightSystemTests` covers the command contracts, actual shipped-table load, experience gates,
weapon combinations, ballistic selection, two-handed shield exclusion, HP fallback, object
state, and attack radius.

## Landed action resolver

`Runtime/Tcl/Engine/FightResolver.cs` now implements all four former action stubs:

- `fight_setactions` selects melee and reaction animations from the original table, enforces
  exact class/pose/floor-or-wall/direction masks, enforces class reach, reports
  `reapproach`/`out_of_range`, and uses the native 15-degree floor-facing gate;
- `fight_setactions_ballistic` selects the original throw animation, enforces weapon range,
  publishes `get_ballisticresult`, and uses the native 30-degree `rotate` gate;
- `fight_setactions_strikeback` applies the original explicit area-action semantics to living
  gnome/monster targets in range. It intentionally does not filter by diplomacy, which is
  required for player-owned training dummies and matches the absence of owner reads in
  `005c1140`;
- `fight_setactions_training` selects and starts configured attack/defense drill actions and
  preserves the callback contract.

The implementation preserves the result vocabulary exercised by the native branches and
`genericfight.tcl`: `production`, `attack`, `out_of_range`, `defender_is_dead`, `rotate`,
`reapproach`, `miss`, and `no_free_defender`. Damage is delayed to the attacker animation's
impact seam by `TimedImpactAction`. Living targets receive `evt_fight_get_hurt`; production
targets take the native ten-percent-scaled direct HP path. Finish and break paths share the
authored callback while clearing `AttackInProgress`. A staged native impact is not recalled by
attacker death or an external action replacement; the wrapper carries it to its original
deadline. Combat experience uses `add_expattrib`, lethal selection marks `DiedInFight`, HP rules
scale class/owner durability, and weapon-combination multipliers participate in damage.

The damage calculation is anchored to `005bdab0`: action attack/guard values, the square root
of the attacker/defender weapon-strength ratio, the `0.2` action scale, the final `0.6`
`DoObjActions` scale, skill influence, and production `0.1` scaling. The port retains a bounded
random skill factor because the surrounding native random helper has not been named; this is a
documented reconstruction point for manual balance comparison, not a missing execution path.

The focused resolver gate covers delayed event damage, callbacks, combat experience, native
range/facing results, ballistic result publication, production damage, training, strikeback,
and an actual Zwerg-versus-Wuker attack driven by the unmodified shipped `fight.tcl` table.

`get_attack_pos` covers the floor place-finder envelope, reachability rejection, and the native
wall-side offset. Action selection now applies the table's exact pose, wall, horizontal, vertical
pair, minimum-experience, response-type, and lethal filters. Fight animation names are qualified
with the fight database before reaching the generic logical-state resolver, so a movement or
climbing slot cannot swallow a fight row with the same bare name.

## Landed world behavior and opponent AI

`Runtime/Tcl/Engine/AiSystem.cs` now hosts the shipped `Scripts/ai/std_ai.tcl` policy instead
of replacing it. Each of the eight owners has an isolated Tcl global scope, and enabled owners
run on the native staggered cadence reconstructed from `FUN_00424680`: once every 50 simulation
ticks, with a six-tick owner offset. The system exposes the population, intruder, invention,
production-place, build-location, and planner-field commands consumed by the original script.

Theft follows the original `beamto_inv` notification seam. A normal first and second offence
produce `Clanstealwarning` and `Clanwarning1`; the third produces `Clantoenemy`, changes both
diplomacy directions to enemy, and closes the offended clan's doors. A boxed-item theft counts
as two offences, so its second occurrence starts the war. The shipped `bIgnoreThieves` switch
is honored for unboxed thefts. Notices are retained by the backend, but their player-facing
`newsticker` presentation is implemented by the later story/UI checkpoint; populated-campaign
wording and frequency remain manual-verification items.

Opponent facts use the native population-cluster threshold of 20 world units and the original
distance metric. Foreign populations require the original FoW visibility threshold when live
FoW data is present; owned boxed productions are excluded. The unchanged policy can now detect
intruders, issue real `evt_task_attack` orders, query live production catalogs and tasks, choose
possible inventions, and write buildup/pack planner fields. Building-location validation shares
the player's terrain, collision, and order-ghost placement laws. Location search follows the
native horizontal, vertical, and depth sampling order, sorts by squared distance, and retains a
bounded nearest-result set for managed-runtime safety.

The diplomacy table defaults to neutral off-diagonal/own diagonal, uses enemy for the world
owner, and `set_diplomacy` writes one direction exactly like `FUN_00669360`. The opponent policy
explicitly changes both directions when war begins. `check_energy` now shares the native
owner/class/range/store law with the player production dispatcher; see `ENERGY_SYSTEM_RE.md`.

`AiSystemTests` covers owner-scope isolation, native cadence, both
theft escalation paths, population/FoW facts, defensive attack orders, production catalogs and
task counts, inventions, planner writes, collision rejection, location ordering, and a full
original `txtinit`/`animinit`/`classinit` load using a real `Feuerstelle` catalog. The
stand-off regression case carries a shipped AI intruder decision through attack, victim defend,
reciprocal damage, death, and fight-loop exit.

Remaining manual verification is presentation-facing: the repaired clips and facing must be
observed in Play, along with death/shatter presentation and opponent behavior in a populated
campaign. The command-click ownership integration described in the final subsection below is a
separate, self-contained patch.

## End-to-end fight-loop derivation

### Action-selection tick and turn order

The shipped state machine is still the scheduler. `genericfight.tcl:4-39` validates the target,
checks death/exit, and refuses to advance while `AttackInProgress` is set. Its phase ladder takes
out weapons, approaches, installs the fight idle, and performs first-strike handling before the
melee call at `genericfight.tcl:41-75,130-172`. A successful `attack <handle>` result immediately
calls `fight_notifytarget` (`:179-218`); that queues `evt_task_defend` unless the target is trapped
(`:700-728`).

The native resolver then performs this order:

1. `005bf660_FUN_005bf660.c:211-258` calls `005bf300` before row selection. If the requested
   defender already owns an attack action, `005bf300:47-154` searches for a nearby living free
   defender; with none, the command reports `no_free_defender`. It does not overwrite the busy
   defender's action.
2. It computes both horizontal masks and the floor-facing error, then performs one coordinated
   attacker/defender selection (`005bf660:319-337`).
3. It creates the dedicated attacker and defender actions and installs the attacker first
   (`005bf660:557-677`), then the defender (`:719-750`).
4. It queues the already-calculated hurt event on the defender at the fixed delay
   (`005bf660:699-706`). Attacker death or later action replacement cannot recall that event.

The port previously omitted step 1, installed the defender first, and allowed a fresh swing to
replace a still-running hurt/fall/get-up action. It now rejects a busy one-on-one defender,
installs the attacker first, and keeps the staged impact independent of later attacker state.
Nearby native retargeting remains represented by the existing `new_defender` script protocol;
the managed resolver currently returns `no_free_defender` rather than performing the native
spatial substitute search itself.

### Rotation and horizontal-direction law

`fight_rotate_towards` is authoritative for pre-swing rotation. It returns immediately on a wall,
forms a vector from `(x, 0, z * 0.5)`, subtracts `1.57`, normalizes to one turn, and either snaps a
sub-0.005-radian remainder or installs a rotate action while the state is disabled
(`genericfight.tcl:1044-1076`). The first-strike path invokes it before waiting
(`genericfight.tcl:139-152`); later resolver misses return through the same rotation/wait path
(`genericfight.tcl:245-280`).

The native melee gate uses the same half-height geometry. `005bf660:319-336` computes the wrapped
heading error and rejects only values strictly greater than `_DAT_00737208` (15 degrees). The gate
is bypassed for wall actions and for the command's authored force flag. The port now shares one
target-heading function between the gate and direction selection, honors the force flag, and does
not apply the floor gate on a wall.

`005bf080_FUN_005bf080.c:20-34` divides floor-relative bearing into front (less than 45 degrees),
right/left (less than 135 degrees, selected by sign), and back. Its wall branch chooses the
dominant relative `x/y` axis (`:35-64`). These masks now filter both attack and response rows. The
EditMode angle table covers all four cardinal headings plus `+/-14.99` accepted and `+/-15.01`
rejected, using the authored `z * 0.5` coordinate law.

### Fight-row selection and animation-bank dispatch

The row schema is explicit in `fight.tcl:22-34`: class, pose, action type, vertical mask, horizontal
mask, collision flag, animation, strength, and minimum experience. Floor idle/attack rows and the
wall-only `kletter*` rows are separate (`fight.tcl:36-39,77-96`); the same separation exists for
Zwerg hits (`:130-191`) and Wuker attacks/hits (`:217-245`). Numeric collision value `2` is used by
real heavy-hit rows (`:160,177`), so parsing it as false was incorrect; every non-zero integer is
now preserved as set.

Attacker selection requires exact class, pose, wall flag, minimum experience, compatibility, and
horizontal-mask intersection (`005bdd20_FUN_005bdd20.c:46-82`). There is no empty-pose wildcard.
Defender selection scans the response types together, uses `exp_F_Defense`, requires exact
class/pose/wall, intersects the defender horizontal mask and the attack/response vertical masks,
and applies collision clearance (`005be480_FUN_005be480.c:61-217`). The old port selected
ward/dodge first, hit second, ignored all direction masks, and retried with an empty pose. That
made wall climbing rows and unrelated reactions eligible in floor combat. Selection now uses the
native exact masks and one combined ward/dodge/hit pool; choosing a dodge is the avoidance result,
instead of applying a second invented dodge roll. Per-animation world-space clearance for rows
whose collision flag is set is the one remaining low-level selector delta: the native offset
builder at `005be480:163-203` depends on animation transform data not currently carried by
`ActionDef`.

Fight animations do not enter the movement-state slot resolver. `005bf660:620-670` copies the
selected row's resolved database/animation ids into `CAct_Attack`, and
`005bdf80_FUN_005bdf80.c:18-73` writes those concrete ids to the action. The fight database overrides are declared by
`fight_map_obj2db` (`fight.tcl:3-20`); otherwise the object's own database is used. The port now
qualifies every selected name as `<fight-db>.<animation>` before creating its generic `AnimAction`.
That prevents `DigglesAnimController.ResolveState` from interpreting a fight animation as a
logical walk/climb state or inheriting a movement-bank fallback. Floor Zwerg/Wuker coverage pins
the resulting `mann.*`/`wuker.*` ids and rejects `kletter` clips.

### Knockdown, hurt, and get-up continuity

The apparent multi-part reactions are one authored fight action. Examples are the light, medium,
heavy, and death `*_get_*` rows in `fight.tcl:133-191` and `:228-242`; direct inspection of
`data/3db/mann.3db` and `data/3db/wuker.3db` shows complete reaction records (6/9/23/25 frames),
including the heavy down/get-up sequence. The native `CAct_Attack` stays in the object action slot
for that complete record. Its tick/completion family (`00408ac0`, `00408d00`, `00408e50`, and
`00408fc0`) hands off the explicit next action/callback without inserting a generic idle. The
action factory also distinguishes `attack`, `anim`, and `fall` (`00405e10_FUN_00405e10.c`); fight
rows are not mover-state requests.

Three port divergences combined into the visible snap: a busy fighter's action could be replaced,
the bare animation could resolve through a movement bank, and a newly-created generic animation
did not begin until its first later simulation tick. Fight actions now hold the busy slot, use a
qualified literal clip, and receive a zero-time priming tick at installation. The regression rig
deliberately maps the bare reaction name to a climbing clip; the fight-qualified name bypasses it,
the second swing returns `no_free_defender`, the same action instance and restart count survive,
and completion holds the authored final frame with no idle insertion.

### Defender event and opponent-AI cadence

The victim response is event-driven, not a resolver-side diplomacy shortcut. A successful attack
queues `evt_task_defend` (`genericfight.tcl:203-218,700-728`). The unchanged handler rejects only a
trapped victim or a current user walk, clears work, sets the attacker and offensive behavior, sets
`approach=0`, and calls the victim's `fight_startfight`
(`z_events.tcl:1623-1663`). That second `fight_startfight` is what writes reverse diplomacy
(`genericfight.tcl:1273`).

Opponent policy is intentionally coarser. Each policy pass refreshes intruders, filters enemies,
then calls `fight_ai` (`std_ai.tcl:304-320`). Defensive mode dispatches interceptors for enemies in
the base (`:661-694`); enemy filtering requires hostile diplomacy, life, and visibility
(`:744-755`). Interceptors query owned Zwerg within 70 units and either queue attack inside eight
units or walk toward the target (`:912-952`); the shared `order_attack` helper has the same
eight-unit split (`:483-493`). The managed five-second/50-tick cadence with owner staggering is
therefore native. In addition, `evt_task_attack_proc` deliberately rejects a non-user attack for
15 seconds after that gnome's last user command (`z_events.tcl:1590-1595`); the defend event does
not apply that cooldown (`:1623-1663`). Before the next policy pass, during that user-order grace,
or before an approaching attacker has produced the first successful `attack` result, a defender
can visibly wait; after an eligible seam there must be a real order.

The per-owner error guard was re-audited: it aborts only the failing owner's current pass
and does not suppress healthy owners or future passes. Entity states are independent of that
guard: `DigglesGameSim.cs:413-469` thinks every enabled entity without an owner test, while the
opponent policy is a later separate call at `:494`. Thus an AI-owned gnome's `fight_dispatch`
continues between policy passes. No `AiSystem.cs` change was justified. The new headless stand-off
starts with the shipped AI intruder query, observes its queued attack, observes the victim's
`evt_task_defend` and reverse diplomacy, records damage to both actors, and continues through one
death and the survivor leaving `fight_dispatch`.

### Explicit attack-order eligibility and remaining click integration

`is_attackable` is the command law (`eventgen.tcl:474-510`): same-owner targets are rejected except
Geisel; owner `-1` is attackable only for `monster` and `protection`; a normal click attacks an
enemy but not a friend/neutral; Shift reverses friend/neutral eligibility, returns `2` for an
enemy, and rejects Geisel. The gnome, production, and monster command branches consume that
predicate at `eventgen.tcl:143-194,369-377,448-470`. EditMode coverage now pins the full table,
including ownerless monster/protection/production targets.

The ignored foreign-gnome order is upstream of this law. `ClickToMoveService.cs:472-489` currently
recognizes exact `Zwerg`/`Frau`/`Baby`, selects a local one, but unconditionally returns for a
foreign one. Consequently the selected local fighters never reach the unchanged event-generator
loop at `ClickToMoveService.cs:555-600`, even when `is_attackable` says the foreign gnome is an
enemy. This file was out of scope for the fight-loop repair, so it was left unedited. The
required integration patch is precise: return inside the local-owner selection branch only; let
foreign gnomes fall through to `EvtGenRun`/`EvtGenFire`. Its regression table should retain local
plain/Shift selection, assert an enemy foreign gnome produces `evt_task_attack`, assert a neutral
foreign gnome walks/selects per `eventgen.tcl`, and retain the foreign `Baby` convert/walk laws.
