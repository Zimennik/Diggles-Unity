# Tcl Object Model & Variable Scoping — Research & Port Design

> STATUS: the §2 divergence is FIXED — the §3-5 design is implemented.
> `TclInterpreter.ActiveGlobalOverride` (`TclInterpreter.cs:44-45`,
> `GlobalScope => ActiveGlobalOverride ?? scopes[0]`) is set/restored around
> entity body evals in `DigglesTclEngine` (RunInEntityContext + CmdCallMethod,
> ~lines 7354/7426), so `global x` inside a body now hits the OBJECT's own
> scope. `ref_get`/`ref_set` are real cross-object accessors
> (`CmdRefGet`/`CmdRefSet`, DigglesTclEngine.cs:6974/6982). §1 is decompile
> ground truth (unchanged); §2 now describes the PRE-fix state (kept for
> context). Tests: `TclObjectScopeTests.cs`.

How the ORIGINAL Diggles (2001) gives each object per-instance Tcl variables, how
OUR interpreter USED TO diverge, and the faithful (but cheap) design that fixed it.
This is FOUNDATIONAL: the object-variable scoping underpins ALL class behaviour
(idle, AI, work, states, methods, reproduction…), not just idle. Produced by a
4-agent RE sweep over `re/decompiled/`, `original/data/`,
`original/Doc/wiggles_tcl.htm`, and `Assets/Diggles/Runtime/Tcl/`.

---

## 1. The original model — one Tcl interpreter PER OBJECT (decompile-proven)

**Every object instance owns a private `Tcl_CreateInterp` interpreter.** All of an
object's per-instance Tcl variables are simply the **global variables of that
object's own interpreter**. Method bodies, `obj_init`/`obj_exit`, `state` bodies and
`handle_event` handlers all evaluate at the **global level of the object's interp**
(`Tcl_EvalObjEx(objInterp, body, 0)` — flags 0, **no proc frame**). No per-object
namespaces, no custom resolvers, no `Tcl_UpVar`/`Tcl_LinkVar`.

Object struct (`CObj`/`CIdiObj`): `obj+0xC0` = private interp (lazy), `+0xC4` = owned
flag, `+0xD4` = `CTclClass*`, type tag `0x616e4f62` ("bOna"). `CTclClass`: `+0x80/+0x84`
= member name/default array (8-byte entries), `+0x1E/+0x1F` = method-record array.

Per-object interp factory — **`CIdiObj::GetInterp` = `FUN_005c9cc0`**:
1. `Tcl_CreateInterp` → `obj+0xC0`.
2. `Tcl_SetAssocData(objInterp, "this", obj)` — binds the object under the key `"this"`.
3. for each class member: `Tcl_ObjSetVar2(objInterp, name, NULL, default, TCL_GLOBAL_ONLY)`
   — seeds members as the interp's globals.
4. registers all OO commands into the object interp.
5. `Tcl_MakeSafe` unless scripting disabled.

Eval pipeline: `FUN_00555b60` (profiling wrapper) → `FUN_006bc2c0:76`
`Tcl_EvalObjEx(objInterp, body, 0)`.

**Resolution INSIDE a method/obj_init/state/event body** (= object-interp global level):

| Construct | Resolves to |
|---|---|
| `set x v` | the object's own variable (per-instance) |
| `$x` (read) | the object's own variable (members pre-seeded) |
| `member x` | class decl; default copied into EVERY object's interp globals at GetInterp |
| **`global x`** | **the OBJECT's scope** (its interp's global) — NOT a shared cross-object global |
| `upvar`/`uplevel` | standard Tcl within the object interp (rare in class code) |
| `this` | NOT a variable — an assoc-data back-ref; the path resolver `CObj::ResolveRef` (`FUN_006055d0`) maps the token `this`/`.`/`..`/`obj` to the owning object |
| cross-object var | ONLY via `ref_get`/`ref_set $otherObj member` (`FUN_006260b0`/`FUN_00625ca0`) |

OO command FUN_ addresses (registration `FUN_0060fc70`): `obj_init` 00610e60,
`obj_exit` 00611240, `inherit` 00612ef0 (STATIC — copies members/methods at def time,
no virtual dispatch), `handle_event` 00611480, `member` 006131f0, `method` 006117d0,
`method_static` 00611b40, `method_const` 00611eb0, `state` 00612220, `state_enter`
00612960, `state_leave` 006125c0; runtime: `new` 00623ea0, `sel` 00623500,
`call_method` 00626970 (binds args into target object interp, evals body there),
`check_method` 00627560, `call_method_static` 00627940, `ref_get` 006260b0, `ref_set`
00625ca0; `def_class` 0062e870 (+ temp class-build interp `FUN_00600740`).

> So `global` inside a body is effectively a no-op-but-correct: you're already at the
> object interp's global level, so `global x` and bare `x`/`set x` all hit the same
> per-object variable. (This is why scripts use both `global gnome_gender` and bare
> `$gnome_gender` interchangeably — they resolve identically.)

---

## 2. Our PRE-FIX model — ONE shared interp + a scope stack (diverged)

> ⚠️ HISTORICAL: this section documents the bug BEFORE the §3 fix landed. The
> `global`-clobber described here is resolved (ActiveGlobalOverride) and
> `ref_get`/`ref_set` are implemented — see the top banner.


`TclInterpreter`: a single `List<TclScope> scopes`, `scopes[0]` = the ONE
interpreter-global, shared by every entity + all top-level code. `CurrentScope` =
top. `TclScope` = `{Vars, Aliases, Level}`. `global`/`upvar` use the per-scope
`Aliases` redirect table (`ResolveVar`, ≤32 hops).

- `set x v` (`TclBuiltins.cs:223`→`SetVar`): writes to **CurrentScope** (top).
- read `$x` (`GetVar`, `TclInterpreter.cs:195`): CurrentScope, then **non-standard
  fallback to `scopes[0]`** (`:202`) — reads leak the shared global; writes don't.
- **`global x`** (`TclBuiltins.cs:246`): aliases `x` → **`scopes[0]`** (shared). ← DIVERGENCE.
- `proc` (`InvokeProc`, `:130`): pushes a FRESH scope (clean locals).
- `RunInEntityContext` (`DigglesTclEngine.cs:1531`): pushes `ent.VarsScope` as the top
  frame for obj_init / state / event bodies. So `set`/`$` round-trip to the entity ✅,
  but `global` aliases to the shared `scopes[0]` ❌.
- `method`/`call_method` (`:1452`): no fresh frame — uses `ent.VarsScope` directly,
  shadow-saves/restores arg names; `global` again → `scopes[0]`.
- `member` (`:93`): decl-only; seeds `ent.VarsScope.Vars[name]=""` on `new` (`:1042`).
- `ref_get`/`ref_set`: **bulk-stubbed to "0"** (`:488`) — cross-object access is a no-op.
- `this`: never a real var; engine commands special-case the literal `this`/`$this`.

### Divergence summary
| Construct | Original (per-object interp) | Ours | Same? |
|---|---|---|---|
| `set x`/`$x` in body | object's own | `ent.VarsScope` (+ read leaks to `scopes[0]`) | ✅ (with leak) |
| **`global x` in body** | object's own | **shared `scopes[0]`** → entities CLOBBER each other | ❌ |
| method args | object interp | overwrite/restore in `ent.VarsScope` | ≈ |
| cross-object | `ref_get`/`ref_set` | stubbed "0"; `global`/shared scope is an unintended channel | ❌ |
| `$this` | assoc-data back-ref | not a var; literal special-cased | ⚠ partial |

The headline bug: **`global` writes go to ONE shared scope**, so e.g. every dwarf's
`global current_workplace; set current_workplace …` overwrites the same slot →
entities trample each other's state. Idle was the first visible symptom; AI/work/
reproduction all rely on per-instance `global`-declared vars too.

---

## 3. Faithful design — per-object namespace WITHOUT per-object interpreters (✅ IMPLEMENTED)

We do NOT create one `TclInterpreter` per entity (350+ interps × ~225 command
registrations = wasteful). We replicate the SEMANTICS with the single shared
interpreter + the existing per-entity `TclScope` + a "current object scope" pointer.
Cost: one Dictionary per entity (already have it) + an O(1) pointer swap per body
eval. **Negligible perf** — the real per-frame cost is ticking 350 state machines,
not scoping.

Design (per the our-impl analysis):
1. **`TclInterpreter.ActiveGlobalOverride`** (`TclScope?`): make
   `GlobalScope => ActiveGlobalOverride ?? scopes[0]`. This re-points `global`
   (`TclBuiltins.cs:246`), `SetGlobal`, and the read-fallback (`:202`) at the entity.
2. **Set/clear the override** in `RunInEntityContext` (`:1531`) and `CmdCallMethod`
   (`:1452`): while the entity scope is active, `ActiveGlobalOverride = ent.VarsScope`
   (restore in `finally`). For `method_static`, leave it null. Now `global x` inside a
   body aliases to the entity's own scope (per-object), matching the original.
3. **TWO-TIER read fallback for genuine shared globals.** Some values are TRUE
   process-globals set at file/init scope and read inside bodies (see Risks): keep the
   real `scopes[0]` as a SECOND fallback after the entity scope, so shared constants/
   tables still resolve, while `global` *writes* and per-instance reads stay
   object-local. (Lookup order in a body: entity scope → real `scopes[0]`.)
4. **`this` (optional, for faithfulness):** seed `ent.VarsScope.Vars["this"]` so a bare
   `$this` interpolates (engine commands already special-case the literal).
5. **Procs:** a `proc` called from within a body goes through `InvokeProc` (fresh
   frame) but reads `GlobalScope` lazily → with the override active, its `global`/
   fallback target the entity, matching "one interp per object." Verify the override
   stays set across the proc call (it does — it's active for the whole `Eval`).
6. **`ref_get`/`ref_set`:** implement for real (read/write another entity's
   `VarsScope.Vars[member]`) — the only sanctioned cross-object channel.

---

## 4. Risks — genuine shared globals (must stay resolvable)

Most `global` uses in class code are object-scope (safe under the new model). But the
script sweep flagged values that look process-global, set at file/init scope and read
inside bodies — these MUST remain resolvable (the two-tier fallback handles them):
- **`ANIM_STILL/ONCE/LOOP`** (`animclassinit.tcl:6-8`) — also `member`-declared, so
  seeded per-object; read via `global ANIM_LOOP` in z_dignwalk/z_work_prod. Safe
  either way (member-seed OR shared fallback).
- **Tech-tree / sparetime tuning tables** `ttt*_<Class>`, `stt*_<Class>` (`techtreetunes.tcl`,
  `sparetimetunes.tcl`) — read via `subst \$tttitems_$classname` etc. in
  `genericprod.tcl`/`z_procs.tcl`. NOTE: zwerg.tcl `call`s these FROM `obj_init`
  (lines ~142-145), so under the per-object model they're set per-object (not truly
  shared) — but the two-tier fallback makes them work regardless of where they land.
- **`temppos`** (campaign) — a real shared global read by `split_template`
  (`DigglesTclEngine.cs:1660`); set at campaign scope (no entity active) → lives in
  real `scopes[0]`. Keep that path working.

`upvar`/`uplevel` in class code: essentially none (only customcommands/file_wrapper/
story) → no risk. Per-instance state (80+ vars in zwerg obj_init: counters, flags,
lists, refs) is all `set`/`$` → already entity-scoped; the fix only corrects `global`.

---

## 5. Implementation plan (✅ DONE — retained as the landed checklist)

1. `TclInterpreter`: add `ActiveGlobalOverride`; route `GlobalScope`/`global`/`SetGlobal`/
   read-fallback through it; implement the two-tier read fallback (entity → real global).
2. `DigglesTclEngine`: set/restore the override in `RunInEntityContext` + `CmdCallMethod`
   (instance only). Seed `this`. Implement real `ref_get`/`ref_set`.
3. Tests: per-instance isolation (two entities with same `global var` don't clobber);
   shared-global still readable inside a body; `ref_get`/`ref_set` cross-object.
4. Re-run the campaign load (loadRealClasses on) — verify idle/work/AI behave per-
   instance, console clean. Then revisit generic idle (it should now "just work"
   because each entity's `seq_idle_*` is private).
5. THEN continue: cutscene/sequencer module (incremental), and a later "clean
   architecture" refactor once behaviour is faithful.

Companion: `TCL_INIT_LOADING.md` (class load), `TCL_INTERPRETER_PLAN.md`,
`ANIMATION_CONTROLLER.md`. Decompile refs in §1.
