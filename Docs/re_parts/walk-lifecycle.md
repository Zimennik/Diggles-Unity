> **RE working note — port status is consolidated & kept live in `Docs/GNOME_BEHAVIOR_COMPLETE_RE.md`.** The decompile addresses/offsets/constants below are ground-truth LAW; the *port-gap / "our state" snapshots are the PRE-FIX analysis* — all such gaps were addressed in Phases 0-7 (DONE, see the parent doc §11). Note: `WalkClimbAction.cs` and `DigglesInputController.cs`, cited in older gap notes, were DELETED in Phase 7.

# AREA: walk-lifecycle — the `action … walk` ACTION lifecycle

Reverse-engineered strictly from the decompiled C in
`the decompile` and cross-checked against the
original Tcl in `the decompile`. Every claim cites
`file:line`. Inferences are explicitly labelled **INFERENCE**.

This area covers the **wrapper / lifecycle** layer of walking — NOT the inner
pathfinder (Calc1/MetaWalk/WalkFloor/Climb, documented in
`Docs/ORIGINAL_PATHFINDING_RE.md`). The wrapper is `CActWalk`, which owns a
`CWalkInfo` option block, lazily spawns a `CActWalk_AutoRetry` child, ticks it,
re-aims it when the gnome drifts, and stamps the per-frame **WalkResult** enum
that the Tcl AI branches on.

---

## 1. Overview — the object graph

Three native objects per active walk, plus the gnome it drives:

| Object | Role | vftable / ctor |
|---|---|---|
| `CActWalk` | the *action* object the action-stack ticks; holds the option block + the retry child | `CActWalk::vftable`, built in `FUN_00405e10` (RTTI id `0x24`) |
| `CWalkInfo` | the parsed-option block (`-target`, `-speedtype`, `-animsets`, callbacks, …) | `CWalkInfo::vftable`, ctor `FUN_00420520` |
| `CActWalk_AutoRetry` | the per-tick retry/segment driver; itself owns a `CAct_MetaWalk` | `CActWalk_AutoRetry::vftable`, built inline in `FUN_004075b0:55-67` |
| gnome (CObj/CChar) | the moved entity; `+0x248` = WalkResult, `+0x2b4` = "is-walking" flag | — |

### Pointer layout used below

- **`CActWalk`** (`param_1` of the dispatcher / tick):
  - `+0x08` walk-info ext / collision flag (set 0 in dispatcher `FUN_00407430:21`)
  - `+0x0c` → **gnome** pointer (`FUN_00406630:22` stores it; everywhere reads `*(param_1+0xc)`)
  - `+0x10` → finish-code Tcl obj, `+0x14` → break-code Tcl obj (`FUN_00406630:22-37`)
  - `+0x18` last result snapshot (`FUN_004075b0:117`)
  - `+0x1c` → **`CWalkInfo`** option block (`FUN_00407430:40-41`)
  - `+0x20` → **`CActWalk_AutoRetry`** child (lazily created; `FUN_004075b0:70`)
  - `+0x24` = **action status** (the local copy of WalkResult; mirrored to gnome+0x248)
- **gnome** (`*(CActWalk+0xc)`):
  - `+0x0c/0x10/0x14` = current world pos x/y/z (read at `FUN_004075b0:121-123`)
  - `+0x248` = **WalkResult** enum (what `get_walkresult` returns)
  - `+0x2b4` = **is-walking** byte (1 while a walk owns the gnome)
  - `+0x36c` = current sub-cell/tile context ptr (`FUN_00420840:1969`)
  - `+0x400` = "inside walk-timer callback" guard byte (`FUN_004075b0:188,193`)
- **`CWalkInfo`** (`+0x1c` of `CActWalk`; in `FUN_00420840` `iVar8`/`local_18`/`param_1`):
  field table in §4.

### Lifecycle at a glance
```
action <obj> walk "<options>" [finishcode] [breakcode]
  → FUN_00405e10 (string-dispatch "walk" → new CActWalk, RTTI 0x24)
  → FUN_00407430 (CActWalk::init: store gnome+codes, build+parse CWalkInfo,
                   seed gnome+0x248=2, gnome+0x2b4=1, action+0x24=2)
      ├ FUN_00406630   stash gnome ptr + finish/break Tcl objs
      ├ FUN_00420520   CWalkInfo ctor (all defaults)
      └ FUN_00420840   CWalkInfo option parser (+ animset resolve FUN_00422a50)
  ── then each frame the action-stack calls CActWalk::Tick = FUN_004075b0 ──
  → FUN_004075b0 (AutoRetry tick: lazily build child, re-aim, tick it,
                   on child-finish stamp WalkResult + fire walk-timer callback,
                   run -walktimercallback every N ticks)
  → FUN_00407b30 (CActWalk::Break — interrupt: action+0x24=1, gnome+0x2b4=0,
                   destroy retry child)
  → FUN_00407bc0 (CActWalk re-plan / restart: rebuild CWalkInfo, gnome+0x248=2)
```

---

## 2. Entry & dispatch

### 2.1 `FUN_00405e10` — action-name → action object factory

A big `strcmp` ladder over `*(param_2)+0xc` (the action-name string) that
news-up the right `CAct_*` subclass into `*param_1`. Walk is the **9th** branch
(`FUN_00405e10:500-499`): when the name == `&DAT_0071ccb8` (= `"walk"`), it
allocates RTTI id **`0x24`** (`FUN_006ba810(0x24)`, `FUN_00405e10:520`), zeroes
`puVar6[1,2,4,5]`, sets `*puVar6 = CActWalk::vftable` (`:481`), and clears
`puVar6[7]=0; puVar6[8]=0` (`:482-483`) — the retry-child ptr (`+0x20`) and
status (`+0x24`) start null/0.

Other branches in the same ladder (for context — the action vocabulary):
`"wait"`(0x1b)/`"slide"`(0x1c)/`"anim"`(0x1d)/`"crawl"`(0x1e)/`"rotate"`(0x1f)/
`"attack"`(0x20)/`"attackballistic"`(0x21) plus `"fall"`(0x25) and one more
(0x26) (`FUN_00405e10:50-603`). Walk is plain ctor (no extra init beyond zeroing).

### 2.2 `FUN_00407430` — `CActWalk::init` (the dispatcher / set-up)

`__thiscall FUN_00407430(CActWalk* this, gnome, srcCtx, finishObj, breakObj, codeObj)`.

Exact effect (`FUN_00407430:21-65`):
1. Ref-bump the three incoming Tcl objs (`param_4/5/6`) (`:21-29`).
2. `FUN_00406630(param_2, param_3, param_4)` — stash **gnome ptr → this+0x0c**,
   finish-obj → +0x10, break-obj → +0x14, code-obj → +0x08, with refcount juggling
   (`FUN_00406630:14-37`). *(Note: the decompiled arg list is shifted; semantically
   this+0x0c becomes the gnome.)*
3. Build a fresh `CWalkInfo`: `FUN_006ba810(...)` (RTTI id for CWalkInfo) then
   `FUN_00420520` (ctor, §3) → store into **this+0x1c** with refcount swap
   (`:37-55`).
4. `FUN_00420840(param_2)` — **parse the option string into the CWalkInfo**
   (`:56`, §4).
5. If `CWalkInfo+0x7b4 != 0` (the `-randompath` flag, `:58`), seed the walk
   target from the gnome's current pos: copy gnome `+0x0c/0x10/0x14` → CWalkInfo
   `+0x7a4/0x7a8/0x7ac` (`:59-61`). So a random-path walk starts "from here".
6. **Seed the lifecycle state** (`:63-65`):
   - `this+0x24 = 2` (action status = WS_NOTFINISHED)
   - **gnome+0x248 = 2** (WalkResult = WS_NOTFINISHED — "walking, not done")
   - **gnome+0x2b4 = 1** (is-walking = true)
7. Ref-drop the three Tcl objs, free if dead (`:66-74`). Returns 1.

> **This step is the contract that the Tcl AI depends on.** The moment a walk is
> issued, `get_walkresult` returns `2` (busy) and stays `2` until the child
> finishes. See `genericfight.tcl:878` (`if {[get_walkresult $obj]==2}` = still
> walking) and `Docs/ORIGINAL_PATHFINDING_RE.md` §3.4.

---

## 3. `FUN_00420520` — `CWalkInfo` constructor (option DEFAULTS)

Sets every field to its default before the parser overrides. Key defaults
(`param_1` = CWalkInfo; indices are `param_1[i]` = byte offset `i*4`):

| Field (offset) | Default | Meaning (see §4) |
|---|---|---|
| `param_1[2]` = +0x08 | `-1` | (target-dummy index `local_24` analogue) |
| `+0x1ec`,`+0x3d0`,`+0x5b4` | `-1`,`-1`,`0` | animset slots 1/2/3 (`[0x7b]=+0x1ec`? see note) |
| `+0x79c` (`0x1e7` byte) | `1` | **-waving** default = 1 (`:54`, `0x1e7*4=0x79c`) |
| `+0x7a0` (`0x1e8`) | `2.0` (`0x40000000`) | **-stopat** default 2.0 (`:55`) |
| `+0x7a4/0x7a8` (`0x1e9/0x1ea`) | `-1000.0` (`0xc47a0000`) | target X/Y sentinel "unset" (`:56-57`) |
| `+0x7ac` (`0x1eb`) | `14.0` (`0x41600000`) | target Z default (`:58`) |
| `+0x7c0/0x7c4` | `0` | objbreak class/dist (`:59-61` region) |
| `+0x7c8` (`0x1f2`) | `-1.0` (`0xbf800000`) | **-targetdist** default −1 (disabled) (`:64`) |
| `+0x7cc..` | `0` | **-climbrot** coords (`:65`) |
| `+0x7d4` (`500`=0x1f4) | `π` (`0x40490fdb`) | climbrot default (`:66`) |
| `+0x7dc` (`0x1f5`) | `0` | **-randompathwalldist** (`:67`) |
| `+0x7e8` (`0x1fa`) | `0` | **-speedtype** default 0 (auto) (`:62`) |
| `+0x7f0`/`+0x7f4` (`0x1fc/0x1fd`) | two RTTI-0x19 objs, `[1]=0x14, [2]=-1` | **walk-timer** state objects (`:37-51`) |
| `+0x7c4`? (`0x1f1`) | `-1.0` (`0xbf800000`) | (`:86`) |
| `+0x7ec`(`0x1fb`) | Tcl str obj from `PTR_0071ce50` (empty) | **-walktimercallback** body (`:72-83`) |

Also (`:84-85`) it registers two named callbacks with the interp:
`FUN_006b9200("walk_collision_callback")` and `FUN_006b9200("obj_use_callback")`
— i.e. the default `-walkcollisioncallback` / use-object callback names.

> Offsets here are reconstructed from the index arithmetic (`param_1[0x1e8]` =
> byte +0x7a0). The crucial defaults the port must reproduce: **-canclimb is NOT
> set here to 1** — it is a *byte* default of 0 unless the parser writes it; see
> §4 note. **-speedtype default = 0** (auto walk→run by distance). **-stopat
> default = 2.0**. **-targetdist default = −1** (off). **-waving default = 1**.

---

## 4. `FUN_00420840` — `CWalkInfo` option parser (THE option table)

A flat hand-unrolled option loop: it walks `param_3` (the option Tcl-list) two
elements at a time (`local_28 = i+2`, `FUN_00420840:143-144`), `strcmp`s the key
against a fixed cascade, and on match reads the value with the right
`Tcl_Get*FromObj`. The cascade order **is** the precedence and is exactly:

```
-target → (DAT_0071d65c) → -object → (DAT_0071d66c) → -dontstop → -canclimb →
-stopat → -useobjects → -speedtype → -randompath → -waving → -randomz →
(DAT_0071d6cc) → -forcetarget → -targetdist → -randompathwalldist →
-walktimercallback → -walktimercounter → -walkcollisioncallback → -objbreak →
-animsets → -climbrot
```
(literals proven at `FUN_00420840:150,296,438,512,586,660,734,803,872,946,1089,
1163,1237,1311,1393,1462,1539,1666,1808`; three are `&DAT_*`, see note.)

### 4.1 Option → field table (proven)

| Option | Value type | Writes (CWalkInfo offset) | Cite |
|---|---|---|---|
| `-target` | `{x y z}` (3-vec via `FUN_006bafe0`) | `+0x7a4/0x7a8/0x7ac` | `:210-213` |
| `-object` | obj ref → `FUN_005c8310` | `local_20` (resolved obj) + sets byte `+0x7bf = 1` | `:357-362` |
| `-dontstop` | bool | byte `+0x79d` | `:494-503` |
| `-canclimb` | bool | byte `+0x7b0` | `:568-577` |
| `-stopat` | double | float `+0x7a0` | `:642-651` |
| `-useobjects` | bool | byte `+0x7b1` | `:716-725` |
| `-speedtype` | int | `+0x7e8` (0 auto / 1 force-walk / 2 force-run) | `:790-794` |
| `-randompath` | int | `+0x7b4` | `:859-863` |
| `-waving` | bool | byte `+0x79c` | `:928-937` |
| `-randomz` | int | `+0x7b8` | `:1002-1006` |
| `-forcetarget` | bool | byte `+0x7bf` | `:1071-1080` |
| `-targetdist` | double | float `+0x7c8` | `:1218-1228` |
| `-randompathwalldist` | double | float `+0x7dc` | `:1293-1302` |
| `-walktimercallback` | Tcl obj | `+0x7ec` (callback body, refcount-swapped) | `:1362-1376` |
| `-walktimercounter` | int | `+0x7e0` (callback period N ticks) | `:1449-1453` |
| `-walkcollisioncallback` | str | registers name via `FUN_006b9200` | `:1512-1529` |
| `-objbreak` | `{class dist}` | `+0x7c0` (class via obj `+0xdc`), `+0x7c4` (dist) | `:1592-1644` |
| `-animsets` | `{a0 a1 a2 a3}` | `+0x08`, `+0x1ec`, `+0x3d0`, `+0x5b4` (4 ints) | `:1721-1791` |
| `-climbrot` | `{x y z}` (3-vec) | `+0x7cc/2000/0x7d4` | `:1869-1872` |

### 4.2 The three `&DAT_*`-named options (NOT resolvable to literals from the C)

The string data labels `0071d65c`, `0071d66c`, `0071d6cc` are not inlined in the
decompile, so I read them by handler behaviour + the binary string table
(`re/notes/strings/Diggles.strings.txt`, which contains `-dummy`, `-digpos`):

- **`0071d65c`** (between `-target` and `-object`, `:226`): handler fetches a
  3-vector via `FUN_006bafe0(local_178)` and falls through (`:280-289`). A *coord*
  option. **INFERENCE: `-digpos`** (the only remaining coord-valued option name in
  the string table). Effect: stub coord fetch (no persistent write seen).
- **`0071d66c`** (between `-object` and `-dontstop`, `:371`): `Tcl_GetIntFromObj`
  → `local_24` (`:424-428`), default `local_24 = 0`. `local_24` is later used as a
  **link/dummy index** into the `-object`'s link table (`local_20[0xe7]`,
  `FUN_004059a0`, `:1923-1927`). **INFERENCE: `-dummy`** (work-dummy index).
- **`0071d6cc`** (between `-randomz` and `-forcetarget`, `:1015`): bool → byte
  **`+0x7bc`** (`:1073-1080`). `+0x7bc` is read in the AutoRetry tick as a gate
  that *disables the re-aim nudge* (`FUN_004075b0:126`). **INFERENCE: a "no
  re-aim / direct path" boolean** (literal name unresolved — candidate `-digpos`
  partner; mark UNRESOLVED).

> The prompt's option list mentioned `-dig`, `-digpos`, `-canswim`, `-counter`.
> **CORRECTION (proven):** this parser has **NO** `-dig`, `-canswim`, or
> `-counter`. The counter-ish option is `-walktimercounter` (`:1393`). `-digpos`
> exists in the string table but maps to the stub coord handler. `-canswim` is
> not a walk option at all in this function.

### 4.3 Animsets — slot meaning

`-animsets {a0 a1 a2 a3}` writes four ints (`FUN_00420840:1721-1791`):
- `a0` → CWalkInfo `+0x08` = **walk** animset id (gait 0)
- `a1` → `+0x1ec` = **run** animset id (gait 1) (`-1` ⇒ running forbidden)
- `a2` → `+0x3d0` = (climb/3rd) animset id
- `a3` → `+0x5b4` = (4th) animset id

### 4.4 Post-parse fixups (run once, after the loop — `FUN_00420840:1892-2014`)

1. **Random-path clamp** (`:1892-1898`): if `-randompath`(`+0x7b4`) set, clear
   `+0x7b1` (useobjects), and clamp `+0x7b8` (`-randomz`) to ≤ 15 (`0xf`).
2. **`-dummy`/`-object` link resolve** (`:1922-1945`): if `local_24 != -1` AND a
   `-object` was given, resolve the object's link table:
   `FUN_0040b550(obj[0xe7])` → `FUN_004059a0(obj[0xe8])` →
   `FUN_00408920(linkrec + 0x5c + local_24*4)` → `FUN_00408a10` returns a 3-vec
   scaled by `DAT_00736fcc` (xy) / `DAT_00736fe0` (z), then offset by the obj's pos
   (`obj[3/4/5]`), then re-projected via `FUN_00424120`. **This is how a work
   walk targets an object's specific work-dummy** (`-object <ref> -dummy <id>`).
3. **No-dummy target snap** (`:1901-1920`): else (no link), if `-forcetarget`
   (`+0x7bc`) is clear, clamp target Z if `> DAT_00737564` (set to `14.5` =
   `0x41680000`), and if the target X/Y lie inside the map AABB
   (`DAT_0077241c+0xa4..0xb0`), project the target onto the nav surface via
   `FUN_00423f20(tx,ty,tz)` → overwrite `+0x7a4/0x7a8/0x7ac` (`:1912-1918`).
4. **Stop-distance flag** (`:1947-1967`): from `-speedtype`(`+0x7e8`):
   - `0` ⇒ compute dist² from gnome to target (Z weighted by `DAT_0073708c`); if
     `> DAT_00737530`, set `+0x7bd = 0x0101` ("needs to move") (`:1948-1957`).
   - `1` ⇒ `+0x7bd = 0` (don't move). `2` ⇒ `+0x7bd = 1` (`:1959-1963`).
   - if `+0x7bd` set AND `+0x7be` clear ⇒ `+0x798 = 1` (`:1965-1967`).
5. **Diagonal-corner passability pre-check** (`:1969-2010`): when not random-path
   and the gnome's sub-cell context (`param_4+0x36c`) is valid, sample the four
   sub-cells around the target via `FUN_00592f10`/`FUN_00407e70`-style `+0x3c`
   lookups + `FUN_00423700`; sets `+0x7d8 = 1` only if all four corners pass,
   else 0 (a "can squeeze diagonally" gate).
6. `FUN_00422a50(param_4)` — **resolve animset NAMES → clip indices** (§4.5).

### 4.5 `FUN_00422a50` — animset name→clip-index resolution

Iterates 4 animset slots (`local_28 < 4`, stride `0x1e4`, `FUN_00422a50:43-220`),
each with up to `0x18` (24) clip slots (5 ints each: id, ?, scale `1.0`, scale
`10.0`, extra). For each it hashes a clip-name (`uVar17 = uVar17*9 + ch`, masked
`& 0x1f`, `:66-71`), binary-searches the per-block name table
(`iVar3+0xcdc + idx*0x100`), and fills the slot with the resolved clip record or a
"missing" sentinel (`-1,-1,1.0,10.0`, `:78-95`). Also fills default speeds when 0:
`0x3dcccccd` (0.1) / `0x3e19999a` (0.15) (`:174-185`). **This is the bridge that
turns the script's animset ids into the actual clip ids the MetaWalk plays.**

---

## 5. `FUN_004075b0` — `CActWalk_AutoRetry::Tick` (the per-frame driver) — COMPLETE

This is `CActWalk::Tick` (the action-stack calls it each frame). `param_1` =
`CActWalk*`. Returns `char` (0 = finished/stop ticking, nonzero = keep ticking).

### 5.1 Lazy child creation + bounds gate (`:43-102`)
```
if (this->retryChild (+0x20) == 0) { // first tick
  info = this->+0x1c; // CWalkInfo
  if (info->+0x7b4 == 0 && // NOT random-path AND
      target (info+0x7a4/0x7a8) is OUTSIDE the playable AABB
        (DAT_0077241c+0xa4..0xb0, margin DAT_00737330)) {
     this->+0x24 = 3;                             // action status = WS_NOPASSAGE
     gnome->+0x248 = 3;                           // WalkResult = WS_NOPASSAGE
     return 0;                                    // give up immediately
  }
  child = new CActWalk_AutoRetry (FUN_006ba810(0x6d)); // RTTI 0x6d
  child fields zeroed; this->+0x20 = child;
  FUN_0041fe30(child, gnome, 0); // bind gnome into child+0x0c/+0x04
  child->+0x14/0x18/0x1c = -1000.0 (0xc47a0000) // "no last pos yet" sentinel
  FUN_00407fb0(child); // build the inner CAct_MetaWalk
}
```
- The AABB reject (`:45-53`) is the **only** place a walk fails *before* even
  trying — an off-map target ⇒ instant WS_NOPASSAGE.
- `FUN_00407fb0` (the child's pathfind kick): if the gnome's pos changed since
  last tick (`:30-32`), it news a **`CAct_MetaWalk`** (RTTI `0x23`,
  `FUN_00407fb0:36-43`), binds the gnome (`FUN_0041fe30`), pulls the resolved
  path/clip via `FUN_004158a0`, sets the MetaWalk state (`+0x08=2`,
  `+0x1c=0xbf800000` etc.), and stamps **child+0x08 = 2** (busy). This is the
  re-plan: every time the gnome physically moves, the inner walker is rebuilt
  toward the (possibly moving) target.

### 5.2 Tick the child + handle finish (`:104-182`)
```
result = child->vtbl+0xc (child) // tick the AutoRetry/MetaWalk → char
if (result == 0) { // the inner walk finished THIS frame
   gnome->+0x2b4 = 0; // is-walking = false
   status = child->+0x08; // the child's terminal status code
   this->+0x24 = status; // mirror to action
   gnome->+0x248 = status; // mirror to WalkResult ← get_walkresult
   release child; this->+0x20 = 0; this->+0x18 = status;
   … the re-aim nudge block (§5.3) …
   … build a Tcl string for the result name (§5.4) …
}
```
So **on completion** the child's own status (4 = reached, 3 = no-passage, 5 =
event, 1 = aborted, 0 = error) becomes the gnome's WalkResult. While the child is
still running, WalkResult stays at the `2` seeded in `FUN_00407430` (busy).

### 5.3 The re-aim "nudge" (`:118-147`) — only on a finished-but-short walk
Guard conditions (ALL must hold):
- last-pos sentinel valid: `DAT_00737138 < info+0x7a4 && < info+0x7a8` (`:118-119`)
- residual dist² to target `> DAT_0073701c` (`:125`) **AND** `> _DAT_00737390`
- result was **not** 3 (no-passage) and **not** 5 (event) (`:125`)
- `info+0x7bc == 0` (the "no-nudge" flag clear, §4.2) (`:126`)
- `info+0x7bf != 0` (`-forcetarget`/`-object` was set) (`:127`)

When satisfied it samples passability around the target sub-cell via
`FUN_00407e70` (the `+0x3c` sub-grid byte lookup, `FUN_00407e70:12-13`), and if
blocked it offsets the target by `FUN_005cd840`/`FUN_005cd7e0` (a small rotation
by π about the target) before the next replan. **This is the "walk a little past
the wall / re-aim toward the actual reachable spot" behaviour** — it lets the
gnome retry an adjacent cell instead of failing outright.

### 5.4 Result-name string (`:149-180`) — the WS_* enum
After finishing it builds a Tcl string naming the result (used for logging /
callback). The `switch(this->+0x24)` is the **authoritative WS enum**:

| value | symbol (`FUN_006b9200(...)`) | cite |
|---|---|---|
| 0 | `WS_ERROR` | `:157` |
| 1 | `WS_WALKABORTED` | `:160` |
| 2 | `WS_NOTFINISHED` | `:163` |
| 3 | `WS_NOPASSAGE` | `:166` |
| 4 | `WS_TARGETREACHED` | `:169` |
| 5 | `WS_WALKEVENT` | `:172` |
| else | `"UNKNOWN RESULT (%d)"` | `:175` |

(Confirmed against `genericfight.tcl:878` comment `;# WS_NOTFINISHED` for value 2.)

### 5.5 The walk-timer callback (`:183-222`) — runs EVERY tick, fires every N
Independent of finish:
```
info->+0x7e4 += 1; // tick counter
N = info->+0x7e0; // -walktimercounter period
if (N != -1 && (info->+0x7e4 % N == 0 || result==0)) { // every N ticks OR on finish
   gnome->+0x400 = 1; // guard: inside callback
   ok = FUN_00555a00(info + 0x7ec); // eval -walktimercallback body
   if (!ok) info->+0x7e0 = -1; // callback errored → stop re-firing
   gnome->+0x400 = 0;
   res = Tcl_GetObjResult(interp); // read callback's return
   if (res == "1" or empty) return result; // "1" ⇒ keep walking
   if (res != "0") FUN_006baea0(...) // non-bool ⇒ log
   this->+0x24 = 5; gnome->+0x248 = 5; // ⇒ WS_WALKEVENT, stop
   return 0;
}
return result;
```
So a **`-walktimercallback`** returning `"0"` (or non-`1`) **interrupts the walk
with WS_WALKEVENT** — the mechanism scripts use to abort/redirect a walk
mid-stride (e.g. enemy appeared, target moved). Returning `"1"`/empty continues.

---

## 6. `FUN_00407b30` — `CActWalk::Break` (interrupt)

Called when the action is replaced/cancelled (`FUN_00407b30:7-20`):
- `this->+0x24 = 1` (action status = WS_WALKABORTED)
- gnome `+0x2b4 = 0` (is-walking = false) (`:11`)
- **gnome `+0x248 = this->+0x24` = 1** (WalkResult = WS_WALKABORTED) (`:12`)
- destroy the retry child: call its vtbl+0x08 (cleanup), release, null `+0x20`
  (`:13-19`).

So an interrupted walk leaves `get_walkresult == 1` (NOT 2, NOT 4). Tcl walk-fail
logic that tests `4 != [get_walkresult]` treats both 1 and 3 as "didn't arrive".

## 6b. `FUN_00407bc0` — re-plan / restart (gnome+0x248 re-seed)
A related entry (`FUN_00407bc0:55-66`) that, on `*param_2==1`, rebuilds the
`CWalkInfo` (`FUN_00420520`), re-derives the target from the gnome pos when
random-path (`+0x7b4`), and **re-seeds the busy state**: `this+0x24 = 2`,
**gnome+0x248 = 2**, gnome+0x2b4 = 1. This is the only *other* writer of
gnome+0x248 besides `FUN_00407430` (seed) and `FUN_004075b0`/`FUN_00407b30`
(terminal). Net rule: **gnome+0x248 is 2 while walking, set to the terminal WS_*
on stop, and set to 1 on break.**

---

## 7. Where gnome+0x248 is read/written (full map)

| Site | Op | Value | Cite |
|---|---|---|---|
| `FUN_00407430` (init) | write | 2 (WS_NOTFINISHED) | `:64` |
| `FUN_00407bc0` (re-plan) | write | 2 | `:65` |
| `FUN_004075b0` (AABB reject) | write | 3 (WS_NOPASSAGE) | `:51` |
| `FUN_004075b0` (child finished) | write | child status (0/1/3/4/5) | `:109` |
| `FUN_004075b0` (timer-cb abort) | write | 5 (WS_WALKEVENT) | `:216` |
| `FUN_00407b30` (break) | write | 1 (WS_WALKABORTED) | `:12` |
| `get_walkresult` (Tcl cmd) | read | returns `*(gnome+0x248)` | (engine; `Docs/ENGINE_TCL_API.txt`) |

Other writers found by grep (`0x248` across `_global`): `FUN_0068fb80`,
`FUN_005d0ce0`, `FUN_00417de0` — these are non-walk action set-ups that also stamp
a status on the gnome (e.g. attack/anim actions reusing the same field); out of
scope here but they confirm `+0x248` is the shared "current action result" slot.

---

## 8. Constants table

| Name | Hex | Dec / meaning |
|---|---|---|
| CActWalk RTTI id | `0x24` | 36 (`FUN_00405e10:520`) |
| CAct_Fall RTTI id | `0x25` | 37 |
| CAct_MetaWalk RTTI id | `0x23` | 35 (`FUN_00407fb0:36`) |
| CActWalk_AutoRetry RTTI id | `0x6d` | 109 (`FUN_004075b0:55`) |
| CWalkInfo timer-obj RTTI id | `0x19` | 25 (`FUN_00420520:37`) |
| WS_ERROR | `0` | walk result enum |
| WS_WALKABORTED | `1` | (break) |
| WS_NOTFINISHED | `2` | (busy — the seed value) |
| WS_NOPASSAGE | `3` | (no path / off-map) |
| WS_TARGETREACHED | `4` | (arrived) |
| WS_WALKEVENT | `5` | (timer-callback aborted) |
| -waving default | `1` | byte (`FUN_00420520:54`) |
| -stopat default | `0x40000000` | 2.0f (`:55`) |
| target X/Y "unset" sentinel | `0xc47a0000` | −1000.0f (`:56-57`) |
| target Z default | `0x41600000` | 14.0f (`:58`) |
| -targetdist default | `0xbf800000` | −1.0f = disabled (`:64`) |
| climbrot default | `0x40490fdb` | π (3.14159) (`:66`) |
| -randomz clamp | `0xf` | 15 (`FUN_00420840:1896`) |
| target-Z clamp value | `0x41680000` | 14.5f (`FUN_00420840:1904`) |
| animset default speeds | `0x3dcccccd`/`0x3e19999a` | 0.1 / 0.15 (`FUN_00422a50:175,180`) |
| sub-grid base offset | `+0x3c` | passability byte block (`FUN_00407e70:13`) |
| link record stride | `0xe8` | 232 (`FUN_004059a0:11`) |

### CWalkInfo field offsets (byte, from base)
`+0x08` walk animset · `+0x1ec` run animset · `+0x3d0` climb animset · `+0x5b4`
4th animset · `+0x798` move-needed · `+0x79c` -waving · `+0x79d` -dontstop ·
`+0x7a0` -stopat · `+0x7a4/0x7a8/0x7ac` target xyz · `+0x7b0` -canclimb · `+0x7b1`
-useobjects · `+0x7b4` -randompath · `+0x7b8` -randomz · `+0x7bc` no-nudge ·
`+0x7bd/0x7be` move-flags · `+0x7bf` -forcetarget/-object · `+0x7c0/0x7c4`
-objbreak class/dist · `+0x7c8` -targetdist · `+0x7cc/0x7d0/0x7d4` -climbrot ·
`+0x7d8` diag-squeeze · `+0x7dc` -randompathwalldist · `+0x7e0` -walktimercounter
· `+0x7e4` tick-counter · `+0x7e8` -speedtype · `+0x7ec` -walktimercallback.

---

## 9. Tcl ↔ native contract

- **Issue:** `action <obj> walk "<opts>" [finishcode] [breakcode]`. Opts is a
  flat Tcl list parsed by §4. The most-used in shipped scripts (counted in
  `original/data/Scripts`): `-useobjects`(20), `-randompath`(14), `-randomz`(12),
  `-waving`(5), `-forcetarget`(5), `-objbreak`(4), `-dontstop`(4), `-dummy`(3),
  `-climbrot`(3); `-target`/`-animsets`/`-speedtype`/`-canclimb` are the
  human-click & work-walk staples.
- **Poll:** `get_walkresult <obj>` → `*(gnome+0x248)`. Script idiom:
  `if {[get_walkresult $o]==2}` = still walking; `if {4 != [get_walkresult $o]}`
  = failed (used by `walkfail` to abandon a job). Seeded to 2 at issue, set to the
  WS_* on stop.
- **finishcode / breakcode:** stored in CActWalk `+0x10`/`+0x14`. Fired by the
  action-stack when the action ends / is replaced (the stack, not these
  functions, evals them — they own only the storage and the status).
- **-walktimercallback / -walktimercounter:** the callback body (`+0x7ec`) is
  `eval`'d every `+0x7e0` ticks (and on finish); returning non-`1` aborts the walk
  with WS_WALKEVENT (§5.5).

## 10. Neighbour-system interaction

- **Pathfinder (Calc1/MetaWalk):** built by `FUN_00407fb0` as a `CAct_MetaWalk`
  child each time the gnome moves; the AutoRetry only *manages* it (re-aim, retry,
  result). Path data fed via `FUN_004158a0`. The `+0x3c` sub-grid passability
  (`FUN_00407e70`) is shared with the nudge logic.
- **Animation:** `-animsets` ids → clip indices via `FUN_00422a50`; MetaWalk plays
  the resolved walk/run/climb clips. Gait is chosen by `-speedtype`
  (0 auto-by-distance / 1 walk-only / 2 run-now) and run-animset ≥ 0.
- **Work/AI dispatcher:** work walks use `-object <ref> -dummy <id>` (no
  `-target`); §4.4-2 resolves the object's work-dummy link to the actual target.
- **Map/world struct** `DAT_0077241c`: provides the playable AABB (`+0xa4..0xb0`)
  used for the off-map reject and the target snap.

---

## 11. PORT GAP ANALYSIS (vs current Unity port)

Port files:
`Assets/Diggles/Runtime/Tcl/Engine/DigglesTclEngine.cs`
(`case "walk"` ~2558, `BuildMetaWalkFromOptions` ~2765),
`Assets/Diggles/Runtime/Tcl/Engine/EntityActions.cs`
(`WalkResultAction` 73, `MetaWalkAction` 250),
`Assets/Diggles/Runtime/Engine/Actions/WalkClimbAction.cs`.

**Matches (faithful):**
- WS enum + the get_walkresult contract. Port `WalkResultAction`
  (`EntityActions.cs:73-90`) seeds `2` on first tick then writes `4`/`3` on
  finish — exactly the `FUN_00407430` seed + `FUN_004075b0` terminal stamp. The
  no-path branch returns `3` (WS_NOPASSAGE) (`DigglesTclEngine.cs:2834`),
  matching the AABB/off-map reject.
- Options parsed: `-target`, `-animsets` (a0=walk, a1=run), `-speedtype`,
  `-canclimb`, `-object`, `-dummy` (`DigglesTclEngine.cs:2777-2808`). `-canclimb`
  default true matches the parser's bool default.

**Gaps (parser):**
1. **Only 6 of ~22 options handled.** Missing entirely: `-dontstop`,
   `-useobjects`, `-stopat`, `-targetdist`, `-forcetarget`, `-randompath`,
   `-randomz`, `-randompathwalldist`, `-waving`, `-objbreak`, `-climbrot`,
   `-walktimercallback`, `-walktimercounter`, `-walkcollisioncallback`,
   `-digpos`, `-animsets` slots a2/a3. The port comment even says "Unhandled
   options (…) are ignored for now" (`DigglesTclEngine.cs:2764`). For faithful
   work/AI behaviour the high-value ones are **`-walktimercallback`/`-counter`**
   (mid-walk abort → WS_WALKEVENT; currently impossible) and **`-stopat`/
   `-targetdist`** (arrival radius — the port hardcodes `Arrive = 0.2f`,
   `EntityActions.cs:275`, ignoring the script's requested stop distance).
3. **`-speedtype` semantics partial.** Port maps 1=force-walk, 2=run-now
   (`MetaWalkAction` ctor `EntityActions.cs:288`). But default `0` "auto by
   distance" uses a hardcoded `RunDistance = 3f` (`EntityActions.cs:259`) rather
   than the native distance/`+0x7bd` move-needed test (§4.4-4). Acceptable
   approximation; flag as not-bit-exact.
4. **`-dummy` is a no-op** in the port (`DigglesTclEngine.cs:2808`): it does
   NOT resolve the object's work-dummy link (§4.4-2 `FUN_004059a0`/link table), so
   work walks target the object origin, not the specific work spot. Port TODO
   already notes "offset via get_linkpos (0 for now)".

**Gaps (lifecycle):**
5. **No re-aim / retry** (`FUN_004075b0:118-147`). The port builds one MetaWalk
   and on no-path immediately returns WS_NOPASSAGE. The native wrapper *retries*
   (replans every time the gnome moves; nudges the target off a blocked cell).
   Missing ⇒ a gnome that ends up just shy of a target (or whose target moved)
   gives up instead of edging in. Medium priority for "feels right" movement.
6. **No mid-walk replan on target motion** (`FUN_00407fb0:30-63` rebuilds the
   MetaWalk whenever the gnome moved). Port path is computed once
   (`DigglesTclEngine.cs:2828`). Fine for static targets; wrong for chasing a
   moving object.
7. **Break → WS_WALKABORTED (1).** The port's action-replace path fires the
   breakcode (`DigglesTclEngine.cs:2629-2634`) but does **not** set WalkResult to
   `1` (`FUN_00407b30:12`). Anything testing `==1` after an interrupt would
   misread. Low priority (scripts mostly test `==2`/`==4`).

**Non-issues:** the port's segmentation (floor→climb runs) corresponds to the
inner MetaWalk, which is a *different* area (`Docs/ORIGINAL_PATHFINDING_RE.md`)
and out of scope for the lifecycle wrapper.

---

## 12. Unresolved / gaps in the RE

1. **Literal name of `&DAT_0071d6cc`** (the byte-`+0x7bc` boolean option). Handler
   and effect are known (disables the AutoRetry re-aim nudge); the string label is
   not inlined in the decompile and not uniquely matchable in the string table.
   Candidates from the table: a `-nonudge`/`-directpath`-style flag. **UNRESOLVED.**
2. **`-digpos` (`&DAT_0071d65c`) full effect.** Confirmed coord-valued
   (`FUN_006bafe0`) but no persistent field write is visible in the decompile —
   likely consumed by a dig-walk variant elsewhere. Marked INFERENCE.
3. **CWalkInfo offsets `+0x7bd/0x7be/0x798`** (move-needed flags) — derived from
   the post-parse fixup arithmetic; their exact downstream consumers in MetaWalk
   were not traced here.
4. **`FUN_00407fb0` MetaWalk field semantics** (`+0x02/+0x07/+0x09` etc.) are set
   but their meaning belongs to the MetaWalk area, not traced.
5. **Finish/break-code firing site.** These functions *store* the codes
   (`FUN_00406630`) and set status; the actual `Tcl_Eval` of finish/break is done
   by the generic action-stack runner (not in this file set). Not located here.
6. **`FUN_00422a50` clip-name source** (`DAT_007735e8` table of 24 clip-name
   strings) — the names themselves (which 24 walk/climb clips) weren't dumped.
