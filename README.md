# Grid

Grid is a game-centric mod/plugin workspace with deterministic diagnostic tooling and strict authority boundaries. Repository artifacts are authoritative for implemented behavior. Documentation must distinguish application behavior from separately invoked repository/operator scripts.

## Architecture baseline

The locked Production information architecture is:

- There is no Home/Games/Workstation tab strip in the main panel.
- Selecting a circular game thumbnail renders that game's mod/plugin workspace in the main panel.
- A game workspace is the mod/plugin workspace; it is not a separate navigation destination layered on top of another workstation surface.
- When the left sidebar is open, selecting a game changes that sidebar to the selected user's game snapshot.
- Library, Search, and Activity are side-panel surfaces, not main-panel tabs.
- Chat History belongs to the assistant rather than the shell's global navigation.
- The assistant is one chat with one composer and an optional structured data-intake form.
- `GAME/GRID` appears only while that structured form is visible.
- Game, Mods, Tools, and Class are structured selections. They are not inferred or mined from prose.
- AI has no diagnostic, remediation, authorization, execution, or verification authority.

See [Grid architecture](docs/architecture.md), [platform architecture](docs/platform-architecture.md), [UI and authority boundaries](docs/ui-authority-boundaries.md), and the [capability-status matrix](docs/capability-status.md).

The repository/operator GTA V guided-setup slice is documented in
[GTA V guided setup](docs/gta-v-guided-setup.md). It is read-only and is not yet
composed into the Production WinUI surface.

## Deterministic diagnostic contract

The public diagnostic result remains exactly:

```text
Affected mod(s):
Mod role(s):
Finding:
Solution:
```

A field that cannot be proved from current evidence is `UNRESOLVED`. Confidence is an internal evidence-strength mechanism; it cannot publish a finding by itself.

The repository contains 32 Class recipes under `scripts/health/classes/`. Classes are routing and capability categories, not 32 monolithic diagnostic scripts. A Class request is formed from explicit structured Game, Class, Mods, and Tools selections plus preserved plain text. Plain text can express the user's claim, but it cannot reclassify the request or create structured technical targets.

## Shared capabilities and game-native adapters

Shared game-independent diagnostic infrastructure belongs in `scripts/health/`. Game-native adapters, collectors, and state-changing actions belong in `scripts/games/{game}/`. Skyrim Special Edition owns MO2, TES4/xEdit, plugin, FormID, asset, and other Skyrim-native behavior under `scripts/games/skyrimspecialedition/`.

Capability manifests describe semantic operations and authority. A source file's existence does not grant Production authority.

## Repository tooling versus Production authority

The repository includes deterministic collectors, planners, proposal builders, authorization wrappers, verification logic, and narrowly scoped executors. Those are separately invoked repository/operator tools unless and until Production composition explicitly wires them in.

In particular:

- diagnosis pipelines are read-only and reject mutation capabilities;
- mutation executors are subordinate to explicit authorized wrappers and lifecycle gates;
- `Set-GridPluginState.ps1` is an executor, while `Invoke-GridAuthorizedPluginState.ps1` is the authorized entry point for that operation;
- mod-chain repair execution is likewise mediated by `Invoke-GridAuthorizedModChainRepair.ps1` and its evidence/proposal/authorization contracts;
- existence of these scripts does not mean the WinUI Production application can launch tools, approve actions, or repair a game autonomously. The one current Production mutation is a direct user-confirmed checkbox transition for one authoritative MO2 `modlist.txt` entry, mediated by the documented one-use authorization, backup, verification, and rollback path.

See [diagnostics](docs/diagnostics/README.md), [capability contracts](docs/diagnostics/capability-contracts.md), and [remediation and rollback](docs/diagnostics/remediation-and-rollback.md).

## Current implementation truth

The repository contains a WinUI 3/.NET application, framework-neutral Core state, MO2 observation infrastructure, deterministic development fixtures, and a substantial separately invoked diagnostic subsystem. Some UI and MO2 application concepts in the repository predate the locked shell model and are therefore **dormant prototype or historical implementation**, not authority to restore obsolete navigation.

The assistant's current execution coverage is presentation/scaffolding only. No model or AI is permitted to diagnose or repair. Repository scripts can perform deterministic work when separately invoked through their contracts; that does not make those operations assistant authority.

See [capability status](docs/capability-status.md) for the explicit Implemented / Partial / Dormant prototype / Planned / Prohibited matrix.

## Production versus development fixtures

Production data must come from persisted real references and current observations. Deterministic fixture identities, game states, health signals, proposals, and other synthetic values are development/test data only and must not become Production defaults. Do not hardcode UNDEFEATED-specific values or other user environment values into source or documentation examples.

## Repository layout

- `src/Grid.Core` — framework-neutral domain/application contracts and state.
- `src/Grid.Mo2` — Windows-only MO2 discovery/observation and related infrastructure.
- `src/Grid.App` — WinUI application shell and UI implementation.
- `scripts/health` — shared deterministic diagnostic infrastructure, Class routing, evidence, capability, lifecycle, and verification logic.
- `scripts/games/{game}` — game-native adapters, collectors, tools, and authorized actions.
- `docs` — architecture, ADRs, diagnostic contracts, status, and verification guidance.
- `tests` — .NET and architectural conformance checks.
- `legal` — provenance, asset, and third-party review ledgers; no final repository license is asserted yet.
- `eng/provenance` — deterministic provenance inventory, upstream comparison, release refusal, and attorney-export tooling.

## Mandatory repository instructions

Before diagnostic, health, inspection, repair, or recovery work, read and follow `AGENTS.md` and [docs/CODEX_DIAGNOSTIC_SCRIPTING.md](docs/CODEX_DIAGNOSTIC_SCRIPTING.md).

## Verification boundary

Windows application builds, UI Automation, installer checks, live MO2/xEdit execution, and game-side verification are Windows-specific and must only be claimed when actually run on a suitable Windows environment. Documentation/conformance checks may run independently of those platform-specific validations.

## Superseded statements

Older roadmap and architecture text that described Home as the permanent main surface, a game rail plus global History/Settings navigation, a globally docked assistant, or a Home-owned Add Game flow no longer describes the locked Production product design. Those concepts may remain in source or historical artifacts as dormant prototype evidence, but they are not current architectural authority. The concise record is maintained in [docs/capability-status.md](docs/capability-status.md) and [ADR 0003](docs/decisions/0003-production-shell-workspace-and-assistant.md).

## Repository-owned verification

`eng/Test-Grid.ps1` is the authoritative verification entry point. Its checked-in inventory is `eng/verification.manifest.v1.json`; registered suites are run in manifest order and a missing registered suite is a pre-execution failure.

Portable source verification:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\eng\Test-Grid.ps1 -Lane Portable -Configuration Debug
```

Windows source verification:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\eng\Test-Grid.ps1 -Lane WindowsSource -Configuration Debug
```

Use `-Configuration Release` when Release is the intended source configuration. The runner never infers Debug versus Release for builds or UI automation. Receipts and detailed logs are written below `artifacts/verification/<run-id>/`.

The portable lane does not certify WinUI automation, Windows-only MO2 behavior, installation, or distribution behavior. The `Distribution` lane is reserved in manifest v1 and intentionally has no registered execution checks.
