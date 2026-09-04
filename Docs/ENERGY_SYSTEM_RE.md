# Energy system reverse engineering and port contract

> Implemented from the original Tcl data and the decompiled native EnergyMgr.
> This document owns the live power-production, distribution, consumption, and source-load law.

## Original data model

Energy producers are ordinary `energy` objects with five native values: energy class, range,
current store, maximum store, and current source load. Production places use energy class and
consumption. The shipped scripts configure them through:

- `set_energyclass`, `set_energyrange`, `set_energystore`, and `set_energymaxstore`;
- `set_energyconsumption` on powered production places;
- `get_energystore` / `set_energystore` for generator refill work;
- `get_energysourceload` to switch generator animations on while a consumer is active.

The important source classes are `Laufrad`, `Wasserrad`, `Dampfmaschine`, and `Reaktor`.
`Laufrad` consumes `Pilzhut`, `Dampfmaschine` consumes `Kohle`, and `Reaktor` consumes
`Kristall` through their unchanged production scripts. `Wasserrad` recharges itself only while
`isunderwater` succeeds. The context-owned fluid system now supplies that query, so a water wheel
can perform its unchanged Tcl refill loop when its position is inside a live fluid cell.

## Native availability law

`FUN_005edcd0` returns ready immediately when the consumer's consumption at production-record
offset `+0x130` is zero or negative. Otherwise it calls the EnergyMgr source scan in
`FUN_005b93d0`. A source qualifies only when all of the following are true:

1. source owner equals consumer owner;
2. source energy class is greater than or equal to consumer energy class;
3. the source production record is enabled (`+0x64`) and the object has the built flag
   `0x400000`;
4. source store (`+0x134`) contains at least one complete consumer portion;
5. horizontal squared distance is strictly less than source range squared.

The scan uses native X/depth coordinates and ignores height. It does not require exact class
equality: a higher-era source can supply a lower-era consumer. `check_energy` exposes this
non-mutating availability test to the opponent policy in `std_ai.tcl`.

## Acquisition, load, and release

Actual consumption is separate from availability. `FUN_005edd80` delegates to
`FUN_005b9510`, registered to Tcl as `get_energy` by the native `LockEnergy` handler. The manager:

- returns success without another charge when the consumer already owns a load record;
- chooses the qualifying source with the greatest current store, preserving source order on a tie;
- deducts exactly one `energyconsumption` portion immediately;
- records `{consumer, amount, 6000 logic ticks}` on the source.

The native game-thread pass decrements the lease once per 10 Hz `SrvDoThink`. Expiry removes the
load without refunding energy. `release_energy` removes the consumer's record earlier, also
without a refund. The source's `+0x13c` load is the sum of the active records' amounts;
`get_energysourceload` returns that value. The shipped scripts only branch on zero versus
positive load even though the original command help describes it as activity percent.

## Port architecture

`EnergySystem` is context-owned and contains only session state. Per-entity configuration and
store values live on `DigglesTclEntity`; active leases live in `EnergySystem`. It registers real
handlers for the complete command family:

- `check_energy`, `get_energy`, and `release_energy`;
- `get_energystore`, `set_energystore`, and `get_energysourceload`;
- `set_energyclass`, `set_energyconsumption`, `set_energymaxstore`, and `set_energyrange`.

`ProdDispatcher` applies the native availability gate before emitting invention, material-haul,
or normal production work for a place. It acquires one portion when a production worker is
actually assigned and releases the load when the worker claim ends or fails. This keeps player
and opponent readiness on the same EnergyMgr law.

Entity deletion removes every lease involving that source or consumer. The runtime exposes all
entity energy fields to the native `.sav` mapper.

## Native save persistence

`FUN_00557b00` serializes `EnergyMgr` between marker 8 and marker `0x50`. The top-level counted
table preserves source order. Each source record contains the stable source object reference,
energy class, range, and an ordered nested vector of active leases. Each lease contains the stable
consumer object reference, charged amount, and exact remaining 10 Hz countdown. Active leases are
therefore continuation state, not disposable production bookkeeping.

The typed `.sav` document retains range and amount as exact float bits. Transactional preflight
requires unique source identities, valid source and consumer references, and finite range and
amount values. Inactive candidate reconstruction restores source class/range, source order, lease
amounts, and remaining ticks only after every entity shell exists; it does not evaluate Tcl or
acquire/deduct energy again.

Current store, maximum store, consumption, enabled/built state, and other production fields are not
part of this global manager table. They remain in class-specific object bodies. The global
`EnergyMgr` slice is now exact, but complete energy continuation still depends on typing and
canonically writing those object-body fields.

## Verification

`EnergySystemTests` pins command state, zero-consumption behavior, owner and class matching,
built/enabled source requirements, store capacity, strict two-dimensional range, fullest-source
selection, idempotent acquisition, source load, release, 6000-tick expiry, and the real
dispatcher gate/acquisition path. Save-state coverage pins exact source order, configuration,
active leases, and countdown continuation. The typed `.sav` corpus and candidate-session tests pin
bounded parsing, identity validation, immutable staging, and side-effect-free restoration. A
populated-campaign manual pass must still verify generator refill work,
source animations, the first `Schmelze` progression loop, and water-wheel refill against a live
fluid volume. The combined energy/fluid behavior still needs populated-campaign Play verification.
