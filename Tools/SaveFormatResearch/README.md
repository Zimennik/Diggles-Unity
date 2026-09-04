# Save-format research tools

Stand-alone Python 3 scripts for inspecting the original `.sav` archives (version `0xA5`,
gzip-wrapped). They were used to reverse-engineer the format documented in
`Docs/ORIGINAL_SAVE_FORMAT_RE.md`; the runtime loader in `Assets/Diggles/Runtime/Save` does not
depend on them.

| Script | Purpose |
|---|---|
| `parse_a5_object_bodies.py` | Structural validator for the object-body stream (follows the native serialization order). |
| `dump_a5_details.py` | Semantic dump of a save: objects, typed fields, managers. `--summary` for an overview. |
| `echo_diff.py` | Field-level diff between a save written by the port and the original's re-save of it. |
| `content_audit.py` | Per-class content comparison of two same-phase saves (finds structurally legal but empty data). |
| `tile_diff.py` | Field-by-field diff of the 53-byte tile records between two saves. |
| `fog_audit.py` | Compares the serialized fog layer between saves. |
| `dump_walk.py`, `scan_walks.py` | Decode `CAct_Walk` action rows. |
| `analyze_w7d_production.py` | Reports native production slots and owner attribute pages. |
| `minidump_triage.py` | Maps a `Diggles.exe` crash minidump to the nearest decompiled function (pass the decompile directory as the second argument). |
| `Ghidra/*.java` | Ghidra scripts that dump save-related functions and vtables. |

All scripts take file paths as arguments; run any of them without arguments for usage.
