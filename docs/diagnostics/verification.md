# Repository verification

Verification claims are bounded by the lane and platform actually executed. `eng/Test-Grid.ps1` is the single repository-owned verification entry point; `eng/verification.manifest.v1.json` is the authoritative ordered suite inventory.

## Manifest contract

Every expected suite is registered explicitly with a stable ID, order, kind, path, lane membership, platform applicability, and configuration mode. The runner validates the manifest before execution and fails if any registered suite path is missing. Runner tests also compare the manifest with the checked-in shared-health and Skyrim `*.Tests.ps1` inventories so a newly added suite cannot silently remain outside the authoritative list.

The v1 manifest registers:

- `tests/ArchitectureConformance.Tests.ps1`;
- every shared-health PowerShell suite under `scripts/health/tests/`;
- every Skyrim PowerShell suite under `scripts/games/skyrimspecialedition/tests/`;
- `Grid.Core.Tests`;
- `Grid.Mo2.Tests`;
- `Grid.DevLauncher.Tests`;
- `Grid.App.UiTests`;
- explicit source build steps for applicable lanes.

## Lanes

### Portable

Run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\eng\Test-Grid.ps1 -Lane Portable -Configuration Debug
```

The portable lane runs repository checks explicitly registered as platform-neutral and the portable `Grid.Core.Tests` build/run. It does **not** certify WinUI, Windows-only MO2 integration, installation, or distribution behavior.

### Windows source

Run Debug:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\eng\Test-Grid.ps1 -Lane WindowsSource -Configuration Debug
```

Run Release:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\eng\Test-Grid.ps1 -Lane WindowsSource -Configuration Release
```

The Windows source lane runs architecture conformance, shared-health PowerShell suites, Skyrim PowerShell suites, an explicit `Grid.sln` build, all four executable test projects, and UI automation. The same configuration is supplied to the solution build, executable test projects, and UI target selection. The UI command also binds `GRID_UI_EXECUTABLE` to the selected configuration's `Grid.exe`, preventing a current Debug source build from being confused with a stale Release UI binary.

### Distribution

`Distribution` is a reserved lane in manifest v1. It intentionally has no registered checks, and invoking it refuses rather than implying that installer or distribution behavior was verified. Installer exercise remains outside this plan.

## Results and receipts

Each registered check has exactly one result:

- `Passed` — command ran and returned exit code 0;
- `Failed` — command ran or launch failed and verification failed;
- `SkippedUnsupportedPlatform` — the manifest declares a platform requirement not present on the current host;
- `NotRun` — a prior failure stopped later execution when `-ContinueOnFailure` was not requested.

A failure makes the receipt's `overallResult` `Failed`. Unsupported-platform skips do not become passes for the unsupported capability.

Each run writes under:

```text
artifacts/verification/<run-id>/
|-- verification-receipt.v1.json
`-- logs/
    `-- <order>-<suite-id>.log
```

The receipt records platform/architecture, .NET SDK when available, PowerShell version, lane, selected configuration, total duration, and for every check: stable command identity, configuration, start time, duration, exit code, result, and detailed log path. The receipt shape is defined by `eng/verification-receipt.v1.schema.json`.

Terminal output is intentionally concise; complete stdout/stderr remains in the per-check logs.

## Verification of the orchestrator

`tests/VerificationOrchestrator.Tests.ps1` verifies:

- authoritative discovery/inventory coverage;
- stable manifest ordering;
- refusal of missing registered suites;
- refusal of duplicate order values;
- explicit Debug/Release selection for executable tests and UI automation;
- configuration-specific `GRID_UI_EXECUTABLE` selection;
- receipt result and ordering validation;
- parseability/version identity of the manifest and receipt schemas.

`tests/ArchitectureConformance.Tests.ps1` also requires the verification runner, manifest, schemas, and orchestrator tests to remain checked in.

## Claim boundary

Linux or macOS execution may validate only checks actually registered as portable. It cannot certify WinUI UI Automation, Windows PowerShell 5.1 behavior, Windows-native MO2 integration, installation, packaged distribution, or live game/mod behavior. Windows source verification likewise does not certify installer/distribution behavior unless a future distribution lane explicitly registers and executes such checks.

## Authorization security coverage

The manifest registers `scripts/health/tests/AuthorizationStore.Tests.ps1`. Security verification covers random-secret non-persistence, exact actor/session/context/scope binding, expiry, exclusive consumption, replay refusal, cloned-grant refusal, historical read compatibility, receipt transplant/content-binding refusal, and current request sealed-case identity reuse. Windows execution of the authoritative lane remains the source of Windows-only verification claims.
