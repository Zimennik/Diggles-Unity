# Water full repair: native flow, block-volume draw, and fog

> Phase 1 recovered initialization and
> gnome reaction; the full-repair round replaces the approximate solver and corrects the draw
> frame/fog contract. Original-data citations are relative to the installed `data/` directory.
> Native citations are relative to `re/decompiled/Diggles/_global/` and include the exported
> function address.

## 1. Initial fluid is authored by template objects, not L2M fluid bits

The real `urw_gng_050_a` template applies both `gng_050.pmp` and
`urw_gng_050_a.l2m`, then creates a `Wasser` object at template-local
`(12.64, 12.375, 13.01)` with `{volume 18} {vpf 0.3}`
(`Templates/urw_gng_050_a.tcl:1-17`). `Wasser` enables fluid and authors the actual
source with `-pos {0 -1 0} -type water`; gameplay delays that initialization until its
one-shot event at `gettime + 1` (`Scripts/classes/items/wasser.tcl:1-26`). Sulfur and lava
use the same object/source mechanism with types `sulfur` and `lava`
(`wasser.tcl:139-193`).

L2M is terrain metadata only. The native loader reads an `M2L\0` header, width and height,
then copies each raw 16-bit value to tile `+0x14`
(`005987c0_FUN_005987c0.c:27-51`). The port's matching decoder assigns bits 8..15 to four
corner values, bits 5..7 to block type, and bits 0..4 to animation frame
(`Assets/Diggles/Runtime/World/L2mData.cs:5-30`). The real L2M target at local cell
`(12,11)` is `0x5400`: corners `(1,1,1,0)`, block type `0`, animation frame `0`. There is
no fluid kind or fill field in that value. The EditMode fixture parses this shipped L2M
and asserts those fields before executing the shipped template.

The native source converts the owner position plus source offset to cell coordinates with
C integer casts, not floor (`0057a1c0_FUN_0057a1c0.c:49-70`). Consequently, placing the
template at `(20,20)` gives:

```text
owner = (12.64 + 20, 12.375 + 20)
source = trunc(owner + (0,-1)) = (32,31)
```

`CFluid::Tick` swaps its current/next work selectors, updates sources, and then runs the
relaxation/work-list passes (`00598920_CFluid_Tick.c:103-110`). The work-list pass clears
the selected next list and iterates the current list (`005791a0_FUN_005791a0.c:35-68`);
source-dirtied coordinates are appended to that next list (`005791a0:81-134`). Therefore a
new source emission is observable in its authored cell immediately, but does not relax out
of that cell until the following fluid tick.

The port keeps the same selector cadence: source cells use truncation; source mutations update
the live fill immediately and dirty self/east/south for the next work list; one solver pass then
consumes only the already-active list. Thus a newly created source is observable immediately but
does not travel during that first source tick. The real-template fixture executes the one-second
initialization event and requires exactly one water cell: `(32,31)`, fill `0.3`, type `1`.

## 2. Native block-volume presentation

### Recovered mesh and level law

The render cache copies simulated fill to cache `+0x18`, the BDB capacity to `+0x20`, the
two eighth-cell bounds to `+0x23/+0x24`, and the block variant's low nibble to `+0x34`
(`005763e0_FUN_005763e0.c:127-177`). It also derives the exposed-surface byte at `+0x30`
from the current, above, and below cells (`005763e0:301-394`).

All three medium renderers use the same fill surface. With native Y increasing downwards:

```text
solidBoundaryY = cellY + upper / 8
surfaceY = solidBoundaryY
               + (lower - upper) * min(fill, 1) / 8
```

Water reads this at `005716e0_FUN_005716e0.c:100-118`, sulfur at
`00573250_FUN_00573250.c:93-107`, and lava at
`00574c60_FUN_00574c60.c:95-113`. Fill above one does not deepen the visible cell; it
increases stored pressure/volume and, for water/lava, opacity.

For an exposed surface, the native mesh uses
`stripCount = (16 - (variant & 15)) / 2`, allocates `2 * stripCount + 2` vertices and
`6 * stripCount` indices, and steps from the fixed front depth once per strip
(`005716e0:181-209`; sulfur `00573250:166-197`; lava `00574c60:176-206`). The front-wall
vertices explicitly store depth `14.4` (`005716e0:686-805`), the same value used by
`DAT_00737560` as the strip-depth origin.

### Recovered material law

The dispatcher binds `alpha.tga` plus a distinct medium texture:

- water: `wasser_env.tga` (`00576c60_FUN_00576c60.c:150-176`);
- sulfur: `schwef_nahtlos.tga` (`00576c60:178-204`);
- lava/lava-water: `lava_nahtlos.tga` (`00576c60:22`).

Type dispatch is water `1`, sulfur `2`, and lava renderer for `3` or `4`
(`00576c60:584-598`). Water's `WaterBlock` table uses alpha
`min(255, max(fill - 1, 1) * 64)` (`005716e0:133-147`). Sulfur's `SulfurBlock` uses a
fixed `0x40` alpha (`00573250:121-132`). Lava uses the same fill-driven alpha law as water
(`00574c60:127-142`).

### Port result

`FluidPresenter` now builds one front volume wall and the native shared depth-strip surface
for every simulated cell. The surface/bottom comes from that cell's live fill and BDB bounds;
the strip count comes from the block variant; front depth is `14.4`; and vertex alpha follows
the native per-medium table. It binds the shipped one-pixel `Texture/Misc/alpha.tga` and the
three shipped `Texture/m064` medium textures without arbitrary replacement tints. Gameplay
and saved state remain owned by `FluidSystem`; the mesh is a presentation-only projection.

The phase-1 mesh converter was nevertheless using `(renderX, renderDepth, -renderY)` directly.
That treated raw native render depth as the port's compressed local height. The corrected world
frame is the same one recovered for the backdrop lane:

```text
RenderFromLocal(local) = (local.x, -local.z, local.y * 2 - 14)
LocalFromRender(render) = (render.x, (render.z + 14) / 2, -render.y)
```

Consequently the front face at render depth `14.4` belongs at local depth `14.2`, and an eight-
strip surface ends at render depth `6.4` / local depth `10.2`. The old converter put that rear
edge at local depth `6.4` (render depth `-1.2`), behind the terrain/camera depth family. That is
why the exposed horizontal fragments dominated while most cell volume disappeared. The repaired
converter calls `BackdropLaws.LocalFromRender`; front walls and top strips now occupy the same
depth frame as terrain.

The executable's per-strip time-varying surface perturbation is presentation only and is not a
gameplay coefficient; the port retains texture drift as its motion layer.

### Fog-of-war draw gate

The native cache samples the selected player's fog byte at the cell's current, south, east, and
southeast corners. Only when all four are zero does it set the cell's hidden flag
(`005763e0_FUN_005763e0.c:26`). Water returns before emitting geometry when that flag is set
(`005716e0_FUN_005716e0.c:81-84`); sulfur and lava use the same gate
(`00573250_FUN_00573250.c:74-77`; `00574c60_FUN_00574c60.c:76-79`).

The port shares terrain's `_FowTex`, `_FowParams`, and `_FowWorldToNative` globals. It samples
`uv=((nativeX+0.5)/width,(-nativeY+0.5)/height)`, applies the recovered gray factor
`saturate((1-(depth-7)*0.125*1.02)*R)`, blends toward `(170,170,155)/255`, and finally multiplies
RGB by channel G. The native terrain/light combiner contains that depth expression at
`004cc040_FUN_004cc040.c:170-180` (`DAT_00737520=7`, `DAT_0073703c=0.125`,
`_DAT_0073713c=1.02`) and the gray RGB literals `0xaa/0xaa/0x9b` at `004cc040:187-189`.
Because fluid is transparent, alpha is multiplied by G as well; an all-black cell is actually
absent instead of leaving a dark translucent silhouette. This is the same black/gray field as
`DigglesGround.shader`, with the transparent-material completion needed for the native hidden
gate.

## 3. Gnome water-state chain

There are three distinct native probes/contracts:

1. The once-per-second attribute event probes feet at `position + {0 -0.1 0}`. Wet feet
   apply lava contact damage and call `sparetime_react_towater`
   (`Scripts/classes/zwerg/z_events.tcl:1694-1714`). That reaction does not interrupt an
   in-progress walk result `2`; otherwise it queues `try_getoutof_water` for the applicable
   spare-time states (`z_spare_procs.tcl:927-965`).
2. The same event probes the mouth at `position + get_linkpos(this, 10)` and assigns the
   result to `is_underwater` (`z_events.tcl:1716`). `isunderwater` is a strict surface
   comparison, so a point exactly on the surface is dry; the recovered formula is in
   `Docs/FLUID_SYSTEM_RE.md` under “Exact recovered data laws.”
3. Walk movement reads the waypoint's 8x8 subcell byte. If swimming is enabled and bit
   `0x04` is set, with a live water-body record, it returns movement surface `1`; otherwise
   bit `0x02` returns floor surface `2`, and neither returns wall/no-surface `0`
   (`00423730_FUN_00423730.c:19-35`). The walk state groups consecutive equal surfaces and
   dispatches water surface `1` to the water executor (`004163a0_FUN_004163a0.c:34-85`).

The authored walk command supplies four animation banks. Bank 2 is `ANIMSET_SWIM`, unless a
diving bell selects `ANIMSET_DIVER` (`z_dignwalk.tcl:21-26,45-51,115-128`). The swim bank maps
`walk_start/loop/stop` and climb states to the shipped swimming clips
(`z_anims.tcl:1369-1388`); drowning clips are declared at `z_anims.tcl:161-170`.

The port now evaluates the exact feet and link-10 mouth probes after each 10 Hz fluid tick.
It writes the Tcl `is_underwater` state from the mouth probe. For an active walk it consumes
the current walkable navigation subcell's existing `SubWater` bit; only when no walkable
subcell is available (for example, a headless fixture or interpolation gap) does it fall back
to the feet-volume probe. The walk wrapper switches to saved native animation bank 2, reasserts
it after an underlying walk/run leg changes banks, remembers that latest dry bank, and restores
it when the water flag clears. No `MetaWalkAction` internals were changed.

Air and drowning remain script-authored rather than duplicated in C#. Gnomes initialize with
`MAX_AIR_UNDERWATER = 15` and `remainingair = 15`, and schedule the attribute update every one
second (`zwerg.tcl:66-71,183-185`). Each mouth-underwater update decrements air by one; at zero,
it removes `0.025` hitpoints per update. Leaving water immediately resets air to 15
(`z_events.tcl:1729-1756`). Underwater death selects `swimwalldrown` or `swimdrown`
(`z_events.tcl:2037-2047`). Making the mouth flag and swim animation consume the same live fluid
state reconnects this unchanged timer/death chain to presentation and movement.

### Diving bell (report only)

The unchanged script auto-equips an inventory bell in deep water, suppresses air consumption
while worn, and, when the bell was not user-commanded, removes it after more than seven dry
attribute updates (`z_events.tcl:1731-1762`). Phase 1 makes no diving-bell behavior change.

## 4. Native flow/leveling law — completed phase-2 RE

### Descriptor and absolute wall gate

The complete packed BDB word at block auxiliary offset `+0x55` is:

| Bits | Native use |
|---|---|
| 0..9 | capacity |
| 10..13 | upper eighth-cell boundary |
| 14..17 | lower eighth-cell boundary |
| 18..24 | vertical aperture, seven bits |
| 25..31 | horizontal aperture, seven bits |

The horizontal kernels read bits 25..31; the vertical kernels read bits 18..24
(`00577b20_FUN_00577b20.c:40-58`; `00577e00_FUN_00577e00.c:40-58`). The descriptor belongs
to the current south/east cell, so each active cell owns its north and west boundaries. The
effective aperture is truncated to integer after applying the stopper factor. If it is zero,
or either adjacent capacity is zero, the kernel returns before any pressure calculation. That
is the native SOLID test—not “capacity means traversable.” BlockDB2 block 59 is a useful open
reference: `0xF1E023C0` decodes to capacity `960`, bounds `8/0`, vertical `120`, horizontal
`120`. Many capacity-bearing wall blocks instead have one or both apertures `0`.

The stopper byte stores two four-bit obstruction values. A direction's permeability is
`1-nibble/15`; overlapping stoppers multiply and are re-quantized to that nibble
(`0057b990_FUN_0057b990.c:218-234`). Thus script-authored permeability `0` produces nibble
`15`, effective aperture `0`, and an absolute wall before the solver runs.

### Per-boundary transfer

`FUN_00578c80` snapshots live type/fill and ages the X/Y directional work floats once
(`00578c80_FUN_00578c80.c:30-94`). In sorted active-cell order it sums the current cell's aged
work with north/west work and dispatches exactly one vertical and one horizontal kernel
(`00578c80:210-245`). There are not four in-place relaxation passes.

For descriptor `(capacity C, lower L, upper U)` and snapshot fill `F`, define the eighth-cell
column and pressure transform:

```text
H(F, rowOffset) = (U - L) * F + (8 + rowOffset - U)
P(H) = H when H <= 8
                  (H - 8) * 8 + 8          when H > 8
```

`rowOffset` is `0` horizontally/current-side and `8` for the north cell in a vertical pair
(`00577b20:69-80`; `00577e00:69-80`). With `A = trunc(rawAperture * permeability)`, nonzero
`A` is floored per medium. Capacities are likewise floored only for the amount/fill conversion.
The signed native-volume transfer from north/west neighbor into current south/east is:

```text
T = (Pneighbor - Pcurrent) * 0.125 * 0.125 * A * relaxation
    + agedDirectionalWork * momentum
T = clamp(T, -currentLiveAmount, neighborLiveAmount)
```

The kernels then divide the two new live amounts by their effective capacities and zero fills
below `0.001` (`00577b20:81-108`; sulfur `005780e0:81-108`; lava
`005786e0:82-109`). Constants below were read from the named `Diggles.exe` `.rdata` symbols and
are tied to the kernel expressions shown:

| Medium | Kernels V / H | relaxation | momentum | min capacity | min nonzero aperture |
|---|---|---:|---:|---:|---:|
| water | `00577e00` / `00577b20` | `1.5` (`DAT_007371c0`) | `0.5` (`DAT_0073708c`) | `32` | `8` |
| sulfur | `005783c0` / `005780e0` | `1.4` (`DAT_0073719c`) | `0.3` (`DAT_00737060`) | `32` | `8` |
| lava | `005789b0` / `005786e0` | `1.3` (`DAT_00737180`) | `0.125` (`DAT_0073703c`) | `128` (`DAT_00737614`) | `16` |
| lava-water | water kernels, emitted type `4` | `1.5` | `0.5` | `32` | `8` |

The shared spatial factor is `0.125` (`DAT_0073703c`) twice, the pressure knee/base is `8`
(`DAT_0073752c`), the ordinary floors `8/32` are literal kernel immediates, and the lava floors
`16/128` are at `005786e0:56-70` / `005789b0:56-70`. Water's complete coefficient expression
is at `00577b20:59-98`; sulfur's is at `005780e0:59-98`; lava's is at
`005786e0:56-99`. The sparse-list continuation threshold is strict `abs(work)>0.004`
(`DAT_00736fc8`, `005791a0_FUN_005791a0.c:551-665`). A source mutation seeds vertical work
with `0.05` (`0x3d4ccccd`, `0057a1c0_FUN_0057a1c0.c:151-161`).

For two open `C=960`, `A=120`, `U/L=8/0` cells and zero momentum, the pinned examples are:

| Fixture | Pressure delta | water | sulfur | lava |
|---|---:|---:|---:|---:|
| same-row full → empty | `8` | `22.5` | `21.0` | `19.5` |
| north full → south empty | `72` | `202.5` | `189.0` | `175.5` |
| either directional aperture `0` | n/a | `0` | `0` | `0` |

### Work-list cadence and `-ventil`

`CFluid::Tick` swaps the two selectors, refreshes stoppers, applies sources, runs the single
snapshot/flow pass, then rebuilds the next sparse list
(`00598920_CFluid_Tick.c:103-110`). Rebuild retains a coordinate only for directional work above
`0.004` with liquid on either side and enqueues self/east/south plus the directionally required
west/north neighbor (`005791a0:551-665`). This double-buffered sparse cadence is what prevents
one tick from teleporting a new volume through an entire shaft.

`-ventil` is a surface pin, not a generic “more flow” toggle. After height/VPF processing, the
source calls the bounded surface helper, computes
`deltaAmount=(foundSurfaceY-sourceActualY)*capacity`, applies the remaining finite-volume cap,
and adds that delta to the source cell (`0057a1c0:168-208`). The helper scans upward through at
most eight full cells, then downward through at most eight empty cells, and returns the exact
bounded fill surface with `min(fill,1)` (`0057aaf0_FUN_0057aaf0.c:24-63,64-99,100-131`). The
source setter's VPF/volume conversion is `*128` (`DAT_00737614`,
`0057b2a0_FUN_0057b2a0.c:72-81,96-105`). The port mirrors that branch and preserves the existing
source/save record layout.

## 5. Verification and save-law guard

EditMode law coverage now includes:

- the shipped template/L2M fixture and exact initial cell `(32,31)`;
- the coefficient table above, including zero-aperture wall, vertical drop, same-row leveling,
  all three medium coefficients, and the nonzero aperture floor;
- an integrated capacity-bearing BDB wall whose vertical aperture is zero;
- vented-source surface pinning;
- fill-to-front-wall/top-strip mapping in the corrected render frame, visible overfill clamp,
  texture identity, alpha texture, and per-medium opacity;
- the native four-corner hidden predicate plus terrain-identical gray/black shader law;
- the existing feet/mouth, navigation-water, drowning, and swim-bank contracts.

Per the lane rules, neither Unity nor a.NET build/test command is run in this round. Static
checks are limited to source/diff inspection. Save-sensitive classes and serialization fields
are unchanged: fills remain unclamped above one, cell/source/stopper snapshot layouts are intact,
persisted native fluid mapping is untouched, and transient flow buffers are rebuilt after restore.
The existing `OriginalSaveFluidContinuationTests.cs` fixture is not edited.

## 6. Files changed by `water-full`

- `Assets/Diggles/Runtime/Tcl/Engine/FluidSystem.cs`
- `Assets/Diggles/Runtime/World/FluidPresenter.cs`
- `Assets/Diggles/Runtime/Shaders/Resources/DigglesFluid.shader`
- `Assets/Diggles/Tests/Editor/FluidSystemTests.cs`
- `Assets/Diggles/Tests/Editor/WaterPhase1Tests.cs`
- `Docs/FLUID_SYSTEM_RE.md`
- `Docs/WATER_PHASE1_RE.md`
