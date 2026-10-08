# GRID

[UNDER DEVELOPMENT]

The **Gaming, Repairs, Investigations & Development (GRID) Game Manager** is a workspace for managing game mods and plugins, built around deterministic diagnostics and one core rule:

**The software, scripts, and documentation must be clear about what is actually implemented, what can make changes, and what cannot.**

The repository is the source of truth for current behavior.

Some capabilities exist as repository scripts that an operator can run separately. That does **not** automatically mean those capabilities are available inside the GRID application.

This README explains that distinction.

---

## What GRID is

GRID is built around individual games.

When you select a game, GRID opens that game’s mod and plugin workspace. The game workspace is the main working surface; it is not a second layer hidden behind another “workstation” or home screen.

The current Production design does **not** use a Home / Games / Workstation tab strip.

Instead:

- selecting a circular game thumbnail opens that game’s workspace;
- when the left sidebar is open, selecting a game changes the sidebar to that user’s snapshot for the selected game;
- Library, Search, and Activity open as side-panel surfaces rather than main-panel tabs;
- Chat History belongs to the assistant, not to GRID’s global navigation.

The assistant itself is intentionally simple: one conversation, one composer, and an optional structured intake form.

When that form is visible, the interface may show `GAME/GRID`.

Game, Mods, Tools, and Class are explicit structured selections. GRID does not extract or invent those selections from ordinary prose.

For the full design model, see:

- [Grid architecture](docs/architecture.md)
- [Platform architecture](docs/platform-architecture.md)
- [UI and authority boundaries](docs/ui-authority-boundaries.md)
- [Capability status](docs/capability-status.md)

---

## What the AI is allowed to do

GRID draws a strict boundary around AI authority.

The AI does **not** have authority to:

- diagnose a game or mod problem;
- decide on remediation;
- authorize a change;
- execute a change;
- verify that a repair worked.

The assistant can help present information and provide interface scaffolding, but deterministic systems — not the model — are responsible for technical diagnosis and controlled execution.

In other words:

**AI can help communicate the work. It does not become the authority for the work.**

---

## How GRID diagnostics work

GRID’s public diagnostic result uses one fixed structure:

```text
Affected mod(s):
Mod role(s):
Finding:
Solution:
```

That format is intentionally stable.

If GRID cannot prove a field from the evidence currently available, the value must be:

```text
UNRESOLVED
```

GRID may internally track confidence or evidence strength, but confidence alone cannot produce a public finding.

A finding must be supported by evidence.

---

## Classes are routing categories

The repository currently contains 32 Class recipes under:

```text
scripts/health/classes/
```

A Class is not one giant diagnostic script.

Classes are categories used to route a request toward the appropriate deterministic capabilities.

A structured Class request is built from explicit selections for:

- Game
- Class
- Mods
- Tools

The user’s plain-language description is preserved alongside those selections.

Plain text can describe what the user believes is happening, but it cannot silently redefine the request, change its Class, or create technical targets that the user did not explicitly select.

---

## Shared diagnostics and game-specific behavior

GRID separates reusable diagnostic infrastructure from game-native behavior.

Shared, game-independent diagnostic systems belong in:

```text
scripts/health/
```

Game-specific adapters, collectors, tools, and state-changing actions belong in:

```text
scripts/games/{game}/
```

For example, Skyrim Special Edition owns Skyrim-specific behavior involving:

- Mod Organizer 2;
- TES4 / xEdit;
- plugins;
- FormIDs;
- assets;
- other Skyrim-native operations.

Those capabilities live under:

```text
scripts/games/skyrimspecialedition/
```

A capability manifest describes what an operation means and what authority it requires.

The existence of a source file or script does **not** give that operation Production authority.

---

## Repository tools are not automatically application features

This distinction is important throughout GRID.

The repository contains deterministic systems for:

- collecting evidence;
- planning;
- building proposals;
- authorization;
- verification;
- narrowly scoped execution.

Many of those systems are currently **repository/operator tools**.

They can be invoked separately through their documented contracts, but they are not automatically wired into the Production WinUI application.

For example, GRID contains scripts that can perform controlled plugin-state changes.

`Set-GridPluginState.ps1` performs the underlying execution.

It is **not** the authorized entry point.

The authorized path is:

```text
Invoke-GridAuthorizedPluginState.ps1
```

Likewise, mod-chain repair execution is mediated through:

```text
Invoke-GridAuthorizedModChainRepair.ps1
```

That path depends on its evidence, proposal, authorization, and lifecycle contracts.

The presence of those scripts does **not** mean the Production application can independently:

- launch arbitrary tools;
- approve actions;
- repair a game;
- authorize its own changes.

---

## The current Production mutation

There is currently one Production state-changing path.

A user can directly confirm a checkbox transition affecting one authoritative MO2 `modlist.txt` entry.

That operation is constrained by a documented process involving:

- explicit user confirmation;
- one-use authorization;
- backup;
- execution;
- verification;
- rollback.

This is a narrow, specific capability.

It should not be interpreted as general autonomous repair authority.

For the relevant contracts, see:

- [Diagnostics](docs/diagnostics/README.md)
- [Capability contracts](docs/diagnostics/capability-contracts.md)
- [Remediation and rollback](docs/diagnostics/remediation-and-rollback.md)

---

## What exists in the repository today

GRID currently includes:

- a WinUI 3 / .NET application;
- framework-neutral Core state;
- MO2 discovery and observation infrastructure;
- deterministic development fixtures;
- a substantial deterministic diagnostic subsystem that can be invoked separately.

The repository also contains older UI and MO2 application concepts that predate the current locked Production shell.

Those older concepts are **dormant prototypes or historical implementation**.

Their presence in the source tree is not authority to restore obsolete navigation or product behavior.

The current capability-status matrix is documented in:

[Capability status](docs/capability-status.md)

That document explicitly distinguishes:

- Implemented
- Partial
- Dormant prototype
- Planned
- Prohibited

---

## Current assistant coverage

The assistant currently provides presentation and scaffolding.

It does not diagnose or repair.

Deterministic repository tooling may perform those operations when separately invoked under the correct contracts, but those capabilities do not become AI authority simply because the assistant can discuss them.

---

## Production data and development fixtures are different things

Production must use real, persisted references and current observations.

Development and test environments may use deterministic fixture data, including synthetic:

- user identities;
- game states;
- health signals;
- proposals;
- other test values.

Those fixtures must not become Production defaults.

Do not hardcode environment-specific values — including UNDEFEATED-specific values or other user-specific values — into Production source or documentation examples.

---

## GTA V guided setup

The repository includes a documented GTA V guided-setup slice:

[GTA V guided setup](docs/gta-v-guided-setup.md)

It is currently read-only.

It is a repository/operator workflow and has **not yet been composed into the Production WinUI surface**.

---

## Repository layout

The main repository areas are:

```text
src/Grid.Core
```

Framework-neutral domain and application contracts and state.

```text
src/Grid.Mo2
```

Windows-only Mod Organizer 2 discovery, observation, and related infrastructure.

```text
src/Grid.App
```

The WinUI application shell and UI implementation.

```text
scripts/health
```

Shared deterministic diagnostic infrastructure, including Class routing, evidence handling, capability contracts, lifecycle logic, and verification.

```text
scripts/games/{game}
```

Game-native adapters, collectors, tools, and authorized actions.

```text
docs
```

Architecture, ADRs, diagnostic contracts, status documentation, and verification guidance.

```text
tests
```

.NET tests and architectural conformance checks.

```text
legal
```

Provenance, asset, and third-party review ledgers.

No final repository license is currently asserted.

```text
eng/provenance
```

Deterministic provenance inventory, upstream comparison, release-refusal, and attorney-export tooling.

---

## Before working on diagnostics or repair

Before doing diagnostic, health, inspection, repair, or recovery work in this repository, read:

```text
AGENTS.md
```

and:

[docs/CODEX_DIAGNOSTIC_SCRIPTING.md](docs/CODEX_DIAGNOSTIC_SCRIPTING.md)

These instructions are mandatory.

---

## Verification boundaries

Some GRID verification can be performed anywhere.

Other verification requires a suitable Windows environment.

The following are Windows-specific and must only be claimed when they were actually run on Windows:

- application builds that depend on the Windows environment;
- WinUI Automation;
- installer checks;
- live MO2 execution;
- live xEdit execution;
- game-side verification.

Documentation checks and architectural or source-conformance checks can run independently of those platform-specific validations.

Do not claim Windows or game-side verification when only portable checks were performed.

---

## Repository-owned verification

GRID has one authoritative verification entry point:

```text
eng/Test-Grid.ps1
```

Its checked-in suite inventory is:

```text
eng/verification.manifest.v1.json
```

Registered verification suites run in manifest order.

If a registered suite is missing, verification fails before execution begins.

### Portable source verification

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\eng\Test-Grid.ps1 -Lane Portable -Configuration Debug
```

### Windows source verification

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\eng\Test-Grid.ps1 -Lane WindowsSource -Configuration Debug
```

Use:

```text
-Configuration Release
```

when Release is the intended source configuration.

The runner does not guess whether Debug or Release should be used.

Verification receipts and detailed logs are written under:

```text
artifacts/verification/<run-id>/
```

The Portable lane does **not** certify:

- WinUI automation;
- Windows-only MO2 behavior;
- installation;
- distribution behavior.

The `Distribution` lane is reserved in manifest version 1 and intentionally has no registered execution checks.

---

## Older documentation and prototypes

Some older GRID material describes product concepts that are no longer part of the locked Production design.

Examples include:

- Home as a permanent main surface;
- a game rail combined with global History or Settings navigation;
- a globally docked assistant;
- a Home-owned Add Game flow.

Those ideas may still exist in source files or historical documents as prototype evidence.

They are not current Production architecture.

The current concise record is maintained in:

- [Capability status](docs/capability-status.md)
- [ADR 0003 — Production shell, workspace, and assistant](docs/decisions/0003-production-shell-workspace-and-assistant.md)

---

## The rule to keep in mind

When reading or working in GRID, do not assume that the existence of code means the Production application can use it.

Always distinguish between:

1. what exists in the repository;
2. what deterministic tooling can do when separately invoked;
3. what is actually composed into Production;
4. what requires explicit authorization;
5. what the AI is allowed to do.

That distinction is central to GRID’s architecture.