# Performance — findings, tools, and the open backlog

> Consolidates the optimization passes and the independent performance audit. Rule of the whole effort: **measure → diagnose →
> fix → re-measure; never land blind.**

## The bottom line

- **The game costs ~3 ms/frame. The Editor costs ~23 ms on top.** A steady
  20–30 FPS at x1 in a dug-down world was **Editor overhead** (hierarchy churn over ~1200
  entities), not game code. Entities now spawn `HideInHierarchy` (**F6** toggles visibility
  for debugging). A later standalone manual test reported **144 FPS at x10**. That is a strong
  result, although a captured, repeatable standalone profile is still pending.
- Sim = **~0.3 ms/frame at x1** even with 1200 drop-items; it is only the **fast-forward**
  bottleneck (×40 steps/frame at x10 → 19–43 ms, scales with entity count).
- Render exonerated at x1: draws 184–300, F4 instancing A/B and `show_fogofwar 0` changed
  nothing. Fog-of-war sim/presenter/overlay all measured cheap.

## Tools (use these; don't guess)

| Tool | How | What it gives |
|---|---|---|
| **PerfHud** | **F3** in Play (`Runtime/Tools/PerfHud.cs`) | FPS, CPU ms, GC/frame, draws/SetPass/verts, `sim X ms (K steps, E ents)`, `army` (frame-driver cost), SLOW-FRAME (>33 ms) logger with `(cpu <ms>)` — cpu≈frame ⇒ CPU-bound, cpu≪frame ⇒ GPU-bound |
| **F4** | in Play | Live-toggles GPU instancing of morph models (A/B the draw path; flag default OFF) |
| **F6** | in Play | Show/hide spawned entities in the Editor hierarchy (hidden = the perf default) |
| **PerfTrace** | `Runtime/Diagnostics/PerfTrace.cs` | Labels hitches: `[perf] sim.step` (>100 ms), `[perf] worldgen.lg_get_level` (>40 ms) |
| **Benches** | `Tests/Editor/PerfSimBench.cs`, `PerfAnimatorBench.cs`, `PerfDugAreaBench.cs`, `PerfFrameServicesBench.cs` — all `[Explicit("perf")]` | Headless alloc/timing micro-benchmarks over the REAL classes/entities |

Bench gotchas (hard-won): wall-clock drifts ±15–30 % between batch runs — trust only >2×
effects and within-run slopes; `GC.GetTotalAllocatedBytes` is absent in this Mono — use
`GetAllocatedBytesForCurrentThread`; gen0-collection **count** never moves on the
post-classinit Boehm heap (fine for the pre-classinit sim benches, dead after).

## Landed fixes

1. **Tcl parse cache** (`TclInterpreter`) — the dominant sim CPU + GC was re-tokenizing every
   eval. Value-keyed `ParsedCommand[]` cache + LITERAL word pre-classification: sim step
   −57..60 %, per-tick garbage → 0.
2. **Terrain remesh reuse** (`BdbPmpWorldBuilder`) — per-scape sub-mesh reuse instead of
   GO/Mesh churn: 4289→24 KB heap/rebuild (~178×); multi-second dig freezes → ~44 ms.
   `StaggerRebuilds` exists but is **default OFF** (changes terrain pop-in; Play-verify first).
3. **obj_query filter reorder** — spatial rejection before class/flag tests: 417→48 µs/call
   @1238 ents (8.6×). Negative result recorded in code: memoizing `ClassIsA` is SLOWER
   (no `inherit` users) — do not retry.
4. **Steady-frame allocator kill** (r5 wave): `AuraBandRenderer` per-frame `Mesh.vertices`
   copies were the 0.1–1 MB/frame (cached; the later "affine unprojection" regression was
   replaced by the exact analytic mapping, parity-pinned); `ParticlePresenter` SourceView
   cache (637→9 µs); SelectionAura reverse map; RaycastAll→NonAlloc; Subtitle/TimeControl gates.
5. **Central frame driver** (`Runtime/Engine/DigglesFrameDriver.cs`) — owns the per-entity
   update army (~5 MonoBehaviours × N ents → batched loops); HUD `army` line reports it.
6. Sim micro: position-sync gated on `IEntityAction.DrivesTransform`; phase-3 snapshot only
   on think ticks; version-gated liveness (`EntitiesVersion`); light frustum-cull on the
   `_DigLights` upload; `WorldSoundService` per-frame concat removed; FoW presenter
   dirty-rect + fade-list target refresh.
7. **Shipped diagnostic defaults** — the production scene no longer enables
   `fileDebugLog` or `verboseChainTrace`. Targeted diagnostics remain available when a
   developer deliberately enables them.

Test gate on EditMode **817 passed / 169 skipped / 0 failed** out of 986;
PlayMode **5 passed / 0 failed**. The command-line gate is documented in
`CONTINUOUS_INTEGRATION.md`.

## Open perf items (next waves — need Play/standalone data first)

- **Reproducible standalone capture** — record scene, entity count, time factor, FPS/CPU/GPU,
  and hardware so the reported 144 FPS at x10 becomes a comparable baseline.
- **`model.buildclip` first-spawn hitches** (131–619 ms) — pre-warm or async clip build.
- **Worldgen-growth hitches**: 0.4–1 GB single-frame allocs + `sim.step` 374–1797 ms at
  growth moments (stamp/blit alloc path; deprioritized twice, now quantified).
- **Unlabeled 19.7 s pure-CPU freeze** (r6) — not sim, not lg_get_level; needs more labels.
- **cpu-0 editor pauses** (r7) — editor-side stalls with no game CPU.
- GPU instancing flag (F4) — verified harmless, not yet proven a win; enable on data.
- Async MeshCollider bake for the apron-band cook; `StaggerRebuilds` enablement.
- Drop-item accumulation (ents 324→1207 over a session) — does the native cap/merge drops?
- `BottomMenuController` 4 Hz per-gnome `get_attrib` eval storm (bounded, minor).

## Verified NON-issues (don't re-suspect without new data)

Sim at x1; fog-of-war sim/presenter/overlay; dig churn (0.09 ms/carve; "dug chunks get
dearer" REFUTED); WorldSoundService; HUD/camera/click-idle paths; Tcl re-parse (cache holds,
gen0=0); dispatcher budgets. Details + measurements:
``.

## Play measurement protocol

On a lagging save: F3 on → read **CPU vs frame** (CPU-bound vs GPU-bound), then the **sim**
line (x1 and x10), then **F4 A/B** on draws, then **draw/SetPass trend vs dug area**, then
SLOW-FRAME labels for hitches. Report FPS/GC/draws steady + x10.
