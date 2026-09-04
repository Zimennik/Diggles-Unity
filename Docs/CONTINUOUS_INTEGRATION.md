# Headless tests

The whole suite (EditMode + PlayMode) can run without opening the Unity editor:

```powershell
$env:DIGGLES_DATA_ROOT = 'C:\path\to\Diggles\data'
.\.github\scripts\run-unity-tests.ps1 -TestPlatform All # or EditMode / PlayMode
```

Requirements:

- The project must be closed in Unity (the editor holds a project lock).
- Unity `6000.0.62f1`. The script looks for it in the Unity Hub default location;
  set `UNITY_EDITOR_PATH` (or pass `-UnityEditorPath`) when it is installed elsewhere.
- `DIGGLES_DATA_ROOT` (or `-DataRoot`) pointing at the original game's `data/` folder.

Results and logs land in `artifacts/unity-tests/` (NUnit XML + Unity log per platform).
The script fails when NUnit reports a failed test or Unity exits with an error.
Skipped EditMode cases are explicitly gated fixtures or benchmarks, not hidden failures.

## Data validation

Before starting Unity the script checks the data folder against
`.github/scripts/original-data.manifest.json` (expected paths, sizes and SHA-256 hashes of the
English Steam `2.1.1.10` release; no game data is stored in the repository). Run it alone with:

```powershell
.\.github\scripts\test-original-data.ps1 -DataRoot $env:DIGGLES_DATA_ROOT
```

Other releases (GOG, other languages) differ in a few files; the manifest check can be skipped by
running Unity's Test Runner from the editor instead (`Window → General → Test Runner`), which
resolves the data root through `diggles_config.json` like the game itself.

## How the data root reaches the tests

EditMode fixtures resolve the data folder through `OriginalDataTestFixture`: the
`DIGGLES_DATA_ROOT` environment variable wins; otherwise the normal `diggles_config.json`
lookup applies. The headless script also writes a temporary project-root `diggles_config.json`
for the PlayMode run and restores whatever was there afterwards.

## CI

The original data is proprietary, so the suite cannot run on a hosted GitHub runner. A
self-hosted Windows runner with an activated Unity editor and a local copy of the data can call
the same script; the repository ships no workflow by default.

## Failure triage

1. Read the NUnit XML first; it is the authoritative pass/fail source.
2. Use the paired Unity log for compilation errors, boot failures and runtime context.
3. Re-run the same platform locally with the script above.
4. Do not change a test expectation until the current C# behaviour has been checked against the
   decompile, the original scripts and the memory-dump evidence that documents that rule.
