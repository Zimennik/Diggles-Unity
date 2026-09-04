# Diggles Engine Architecture — RE Map & Unity Port Design

> §§1–7 are reverse-engineering ground truth (VAs/laws) and are left as-is.
> §8 is the port-side design proposal; much of it has since landed — see the
> corrected pathfinding/timer/spawner rows below.

**Purpose.** Walk the original `Diggles.exe` top-down — entry point,
init, frame loop, subsystem ownership, Tcl integration, resource
lifecycle — and propose how each piece maps to a cleaner C# / Unity
architecture. This is the architectural foundation the rest of the
port hangs on.

**Status.** Sections 1–5
verified against decompiled sources + CallStat strings. Sections 6–7
sketched from incidental evidence; deeper trace needed before relying.
Section 8 is the design proposal — no longer purely aspirational: the
Tcl bridge, entity spawner, animator, timer scheduler, terrain builder,
and the faithful pathfinder pipeline have all landed (see corrected rows
in §8.2). The proposal's naming (e.g. `GameSimDriver`, `FogOfWarService`
as one clean service) is directional; the shipped code often uses
different type names — treat §8 as intent, not a file index.

**Sources of truth.**
- `re/decompiled/Diggles/_global/*.c` — Ghidra decomp output, ~11k files.
- `re/decompiled/Diggles.callstat_rename.tsv` — function names recovered
  from the binary's embedded CallStat profiler strings.
- `re/decompiled/Diggles.source_attribution.tsv` — function-address →
  `wiggles/...cpp` source file mapping.
- `dumps/diggles_loaded.dmp` (1 GB) — full process dump of running game;
  `dumps/extracted/*` — pre-extracted singleton regions.

Addresses are VAs (`ImageBase = 0x400000`). Filenames in
`_global/<addr>_<symbol>.c` form, so `006b86a0_CWigglesApp_Run.c` is
`CWigglesApp::Run` at VA 0x6b86a0.

---

## 1. Entry & boot sequence

```
006cf39b entry; PE entry, RVA 0x2cf39b
  ├─ ___security_init_cookie
  └─ ___scrt_common_main → FUN_006cf21f; CRT init + __initterm
        └─ FUN_006b7a50(hInstance=0x400000, 0, cmdLine, showCmd)   ; WinMain
              ├─ __controlfp(0x300, 0x100)            ; FP exceptions off
              ├─ _setlocale(0, "English")
              ├─ FUN_006d470b(GetTickCount())        ; seed RNG
              ├─ GetModuleFileNameW + FUN_006df394   ; chdir to exe dir
              ├─ SetPriorityClass(NORMAL_PRIORITY_CLASS)
              ├─ GetDriveTypeW                       ; require DRIVE_FIXED
              ├─ branch on cmd-line:
              │    -version  → write version.txt, exit
              │    -infofile → dump info, exit
              │    (else)    → continue
              ├─ CoInitialize(NULL)
              ├─ FUN_006b8380()              ; ??? — likely log init
              ├─ GlobalMemoryStatus(&local_848)
              ├─ FUN_006b8f50()              ; ??? — possibly version-check / EULA
              ├─ -vmem path: pool allocator setup, sizes 0x40/0x100/0x400/0x1000
              ├─ DAT_00768760 = FUN_0042be80 ; install allocator vtable hooks
              ├─ FUN_006ba810(0xf0)          ; alloc primary app object (240 B)
              ├─ FUN_006ba810(0x112)         ; alloc secondary app object (274 B)
              ├─ FUN_006b8fb0(cmdLine)       ; parse command-line into globals
              ├─ tclerrors.txt setup
              └─ CWigglesApp::Run()          ; ← main app entry, 006b86a0
              (cleanup on return: CoUninitialize, fclose, etc.)
```

Inside **`CWigglesApp::Run` (0x6b86a0)**:

```
1. CallStat tracker register
2. FUN_006bc020(this); trivial; initializes two ref-counted slots
                              ; on the app object — NOT meaningful init
3. Tcl_DeleteInterp (no-op first time)
4. Tcl_CreateInterp; ← Tcl interpreter is created here, ONCE,
                              ;   for the lifetime of the app. Saved in
                              ;   local_20, refcount tracked in
                              ;   DAT_00772dd4.
5. if (app.field[2] != 0):; "do we run game thread?" — false for some
                              ; modes (-infofile etc.)
   a. FUN_00440000("CGameThread"); trace marker
   b. local_1bc[0] = Threads::CGameThread::vftable; in-place vtable bind
   c. FUN_00440bd0; CGameThread default ctor
   d. Sets ~25 fields on the CGameThread stack object (init):
      - Frame-rate target constants: 0x3f99999a (1.2f), 0xc47a0000 (-1000),
        0xbf4ccccd (-0.8f) — camera dolly defaults
      - GetTickCount() ×2 into +0x9c, +0xa0 (per-second & per-25-ms
        scheduler anchors)
      - local_88 = local_84 = 0xc47a0000 = -1000 — m_fViewMoveToX/Y
        (camera-target sentinel = "no target", checked in CGameThread::Draw)
   e. FUN_006ae6b0(app + 0xc, allocator); CGameThread::Init(...)
      — runs CALL_006aeb30 = CGameThread::Init (file 0x6aeb30 missing
      from decomp dir but addr is named in callstat_rename.tsv)
   f. FUN_00448cd0(&gt_obj); ← drives the actual game-loop
      iteration; calls CGameThread::Cycle until quit flag
   g. FUN_006ae630; CGameThread::Shut
6. Tcl_DeleteInterp(local_20)
7. if (DAT_00772dd4 == 0) Tcl_Finalize; only on last interp
8. Return to WinMain cleanup
```

**Takeaway.** The Tcl interpreter is **one global instance** created
before the game thread starts, destroyed after it exits. There is no
nested / per-mission interpreter — all scripts share state.

---

## 2. Main loop — `CGameThread::Cycle` @ 0x6b37d0

The actual driver is `FUN_00448cd0`, which calls `CGameThread::Cycle`
each iteration until the quit flag flips. **CallStat strings inside
Cycle name every phase** — we have ground-truth phase breakdown.

```
CGameThread::Cycle; one iteration = one frame
├── ServiceManager::Get.Tick; first call into globals
├── TimeManager (DigglesPort): SetTimer / IsAllowed
│ │ IsAllowed returns true periodically →
│ └─ AchievementManager::Store(9, 0); play-time achievement
├── FUN_005653f0; ??? — early per-frame hook
├── SetThreadPriority based on DAT_007683c8 mode
│ 0 → THREAD_PRIORITY_NORMAL (0)
│ 1 → THREAD_PRIORITY_HIGHEST (2)
│ 2 → THREAD_PRIORITY_NORMAL (0)
├── Heap-check (every 30 sec) — writes "Heap check %d.%03d secs"
│ to log file at DAT_007724b8
│
├── if (paused / *(char*)(param_1+0x39) != 0):
│ // server-only or paused path: receive net msg, redraw once
│     ...
└── else (normal frame):
      ├── DVar8 = GetTickCount()       ; now-ms
      ├── if (now > app.tickPerSec):  app.tickPerSec += 1000 ; FUN_006b75e0()
      │                                                    ; (per-sec hook)
      ├── if (now > app.tickPer25ms): app.tickPer25ms += 0x19 (=25 ms)
      │                                FUN_0054df60()  ; (per-25-ms hook;
      │                                                  also called inside
      │                                                  SrvDoThink "1st")
      ├── FUN_00565940 → debug-server socket bind (TCP :8082) when enabled
      │
      ├── ── "CNetMgr::Get().Tick();" ──
      │     FUN_005b28d0()  + FUN_005b2ee0()
      │
      ├── ── "CGameThread::Cycle() SrvDoThink" ──
      │     CGameThread::SrvDoThink()                ; SEE § 2.1
      │
      ├── ── "CGameThread::Cycle() DoRenderTick" ──
      │     (renderer pre-pass; no explicit call captured)
      │
      ├── ── "CGameThread::Cycle() Draw" ──
      │     FUN_006b5d90()  = CGameThread::Draw      ; SEE § 2.2
      │
      └── ── "CGameThread::Cycle() Sleep" ──
            // Adaptive frame limiter
            // - measures real elapsed dt
            // - integrates with constants 0x73 70-area
            //   (DAT_007371a8, _DAT_00737100, _DAT_007372a0, DAT_007372a8 …)
            // - rolls Sleep(DAT_00772dd8) — variable sleep amount
            // - smooths a PID-ish term into the next-frame target
```

### 2.1 SrvDoThink @ 0x6b4a30 — game-logic tick

```
CGameThread::SrvDoThink
├── if (game-time game_t <= real_t): // run one logic step if we owe
│ "SrvDoThink 1st"
│ FUN_0054df60; per-25-ms hook (input sample? UI?)
│ "SrvDoThink UpdateLightMap"
│ "SrvDoThink GetObjMgr.DoMaintenanceTick;"
│ FUN_005d4250
│ "SrvDoThink CAiCentral"
│ FUN_004244e0 + FUN_00424680 (AI dispatch)
│ FUN_004244e0 + FUN_00424560
│
├── while (game_t <= real_t AND local_24 > 0): // catch-up, max 10 steps
│ game_t += DAT_007371a8; the FIXED STEP
│ "SrvDoThink ProdControlMgr"
│ FUN_005ee9d0 + FUN_005eed70; production planner tick
│ //... per-step game-logic...
│
└── Other named sub-phases also surface in this function (visible in
    CallStat strings across sister functions):
    "SrvDoThink() Physics"
    "SrvDoThink() GetObjMgr().HandleEvents() All Tick"        ; iterate
                                                              ; entities[]
                                                              ; → CObj::Tick
    "SrvDoThink() GetObjMgr().HandleEvents() Events"          ; FUN_00605f00
                                                              ; FUN_00606230
                                                              ; → event dispatch
    "SrvDoThink() GetObjMgr().HandleEvents() Active Objs"     ; second pass
    "SrvDoThink() GetObjMgr().HandleEvents() Active Objs Evt" ; → CObj::HandleSingleEvent
    "SrvDoThink() GetObjMgr().DoObjUpdates()"
    "SrvDoThink() GetObjMgr().DoTrans()"
    "SrvDoThink() FOW"                                        ; CFogOfWar::UpdateFoW
    "SrvDoThink() World::g_pMap->Tick()"
    "SrvDoThink() World::GetMapValidator().Service()"
    "SrvDoThink() TriggerMgr"
```

**Fixed-step pattern**: `DAT_007371a8` is the simulation step; the engine
runs **up to 10 logic ticks per render frame** to catch up if the previous
render lagged. This is the "Glenn Fiedler accumulator" pattern, decades
older than the article.

**State-body / event integration with Tcl** happens inside
`CObj::Tick` (0x5cc600) and `CObj::HandleStates` (0x5cac90) and
`CObj::HandleSingleEvent` (0x5caa00) — these eval Tcl bodies that the
class system registered via `state NAME {... }` and `handle_event` (see
§ 4).

### 2.2 Draw @ 0x6b5d90 — render frame

CallStat-named phases, in order:

```
CGameThread::Draw
├── "Draw if (m_fViewMoveToX > -1000.0f)"; camera dolly tween
│ // m_fViewMoveTo[XY] = -1000 sentinel ⇒ no active tween
├── (Draw camera setup, view matrix, follow object)
├── "Draw Pre-Loading"; lazy-load any visible-but-unloaded assets
├── "Draw DoLinkUpdates"; resolve object-to-object link pointers
├── "Draw BeginBuildFrame"; D3DMgr::BeginScene equivalent
├── "Draw Sel/Hover Change"; selection / hover glow state
├── "Draw Sound"; CSoundMgr::Update spatial sounds + music
├── "Draw MsgPump"; SDL_PollEvent loop (Windows msg pump)
│ (input becomes engine events here; routed to ViWin* + Tcl triggers)
├── "Draw CDisplay::Draw"; CDisplay::Draw @ 0x4781b0
│ ├── CDisplay::Draw sub-regions 1..13 (named in 004781b0_CDisplay_Draw.c)
│ └── CDisplay::DrawObjects @ 0x473cc0 — batched object render:
│ "DrawObjects AlphaObjs"
│ "DrawObjects SelHoverGlow"
│ "DrawObjects Specials"
│ "DrawObjects Particles"
│ "DrawObjects DummyRenderjobs"
│ "DrawObjects Mouse"
├── "Draw Render Sync 1"; CPU/GPU sync 1
├── "Draw EndBuildFrame"; D3DMgr::EndScene
│ // CDXExecBuf::Render sub-phases (DX7 command buffer flush):
│ // "CDXExecBuf::Render Stateswitch"
│ // " DrawVB / DrawPrimitive / DrawIndexedVB /
│ // DrawIndexedPrimitive / Per-Job"
├── "Draw Render Sync 2"; CPU/GPU sync 2 (probably Flip wait)
└── "Draw StatusUpdate"; HUD / debug overlay update
```

The DX7→D3D11 shim (`DigglesPort.dll`) sits between `EndBuildFrame` and
hardware. For the Unity port everything from `BeginBuildFrame` onward
is replaced by Unity's render pipeline.

### 2.3 Frame budget summary

| Phase | Frequency | Work |
|--------------------------------|---------------------|-----------------------------------|
| Per-second hook                | 1 Hz                | bookkeeping (`FUN_006b75e0`)      |
| Per-25-ms hook                 | 40 Hz               | input / UI (`FUN_0054df60`)       |
| **Logic step (SrvDoThink)** | **Fixed `DAT_007371a8`, up to 10/frame** | physics, AI, events, objects, fog, prod |
| Net tick | each frame | CNetMgr (multiplayer only) |
| Render | each frame | CDisplay::Draw + DX flush |
| Sleep | each frame | Adaptive limiter |
| Heap check | 30 s | log allocator stats |

**Open: confirm `DAT_007371a8`'s actual float value.** The dump can
tell us — `dumps/diggles_loaded.dmp` at VA 0x7371a8 holds the running
game's step size. Empirically the per-25-ms hook is named "1st" and
runs inside `SrvDoThink`'s leading branch — so the **default** sim step
is 25 ms = 40 Hz. Animation playback is independently fixed at 10 Hz
(`DAT_007373f0 = 10.0`, see `re/notes/ANIMATION_SYSTEM.md`).

---

## 3. Subsystem class map

Built from `Diggles.callstat_rename.tsv` (46 entries, "already_named"
status), a systematic grep across CallStat strings in decomp files,
and `recon_summary.md`'s source-tree enumeration. **Every class listed
has direct evidence** (an address, a CallStat string, or both).

### Core orchestration

| Class | Key methods (VA) | Source file |
|------------------------|-----------------------------------------------|--------------------------------------------|
| `CWigglesApp` | `::Run` 0x6b86a0 | `wiggles/Wiggles/Wiggles.cpp` |
| `CGameThread` | `::Init` 0x6aeb30, `::Cycle` 0x6b37d0, `::SrvDoThink` 0x6b4a30 / 0x5d38c0, `::Draw` 0x6b5d90, `::CreateMap` | `wiggles/Wiggles/Threads/GameThread.cpp` |

### Object model & Tcl bridge

| Class | Key methods |
|------------------------|---------------------------------------------------------------|
| `CObj` | `::Tick` 0x5cc600, `::Tick Particles` 0x5d89e0, `::HandleStates` 0x5cac90, `::HandleSingleEvent` 0x5caa00; member access via `CObj::GetObj` 0x5c7050, `::GetObjSafe` 0x5c71c0 |
| `CObjMgr` | `::HandleEvents` (via CallStat: All Tick / Events / Active Objs), `::DoObjUpdates`, `::DoTrans`, `::DoMaintenanceTick`, `::ShutDynObjects` 0x5d2920 |
| `CTclClassMgr` | per `recon_summary` — manages content classes from Tcl |
| `CTclClassDefMethods` | `::Register` 0x60fc70 |
| `CTclGlobalMethods` | `::Register` 0x618010 (registers global Tcl commands like `new`, `sel`, `set_pos`, …) |
| `CTclMapEditMethods` | `::def_event` 0x66c170 |
| `CTimerEvtMgr` | (timer_event scheduling — file enumerated in recon_summary) |
| `CTclRootObj` | 0x5c7410 — root of Tcl object tree |
| `CTclObjInterp::Init` | 0x600740 — sets up the engine's Tcl command tables |

### World / map / fog

| Class | Key methods |
|------------------------|---------------------------------------------------------------|
| `CMap` | `::Init`, `::Clear`, `::Shut` (in Map.cpp range 0x593-0x594) |
| `CMapMgr` / global | tile array singleton at `DAT_0077241c` (124 B/tile) |
| `CBlockDB` | `::LoadDB` 0x565ab0 |
| `CMapBeautifier` | `::ScanAtPos` 0x58b130 (the `block_idx` resolver) |
| `CMapValidator` | `::Service` (CallStat in SrvDoThink) |
| `CFogOfWar` | `::UpdateFoW` 0x57f5c0, `::NewVisibleBlock` 0x57dfc0, `::RemoveFog*` 0x57e120/0x57ea10/0x57f170, `::FadeBlocks` 0x57e710, `::Visible` 0x57ff40, `::FindNextVisible` 0x580260 |
| `CFluid` | `::Tick` 0x598920, `::Draw` |
| `CMapPathfinder` | `::Calc1` 0x59d440 (CKPoint) + 0x59d640 (CVec3D) |
| `CPlaceFinder` | `::FindPlace` 0x5a9700, `::FindPlaceLongRange` 0x5aa310 |
| `CLevelGen` / `CMapGen`/ `CDigMark` / `CLightSrc` | enumerated by source tree only — no CallStat refs |

### Rendering

| Class | Key methods |
|------------------------|---------------------------------------------------------------|
| `CDisplay` | `::Draw` 0x4781b0 (sub-regions 1..13), `::DrawObjects` 0x473cc0 (AlphaObjs / SelHoverGlow / Specials / Particles / DummyRenderjobs / Mouse) |
| `CScapeRenderer`       | `::RenderScape` 0x4c4de0 — terrain renderer (per-tile FUN_004ca830) |
| `CScapeIconMgr` | `::Draw` 0x4c0940 |
| `CModelRenderer` | `::RenderShadowMeshPro` 0x48e5d0 + ~10 sister functions |
| `CGroundRenderer` | (file in source tree; not yet pinned to address) |
| `CWallRenderer` | (file in source tree) |
| `CDummyObjMgr` | `::FlushRenderJobs` 0x5b8930 |
| `CMeshDestructor` | (file in source tree — gibs / breakable mesh) |
| `CParticle` | (file in source tree; tied to `DrawObjects Particles`) |
| `CDxBase` / `D3DMgr` | DX7 wrapper: `::InitStdShaders` 0x457b90, `::ApplyShader` 0x45e120, `::UpdateD3DRenderState` 0x45e240 |
| `CDXExecBuf` | `::Render` 0x4528f0 (state/Draw* phases) |

### Asset I/O

| Class | Key methods |
|------------------------|---------------------------------------------------------------|
| `C3DDB` | `::Persist(CUniArc&)` 0x402ff0 (sub-phases: Header / CMaps / Objects / Meshes / Materials / Anims / FollowMesh / Shadows / Mapping / NLst/UVLst/VLst/IdxLst maps) |
| `C3DDBMgr` | 0x460460 — the runtime `db_load` registry |
| `CMaterial` | `::Persist` 0x402220 |
| `CMesh` | `::Persist` 0x402640 |
| `CAnim` | `::Persist` 0x4029a0 |
| `CObj` (asset side) | `::Persist` |
| `CShadow` | `::Persist` |
| `CCMap` (.3db cmap) | `::Persist` |

### Actions (per-entity behaviour)

| Class | Role |
|------------------------|---------------------------------------------------------------|
| `CAct_Walk` | walk path |
| `CAct_Walk_AutoRetry` | walk with auto-retry on pathing failure |
| `CAct_Climb` | climb mesh / ladder |
| `CAct_Door` / `Act_Elevator` / `Act_Ladder` | interactive objects |
| `CAct_MetaWalk` | high-level "go to" composed of multiple Walk steps |
| `CAct_WalkFloor` / `Act_WalkObject` | walk on specific surface / over object |
| `CAct_Lore` | `::GetPosRotSpeed` 0x413ea0, `::SetLorePos` 0x4143d0 — info/dialogue actor |
| `CFloorPathMap` | floor pathing helper |

### AI / production / fight

| Class | Role |
|------------------------|---------------------------------------------------------------|
| `CAiCentral` | top-level AI loop (called as CAiCentral phase in SrvDoThink) |
| `CAiGameControl` | per-player AI control |
| `CProdPlanner` | `::GnomeIdle` 0x434a80, `::MaintenanceTick` 0x433a70 |
| `CProdPlannerMgr` | `::Tick` 0x432720, `::Reset` 0x432970 |
| `CProdAllocator` | resource → task allocation |
| `CTechTree` | upgrade graph |
| `CFightMgr` | combat (file in source tree) |
| `CSparetime` / `CStoryMgr` / `CTransportLogic` / `CTriggerMgr` | content / story / cargo / trigger plumbing |

### Audio

| Class | Role |
|------------------------|---------------------------------------------------------------|
| `CDXSound` | `::CreateSimpleBufferFromFile` 0x454b40, `::GetFileFormat` 0x4548f0 |
| `CDXSound::CSimpleBuffer` | `::Init` 0x4545f0 |
| `CSoundAnimMgr` | sound coupled to animation events |
| `CSoundMultiplexer` | mixer (channel pool) |
| `CSoundSrcMgr` | spatial sound sources |

### UI

| Class | Role |
|------------------------|---------------------------------------------------------------|
| `CDesktop`             | top-level UI root (`FUN_006b5d90` is `CGameThread::Draw,CDesktop::Tick` ambiguous_2) |
| `ViWinRoot` | `::Tick` 0x529c10 — UI root tick |
| `ViWin*` | 30 widget classes (Bar / BigInfo / Chat / Clock / DragBar / Frame / Inventory / ListCtrl / PushButton / ScrollField / TechTree / …) |
| `ViFont` / `ViBitmapMgr` / `ViControlMgr` / `ViEvt` | font / bitmap / control / event helpers |
| `VIMapEdit` | map editor mode (only when `get_mapedit` returns true) |
| `UIRunes` | rune-style HUD overlay |

### Networking (deferred — Phase 0+)

| Class | Role |
|------------------------|---------------------------------------------------------------|
| `CNetMgr`              | `::Tick` (CallStat in CGameThread::Cycle as `CNetMgr::Get().Tick();`); `FUN_005b28d0` + `FUN_005b2ee0` |
| `KNetConnection` / `KNetMsg` / `KNetSession` / `NetMapUpdate` / `NetObjUpdate` / `SrvObjUpdateMgr` | session + delta sync |

### Foundation (KStdLib)

| Class | Role |
|------------------------|---------------------------------------------------------------|
| `KStr` | string class — 413 attributed funcs |
| `DSArray` / `DSFastArray` / `SafeArray` | container templates — 354 attributed funcs |
| `KUtfIO` | I/O wrappers — relevant for.3db /.BDB /.sav reader |
| `PropDB` | property database — likely save serialization |
| `KVM_Int` / `TclHash` / `TclWrap` | Tcl integration helpers |
| `CUniArc` | archive stream (used by `*::Persist` family) |
| `DirScan` / `Pic` / `UniArc` | misc utilities |

### Globals worth knowing

| Address | Role |
|-------------|-----------------------------------------------------------------------|
| `DAT_00772400` | **the game-state mega-struct** (referenced everywhere as `(DAT_00772400 + N)`); offset map: +0x8a = "logic enabled" bool, +0x8c, +0x90 = misc flags, +0x93 = "draw-render enabled" bool, +0x100 = mapedit mode, +0x59a = log-rotation bool, +0x5b4 = current map ptr, +0x100..+0x500 area = HUD widgets / map dim, +0xa088 = real-time double, +0xa0b0 = game-time double, +0xa098 = "ai enabled" bool, +0xa0d4 = camera anim state, +0x1ac = some smoothed float |
| `DAT_0077241c` | tile array base (124 B per tile, `world.X × world.Y`) |
| `DAT_00772440` | TCL command table base (two arrays at +0x14 and +0x0c, populated by ctor static-init) — see § 4 |
| `DAT_007724b8` | log FILE* |
| `DAT_007724bc` | ref-counted "primary app object" (small struct, see CWigglesApp::Run) |
| `DAT_00772dd4` | Tcl_Interp refcount |
| `DAT_007683c8` | thread priority mode (0/1/2) |
| `DAT_00768374` | "every N cycles" threshold for full-object-tick |
| `DAT_007371a8` | fixed simulation step (double) |
| `DAT_007373f0` | animation tick rate = 10.0 Hz |
| `DAT_0073708c` | per-block elevation scale = 0.5 |
| `DAT_00737520` | terrain Z base = 7.0 |
| `DAT_00768960` / `DAT_00737090` | render rate / window-time constants |

---

## 4. Tcl integration points

The Tcl interpreter is **the high-level script controller** with the
C++ engine providing every implementation. Two registration
mechanisms feed it:

1. **Engine commands** registered at startup by `CTclObjInterp::Init`
   (0x600740). This walks two static command tables at `DAT_00772440`
   (one for global functions like `new` / `sel` / `set_pos`, one for
   class methods); each table entry is `{ string_ptr, function_ptr }`,
   processed via `Tcl_CreateObjCommand(interp, name, fn, 0, 0)`. The
   tables themselves are populated by C++ static-init in the various
   `TclGlobalMethods.cpp` / `TclObjMethods_AG.cpp` / `_GetSet.cpp` /
   `_OU.cpp` / `TclMapEditMethods.cpp` translation units. (~341
   commands used ≥ 10× in script corpus.)

2. **Content classes** declared in `.tcl` via `def_class`, dispatched
   through `CTclClassMgr` and `CTclClassDefMethods::Register`
   (0x60fc70). Each Tcl class registers methods/states/event handlers
   whose bodies are Tcl strings stored on the class.

**Where the engine CALLS Tcl** (Tcl_EvalObjEx callsites — 11 total):

| File / Address | Likely role |
|---------------------------------|----------------------------------------------|
| `006b86a0` `CWigglesApp::Run` | Initial `systeminit.tcl` source load |
| `006bc230` / `006bc2c0` / `006bcb00` | `call` / `source` / `load_file` Tcl loader implementations |
| `0060fc70` `CTclClassDefMethods::Register` | bodies registered against classes |
| `005c9cc0` (in CObj.cpp range) | `CObj::HandleSingleEvent` evaluator: runs the Tcl handler body when an event fires on an entity (`handle_event NAME {... }`) |
| `00600740` `CTclObjInterp::Init`| (init-time loads) |
| `004f5d30` / `004e7990` | likely sequencer / cutscene queue exec |
| `0042af20` / `00429d00` | likely production / AI eval |

**Frame-time Tcl execution paths:**

1. **Event dispatch.** In `SrvDoThink → GetObjMgr.HandleEvents
   Active Objs Evt`, for each pending event on each active entity,
   `CObj::HandleSingleEvent` (0x5caa00) looks up the `handle_event`
   body on the entity's class chain and evals it via Tcl_EvalObjEx.
2. **State body.** In `CObj::HandleStates` (0x5cac90), if the entity
   has a current `state NAME`, its body Tcl is evaluated (per-tick or
   per-N-ticks — TBD; the function `CObj_HandleStates 2` exists, hint
   at multi-pass).
3. **Timer events.** `CTimerEvtMgr` matures scheduled events and
   places them on the per-entity event queue → routed back through
   path #1.
4. **Triggers.** `CTriggerMgr` (in SrvDoThink) fires `Trigger_*` Tcl
   bodies when their conditions hit.
5. **Sequencer queues** (`+sq_*`, `do_wait`) — Tcl coroutine-style
   command lists driven by the cinematic system; advanced each
   frame.

**Latin-1 / encoding.** Diggles ships `.tcl` files in Windows-1252 /
Latin-1 (German umlauts in literals). The original `tcl83.dll` uses
the default 8-bit interp; the C# reimplementation pins Latin-1 reads
(see `Docs/TCL_INTERPRETER_PLAN.md` § Out of scope).

---

## 5. Resource lifecycle

### 5.1 `.3db` files (`C3DDB::Persist` 0x402ff0)

Registered via `db_load <name>` Tcl command (handler `FUN_0064bc90` →
`FUN_004ce0c0`). `animinit.tcl` calls `db_load` in a fixed order for
~40 db files. Each call:

1. Open `data/3db/<name>.3db`.
2. Run `C3DDB::Persist` over the byte stream — sub-phases (named in
   the CallStat strings inside `00402ff0_C3DDB_Persist.c`):
   `Header → CMaps → Objects → Meshes → Materials → Anims →
   FollowMesh → Shadows → Mapping → Lst Count&Init → NLst map →
   UVLst map → VLst map → IdxLst map`.
3. Store in the global `C3DDBMgr` (0x460460) under `<name>`.

Then per `Object` (e.g. `mann.standard`), the animator looks up the
mesh chain via `Object.indices[]` → `Anim.indices[]` → `Mesh`. See
`re/notes/ANIMATION_SYSTEM.md` and `WORLD_SPAWN_PIPELINE.md` § 4.

### 5.2 BDB (`CBlockDB::LoadDB` 0x565ab0)

Loaded once at world init (probably in `CGameThread::CreateMap`). One
of `BlockDB[0-2].BDB` is selected per template (`urw_*` campaign uses
`BlockDB2.BDB` confirmed via runtime dump). Layout: header → cell
table → per-block aux (89 B) → index pool → descriptors → palette →
per-cell vertex/normal index streams. See
`unity/Diggles/RENDERING_PIPELINE_COMPLETE.md` § 2.1.

### 5.3 PMP / L2M (per-template)

Loaded by `map_template <path> X Y` and `map_setlayer2 X Y <l2m>`
Tcl commands. `map_template` blits a width × height grid of
`block_idx + heightmap + zone + terrain_type` at `(X, Y)`. `map_setlayer2`
overlays L2M cell IDs (material + animation metadata). Several
templates per campaign; `split_template` recursively loads 16×16
sub-chunks.

### 5.4 Textures (`.tga`, `.kft` fonts)

Loaded on-demand via `CScapeRenderer` / `D3DMgr::SetTexture`. Engine
caches per-class. SEK-Ost's `DigglesPort.dll` re-implements all
texture binding paths on D3D11 — `ChunkedTexture@Texture`,
`Image@Texture` are exported types.

### 5.5 Audio (`.wav` / `.mp3`)

`sound bind NAME PATH` registers a buffer via
`CDXSound::CreateSimpleBufferFromFile` (0x454b40). `sound play NAME`
plays it through `CSoundMultiplexer`. SoLoud back-end in DigglesPort.

### 5.6 Persistence pointer chain

```
data/
├── 3db/ → C3DDBMgr (db_load chain in animinit.tcl)
├── BlockDB[0-2].BDB → one CBlockDB per campaign
├── Templates/
│ ├── *.pmp → map_template (per-tile block_idx baked in)
│ ├── *.l2m → map_setlayer2 (cellId overlay)
│ └── *.ent → entity placement (binary; per file 1.4 K loaded by ??)
├── Texture/ + Ground/ + GUI/ → loaded on-demand via D3DMgr
├── Sound/ + Music/ → loaded by sound bind / adaptive_sound
├── Scripts/ → call'd by Tcl from systeminit.tcl onward
├── GameScripts/ → same; per-game callbacks
├── Gamesave/*.sav → custom save (gzip + PropDB-style binary)
└── alphabet.bin + sphere.dat + texturelist.txt + messages.txt → constants
```

---

## 6. Save / load pipeline (lower confidence)

Evidence so far is incidental — no CallStat string mentions save/load
explicitly. What we know:

- `data/Gamesave/*.sav` — 98 files, custom format (gzip + binary).
- `Wiggles/Basics/PropDB.cpp` has 7 attributed funcs — likely the
  serialization helper (key/value typed property bag).
- `KUtfIO` provides the file-stream wrappers (8 funcs).
- `CUniArc` is the archive abstraction used by `*::Persist` for the
  asset side; very likely reused for save data.
- The.sav format has not yet been parsed by this project.

**Sketch (needs verification):** save = gzip-stream of `CUniArc` payload
containing entity records (each entity's class name + member vars +
position/rotation/state) + map state (tile-by-tile flags, zone
overrides) + Tcl globals + fog-of-war bitmask + production state.

**Next step when this becomes urgent:** decompile the `.sav`-write
function (find it via xref to `OpenA`/`WriteFile` on a path matching
`*.sav`), then walk the field writes.

---

## 7. Shutdown

Sketch from `CWigglesApp::Run` exit + `CGameThread::Shut`
(`FUN_006ae630`):

```
FUN_00448cd0 returns when game thread sets the quit flag.
└── FUN_006ae630 = CGameThread::Shut
    ├── CObjMgr::ShutDynObjects (0x5d2920)
    ├── CMap::Shut
    └── (other dtors run via vtable cascade)
CWigglesApp::Run continues:
├── Tcl_DeleteInterp(interp)
├── if (DAT_00772dd4 == 0) Tcl_Finalize
├── FUN_006cecd8(piVar8, 0xc); presumably "free primary app object"
├── if (allocator) FUN_006b8310(allocator); allocator dtor
├── if (logFile) _fclose(logFile)
├── CoUninitialize
└── FUN_006b8380 x 2
WinMain returns to CRT; _exit(iVar7).
```

Order matters: object manager → map → Tcl → app → COM → CRT.

---

## 8. Mapping to the Unity port — proposed C# architecture

The point of this doc is NOT to copy the original; it's to build a
cleaner, idiomatic-Unity equivalent that preserves the behaviour the
Tcl scripts depend on. Per [[feedback-reuse-original-tcl]] all `.tcl`
runs unmodified, so the C# surface is fixed at the boundary — but
inside the boundary we design fresh.

### 8.1 Cardinal design rules (port-side)

1. **Frame loop owned by Unity.** Don't replicate `CGameThread::Cycle`.
   Unity's `Update` / `FixedUpdate` / `LateUpdate` already give us a
   well-defined frame structure. Map original phases to Unity callbacks.
2. **No threading.** Original is effectively single-threaded already
   (the "thread" naming is misleading — there's no concurrent loop).
   Keep it single-threaded; lean on `async/await` only if we need it
   for I/O.
3. **DI via VContainer + SignalBus / Publisher.** No singletons-as-globals
   (the original's `DAT_00772400` mega-struct is the anti-pattern to
   avoid). Each subsystem is a service registered in the LifetimeScope.
4. **Events as `Publisher<T>` / `SignalBus`.** The original's
   `CObjMgr::HandleEvents` queue is replaced by typed signals. Tcl
   `handle_event NAME` handlers register via a Tcl-side bridge that
   subscribes to the matching C# signal.
5. **State bodies stay in Tcl.** No FSM rewrite. The C# side schedules
   ticks; each tick runs the entity's `state NAME` body Tcl, period.
6. **Asset import via `ScriptedImporter`** (already in place). Don't
   reinvent C3DDB::Persist's run-time scan — pre-bake at import.
7. **Render via URP**, replacing DX7 + DigglesPort entirely.

### 8.2 Subsystem-by-subsystem mapping

| Original subsystem | Original entry | Proposed C# / Unity equivalent | Why differs |
|---|---|---|---|
| `CWigglesApp::Run` | 0x6b86a0 | `DigglesAppHost: MonoBehaviour` on a scene-root object; wires VContainer scope, loads `systeminit.tcl` in `Awake`, owns the interpreter lifecycle | Unity scenes replace ad-hoc app object |
| `CGameThread::Cycle` | 0x6b37d0 | Implicit (Unity's main loop) | Unity gives us Update / FixedUpdate / LateUpdate — no manual loop needed |
| `CGameThread::SrvDoThink` (logic step) | 0x6b4a30 | `GameSimDriver: MonoBehaviour` in `FixedUpdate` with `Time.fixedDeltaTime = 25 ms`. Runs (in order): ObjectTickPass → EventDispatchPass → ActiveObjsPass → FogOfWarPass → MapTickPass → TriggerMgrPass → ProdMgrPass | Unity's FixedUpdate IS the accumulator pattern — built in |
| `CGameThread::Draw` | 0x6b5d90 | Mostly automatic via URP. Render-relevant pre-passes (link updates, sel/hover, status update) move to `LateUpdate` on per-system MonoBehaviours | Unity owns the render pipeline |
| `CDisplay::DrawObjects` | 0x473cc0 | Per-`DigglesObject` MeshRenderer + URP material with per-instance properties | URP handles batching |
| `CObj` / `CObjMgr` | 0x5cc600 / 0x5d2920 | `DigglesObject: MonoBehaviour` (already exists). `DigglesObjectRegistry` service tracks active entities (replaces `CObjMgr`) | MonoBehaviour IS the entity already |
| `CObj::HandleStates` | 0x5cac90 | `EntityStateTickSystem` ticks all entities with a non-null `CurrentState`, evals their Tcl body | One driver, not per-entity tick — cheaper |
| `CObj::HandleSingleEvent` | 0x5caa00 | `EntityEventBus.Publish(evt)` → handlers registered by Tcl `handle_event` calls dispatch to Tcl interpreter | Typed signals replace the queue |
| `CTimerEvtMgr` | (file in tree) | ✅ landed — `Runtime/Engine/Sim/TimerScheduler.cs` (`Schedule(handle, eventName, interval, repeat, attime)`) | Trivial in C# |
| `CTclClassMgr` + `CTclGlobalMethods::Register` | 0x618010 etc. | `Diggles.Runtime.Tcl.Engine.DigglesTclEngine.Register` (already partly in place) | Same shape, cleaner code |
| `CMap` / `DAT_0077241c` | 0x593-0x594 | `IMapService` exposing `GetTile(x, y) → DigglesTile` over a flat `NativeArray<DigglesTile>` (Burst-friendly later) | Cache-friendly storage; no 124 B struct |
| `CBlockDB` | 0x565ab0 | `BdbData` ScriptableObject created by `BdbParser` at import time | Pre-bake at import, not at runtime |
| `CMapBeautifier::ScanAtPos` | 0x58b130 | Skip — PMP files ship `block_idx` baked. Reimplement only if mod support requires it | We don't need the resolver for shipped campaigns |
| `CFogOfWar` | 0x57f5c0 et al | `FogOfWarService` with `RWStructuredBuffer<uint>` bitmask; URP shader samples it for terrain darkening | Shader-side reveal is one-line in URP |
| `CMapPathfinder::Calc1` | 0x59d440 / 0x59d640 | ✅ ported — faithful pipeline in `Runtime/Navigation/`: `NavWorld.cs` + `Calc1Pathfinder.cs` + `NavWorldService.cs` + `FloorPathMap.cs` (the earlier custom mesh nav-graph was fully deleted). It's a 4-cardinal Dijkstra (not A*), 4-variant dispatch by (bCanClimb, iOwner>7). Full detail in `Docs/ORIGINAL_PATHFINDING_RE.md`. NavMesh skipped as anticipated — wouldn't fit dynamic dig terrain. | — |
| `CFluid::Tick` | 0x598920 | `FluidSystem`, context-owned deterministic 10 Hz sparse-cell simulation; `FluidPresenter` observes snapshots | CPU state keeps Tcl queries, native-save capture, tests, and mass conservation authoritative; see `FLUID_SYSTEM_RE.md` |
| `CScapeRenderer` (terrain) | 0x4c4de0 | Already implemented in `BdbPmpWorldBuilder` + `DigglesGround.shader` URP material | Done |
| `CModelRenderer` | 0x48e5d0 | Standard URP MeshRenderer per `DigglesObject` | Trivial in Unity |
| `CScapeIconMgr` (terrain icons) | 0x4c0940 | UI overlay via UIDocument (UI Toolkit) with worldspace anchor | Move to UI Toolkit |
| `CAiCentral` / `CAiGameControl` | 0x4244e0 etc. | `AiService`. The actual decision Tcl lives in `std_ai.tcl` already; C# side dispatches to it on each step | Tcl drives, C# orchestrates |
| `CProdPlannerMgr::Tick` + `CProdPlanner::GnomeIdle` | 0x432720 / 0x434a80 | `ProductionService`; the `gen_tt.tcl`-based tech tree is data-driven so no schema changes | Same shape, modern allocator |
| `CFightMgr` | (file in tree) | `CombatService`; uses URP for hit FX | Standard service |
| `CSoundAnimMgr` + `CSoundMultiplexer` + `CSoundSrcMgr` | (files in tree) | Pool of `AudioSource` + `IAudioService` that maps `sound play NAME` → AudioClip lookup → checkout from pool | Unity audio replaces everything; SoLoud / DSOUND irrelevant |
| `ViWin*` (30 widgets) | 0x529c10 et al | **Unity UI Toolkit** with USS/UXML. Tcl bindings stable — Tcl scripts say `textwin show $foo` and the C# side maps to a `UIDocument`. Per master_plan A2, do not port widget-for-widget | Re-authoring HUD/menu in Toolkit is faster than porting fixed-function widgets |
| `CDesktop` | 0x6b5d90 ambig. | UI Toolkit root `UIDocument` | Same |
| `CDXExecBuf::Render` (DX7 command buffer) | 0x4528f0 | DROP entirely | Unity owns the render queue |
| `D3DMgr` / `CDxBase` | 0x457b90 et al | DROP | URP replaces |
| `CDXSound::CreateSimpleBufferFromFile` | 0x454b40 | DROP — use Unity AudioClip + `Resources.LoadAsync` / Addressables | Unity audio is good and cross-platform |
| `CNetMgr` / WS2_32 paths | (file in tree) | **Deferred** (master_plan A6) | Multiplayer redesign — out of scope for v1.0 |
| `KStr` / `DSFastArray` / `SafeArray` | -- | Standard C# `string` / `List<T>` / `T[]` | Foundation lib not needed in C# |
| `PropDB` + save/load | -- | `JsonUtility` or MessagePack for `.sav.json` (modern format) or a custom binary that matches the original's gzip+PropDB if mod-save compat is wanted | Don't carry forward the binary format unless required |
| `start.mov` intro | -- | Unity `VideoPlayer` | Standard |
| Steam achievements | DigglesPort | Unity Steamworks.NET | Standard |

### 8.3 Where the per-frame architecture lands in Unity

```
Unity frame
├── Input event collection (PlayerInput)
│
├── FixedUpdate (every 25 ms, exactly DAT_007371a8 default):
│ GameSimDriver.Step:
│ ├── PerStepHook (= the "1st" / per-25-ms hook)
│ ├── LightMapService.Update
│ ├── EntityRegistry.MaintenanceTick
│ ├── AiService.Step
│ ├── ProductionService.Step
│ ├── EntityRegistry.TickAll // calls DigglesObject.SimTick
│ ├── EventBus.Drain // routes to Tcl handle_event
│ ├── EntityRegistry.HandleStates // evals state Tcl bodies
│ ├── FogOfWarService.Step
│ ├── MapService.Step
│ ├── MapValidatorService.Service
│ └── TriggerMgr.Step
│
├── Update (every render frame):
│ ├── DigglesAnimator.SampleFrame // 10 Hz vertex morph, decoupled
│ ├── WalkAction.Tick (per-entity)
│ └── DigglesInputController.OnClick (raycast → set WalkAction)
│
├── LateUpdate:
│ ├── SelectionService.UpdateHoverGlow
│ ├── CameraController.UpdateView (replaces m_fViewMoveTo* tween)
│ └── HudService.UpdateOverlays
│
└── URP render (automatic):
    ├── Terrain (DigglesGround shader, per-tile vertex weights)
    ├── DigglesObject MeshRenderers
    ├── FogOfWar overlay pass
    ├── Particle systems (replace CParticle)
    └── UI Toolkit canvas
```

Compare with original's `CGameThread::Cycle` (§ 2). Same logical
content; different host.

### 8.4 The Tcl ↔ Unity boundary (canonical contract)

This is **THE invariant**: Tcl scripts in `original/data/` don't know
the engine is gone. They call `new ZwergXxx...`, `set_pos $h 10 20 5`,
`sq_camera fix 50 30 12`, etc., and the C# side honours each. The
`DigglesTclEngine.Register` registry is the contract.

For Phase 4 (next sprint), the bridge needs:

- `new ClassName Name "X Y Z" "rX rY rZ"` → `EntitySpawner.Spawn(...)`
  which calls `MarkerModelResolver.ResolveDynamic` and creates the GO
  pair (outer/inner, see [[reference-coordinate-system-hack]]).
- `del $h` → `EntitySpawner.Destroy(handle)`.
- `set_pos $h x y z` / `set_anim $h obj.anim mode` / `set_rot $h rX rY rZ`
  → write to the bound `Transform` / `DigglesAnimator`.

For Phase 7 (event loop), the bridge adds:

- `timer_event $h evtName -interval N -repeat M -attime T` →
  `TimerScheduler.Schedule(...)`. On maturity, the scheduler raises an
  `EntityEvent { handle, name, payload }` which the Tcl bridge routes
  to the entity's `handle_event` body.
- `state $h NAME` → `EntityRegistry.SetState(handle, name)`; per-step
  ticker iterates entities with current state and evals the body.

For Phase 6 (sequencer), the bridge adds a `SequencerHost`
MonoBehaviour with one `Coroutine` per active queue; `+sq_*` commands
enqueue, `do_wait time N` yields for N seconds.

---

## 9. Open gaps (rank by impact)

1. **`DAT_007371a8` actual value** — confirms our 40 Hz assumption.
   Pull from `dumps/diggles_loaded.dmp` at VA 0x7371a8 (8 bytes,
   double).
2. **`CGameThread::Init` decomp** — the file
   `006aeb30_*.c` is missing from the extracted directory. Re-export
   from Ghidra to see the init order (asset preload? class register
   sequence? Tcl source load order before `systeminit.tcl`?).
3. **`FUN_006bc020`'s real purpose** — currently looks trivial but
   may carry side-effects. Worth verifying.
4. **MsgPump details in `Draw`** — SDL_PollEvent and SDL2 integration.
   This is where input events become engine events; the routing matters
   for our `DigglesInputController` design.
5. **Save/load format** — § 6 is best-effort. If we want save-file
   compatibility with the original (carry forward existing saves) we
   need to RE the writer/reader. If we ship our own JSON saves, we can
   defer indefinitely.
6. **CNetMgr's actual surface** — deferred but worth a quick spec
   sweep if multiplayer ever returns to scope.
7. **`CMapBeautifier::ScanAtPos`** — only blocks new-PMP support
   (mods that ship un-baked PMP). Skippable for shipped campaigns.

---

## 10. References

- `unity/Diggles/Docs/COORDINATE_SYSTEM.md` — axis conventions.
- `unity/Diggles/Docs/TCL_INTERPRETER_PLAN.md` — Tcl phase plan.
- `unity/Diggles/RENDERING_PIPELINE_COMPLETE.md` — BDB block geom RE.
- `re/notes/master_plan.md` — 5-phase strategic plan.
- `re/notes/recon_summary.md` — initial recon + module enumeration.
- `re/notes/tcl_api_surface.md` — Tcl API frequency analysis.
- `re/notes/WORLD_SPAWN_PIPELINE.md` — file formats + class→model resolution.
- `re/notes/ANIMATION_SYSTEM.md` — 10 Hz vertex-morph anim model.
- `re/decompiled/Diggles.callstat_rename.tsv` — 46 CallStat-recovered names.
- `re/decompiled/Diggles.source_attribution.tsv` — VA → source.cpp map.
- `dumps/diggles_loaded.dmp` + `dumps/extracted/*` — runtime singletons.
