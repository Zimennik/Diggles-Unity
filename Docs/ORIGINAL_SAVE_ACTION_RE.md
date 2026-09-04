# Original Save Action Reverse Engineering

## Status

The version `0xA5` action table is structurally verified across all 98 available native saves. The
corpus contains 118,374 action records, including 6,696 non-null actions. Types `2`, `4`, `7`, and
`9` do not occur in available fixtures; their layouts are instruction-derived rather than
fixture-verified.

The production reader exposes typed payloads and the immutable runtime stager indexes them by stable
owner ID. The side-effect-free candidate factory accepts every current action type. Candidate
installation remains deliberately separate and must happen only after identities, object state,
terrain, navigation, game time, and RNG are restored. Crawl, AttackBallistic, and Lore currently use
compatibility-only shells: all serialized fields survive runtime capture, but their unported native
movement/projectile/Lore dynamics remain an explicit candidate-session activation blocker.

## Evidence

- action table serializer: `FUN_00406fe0` at `0x00406fe0`;
- type factory/dispatch: `FUN_00406730` at `0x00406730`;
- shared action state: `FUN_004065c0` and `FUN_00406630`;
- object reference serializer: `FUN_00555d70`;
- current structural reader: `Assets/Diggles/Runtime/Save/OriginalSaveArchiveReader.cs`;
- port action runtime: `Assets/Diggles/Runtime/Tcl/Engine/EntityActions.cs`, `MoveAction.cs`,
  `TimedImpactAction.cs`, and `FightResolver.cs`.

## Table envelope

The current table after marker 12 is:

```text
repeat:
    Int32 ownerObjectId
    if ownerObjectId == -1: stop
    Int32 actionType
    action-specific payload
Int32 0x626C6133

Int32 auxiliaryCount
repeat auxiliaryCount:
    Int32 value
    Int32 nestedCount
    nestedCount * (8 * Int32)
    3 * Int32

Int32 0x626C6134
```

Every fixture-backed non-null action has the same object ID in its table envelope and common owner
field. Object references use registry IDs with `-1` for null.

Before `0xA5`, each record also uses sentinels `0x626C6130`, `0x626C6131`, and `0x626C6132`.
Only Anim, Rotate, and Lore deserialize legacy payloads. Other legacy types restore no action.

## Type mapping and corpus coverage

| ID | Native type | Count | Fixture-backed |
|---:|---|---:|---:|
| 0 | null | 111,678 | yes |
| 1 | `CAct_Wait` | 761 | yes |
| 2 | legacy `CAct_Slide` | 0 | no |
| 3 | `CAct_Anim` | 2,791 | yes |
| 4 | `CAct_Crawl` | 0 | no |
| 5 | `CAct_Rotate` | 38 | yes |
| 6 | `CAct_Attack` | 4 | yes |
| 7 | `CAct_AttackBallistic` | 0 | no |
| 8 | `CAct_Fly` | 498 | yes |
| 9 | `CAct_Lore` | 0 | no |
| 10 | `CAct_Walk` | 1,861 | yes |
| 11 | `CAct_Move` | 743 | yes |

Type 2 is retained losslessly in the document but restores no A5 runtime action.

## Common action state

Most active serializers contain the following state in this binary order:

```text
native string Argument
Int32 OwnerObjectId
native string FinishCode
native string BreakCode
Int32 StatusOrOpaque
```

`StatusOrOpaque` must remain raw. Many records contain `0xDFDFDFDF`; Walk uses this field as status,
while other action classes can leave it effectively opaque.

## Payload layouts

### Null and legacy Slide

Types 0 and 2 have no current payload. Both stage as no active runtime action, but type 2 must remain
lossless when an untouched document is rewritten.

### Wait

Serializer: `FUN_00417b30`.

```text
Common
Double AbsoluteDeadline
```

Restore `max(0, AbsoluteDeadline - RestoredGameTime)`. Do not restart the original argument
duration.

### Anim

Serializer: `FUN_004088d0`.

```text
Common
Double AbsoluteDeadline
```

Animation playback state is also serialized by the graphics managers. Restore remaining time and
coordinate it with restored clip progress rather than replaying the full clip.

### Crawl

Serializer: `FUN_0040cc70`.

```text
Common
Int32 RemainingTicks
Raw32 RandomYawOrPhaseBits
```

The port now retains both fields in `NativeCrawlActionSaveState`. Its compatibility shell advances
the saved 10 Hz countdown without moving the entity, selecting animation slots, consuming RNG, or
evaluating Tcl. This is sufficient for side-effect-free staging and lossless runtime capture, not
faithful live continuation.

### Rotate

Serializer: `FUN_004176d0`.

```text
Common
Int32 DirectionSign
Int32 LastAppliedDirectionOrState
float AngularStepRadiansPerTick
Int32 LeftAnimationSlot
Int32 RightAnimationSlot
Int32 TurnClipFrameCount
float AbsoluteTargetYawRadians
```

Restore from the entity's saved current rotation toward the absolute target. The port now uses an
explicit `RotateActionSaveState`; candidate construction retains the target, angular step, remaining
angle, and native direction/animation fields without starting a clip or advancing the turn.

### Attack

Serializer: `FUN_00409120`. Class fields precede Common.

```text
native string ChainedActionVerb
native string ChainedActionArguments
5 * Raw32
Double AbsoluteDeadline
3 * byte
Int32 CurrentFrameOrTick
4 * byte
5 * Raw32
byte
Common
```

The first four leading values are two animation range pairs used by `FUN_00408d00` and the cleanup
path in `FUN_004090f0`; the fifth remains raw. In the later five-value group, the first three values
are an impact offset and the final two bound the frame interval in `FUN_00408e50`. Transient pointers
at `+0x1c/+0x20` are cleared by the native loader and are not serialized as stable references.

The four available fixture records have zero impact offsets and frame flags. One record chains to
`wait 0.30000001192`; the other three have no chained verb. The restored port action retains every
raw field, advances the saved deadline/frame shell, and dispatches a chained verb only on the first
ordinary terminal simulation tick. Candidate creation and installation do not evaluate Tcl.

This native payload does not contain a defender ID or damage. Port-owned pending combat damage is
therefore not inferred from `CAct_Attack`; it remains the separately saveable structured
`TimedImpactAction` state described below.

The port now represents pending `TimedImpactAction` damage as a serializable value object containing
attacker ID, defender ID, damage, optional training skill, and remaining delay. Candidate creation
has no effect; the impact is claimed exactly once on its ordinary post-commit terminal tick.

### AttackBallistic

Serializer: `FUN_0040b400`. Class fields precede Common.

```text
Int32 TargetObjectId
Int32 Mode
Int32 scheduleCount
scheduleCount * Int32
2 * Raw32
Int32 transientHandleCount
transientHandleCount * Int32
2 * Raw32
byte
3 * float LastTarget
2 * byte
float TargetVerticalOffset
Double AbsoluteDeadline
Common
```

The second vector behaves like transient projectile/render handles and must not be treated as stable
Unity object references. The load planner validates `TargetObjectId` against the stable identity
domain but deliberately does not resolve this transient handle vector. The port compatibility shell
retains every field and advances only the saved absolute-deadline remainder; it does not recreate a
projectile or apply damage.

### Fly

Serializer: `FUN_00410840`.

```text
Common
3 * float Target
3 * float VelocityOrStep
3 * float PreviousPosition
float HeadingOrPhase
float AngularVelocity
float VerticalAccumulator
byte ArrivedOrCloseMode
Int32 TickCount
byte SwimMode
```

Restore the saved target and progress directly. Re-running random Fly option parsing would consume
RNG and choose a different target. The port's `FlyActionSaveState` now retains the already-selected
target and all saved motion/progress fields. Live continuation still uses the port's simplified
straight-line flyer rather than the complete native motion equations.

### Lore

Serializer: `FUN_00414ea0`.

```text
Common
float Gravity
float Friction
Int32 trailerCount
trailerCount * (Int32 TrailerObjectId, Raw32 TrailerOffsetOrDistance)
Int32 waypointCount
waypointCount * (8 * Raw32)
```

Each waypoint contains two three-component vectors and two optional scalars. The complete semantics
remain unnamed, so all eight values stay raw. The load planner validates each trailer object ID. The
port compatibility shell retains gravity, friction, trailer pairs, and flattened waypoint values,
but remains running without advancing native Lore motion or firing completion callbacks. Live
activation is blocked until those dynamics are ported.

### Walk

Serializers: `FUN_00407bc0` and `FUN_004234d0`.

```text
Common
repeat 4 animation banks:
    Int32 BankStateOrAnimSet
    repeat 24 animation slots:
        5 * Raw32
Int32 MoveNeededOrApproach
byte Waving
byte DontStop
float StopAt
3 * float Target
byte CanClimb
byte UseObjects
Int32 RandomPath
Int32 RandomZ
byte NoNudge
3 * byte move flags
Int32 ObjectBreakClassId
float ObjectBreakDistance
float TargetDistance
3 * float ClimbRotation
byte DiagonalSqueezeFlag
float RandomPathWallDistance
Int32 WalkTimerCounter
Int32 TickCounter
Int32 SpeedType
native string WalkTimerCallback
native string WalkCollisionCallback
native string ObjectUseCallback
```

The native loader rebuilds `CWalkInfo`, sets the action and entity walk result to 2, and marks the
owner as walking. The port should restore position, rebuild navigation, replan to the saved target,
preserve semantic flags/distances/speed/callbacks, and set `WalkResult = 2` before installation.
Literal restoration of native pathfinder internals is not appropriate for the Unity navigation
implementation.

The port now stages all four animation banks and their 480 raw slot values, the saved absolute
target, movement flags, distances, callbacks, counters, speed, and climb fields in
`WalkActionSaveState`. `RestoredWalkAction` does not plan during candidate construction; it rebuilds
the Unity route from the saved current position to the saved target on the first ordinary
post-commit tick. This avoids Tcl argument parsing and RNG consumption during load. Prepared action
metadata carries the native `WalkResult = 2` value for the future atomic commit installer.

### Move

Serializer: `FUN_00416ad0`.

```text
Common
3 * float Start
3 * float Target
3 * float Step
Int32 RemainingNativeTicks
```

Restore from the current saved position toward the target using remaining ticks. The port's
`MoveAction` needs a state constructor and capture contract.

## Document model requirements

The binary document remains independent from runtime action classes. It needs:

- an enum for all IDs `0..11`;
- raw native strings that retain declared length and bytes including the terminator;
- a raw 32-bit value type for unnamed integer/float bits;
- one sealed payload type per action ID;
- table owner ID and common owner ID validation;
- lossless auxiliary records after sentinel `0x626C6133`;
- source-version-aware legacy/current envelopes.

Unknown fields must remain raw rather than receive speculative semantic names.

## Runtime persistence boundary

Runtime actions require an injected persistence service and an explicit saveable action contract.
Wrappers must expose semantic state rather than arbitrary nesting:

- Walk wrappers become one semantic walk state;
- Sequence stores explicit resumable children;
- pending combat impact stores a value object, never a delegate;
- Climb and elevator actions need port-native persistence even though they do not map one-to-one to
  the native action IDs.

The existing `DigglesTclEntity.Action` field is not assigned by the current runtime. Action verb and
arguments must be stored explicitly or exposed through each action save state.

## Transactional restore law

1. Parse and validate the complete action table without live mutation.
2. Validate unique owner IDs, bounded vectors, common/table owner equality, finite required values,
   and all stable object references.
3. Stage object identities, object state, inventory/equipment, graphics, world, navigation, clock,
   and RNG.
4. Create runtime actions in a temporary owner-ID dictionary without callbacks or side effects.
5. Atomically install `ActiveAction`, finish/break code, and walk result during session commit.
6. Let the first normal simulation tick complete already-expired actions and run finish callbacks.

Loading must not invoke Tcl `CmdAction`, execute break/finish code, call `Tick(0)`, or rerun random
option parsing. `DigglesGameSim` already clears a completed action before evaluating its finish
callback, which provides the required post-commit callback boundary.

## Original-compatible writing policy

Direct encoding is practical for Wait, Anim, Rotate, Fly, and Move once port actions expose their
state. Walk is harder because the native format expects a large `CWalkInfo` block.

Untouched imported Walk payloads can be retained losslessly. Newly created port Walk state requires
an original-executable-validated canonical restart encoding. An expired native Wait whose finish Tcl
reissues the semantic action is a possible compatibility bridge, but it remains a proposal and must
not be used until tested in the original executable.
