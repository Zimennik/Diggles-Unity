# Native newsticker contract

This note records the executable-backed contract implemented by `NewsTickerSystem` and
`NewsTickerWidget`. It supersedes the older assumption that `newsticker` was only a harmless
notification stub.

## Native evidence

- Tcl handler: `FUN_0051acc0`.
- Window class constructor/destructor: `FUN_005190c0` / `FUN_00519170` (`ViWinNewsTicker`).
- Draw, input, history, queue, and tick paths: `FUN_00519300`, `FUN_00519800`,
  `FUN_00519bf0`, `FUN_0051a460`, and `FUN_0051a710`.
- Root-window construction: `FUN_00527fa0`.
- Original command documentation: `original/Doc/wiggles_tcl.htm:917-929`.
- Representative shipped callers: `baby.tcl:429-438`, `z_events.tcl:1828-2004`,
  `z_work_common.tcl:18-25/629-630`, `std_ai.tcl:451-480`, and
  `story.tcl:465-566`.

The command string is registered to `FUN_0051acc0`. Its neighboring read-only strings confirm
the `new`, `msg`, `mask`, and `-text` branches at virtual addresses `0x00726c48`, `0x00726c4c`,
`0x00726c60`, and `0x00726cc8`.

## Native state

`ViWinNewsTicker` keeps three distinct collections:

| Window offset | Meaning |
|---|---|
| `+0x44` | next notification id, initialized to 1 |
| `+0x58/+0x5c` | active 0x40-byte entries and count |
| `+0x60/+0x64` | retained 0x14-byte history entries and count |
| `+0x68` | category mask, initialized to all bits enabled |
| `+0x74/+0x78` | currently scrolling display queue and count |

Deleting or expiring an active notification does not delete its history copy. Changing a
history-enabled entry replaces the history copy with its latest text/color/click data; changing
`-history` to false removes that copy. This is why a message that already left the scrolling
strip remains clickable in the expanded history window.

`FUN_00519230` serializes the active array, retained history array, category mask and display
queue. It does not serialize `+0x44`, so the port derives the next collision-free ID from the
maximum ID present in those collections.

The serialized category word is not always a valid enum. `FUN_0051c0c0` and `FUN_00519ea0`
initialize a new active record without writing `+0x08`; only an explicit `-category` option writes
it. `FUN_00519bf0` then copies the same word to history. In the current 103-save corpus, 32 of 48
active records and 1,682 of 3,785 history records contain values outside 0..5, including the debug
fill `0xDFDFDFDF`. These values are native allocator residue, not format corruption. The save
compatibility model retains the exact raw word; the safe runtime maps an out-of-range word to the
documented/default `fight` category. A canonical writer must emit a valid semantic category after
runtime mutation.

## Command contract

The native subcommands and option names are case-sensitive. Category values are compared
case-insensitively.

### `newsticker new owner ?option value...?`

Creates an entry only when `owner` is the local player or `-1`. A foreign owner is silently
ignored before option parsing and returns an empty Tcl result. The first real entry receives id 1.

Binary defaults:

| Field | Default |
|---|---|
| text | `msg` |
| category | `fight` |
| color | `{255 255 255}` |
| priority | `1.0` |
| timeout | `30` seconds |
| history | true |

The shipped HTML says 60 seconds, but the executable passes `0x41f00000`, or 30.0, to the entry
constructor. The implementation follows the executable.

### `newsticker change id ?option value...?`

Updates an active entry and returns the id. Id `-1` creates a new default entry first. A missing
positive id is a valid no-op after its option values have been parsed. Native option failure
removes the active entry before returning a Tcl error; its already-created history copy remains.

Both `new` and `change` share the native option loop:

- `-text text`
- `-color {r g b}` with each channel clamped to 0..255
- `-priority float`
- `-category fight|progress|gnomecritical|gnomeinfo|material|mood`
- `-time float`
- `-click script`
- `-history boolean`

The common handler means the executable accepts `-click` on `new` and `-category` on `change`
even though the old HTML omits those combinations.

### `newsticker delete id`

Removes the active/display record and ignores trailing arguments. The independent history record
is retained.

### `newsticker mask ?token...?`

Each token is `+category`, `-category`, or `?category`. Query returns 1 when at least one queried
category is enabled, otherwise 0. Modifier-only and empty calls return 0. The native handler
validates the category after the first character but ignores an unknown prefix.

## Timing and presentation

Positive timeouts consume scaled game time. A value already at or below zero is not decremented
and therefore remains active indefinitely. Pause and a zero time factor freeze positive ticker
timeouts with the rest of the game-thread UI models.

`FUN_00527fa0` places the ticker at logical `(195,18)`, 28 pixels high, with its right edge 265
pixels from the screen edge. Expanding history adds `0xa2` (162) pixels. `FUN_00519300` supplies
the dark `0xbe141414` fill and translucent `0x78c8c8c8` border. The port uses those dimensions
and colors, scrolls the priority-ordered active entries, and loads the original
`GUI/buttons/LO_Pfeil_down.tga` and `LO_Pfeil_up.tga` for the history toggle.

## Port integration

- `DigglesTclContext.NewsTicker` owns all active/history/mask/id state.
- `DigglesGameSim` advances timeouts with scaled simulation time.
- `GameplayHud` creates the code-built widget without scene or prefab changes.
- Clicks evaluate the stored Tcl script, including history-only messages.
- The validated runtime capture/restore contract exposes active entries, history, mask, timers, click
  scripts, the compatibility display queue, and the next id alongside the document-window state.
  Validation runs before the live world graph is mutated. Restoring a save never evaluates a stored
  click script.
- `NewsTickerSystemTests` covers commands, defaults, owner filtering, options, masks, history,
  clicks, timeout behavior, and capture/restore validation.

The full native priority accumulation and pixel-by-pixel queue acceleration are presentation
details. The native archive nevertheless retains the current queue IDs and float offsets, so the
save model preserves them exactly. The current widget still uses priority ordering and continuous
scrolling rather than those native sub-pixel acceleration constants; any ticker mutation invalidates
the retained compatibility queue so stale offsets cannot be emitted as current state.
