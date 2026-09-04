# Tcl Init / Class-Load Sequence — Research & Port Plan

> STATUS: the §5 "CRITICAL GAPS" that blocked running `classinit.tcl` are now
> CLOSED — `glob`, `cd`, `pwd`, and `file exists` are implemented for real in
> `DigglesTclEngine` (`CmdGlob`/`CmdCd`/`CmdPwd`/`CmdFile`, resolving against a
> real `ctx.Cwd`). The real class system loads and drives the live campaign
> (684 classes / 339 entities). §§1-4 are decompile ground truth
> (unchanged); §5-6 status is updated inline below.

How the ORIGINAL Diggles (2001) boots its Tcl: the full init chain from the
engine's first `Tcl_EvalFile` down through `classinit.tcl` and the ~300 game
classes. Plus a GAP ANALYSIS of our C# interpreter and the concrete plan to run
the real load. Produced by a 4-agent RE sweep over
`original/data/`, `re/decompiled/`, and `Assets/Diggles/Runtime/Tcl/`.

> Why this doc: we want the runtime loader to drive EVERY entity from its real
> class Tcl (idle tables, animsets, states, methods) — not hardcoded C#. That
> means running the real class system. This maps exactly what to load, in what
> order, and which interpreter gaps block it.

---

## 1. Engine-side boot entry (decompile)

Tcl is dynamically linked (`original/tcl83.dll`). The engine bootstrap function
at **`Diggles.exe @0x006AEB30`** (not in the decompile set — sits in a gap; found
by `.rdata` string-xref) evaluates three scripts, in order:

1. **`data/scripts/misc/genattribs.tcl`** (`.rdata` VA 0x735CE0) — global
   `def_attrib`s (experience / resource / status attribs).
2. **`data/userinit.tcl`** (VA 0x735D50) — optional user override; **not shipped**
   (gated on engine flag `DAT_00772dd0`).
3. **`data/systeminit.tcl`** (VA 0x735D90) — **the true top-level game-init root.**

Per-file load is two-stage:
- **Preprocess + load:** `FUN_006bb140` — `fopen`/`fgets`, interprets C-style
  `//# IF / ELSE / ENDIF / DEFINE / UNDEF / STOPIF[NOT]` directives (e.g.
  `systeminit.tcl:13-17` `//# IF FULL`), builds a `Tcl_NewStringObj`.
- **Evaluate:** `FUN_00555a00` (profiling tag `"TCL:CObj TclEval"`) → `Tcl_EvalObjEx`.

The interp is created by **`FUN_005c9cc0`**: `Tcl_CreateInterp` then
`Tcl_CreateObjCommand` for every engine command in the two tables at
`DAT_00772440` (~225 commands). (Matches `re/notes/tcl_api_surface.md`.)

> NOTE: the `//#` preprocessor is the ENGINE's, not Tcl's. Our `call`/`source`
> loader does not currently handle `//#` directives — `systeminit.tcl` uses them
> (FULL vs DEMO). Minor: we can strip/branch them, or start the load at
> `classinit.tcl` (which has none) and skip systeminit's version banner.

---

## 2. `systeminit.tcl` — the data-side init order

A flat, explicit sequence of `call`s (NOT glob-driven). Order + gating:

| # | call | line | gated by |
|---|---|---|---|
| 1 | `scripts/init/customcommands.tcl` |:10 | `!minimalrun` |
| 2 | `scripts/init/shader.tcl` |:24 | — (stub file) |
| 3 | `scripts/init/txtinit.tcl` |:25 | — |
| 4 | `scripts/init/animinit.tcl` |:26 | — |
| 5 | **`scripts/init/classinit.tcl`** |:27 | — → loads classes; calls `eventgen.tcl` at its end (`classinit.tcl:111`) |
| 6 | `scripts/init/soundinit.tcl` |:31 | `!minimalrun` → also `z_soundinit`, `t_soundinit` |
| 7 | `scripts/init/adaptiveinit.tcl` |:33 | gated |
| 8 | `scripts/init/lginit.tcl` |:36 | gated → `lgtools.tcl` |
| 9 | `scripts/init/talkinit.tcl` |:39 | gated → text/talk/*_<locale>.tcl |
| 10 | `scripts/init/claninit.tcl` |:41 | gated (1-line stub) |
| 11 | `scripts/init/makettree.tcl` |:43 | gated; + `init_techtree gen_tt.tcl`:44 |
| 12 | `scripts/init/fight.tcl` |:49 | `!minimalrun` |
| 13 | `ai init 0.../std_ai.tcl` |:54 | — |
| 14 | `map create 64 64 {}` |:64 | — |
| 15 | start templates (`unq_menue`, `urw_gng_*`) or `userstartup.tcl` |:69-98 | `!minimalrun && !mapedit` |
| 16 | `load_done` |:108 | — |

**Gating commands:** `minimalrun` (skip the bulk on a headless/minimal run),
`startcache present|load|write|enabled` (class-def cache; engine flag
`UseTclClassCache`, sentinel `scripts/flush_tclclasscache.tcl`), `get_mapedit`,
`file exists data/userstartup.tcl`.

---

## 3. `classinit.tcl` — the class-load mechanism

```tcl
def_attrib Pilz 0 10000 0; … (10 global attribs); classinit.tcl:4-13
if { ![minimalrun] } {
  if { [startcache present] } { startcache load };:19-22
  else {
    foreach dir { characters items deco work sparetime story } {
       cd Data/scripts/classes/$dir
       set filelist [lsort [glob -nocomplain "*.tcl"]]      ; :29  ← glob enumerates
       cd ../../../..
       foreach f $filelist {
          if {characters && [string first "_" $f]!=-1} continue   ; :32 skip includes
          if {story && $f=="sequencer.tcl"} continue              ; :73
          catch "call scripts/classes/$dir/$f"               ; :34 per-file, error-isolated
       }
    }
    call scripts/classes/zwerg/{zwerg,pzwerg,actors,baby}.tcl ; :80-83 explicit
    if {[startcache enabled]} { startcache write }
  }
}
def_attrib GnomeAge …; def_attrib GnomeStrike …;:10
call scripts/init/eventgen.tcl;:111
reset_owner_attribs;:113
```

**Class dirs + file counts** (top-level = loaded; `_`-files = includes pulled in
by their parent class via `call`):

| dir | files | top-level | notable |
|---|---|---|---|
| `classes/characters` | 27 | 17 (skip `_`) | Hamster, Troll, Spinne, Krake, Fisch, Raupe, Schmetterling, Drachenbaby, Fenris, Riesenhamster, … (`rh_/t_/w_*` are includes) |
| `classes/items` | 16 | 15 | Werkzeuge, Pilz, Gold, Rohstoffe, Muetzen, Magie, Aufzug, Wasser, … |
| `classes/deco` | 10 | 10 | all `dummy_*` (kristall/lavawelt/oberwelt/…), `class_disablescripting` props |
| `classes/work` | 30 | 30 | Feuerstelle, Saegewerk, Schmiede, Brauerei, Farm, Krankenhaus, … (buildings) |
| `classes/sparetime` | 18 | 18 | Bar, Disco, Theater, Tempel, beds/baths, … |
| `classes/story` | 17 | 10 (skip sequencer) | StoryMgr, GameObserver, Trigger_* |
| `classes/zwerg` | 20 | 4 explicit | zwerg, pzwerg, actors, baby (+16 `z_*` includes) |

~144 class files, **673 `def_class`**, ~300 registered classes.

---

## 4. Class-file anatomy

`def_class <Name> <parent|none> <type> <capflags> {caps} { BODY }`. The BODY runs
**at registration** with `CurrentClass` set; `obj_init`/states/methods/events are
stored and run later (obj_init on `new` — already wired in our `CmdNew`).

A class BODY typically:
- top-level `call`s its anim + attrib includes (`animclassinit`, `<x>_anims`,
  `obj_attribs`, `genattribs`);
- `class_defaultanim` / `set_class_anim` (state→clip) / `set_class_animset N {…}`
  (gait maps);
- `obj_init { … }` — 100+ instance locals, MORE `call`s of behaviour includes
  (z_events/z_procs/z_work_*/z_spare_*/genericfight/seq_idle/…), `set_anim`,
  `set_attrib`, `state_trigger idle`, `state_enable`, `timer_event`s;
- `state` / `state_enter` / `state_leave` bodies, `method`s, `handle_event`s.

**`zwerg.tcl` transitive closure ≈ 24 files** (animclassinit, z_anims, obj_attribs,
genattribs, genericfight, z_events, z_procs, z_dignwalk, z_faceanim, z_work_*×4,
z_spare_*×5, z_work_strike, z_methods, takeitems, seq_idle, techtreetunes,
sparetimetunes). So loading the dwarf alone cascades ~two dozen `call`s.

**Command frequency in class bodies** (all 144 files): `set_class_anim` 1838,
`method` 1151, `def_class` 673, `call_method` 571, `obj_init` 492, `handle_event`
360, `class_defaultanim` 259, `set_attrib` 201, `state` 103, `state_trigger` 74,
`set_class_animset` 46, `obj_exit` 31, `member` 20, `state_leave/enter` 11/10.
`inherit` and `def_idiobjclass` ~0 in class bodies (idiobjclass used by animinit's
dummy generators). Note: locals (`set`) dominate over `member` — vars persist on
the instance scope from obj_init.

---

## 5. GAP ANALYSIS — our interpreter vs the real load

Cross-ref `Assets/Diggles/Runtime/Tcl/`. Legend: **REAL** / **STUB** (no-op
`""`/`"0"`) / **MISSING**. (In `DigglesTclEngine.Register`, explicit
`RegisterCommand` wins over the bulk-stub list, which only fills names not already
registered — so the noisy bulk list does NOT shadow real impls.)

### Already REAL (the class machinery — good news)
`def_class` (`:21`/CmdDefClass:721), `def_idiobjclass` (:22), `member` (:93),
`method`/`_const`/`_static` (:43-45), `state`/`state_enter`/`state_leave` (:60-62),
`state_trigger`/`_reset`/`_enable`/`_disable`/`_get` (:63-66,87-89), `handle_event`
(:48), `obj_init`/`obj_exit` (:91-92), `inherit` (:94), `class_defaultanim` (:23),
`set_class_anim` (:30), `set_class_animset` (:31), `in_class_def` (:41),
`get_class_name`/`_type`/`_category`/`_flags` (:126-144), `call`/`source`/`callnc`
(:336-338, DataRoot-relative, ISO-8859-1, cached), `call_method`/`check_method`
(:96-102). Core language: `set/proc/if/while/for/foreach/switch/catch/eval/subst/
upvar/global/list*/string/format/array/expr/lsort` — all REAL.

`minimalrun`→`"0"` (STUB) and `startcache present`→`"0"` (STUB) **deterministically
select the full glob-enumeration branch** — which is what we want.

### CRITICAL GAPS — NOW CLOSED (all four implemented)
> ✅ RESOLVED. `DigglesTclEngine.Register` binds real impls that override the
> `TclBuiltins` stub (the `TclBuiltins.cs` `Glob(...) => ""` stub is still
> present but shadowed by the engine command). `ctx.Cwd` is a real per-context
> working directory that `glob`/`call`/`file` resolve against.
1. **`glob` — REAL** (`DigglesTclEngine.CmdGlob`, registered at `glob`). Honors
   `-nocomplain`/`-directory`/`--`, enumerates `DataRoot + Cwd`, returns base
   names — so `lsort [glob -nocomplain "*.tcl"]` populates every class loop.
2. **`cd` / `pwd` — REAL** (`CmdCd` / `CmdPwd`; `ctx.Cwd`). `classinit`'s
   `cd Data/scripts/classes/<sub>; glob *.tcl; cd../../../..` works.
3. **`file` — REAL** (`CmdFile`, incl. `file exists`; `systeminit.tcl:69`).

### Secondary (NOT on the class-registration path)
`ClassList` MISSING (makettree), `open/close/gets/seek` STUB (makettree writes
gen_tt.tcl), `db_load`/`def_texture`/`load_scape_texture`/`sound`/`fight_*` STUB
(engine-native; fine for class registration), `def_attrib` STUB (declaration-only —
no class body on the critical path reads attrib metadata back).

---

## 6. Verdict & plan — LARGELY EXECUTED

> ✅ The recommended path was taken: real `glob` + `cd`/`pwd` + `file` landed,
> so `classinit.tcl` now runs UNMODIFIED and the runtime loader registers the
> real class system (see `reference_tcl_init_loading` in MEMORY: 684 classes /
> 339 entities load; glob+cd+file done). The plan below is retained as history.

**`classinit.tcl` runs as-is now.** Every class-definition command was already
REAL; the load-enumeration trio `glob` + `cd`/`pwd` (+ `file exists`) has since
been implemented (§5). Two paths were considered:

- **RECOMMENDED — implement real `glob` + `cd`/`pwd` + `file exists`** (a `Cwd`
  string on `DigglesTclContext`; `glob`/`call`/`file` resolve `DataRoot + Cwd`).
  Small, well-scoped, lets `classinit.tcl` (and later `lginit`/level-gen globs)
  run UNMODIFIED — on-strategy with "reuse the original Tcl, don't transpile."
- Alternative — a C# driver that enumerates `classes/**/*.tcl` and `call`s each.
  Less code-faithful; still want glob/cd eventually.

### Proposed sequence (next iteration)
1. **Interpreter:** implement real `glob` (filesystem, `-nocomplain`/`-directory`/
   pattern), `cd`/`pwd` (context Cwd), `file exists`; make `glob`/`call`/`file`
   honor Cwd. Add a `//#` preprocessor pass (or start at classinit). Verify with a
   unit test that globs `classes/characters/*.tcl`.
2. **Runtime loader:** in `DigglesWorldRuntime`, before spawning, run the real init
   subset that registers classes — minimally `call scripts/init/classinit.tcl`
   (which needs the trio above), ideally the systeminit prefix
   (`txtinit`→`animinit`→`classinit`). Keep tolerance flags + per-file `catch`.
3. **Remove the hardcode:** drop the C# `seq_idle_anims` literal + the
   `EnsureClassAnims`/OneOfEach stubs — the real `obj_init` now sets per-instance
   `seq_idle_anims` and the real animsets on spawn.
4. **Generic driver (for ALL):** drive each spawned entity by its class — the real
   classes put themselves into `state_trigger idle` in obj_init; ensure our sim
   ticks that, so dwarves/animals/etc. each run their OWN idle (no per-class C#).
   `seq_idle` is generic (reads per-instance `seq_idle_anims`), so it covers every
   class whose obj_init defines a table; others use their default loop/state.
5. **Iterate on gaps:** loading ~144 class files will surface missing/needed engine
   commands; fill them as real impls (or safe stubs) guided by §5. `catch` per file
   keeps one bad class from aborting the run.
6. **Cutscene / sequencer module (incremental):** once entities are class-driven,
   build out the cinematic sequencer (Phase 6) the SAME way — walk through the
   `+sq_*` / `do_wait` / `set_view` / camera / text / sound commands the story
   scripts call, implementing each missing one as we hit it (real impl or faithful
   binding to Unity camera/audio/UI). Grow coverage script-by-script rather than
   up-front. This unblocks the tutorial/story cutscenes end-to-end.

Companion docs: `TCL_INTERPRETER_PLAN.md` (interpreter status), `ANIMATION_CONTROLLER.md`
(controller + animsets), `NAVIGATION_AND_MOVEMENT.md` (MetaWalk). Engine API:
`ENGINE_TCL_API.txt`, `re/notes/tcl_api_surface.md`.
