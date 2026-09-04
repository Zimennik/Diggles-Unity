# DiggleInfoPanel — controller/binding spec

> Source: the panel spec + the scene/prefab as authored in the editor.
> The layout is already authored in the editor; code must not build widget trees — it binds to
> the authored objects and instantiates the player's sub-prefabs into the player's containers.

## Authored scene contract (do not restructure)

- `DiggleInfoPanel` — object on the scene canvas. Carries a `CanvasGroup` with alpha=0 and
  raycast blocking off (hidden state). Show/hide = drive this CanvasGroup (alpha +
  blocksRaycasts), the InventoryPanelView fade pattern.
- `DigglesHolder` — layout container inside the panel; rows are instances of
  `Assets/Diggles/Prefabs/UI/DiggleInfoPanel/DiggleItem.prefab`.
- `DigglesStats` — sub-panel inside `DiggleInfoPanel`, visible only while a specific gnome is
  selected. Its stat rows use `DiggleStat.prefab`, knowledge rows use `DiggleExp.prefab`.
- Bars are uGUI `Image` with `fillAmount` on a child named `Fill`.
- Future (reserve API, do not implement views): two buttons above the panel — "collapse" and
  "switch to buildings list".

## Components to implement (one small view script each, binding-only)

1. `DiggleInfoPanelController` — entry point (scene component on `DiggleInfoPanel`).
   Owns: roster sync (spawn/despawn/reorder of `DiggleItemView` rows in `DigglesHolder`),
   visibility law, selection sync both ways, `RightPanelModel` integration.
2. `DiggleItemView` (on DiggleItem.prefab root): name text, HP bar (`Image` `Fill`),
   `WorkStatusView`, `MoodIconView`, "selected" marker object, click handling
   (plain = select this gnome, Shift+click = additive multiselect).
3. `WorkStatusView` — 4 child objects, exactly one active:
   worktime-with-job / worktime-without-job / freetime / manual mode.
4. `MoodIconView` — 3 child objects, exactly one active: happy / neutral / sad.
5. `DiggleStatsView` (on `DigglesStats`): TMP name, activity line (e.g. "(free time)" /
   "Carrying a stone to the campfire"), 4 stat rows via `DiggleStatView`
   (label TMP + `Image` fill + value text), knowledge rows via `DiggleExpView`
   (same shape, different colour, value format `N/100`).

Binding rules: serialized references with name-based auto-find fallback,
`[FormerlySerializedAs]` when renaming — the InventoryPanelView precedent. Missing reference =
warn once + degrade, never throw. No per-frame string/GC churn: bounded polling cadence
(bottom-menu attribute-polling precedent) or dirty-flag pushes.

## Behavior law

- The panel is visible ALWAYS, except: (a) it is collapsed, or (b) the buildings list is
  currently open. (Buildings list itself = future panel; treat as a mode enum now.)
- RMB with nothing selected toggles gnome list <-> buildings list. Bind through the existing
  seam `RightPanelModel` (`Assets/Diggles/Runtime/UI/RightPanelModel.cs`; ClickToMoveService
  already calls `RequestToggle` on empty-RMB). The seam is currently a single bool — extend
  it to a mode (Gnomes | Buildings, + Collapsed latch) WITHOUT breaking the existing caller.
- Selecting a gnome in the world auto-opens the gnome list with that gnome marked; the
  `DigglesStats` sub-panel opens for the (primary) selected gnome.
- Clicking a `DiggleItem` selects that gnome; Shift+click = multiselect. Route through the
  SAME selection path the world click uses (`ctx.SelectedHandle` / `ctx.Selection` in
  `ClickToMoveService` — expose a `SelectFromUi(handle, additive)` seam there rather than
  mutating ctx from UI code, so selection auras/HUD stay one law).

## Data sources (port anchors — verify exact names at implementation)

- Roster: own-player gnomes from `ctx.Entities` (kind gnome; owner = human player).
- Name: entity display name (`set_objname` / names.txt auto-naming already implemented).
- HP bar + health stat: `atr_Hitpoints` fraction (native vitals quartet anchors: slots
  26..29 = HP/../../Mood in the NATIVE save order; the port reads through AttributeSystem —
  use attribute NAMES, not slots).
- Stats rows: health, nutrition (satiety), attention, mood — resolve the exact `atr_*`
  registry names from def_attrib at impl time.
- Knowledge rows: SkillSystem values, display `floor(value)/100`.
- Mood icon thresholds (happy/neutral/sad): RE the original thresholds at impl; until then
  constants in one place, flagged as approximation.
- Work status: work-window membership from the gnome's worktime window on the 12-hour clock
  (ProdDispatcher / WorkClockWidget law) x has-assigned-job; "manual mode" = gnome executing
  a direct player order. Exact predicates live in the dispatcher — reuse, do not duplicate.
- Activity line: mapping current task/state -> text. Strings go through the `DigglesText` /
  localization-id seam — NO hardcoded literals in code (English-only directive; the
  user's Russian examples are illustrative).

## Out of scope (this task)

- Buildings list panel content (only the mode switch + hide law).
- The two top buttons' visuals (API only: `SetCollapsed(bool)`, mode switch).
- Speech bubbles, finder search, property panels.

## Acceptance

- EditMode: POCO model tests — roster diff (join/leave/death), status predicate table,
  mood threshold table, selection routing (plain/shift), visibility law
  (collapsed/buildings/selection transitions).
- Play: rows track live gnomes; bars move; selecting in world highlights row + opens
  stats; clicking rows selects in world (shift additive); empty-RMB toggles list mode;
  hidden panel blocks no raycasts.
