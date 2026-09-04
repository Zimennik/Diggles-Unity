# Document window and quest journal RE

Status: implemented from the retail `Diggles.exe` handlers and the shipped
document Tcl files.

## Native entry points

| Surface | Native function | Evidence |
|---|---:|---|
| `layout` Tcl dispatcher | `FUN_005154c0` | `Tcl_GetIndexFromObj` table at `0x00768788` |
| `textwin` Tcl dispatcher | `FUN_00531c60` | manual `show`/`hide`/`info`/`run` comparisons, then forwards to `FUN_005154c0` |
| generic document open | `FUN_00507390` | constructs `ViWinLayout`, clears it, evaluates the requested script, and installs the close overlay |
| built-in info page open | `FUN_00507560` | maps `gnomes`, `prods`, `pedigree`, and `questlog` to their retail scripts |
| script evaluation | `FUN_00515340` | prefixes bare names with `data/scripts/text/doc/<locale>/` and exposes the active layout through `DAT_007723f4` |
| clear | `FUN_00514940` | resets layout items, scroll state, and the link-zero sentinel |
| print | `FUN_00513ec0` | feeds text/format markup to the active `ViWinLayout` |
| link input | `FUN_005145b0` | normal links load another page; execute links evaluate Tcl; secondary targets are supported |
| close | `FUN_00531f40` | closes the generic page and technology-tree surfaces |

The retail layout subcommand table is exactly:

1. `present`
2. `log`
3. `clear`
4. `print`
5. `addlink`
6. `autolink`
7. `addxlink`
8. `autoxlink`
9. `reload`

Subcommand names are case-sensitive because the native dispatcher uses
`Tcl_GetIndexFromObj` without a case-folding flag.

## Link law

`clear` creates a sentinel record at index zero. The first usable link therefore has ID 1.
`addlink`/`addxlink` return the new integer ID. The auto variants also return the original
inline marker:

```text
/(su<ID>)<visible text>/(su0)
```

Normal links treat the target as another document script. Execute links treat it as Tcl.
The optional fourth auto-link argument is the secondary-button target and follows the same
normal-versus-execute law. `reload` clears the page and evaluates its original script again;
the gnome and production reports use this after selection changes.

The retail Tcl help also advertises `textwin deflink <id> <scriptname>`, and the shipped
`hlp_index.tcl` calls it. The final `FUN_00531c60` binary dispatcher no longer exposes that
verb. The port implements this one compatibility verb so the original help index remains
functional; it does not change the nine-command native `layout` table.

## `textwin` law and page routing

- `textwin run <script>` opens a generic document. A name without `/` or `\` resolves to
  `data/scripts/text/doc/<locale>/<script>`; a path is used as supplied.
- `textwin hide` closes the document overlay. This is why the sequencer calls it before a
  cutscene.
- `textwin show` reveals an already open document.
- `textwin info questlog` opens `diary_15.tcl`.
- Other native built-in document modes are `gnomes` -> `info_gnomes.tcl`, `prods` ->
  `info_prods.tcl`, and `pedigree` -> `info_stammbaum.tcl`.

The active layout pointer exists only while a document script or execute link is evaluating.
Consequently, `layout present` reports that active render context, and state-changing layout
verbs outside it fail with the native `no window open` error.

## Quest journal data flow

The original journal does not use a native `questlog` command. Its behavior is a Tcl proc in
`Scripts/misc/onlinehelputils.tcl`:

1. `diary_15.tcl` clears the layout and loads `onlinehelputils.tcl`.
2. Each `questlog <QL_event> <headline> <body>` checks `is_storymgr` and
   `sm_get_event <QL_event>`.
3. Inactive events produce no page content.
4. An active `<QL_event>_done` flag replaces the body with the localized `Quest Done` text.
5. If no quest is active, `questlog_end` prints the localized `No Quests` text.

The previous bulk stubs for `questlog`, `paragraph`, `linkline`, `ohlp_initstyle`, and
`pickone` were not harmless: registered commands resolve before Tcl procs in the port, so they
silently shadowed the unchanged retail implementations. They have been removed.

## Layout markup used by retail data

The port consumes the formatting stream directly rather than rewriting the pages. Supported
directives cover every family found in the shipped document scripts:

- paragraph: `/p`;
- alignment: `al`, `ac`, `ar`, `ab`;
- font/style spacing: `fn`, `ls`, `ws`, `ml`, `mr`, `hp`, `bo`;
- tabs and literal text: `ta`, `tb`, `tx`;
- links: `su`;
- original images: `ii`, `il`, `ir`, with `is` inset;
- report controls: `ccHealthbar`, `ccAttribbar`, `ccAttribrangebar`, and `ccEditName`.

Text and links become TMP rich text. Image directives load their TGA files directly through
`TgaLoader`. Report controls resolve live entity names and attributes into compact readable
bars; the dedicated property-panel UI remains a separate backlog item.

## Port architecture

- `DocumentWindowSystem` owns current script, raw markup, link records, visibility, command
  dispatch, page evaluation, clicks, and save state.
- `DocumentMarkupParser` converts the retail stream to text/image render blocks while keeping
  original link IDs.
- `DocumentWindowWidget` is a code-built uGUI overlay with scrolling, direct TGA reuse, the
  original `xoverlay_Crossout.tga` close art, left/right link input, and Escape close.
- `GameplayHud` creates the widget once from its injected `DigglesTclContext`.
- `DocumentWindowSystem` exposes validated capture/restore state for the native `.sav` runtime
  mapper; no subsystem-specific disk format owns this state.

No scene or original Tcl file is modified.

## Verification

`DocumentWindowSystemTests` covers:

- active-layout error and `present` behavior;
- print, clear, link IDs, auto-link markup, left/right targets, file links, and Tcl links;
- `run`, `reload`, `info`, `hide`, and the retail `deflink` compatibility verb;
- absence of native shadowing for all five online-help procs;
- formatting/link/image conversion;
- execution of the unchanged locale-specific `diary_15.tcl` against live StoryMgr flags;
- runtime snapshot round-trip and validation before graph mutation.

The real game PlayMode scene also creates the new overlay without null/missing-reference errors.
