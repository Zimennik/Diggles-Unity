# Audio setup (AudioController + AudioMixer + SettingsService)

The runtime audio authority is `Runtime/Engine/AudioController.cs` (a VContainer-injected
MonoBehaviour). Volumes come from `Runtime/Engine/SettingsService.cs` (PlayerPrefs-backed),
and routing/mixing is done through an **optional** `AudioMixer` asset wired in the Inspector.

Controllers live **on the scene** (not created at runtime). VContainer registers the scene-placed
`AudioController` by **explicit Inspector reference** on `DigglesGameInstaller`
(`RegisterComponent`, *not* a `RegisterComponentInHierarchy` scene scan) and injects it; the
AudioMixer is a serialized field you drag in. The controller works **without** the mixer (falls
back to `AudioSource.volume`), so audio plays even before you wire it.

## 1) Create the AudioMixer asset (one-time, ~3 min)

The asset can live anywhere (no Resources folder needed — you wire it by reference).

1. In the Project window: right-click → **Create → Audio Mixer**. Name it e.g. **`DigglesAudio`**.
2. Open it (double-click → Audio Mixer window). It has a **Master** group by default.
3. Add three child groups under **Master** (right-click Master → Add child group, or the `+`):
   **`Music`**, **`Sfx`**, **`Voice`** (names matter — found via `FindMatchingGroups(name)`).
4. Expose each group's volume to script. For **each** of Master / Music / Sfx / Voice:
   - Select the group; in the Inspector find **Attenuation → Volume**, right-click it →
     **Expose 'Volume (of <Group>)' to script**.
   - Open **Exposed Parameters** (top-left of the Audio Mixer window) and **rename** each to
     EXACTLY (case-sensitive): **`MasterVolume`**, **`MusicVolume`**, **`SfxVolume`**, **`VoiceVolume`**.

## 2) Place the controllers on the scene (one-time)

> **Composition root (explicit Inspector wiring — no scene search):**
> 1. **`GameLifetimeScope`** component on a scene GameObject — the DI scope.
> 2. **`DigglesGameInstaller`** component (a MonoInstaller) on a scene GameObject. Assign the five
> controllers below to its fields (**Audio Controller / Screen Manager / Particle Presenter /
> Cutscene Camera Driver / Elf Presenter**). The installer also carries UI-view fields
> (**Main Menu View / Gameplay Hud / Bottom Menu View / Inventory Panel View**) and an optional
> **Class Registry** (`DigglesClassAssetRegistry`) data asset — all independent of audio and each
> skipped if unassigned.
> 3. On **`GameLifetimeScope`**, assign the **`DigglesGameInstaller`** to its **Installer** field.
> 4. On **`DigglesWorldRuntime`**, assign the **`GameLifetimeScope`** to its **Game Scope** field.
>
> `DigglesWorldRuntime` builds the scope once the engine is ready; the scope registers the core
> (engine + context + settings + sim-tick + click services) and runs the installer, which registers
> the Inspector-wired controllers (`RegisterComponent`). An unassigned controller ref is skipped (and warned).

1. In the scene that has the `DigglesWorldRuntime`, create an empty GameObject named
   **`DigglesControllers`** (any location; e.g. a sibling of the runtime object).
2. Add the **AudioController** component to it.
3. Drag your **`DigglesAudio`** mixer asset onto the AudioController's **Audio Mixer** field.

That's it. On Play, `DigglesWorldRuntime` builds the DI container, which registers this
`AudioController` (via the installer's Inspector reference), injects the context + settings, routes
the Voice/Music/Sfx sources to the matching mixer groups, and applies volumes from `SettingsService`
(linear 0..1 → dB).

> **Note:** positional one-shot SFX (`sound play`/`playglobal`) are drained by `WorldSoundService`
> (native falloff `FUN_004d19a0`), not by `AudioController` — the controller owns the **voice** +
> **music** streams. The `Sfx` source/group is still used by the `PlaySfx` public API.

### ScreenManager (coordinator) + FadeView / SubtitleView / LetterboxView (dumb views)

The screen HUD is split for SRP: **ScreenManager** is a thin coordinator (the only DI-injected piece)
that reads the cutscene state and drives three dumb view components it references. The old standalone
`SubtitleController` / `CinematicBarsController` were replaced by `SubtitleView` / `LetterboxView`
(same logic, now fed by ScreenManager) and **deleted**.

1. Add the four components (on `DigglesControllers`, or on dedicated child UI objects — your choice):
   - **ScreenManager**
   - **FadeView**, **SubtitleView**, **LetterboxView**
2. Wire each VIEW to its own UI (all optional — an unset ref just skips that piece):
   - `FadeView` → **Overlay**: fullscreen black `Image`.
   - `SubtitleView` → **Group** (`CanvasGroup`) + **Label** (`TMP_Text`).
   - `LetterboxView` → **Group** (`CanvasGroup`) + **Top Bar** + **Bottom Bar** (`RectTransform`s,
     authored at their OFF-SCREEN rest position). Timing/ease/slide fields carry the same defaults.
3. Wire **ScreenManager** → its three view refs: **Fade** → FadeView, **Subtitle** → SubtitleView,
   **Letterbox** → LetterboxView.
4. Delete the now-orphaned `SubtitleController` / `CinematicBarsController` GameObjects/components
   (their scripts are gone → they show as "missing script").

Only `ScreenManager` is DI-injected; the views are plain components it drives, so nothing else needs
registering.

### ParticlePresenter (cutscene particles)

Add the **ParticlePresenter** component to `DigglesControllers`. No wiring — it builds its own
emitters. (Per-type visual presets are still a future refinement.)

### CutsceneCameraDriver + ElfPresenter (replaces the old DigglesCutscenePresenter)

The cutscene camera + narrator elf were split out of the deleted `DigglesCutscenePresenter` into two
scene controllers. Add both to `DigglesControllers` — **no wiring needed**:

- **CutsceneCameraDriver** — poses the camera during cutscenes (finds `Camera.main` + its
  `DigglesCameraController` automatically; drives it from the cutscene camera state).
- **ElfPresenter** — spawns + drives the narrator elf model (it owns the `elfe` model, spawned
  hidden on start). `Elf Scale` (default 1.6) is tunable in its Inspector.

Both self-resolve their dependencies from the injected context + `Camera.main`, so you only add the
components. (The old `DigglesCutscenePresenter` was created at runtime — it is gone.)

> The DI container injects each assigned scene component. An **unassigned** Inspector ref is skipped
> and warned: `[DigglesGameInstaller] <X> not assigned in the Inspector — that feature is disabled.`
> The world still loads.

> If the `Audio Controller` field is left unassigned, the container logs
> `[DigglesGameInstaller] AudioController not assigned in the Inspector — that feature is disabled.`
> and audio is simply off — the rest of the world still loads.

## Names the code expects (must match exactly)

| Mixer group | Source routed to it | Exposed param |
|-------------|---------------------|---------------|
| `Master` | (root) | `MasterVolume` |
| `Music` | music stream | `MusicVolume` |
| `Sfx` | one-shot SFX | `SfxVolume` |
| `Voice` | cutscene voice | `VoiceVolume` |

Change the param consts / group names in `AudioController.cs` if you rename them.

## Settings

`SettingsService` persists volumes via PlayerPrefs (keys `diggles.audio.master|music|sfx|voice`
+ `diggles.audio.muted`). Defaults are all `1.0` (full), not muted. Call
`SetMasterVolume/SetMusicVolume/SetSfxVolume/SetVoiceVolume(0..1)` or `SetMuted(bool)`; each
persists immediately and raises `Changed`, which `AudioController` listens to and re-applies to
the mixer. A future settings UI binds to this service.

## Verifying

- No mixer wired → console logs `[AudioController] no AudioMixer wired …` and audio plays at full
  volume (unchanged from before the refactor).
- With the mixer → no such log; moving the exposed params (or calling the SettingsService setters)
  changes the channel volumes live.
- Cutscene audio (voice segments, music, SFX) must behave exactly as before — the playback logic
  was moved verbatim from the cutscene presenter.
