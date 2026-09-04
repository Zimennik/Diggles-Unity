> **RE working note — port status is consolidated & kept live in `Docs/GNOME_BEHAVIOR_COMPLETE_RE.md`.** The decompile addresses/offsets/constants below are ground-truth LAW; the *port-gap / "our state" snapshots are the PRE-FIX analysis* — all such gaps were addressed in Phases 0-7 (DONE, see the parent doc §11). Note: `WalkClimbAction.cs` and `DigglesInputController.cs`, cited in older gap notes, were DELETED in Phase 7.

> **Correction** `gnome+0x36c` is the owner index, not a voxel-depth layer.
> `tile+0x08` stores eight owner orders. The native and port both resolve one BDB block per
> tile, and the standard mode-1 target-11 tunnel profile is implemented.

# tcl-dig-complete — `z_dignwalk.tcl` annotated in full + the native contract

> Ground-truth RE of `original/data/Scripts/classes/zwerg/z_dignwalk.tcl` (1117 lines), proc by proc,
> with the EXACT native command each line calls and its expected return. Cross-cited against the
> decompile (`re/decompiled/Diggles/_global/*.c`), `ENGINE_TCL_API.txt`, and the current port
> (`Assets/Diggles/Runtime/Tcl/Engine/DigglesTclEngine.cs`, `EntityActions.cs`).
>
> **Axis frame (ground truth, per task brief):** native **X = horizontal east** (walk axis); native
> **Y (tile rows) = the gameplay VERTICAL** (+y = down/deeper, gravity, climb axis); the BDB per-sub-cell
> nibble = **DEPTH into the screen** (the dig axis), NOT elevation. The packed-vector "z" the Tcl carries
> in `[lindex $pos 2]` is this depth nibble's engine value (`nibble*0.5+7`, range 7..14.5). Where the Tcl
> talks about "height" via `[lindex $pos 1]` (the **Y** component) it means the gameplay vertical; where it
> compares the **z** component it means depth-into-screen.
>
> File header (`:1-4`): three sections — (1) **dig** / Graben, (2) **walk** / Gehen, (3) **rotate** / Drehen.
> `walk_action` (the walk wrapper, `:21`) is physically listed first.

---

## 0. One-paragraph summary

`z_dignwalk.tcl` is the dwarf's **dig loop** plus every **walk/rotate** entry point. The dig loop is a
tasklist state machine: `dig_starttask` finds the wall edge (`get_digedge`) and queues a walk-to-standoff
(`walk_pos_dig`) + `dig_continue`; `dig_continue` picks the next diggable voxel (`dig_next`), gates it
(`dig_check` = ≥2 neighbour cells with `get_hmap<13`), optionally re-aims a Kristallstrahl laser, queues
the correct **dig animation** (`dig_anim`, which branches on tool = Spitzhacke / Presslufthammer / Kristallstrahl
and on pose = up/front/down on the floor, or up/right/down/left on the wall), and the anim's finish-callback
runs `dig_execute` (loops `dig_apply` to carve + grant exp/ore) then re-enters `dig_continue`. Every walk
proc funnels through `walk_action`, which selects the 4-slot animset `{slow fast dive standardwalk}` from
inventory/state/attributes and calls the native `action this walk "…"`. The **contract our natives must
satisfy** is the table in §3.

---

## 1. `walk_action` — the walk wrapper (`:21-129`)

`proc walk_action {paramstring {finishcode ""} {breakcode ""} args}` — the ONLY proper way to start a
gnome walk. It builds the 4-element `-animsets {slow fast dive standardwalk}` list and forwards to the
native `action this walk`.

Pseudocode (faithful to the lines):
```
globals: 18 ANIMSET_* ids + is_wearing_divingbell, event_repeat, is_escaping
# 1) ensure -speedtype is present (default 0); record its index for in-place rewrites
speedidx = lsearch paramstring "-speedtype"
if speedidx>=0: speedtype = paramstring[speedidx+1]
else: append "-speedtype 0"; speedtype=0; speedidx = end
# 2) repeated event ⇒ force run
if event_repeat: speedtype=2; lrep paramstring speedidx 2 #:40-43
# 3) underwater clip (:47-51)
dive = is_wearing_divingbell ? ANIMSET_DIVER: ANIMSET_SWIM
# 4) escape clip (:53-57)
fast = is_escaping ? ANIMSET_FLEE: ANIMSET_RUN
# 5) tiredness ⇒ slow walk; forbid run unless forced (:61-68)
if get_attrib(this,atr_Alertness) < 0.5:
    slow = ANIMSET_WALKTIRED;  if speedtype!=2: fast = -1
else:
    slow = ANIMSET_STANDARDWALK
# 6) long-range vehicles (catch{} guarded) (:72-98)
if inv_find this Hoverboard >=0: fast=ANIMSET_HOVERBOARD; speedtype 0|2 → rewrite to 3(autodist) or 2(repeat)
elif inv_find this Reithamster>=0: fast=ANIMSET_HAMSTERRIDE; (same speedtype rewrite)
# 7) Kiepe (pannier) (:101-109)
if is_wearing_pannier this: slow=ANIMSET_WALKWITHPANNIER; dive=same (unless divingbell); if fast==RUN: fast=-1
# 8) script wishes (:113-117)
if "-withbox" in args: slow=ANIMSET_WALKWITHBOX; fast=-1; dive=ANIMSET_WALKWITHBOX
# 9) strike state (:119-123)
if state_get(this)=="strike": slow=ANIMSET_WALKSTRIKE; fast=-1; dive=ANIMSET_WALKSTRIKE
# 10) emit (:125-128)
append "-animsets {slow fast standardwalk-as-dive? actually {slow fast dive ANIMSET_STANDARDWALK}}"
action this walk "$paramstring" $finishcode $breakcode
```
> NOTE the emitted list (`:125`) is `{$slow $fast $dive $ANIMSET_STANDARDWALK}` — slot0=slow(walk),
> slot1=fast(run), slot2=dive(swim), slot3=standardwalk fallback. `-1` in a slot = "this gait forbidden".

**Native calls & returns:** `lsearch/lindex/llength/concat/lrep` (pure Tcl); `get_attrib this atr_Alertness`
→ float; `inv_find this <class>` → int slot or −1; `is_wearing_pannier this` → 0/1; `state_get this` → state
name string; `action this walk "<opts>" <fini> <brk>` → starts the CAct_MetaWalk action (return ignored).

---

## 2. Section 1 — DIG (`:136-584`)

### 2.1 `dig_starttask {startpos}` (`:136-164`)
```
if startpos==0: current_digpos = get_pos this # else use the global current_digpos
startpos = current_digpos
gnome_announce_dig this $startpos # cosmetic HUD/marker
edgepos = get_digedge $startpos this # find the wall edge to start at
if vector_unpacky $edgepos > 0: # VALID? (y>0 = real edge, sentinel is -1)
    originalz = lindex edgepos 2                          # SAVE the depth nibble
    walkpos   = vector_fixdig $edgepos [get_pos this]     # reachable standoff toward the gnome
    lrep walkpos 2 $originalz                             # *** the "originalz lrep patch": restore depth ***
    tasklist_add this "walk_pos_dig {$walkpos} {$edgepos}"
    tasklist_add this "dig_continue"
    current_digpose = ""                                 # global: no pose yet
    return true
else: return false
```
> **The `originalz` lrep patch (`:148,150`)** is load-bearing: `vector_fixdig` recomputes the standoff's z
> from the floor heightmap (a WALK height), which would corrupt the **dig depth** carried in slot 2. The proc
> saves `edgepos`'s slot-2 (the depth nibble) and `lrep`s it back into `walkpos` so the subsequent walk keeps
> the correct dig-depth reference. **Port gap:** our `vector_fixdig` already returns a floor z; the Tcl runs
> unmodified so the `lrep` still overwrites slot2 with `originalz` — fine, but our `get_digedge` must return a
> meaningful slot-2 (depth) for the patch to carry the right value (currently it returns the floor hmap z).

The commented-out block (`:155-159`) shows the historical tool pre-select (Spitzhacke vs Presslufthammer)
was moved into `dig_continue`/`dig_anim`.

### 2.2 `dig_check {pos {try 0}}` (`:166-183`)
Diggability gate by **free-neighbour count**:
```
x,y = pos[0],pos[1]
cpos = get_pos this + {0 -0.8 0} # body anchor, 0.8 up the Y(vertical)
w = get_angle_num4 $pos $cpos # 0..3 quantized dir of (cpos-pos)
# per-direction 3-cell neighbour stencil (the cells "across" the dig face):
switch w {
  0 {xl={0 -1 1}; yl={-1 -1 -1}} # pos2 below pos1 → sample the row above
  1 {xl={1 1 1}; yl={0 -1 1}} # pos2 right → sample the column left
  2 {xl={0 -1 1}; yl={1 1 1}} # pos2 above → sample the row below
  3 {xl={-1 -1 -1}; yl={0 -1 1}} # pos2 left → sample the column right
}
freeneighbours = count of i in 0..2 where get_hmap(x+xl[i], y+yl[i]) < 13
return (freeneighbours>1) ? 1: 0
```
> `get_hmap < 13` = "this sub-cell is open enough to stand/move through". Needs **>1** (≥2 of 3) free, i.e.
> the dig face must be reachable from at least two sides. The xl/yl tables are **exactly** the cross-check
> the port's `get_angle_num4` RE was validated against (`DigglesTclEngine.cs:889`).

### 2.3 `enable_digbrushes {own}` (`:185-203`)
Tutorial/UI gating of the 4 dig-brush buttons by `gamestats attribmax own exp_Stein` vs thresholds
`$tttexp_digbrush2..4`. Not part of the dig loop; pure owner-attrib UI. (`im_in_tutorial`, `set_owner_attrib`,
`gamestats attribmax` natives.)

### 2.4 `dig_continue {{try 0}}` (`:23`) — the loop core
Runs once per dispatch tick **only while** the gnome still has worktime AND the order is still "dig":
```
globals: current_time_plan current_worktask current_digpose current_tool_class
if get_remaining_sparetime(this)==0.0 OR current_worktask!="dig": return # job over / changed
if try>6: dig_resetid this # past try 6, drop the dig-id filter
ground = (get_gnomeposition this == 0) ? 1: 0 # 0=on floor, 1=on wall
cpos = get_pos this
digpoint = (dig_next $cpos this $try) + {0 0 -1} # next voxel, pushed 1 deeper in z(depth)
# --- Kristallstrahl re-aim (laser): only on the ground, digging DOWNward, with the beam in inv (:214-235)
laser = (ground && cpos.y>digpoint.y && inv_find this Kristallstrahl != -1) ? 1: 0
if laser: rotate the SEARCH: keep calling dig_next at higher try until |roty-digdir|<=0.4 or try>=8;
          if still off, restore oldpoint/oldtry and dig_resetid.
gnome_announce_dig this $digpoint
if vector_unpacky $digpoint > 0 AND try<9: # VALID voxel and not exhausted
    if !laser && !dig_check($digpoint,$try): dig_continue(try+1); return   # gate; advance try
    walkpos = vector_fixdig $digpoint [get_pos this]       # standoff for this voxel
    nonewwalk = 0.5                                        # walk only if we must move > this
    if laser||inv_find this Presslufthammer==-1: airhammer=0 else: airhammer=1; nonewwalk=1.1
    walkdist = vector_dist3d [get_pos this] $walkpos
    if laser: nonewwalk = 10.0                             # laser reaches far → rarely re-walk
    if walkdist > nonewwalk:                               # need to reposition
        dig_endanim 0
        tasklist_add this "walk_pos_dig {$walkpos} {$digpoint}"
        # retry ladder via the global walkfail_tasks:
        incr try
        if try<8 : walkfail_tasks = {"tasklist_clear this" "dig_continue $try"}
        elif try==8: walkfail_tasks = {"tasklist_clear this" "dig_starttask 0"}
        else: walkfail_tasks = ""
    # tool change (queues change_tool + a final "change_tool 0 0 0" deselect)  (:262-276)
    toolchange = 0
    if airhammer && current_digpose not [wh]?: (dig_endanim if was laser); add change_tool Presslufthammer; toolchange=1
    elif laser && current_digpose not l?:      (dig_endanim if was [wh]);   add change_tool Kristallstrahl; toolchange=1
    elif !airhammer && !laser:                  add change_tool Spitzhacke
    if toolchange || current_tool_class starts in "PressKrist": add change_tool 0 0 0
    tasklist_add this "dig_anim {$digpoint}"
    # immediately pump the first queued task this tick:
    if tasklist_cnt this: cmd=tasklist_get this 0; tasklist_rem this 0; eval $cmd
else: #:284-291 no more voxels → finish
    dig_endanim
    dig_resetid this
    state_triggerfresh this work_idle
    return true
```
**The `nonewwalk` radii** (`:243-248`): Spitzhacke **0.5**, Presslufthammer **1.1**, Kristallstrahl **10.0**
(plus the deeper-tool ranges in `dig_execute`). These are the "don't re-walk unless the next voxel is farther
than this" thresholds — a longer reach for heavier tools means fewer reposition walks.

> **`current_digpose` encoding** (the global tracking the active pose): a 2-char string
> `<tool><idx>`: tool ∈ `{l=laser, w=airhammer-wall, h=airhammer(hand,floor)}` or `""` (Spitzhacke/none);
> idx is the direction index. Used by `[string match {l?}]`, `{[wh]?}` etc. to know which transition
> animation to play. `dig_anim` writes it (`:469,514,537`); `dig_endanim` plays the closer + clears it.

### 2.5 `dig_execute {digpoint}` (`:295-383`) — the carve + reward
Runs **inside the anim finish-callback** (`wait_time_dig`/`play_anim` finish → `dig_execute {..};dig_continue`).
```
globals: tttgain_dig tttinfluence_dig tttfailmax_dig current_digpose
digattr = get_attrib this exp_Stein
minextrarange = 3.0
if current_digpose is l?: laser=1; airhammer=1
else: laser=0; if inv_find Presslufthammer!=-1: airhammer=2 else: minextrarange=2.0; airhammer=1; digattr*=0.5
thispos = get_pos this + {0 -0.8 0}
dig_z = digpoint[2] # the depth slot
digcount = hmax(0.5, sqrt(digattr*tttinfluence_dig*airhammer))
gnome_announce_dig this $digpoint
while laser OR (vector_dist(digpoint,thispos) < 3.0+digattr*2.0 AND |digpoint[2]-dig_z|<2):
    if laser OR rand() < digcount*get_material(digpoint)*0.7:      # *** the per-swing carve probability ***
        if dig_apply $digpoint this:                              # *** THE world-mutating carve ***
            foreach (attr,val) in tttgain_dig: add_attrib/add_expattrib (last is exp)  # :317-327
            rndval = im_in_campaign ? 0.01 : 0.04
            if rand()<rndval: spawn ore (Stein/Eisenerz/Kohle/Golderz/Kristallerz) by zone-weighted table  # :333-372
        if digattr<tttfailmax_dig OR laser: break                 # weak digger / laser: one carve per call
        digpoint = dig_next thispos this 1 1                       # else step to next voxel within reach
        if !dig_check(digpoint): break
        digcount *= 0.5
    else: break
return true
```
> The **ore spawn** (`:328-372`): `sel /obj; new $cc "" $digpoint {0 0 0}; set_physic; set_autolight false; set_owner -1`.
> Zone weighting comes from `sm_get_event` campaign flags or `civ_state` in skirmish; the `mix` tables
> (`:355`) map a `random` roll to one of {Stein,Eisenerz,Kohle,Golderz,Kristallerz}. **Cosmetic-ish for nav,
> but real for economy.**

### 2.6 `dig_anim {digpos {accident_possible 1}}` (`:385-553`) — pose/tool animation selection
Branches on `get_gnomeposition` (floor vs wall) and tool. Queues the right animation chain via `tasklist_addfront`,
then pumps the first task. Detailed structure:

**FLOOR (`get_gnomeposition==0`), non-laser or digging upward (`:391-472`):**
```
airhammer = (inv_find Presslufthammer != -1)
digattr = get_attrib this exp_Stein
animcnt = hmax(4.8 - digattr*5.0, 1)
animcnt = hf2i(animcnt)
accident = accident_possible ? hmax((tttfailmax_dig-digattr)*5, 0.0): 0
height = get_posy(this) - digpos[1] # Y(vertical) delta gnome→target
digpose = height>1.6 ? 0(up): height>0.6 ? 1(front): 2(down) #:399-407
dir = {up front down}[digpose]
# choose animation:
if accident>0 && !airhammer: queue play_anim dig${dir}accident, then re-queue dig_anim {digpos} 0
elif airhammer: animcnt>1 ? play_dig_anim_loop airhamm${dir} animcnt {digpos} accident
                          : play_anim airhamm${dir}loop {digpos}
else: play_anim dig${dir} {digpos} # Spitzhacke
# re-evaluate pose against a finer anchor (cpos = pos + {0 -0.7 0}) for the AIRHAMMER transition table:
... compute digdir = get_anglexz(cpos, digpos+{0 0 -0.5}); pose recompute (height>0.8/-0.1)
diff = roty - digdir; wrap to [-π,π]; if |diff|<0.3: set_roty digdir; angle=0 else angle=1
if airhammer:
    if angle: queue (change_tool 0 0 0; play_anim airhamm${dir}start) AFTER (change_tool Presslufthammer 0 0; rotate_toangle digdir)
              and, if had a pose, (play_anim airhamm${old}end)
    elif current_digpose=="": play_anim airhamm${dir}start
    else: pose-to-pose transition via switch o$digpose over {u1,u2,d1,d0,f0,f2} → airhammu2f / f2d / d2f / f2u …
    current_digpose = h$digpose
elif angle: rotate_toangle digdir
```
**FLOOR, laser (Kristallstrahl) downward (`:473-515`):**
```
cpos=pos+{0 -0.7 0}; digdist=hmax(dist3d(cpos,digpos),0.1); digdir=get_anglexz(cpos,digpos)
diff=roty-digdir wrap; |diff|<0.4 ? set_roty;rotate=0: rotate=1
# pose by slope (height/digdist > 0.7 ⇒ overhead)
if (posy-0.7-digpos[1])/digdist>0.7: digpose="l0"; diganim=mann.laserbohrer_oben_loop
else: digpose="l1"; diganim=mann.laserbohrer_vorne_loop
animcnt = int(hmax(animcnt-1.5,1)*1.6)*0.8
if current_digpose==digpose && !rotate:
    queue: wait_time_dig animcnt {digpos}; laser this 12 {digpos} (animcnt+0.5)
else:
    queue: play_anim_time $diganim animcnt {digpos}; laser this 12 {digpos} (animcnt+0.5)
    if rotate: change_tool 0 0 0; play_anim laserdrill${up|fr}start  +  change_tool Kristallstrahl;rotate_toangle  + (laserdrill${old}stop)
    else: pose-to-pose: laserdrillupstart / laserdrillfr2up / laserdrillup2fr / laserdrillfrstart
current_digpose = digpose
```
**WALL (`get_gnomeposition!=0`) (`:516-545`):**
```
digpose = get_angle_num4(get_pos this, digpos) # 0..3 = up/right/down/left around the wall
if airhammer:
    dir = "urdl"[digpose]
    queue play_dig_anim_loop airhammwall$dir animcnt {digpos}
    if current_digpose=="": play_anim airhammwall${dir}start
    else: walk the lut chain (l↔u↔r↔d) playing airhammwall${next}2$dir until aligned
    current_digpose = w$digpose
else:
    dir = {up right down left}[digpose]
    play_anim digclimb$dir {digpos}                        # Spitzhacke on a wall
if |roty - 3.14| > 0.3: rotate_toback # face INTO the wall (π)
```
Tail (`:546-552`): pump the first queued task (`tasklist_get/rem/eval`).

> **All animation names referenced** (the dig anim vocabulary the anim DB must contain):
> Spitzhacke floor: `digup/digfront/digdown` (+ `…accident`); Spitzhacke wall: `digclimbup/right/down/left`.
> Airhammer floor: `airhammuploop/frontloop/downloop`, `airhamm{up,front,down}start/end`, transitions
> `airhammu2f, airhammf2d, airhammf2u, airhammd2f`; wall: `airhammwall{u,r,d,l}loop/start/end` + `…2…`
> transitions. Laser: `mann.laserbohrer_oben_loop`, `mann.laserbohrer_vorne_loop`,
> `laserdrill{up,fr}start/stop`, `laserdrillfr2up`, `laserdrillup2fr`. Tool swaps (from `change_tool`,
> z_procs.tcl:1602): `airhamm{insert}starta/startb/enda/endb`, `tooltakeout{insert}_a/_b`,
> `toolputaway{insert}_a/_b` (insert ∈ {"", "left", "wall"}).

### 2.7 `dig_endanim {{toolchange 1}}` (`:555-579`)
Plays the **closer** for the active pose and puts the tool away:
```
switch current_digpose[0]:
  "l" → play_anim laserdrill{up|fr}stop; tool=2
  "w" → play_anim airhammwall{u|r|d|l}end; tool=1
  "h" → play_anim airhamm{up|front|down}end; tool=1
if tool: change_tool {""|Presslufthammer|Kristallstrahl}[tool] 0 0; if toolchange: change_tool 0
current_digpose = ""
```

### 2.8 `work_dig {digpos}` (`:581-584`)
Trivial: `tasklist_add this "walk_pos $digpos"`. (Legacy/simple "walk to dig point".)

---

## 3. Native ↔ Tcl contract (what our engine MUST return)

| native (Tcl name) | decompile | returns | port status |
|---|---|---|---|
| `get_digedge ‹topos› ‹obj›` | `FUN_005981d0` (Ghidra drops the inner body) | packed vec of the nearest **reachable floor edge** next to diggable rock; wrapper Z is always 14, including failure `{-1,-1,14}` | implemented as inference-backed reusable-array 4-neighbour BFS through diggable rock; the exact visible shell is pinned at `:84-100`; Tcl ref packing is exact at `FUN_00635180:153-158` |
| `dig_next ‹vec› ‹obj› [‹bestidx›] [‹onlyLastDigID›]` | `FUN_00597b20` (clean) | best diggable voxel `{x y rim+4}` clamped z∈[1,14]; **`-1 -1 -1`** if `param_4(bestidx)==0`; metric = `dx² + (y-(py-Δ))² + (rim+4-(pz+Δ))²·0.5` + layer-mismatch penalty | implemented (`PickDiggableCell`, `RimHeight+4` clamp [1,14]) — faithful |
| `dig_apply ‹vec› ‹obj›` | `FUN_006347a0`=`DigApply` → carve `FUN_00596b80` | **1** if a node changed, else 0; reads owner `obj+0x36c`; re-resolves block_idx via the BDB aux graph | implemented (`CarveStep`) with owner target, rim gate, transition accept, completion sweep, and dig-ID cache |
| `vector_fixdig ‹vec› ‹vec›` | `FUN_00594e00` (clean) | step ~1 unit from arg1 toward arg2, spiral-snap to nearest **walk** sub-cell (`+0x3c&1`), then scan +y for a **floor** cell (`&2`), z = blockHmap(at `subY-5`)+1 clamped ≤14, xy ·0.125 | implemented (step+snap to SubWalk, z=hmap+1) — **missing the +y floor scan + the `-5` sub offset (GAP)** |
| `get_hmap ‹x› ‹y›` | `FUN_004093f0`/`FUN_0062faf0` | per-sub-cell height nibble 0..15 (×8 sub resolution); `<12/<13` = open | implemented (`GetHmapSub`) — faithful |
| `get_material ‹vec›` | (map) | 0=stone 1/2=sand/earth 3=scapehole — used in the carve probability | implemented (`GetMaterial`) |
| `get_gnomeposition ‹obj›` | `FUN_0068d8a0`=`GetGnomePosition` (clean) | **0**=standing on floor (any of 3 rows `subY+1/subY/subY-1` has `+0x3c&2`), else **1**=on wall; OOB ⇒ 0 | implemented (3-row SubFloor probe) — **EXACT match** |
| `get_walkresult ‹obj›` | obj+0x248 | 0=ERROR 1=ABORTED 2=NOTFINISHED 3=NOPASSAGE **4=TARGETREACHED** | implemented (`WalkResult`, stamped by `WalkResultAction`) |
| `get_angle_num4 ‹p1› ‹p2›` | `FUN_00646b10`=`GetAngleNum4` (clean) | 0..3 quadrant of d=(p2−p1) on native X/Y plane; `(0x3f − trunc((atan2(dy,dx) − 3π/4 + 10π)/(−π/2))) & 3` | implemented (instruction-exact) — **EXACT match** |
| `get_anglexz ‹v1› ‹v2›` | (misc) | xz-plane angle | implemented (port negates dx for handedness) |
| `gnome_announce_dig ‹obj› ‹pos›` | (obj) | cosmetic dig-marker keepalive | implemented (marker keepalive) |
| `dig_resetid ‹obj›` | (object) | clears the gnome's dig-id filter (obj+0x250 dig id) → `dig_next` stops filtering by `onlyLastDigID` | implemented (`DigSetId=-1`) |
| `action ‹obj› walk "‹opts›" [fini] [brk]` | CAct_MetaWalk | starts the walk action; finish/break codes eval'd on arrival/interrupt | implemented (`BuildMetaWalkFromOptions` → MetaWalk/Climb segments + `WalkResultAction`) |
| `change_tool` | **Tcl proc** (z_procs.tcl:1602) | queues tool in/out anims | resolves as a proc (not native) |
| `laser ‹obj› ‹link› ‹dest› ‹dur›` | (misc) | the Kristallstrahl beam VFX | **bulk-stubbed "0"** — cosmetic, dig still carves via `dig_apply`; the beam is invisible (GAP, cosmetic) |
| `play_anim / play_anim_time / play_dig_anim_loop / wait_time_dig` | **Tcl procs** (z_procs.tcl:186-249) | queue/run an action whose finish runs `dig_execute;dig_continue` | resolve as procs; `action this anim/wait` natives back them |

The rim helper **`FUN_005968b0`** (clean): returns `max(aux[0x53]N̄W̄, aux[0x52]N̄, aux[0x51]W̄, aux[0x50]self)`
of the **transitioned** neighbour blocks (transition idx at aux `0x4c/0x48/0x44/0x40`), `0xffffffff` if no
transition exists. This is `dig_next`'s "rim" and the validity gate. Port = `RimHeight`.

---

## 4. Section 2 — WALK (`:590-945`)

Every entry point sets up state, fixes the target with `vector_fix`, and calls `walk_action` (or `action … walk`).

| proc | line | does | key opts to `walk_action`/`action` |
|---|---|---|---|
| `walk_pos {pos {force 0}}` | 590 | the default move; `beam_back`, `note_pathlength`, `state_disable`, `vector_fix`, walk; finish=`walkfail` | `-target {pos} -waving 1 -forcetarget {0|force}` |
| `walk_pos_with_box {pos}` | 607 | box carry: slow, **no climb** | `-target … -forcetarget 0 -canclimb 0`, arg `-withbox` |
| `run_pos {pos {force}}` | 621 | forced run | `-target … -waving 1 -speedtype 2` |
| `run_away {pos}` | 635 | flee; `is_escaping=1`; random-path fallback; re-arms self on finish/break | `-randompath N -waving 0 -speedtype 2` OR `-target … -waving 0 -speedtype 2` |
| `run_pos_obj {pos obj {dist}}` | 663 | fight approach; weapon-pose animset (256/257/258); `-dontstop` + `-walktimercallback` when `obj==attack_item` | `-target … -animsets N [-dontstop 1 -walktimercallback fight_walk_timer_callback -walktimercounter T]` |
| **`walk_pos_dig {pos dpos}`** | 705 | **the dig-loop walk.** `state_disable`, `vector_fix`; if on a wall AND `get_hmap(pos)<12` → run; finish=`walkfail;return true` | `-target {pos} -waving 0 -dig 1 -digpos {dpos} [-speedtype 2]` |
| `walk_random {plength}` | 724 | wander to a random `get_place` | via `walk_pos` |
| `walk_near_item {item radius {tol} {speed}}` | 735 | walk to within `radius` of an item/point; multi-stage `get_place` fallback (→ `-materials false` → `vector_fix itempos`); several early "already close / unreachable wall" exits | `-target {walkpos} [-speedtype N]` |
| `walk_dummy {item dummy {speed}}` | 814 | walk to an object's work **dummy** (link point) | `-object item -dummy dummy -speedtype N` |
| `walk_dummy_with_box {…}` | 830 | same, box carry, no climb | `… -canclimb 0`, `-withbox` |
| `walk_outoftransit {}` | 846 | if `vector_unpackz(pos)>13` (in transit/too deep) → step out to z=13 | `walk_pos` |
| `walk_down_from_wall {}` | 855 | find a ground place via expanding `get_place_long` rings (5..50), avoid underwater; walk there; sets `walkfail_tasks` | `walk_pos` |
| `walk_around {}` | 884 | idle wander + `adjust_gnome_rotation` | — |
| `walk_near_hamster {item}` | 889 | move to within a hamster's scanrange | `walk_pos` |
| **`walkfail {}`** | 910 | **the universal walk finish/fail handler** | — |
| `note_pathlength {pos {lock}}` | 937 | placelock the target for `pathlength*2`; accumulate `planned_pathlength` | `placelock_set` |

**`walkfail` (`:910-935`) — THE gate everything funnels through:**
```
placelock_rem $myref
if walkfail_tasks unset: walkfail_tasks=""
if get_walkresult(this) != 4: # NOT arrived
    if walkfail_tasks=="":  # give up
        tasklist_clear; kill_all_ghosts; stop_prod; gnome_failed_work this; state_triggerfresh this idle
    else:                   # retry chain (set by dig_continue / walk_down_from_wall)
        eval each entry of walkfail_tasks; state_enable this
else: # arrived (result==4)
    walkfail_tasks=""; state_enable this
```
> **This is why `get_walkresult` MUST return 4 on arrival** — otherwise EVERY walk "fails", clears the
> tasklist, and the gnome bails to idle (the original "0-stub broke everything" bug, now fixed in the port).

**`walk_pos_dig` speed gate (`:710`):** `if get_gnomeposition(this) && get_hmap(pos[0],pos[1])<12: -speedtype 2`
— a gnome **on a wall** heading to an **open** (hmap<12) cell runs; otherwise walks. **Port gap:** our
`BuildMetaWalkFromOptions` ignores `-dig`, `-digpos`, `-waving`, `-forcetarget`, `-dontstop`,
`-walktimercallback`, `-randompath` (parses only `-target/-animsets/-speedtype/-canclimb/-object/-dummy`).
Functionally the dig loop still works (the walk reaches the standoff and `WalkResult=4`), but `-dig 1`/`-digpos`
(which in the engine let the walker stop early at the right standoff and face the dig) are not honored — the
port relies on the standoff already being the target.

---

## 5. Section 3 — ROTATE (`:951-1116`)

| proc | line | body |
|---|---|---|
| `rotate_toright/left/back/front` | 951-954 | `action this rotate {4.71|1.57|3.14|0} {state_enable this}` — absolute Y in radians |
| `rotate_to {dir}` | 956 | switch dir → one of the 4 above (default front) |
| `rotate_towards {item {direction}}` | 971 | face toward an item: `vector_angle` on the **x/z·0.5** projection, ±π/2 by direction; skip if already aligned (<0.05); `action this rotate $angle` |
| `rotate_towards_pos {pos}` | 995 | `action this rotate (1.57 + vector_angle(get_pos,pos))` |
| `rotate_towards_with_box {…}` | 1002 | same as `_towards` but with `ANIMSET_WALKWITHBOX` |
| `rotate_toclock {clock}` | 1025 | `degree = π + clock*0.5236` (clock face → radians; 0.5236≈π/6 per hour) |
| `rotate_toangle {angle {anim}}` | 1032 | `action this rotate $angle [$anim] {state_enable this}` — used by `dig_anim` to aim at the dig face |
| `adjust_gnome_rotation {}` | 1042 | if `|roty|>1.2` queue `rotate_tofront` |
| `walk_out_of {item}` | 1049 | escape an object's bbox via `check_ghost_coll` + `get_place -rect/-clip`; fallback `walk_pos "x y 14"` |
| `fight_rotate_towards {}` | 1079 | face `attack_item`; weapon-pose animset; returns 0=fail/1=success/2=no-need |

> All rotates go through the native `action this rotate <angleRad> [animset] {fini} {brk}`. The named
> directions assume the engine's facing convention (front=0, **left=π/2**, back=π, **right=3π/2**); the port's
> `get_anglexz`/`FaceRotationForNativeAngle` negate dx to match that handedness (`DigglesTclEngine.cs:1044`).
> `get_gnomeposition` guards `rotate_towards*` (`:972,1005,1081`): a gnome on a wall does NOT rotate.

---

## 6. Interaction with neighbouring systems

- **Order entry:** `evt_task_dig_proc` (z_events.tcl) → `dig_starttask {clickpos}` → `state_triggerfresh work_dispatch`.
- **Dispatch:** `state work_dispatch` (z_work_states.tcl) pops tasklist[0] each tick and `eval`s it; the dig
  loop is entirely tasklist-driven (`tasklist_add/_addfront/_get/_rem/_cnt/_clear`).
- **Anim finish chaining:** the dig CARVE only happens in the **finish-callback** of an anim/wait action
  (`play_anim … {digpos}` / `wait_time_dig`), which is `dig_execute {digpos}; dig_continue`. So the carve is
  paced by animation length (`animcnt` from `digattr`). `action this anim/wait` natives drive this.
- **Walk:** every walk → `walk_action` → `action this walk`; arrival/abort → `walkfail` via the action's
  finishcode; `get_walkresult` decides retry vs give-up.
- **Economy/stats:** `dig_execute` grants `exp_Stein` and other `tttgain_dig` attribs and spawns ore — the
  techtree tunes (`tttgain_dig`, `tttinfluence_dig`, `tttfailmax_dig`, `tttexp_digbrush*`) come from
  `misc/techtreetunes.tcl`.
- **Carve world-mutation:** `dig_apply` → native `FUN_00596b80` re-resolves `block_idx` (see
  `Docs/DIGGING_MECHANIC.md` §2) and re-meshes — the ONLY world-changing call here.
- **Place reservation:** `note_pathlength`/`placelock_set`/`placelock_rem`/`$myref` reserve the destination
  so two gnomes don't path to the same spot.

---

## 7. PORT GAP analysis (vs current C# in `DigglesTclEngine.cs` / `EntityActions.cs`)

1. **The lane-B `walk_action` delta is `-dig 1`.** It suppresses the port-only post-relax
   floor settle exactly at `FUN_00420840:1901-1919`, while candidate selection remains
   `get_digedge`'s job. Other walk options have their own implementation status in the walk
   lifecycle RE; this dig note does not reclassify that larger option surface.
2. **`vector_fixdig` simplification:** port does step-toward-gnome + snap-to-walk + z=hmap+1; it omits the
   native's **+y floor-cell scan** and the **`subY−5`** offset when reading the block heightmap. Likely fine
   for floor digs; may mis-place a standoff at steep walls.
3. **`get_digedge` residual gap:** the checked-in decompile still omits the inner body. The implemented
   search is the §4 inference-backed BFS through diggable rock, with long-corridor and failure
   regressions; exact per-sub-cell candidate gating and metric remain unresolved. The wrapper input is
   not unresolved: `FUN_00635180:153-158` packs the dwarf reference as
   `ROUND(obj coordinate + 0.5)`, and:158-161 hard-codes output Z=14.
4. ~~**Missing eight-layer tunnel voxels**~~ — **closed as a false premise.** `obj+0x36c` is the
   owner selecting a 4-bit order; the single block/corner transition is native. Mode 1 decodes to
   target 11 and produces the standard authored tunnel profile. See DIGGING_MECHANIC §7.
5. **`laser` stubbed:** the Kristallstrahl beam VFX is invisible (returns "0"); the carve still happens via
   `dig_execute`'s `dig_apply`. Cosmetic gap.
6. **`add_expattrib` stubbed "0"** (`:458`): `dig_execute`'s exp reward (`tttgain_dig` last entry →
   `add_expattrib exp_Stein`) is dropped, so dig skill never grows from digging in the port (economy gap).
7. **`get_place`/`get_place_long`/`check_ghost_coll` are sentinels** (`"-1 0 0"`/`"0"`): `walk_near_item`,
   `walk_down_from_wall`, `walk_out_of`, `walk_near_hamster` fall back to their degraded paths. The core dig
   loop doesn't depend on these.
8. **`get_remaining_sparetime`** gates `dig_continue` (`:208`): must return >0 while the gnome has worktime
   and 0 when the window closes, else the loop never starts or never ends. (Implemented; verify the clock
   wiring — `ProdDispatcher.WorktimeHourSeconds`.)

---

## 8. Unresolved / to verify

- **`get_digedge` inner search** — reconstruct from the removed Ghidra blocks (byte-level) or a live probe;
  confirm exactly which cells it scans and what z it puts in slot 2 (depth vs floor-hmap) so the `originalz`
  lrep is faithful.
- **`dig_next` distance-metric constants** — `DAT_007370c0`, `DAT_007374e0` (the `(py-Δ)`/`(pz+Δ)` offsets),
  `DAT_00737058` (the z·0.5 weight), `DAT_0073752c` (=8.0, sub scale), `DAT_00737330`/`DAT_00737138`(=1)/
  `DAT_00737550`(≈14) (the z clamp) are referenced but their literal float values weren't read from the data
  segment here — only inferred from the port's working `RimHeight+4 clamp[1,14]`. Read them to confirm.
- **`-dig 1 -digpos` engine semantics** — what CAct_MetaWalk does differently when `-dig`/`-digpos` are set
  (early stop distance, auto-face). Needed to make `walk_pos_dig` byte-faithful.
- **`laser this 12 {digpos} dur`** — the beam args (12 = link id?) and whether the laser also carves natively
  vs. only `dig_execute`. Currently `dig_execute` carves; verify the engine doesn't double-carve via `laser`.
- **`vector_fix`/`vector_fixdig` z clamp** — confirm `DAT_00737564`/`DAT_00737550` are the same ~14 ceiling.
- **`run_pos_obj` `switch { $pose }`** (`:671`) and `fight_rotate_towards` `switch { $pose }` (`:1105`) use a
  **braced** switch argument — in Tcl that switches on the literal string `"$pose"` token list, almost
  certainly an original-script bug (the default branch always wins). Note for faithful porting: replicate the
  bug, don't "fix" it.
