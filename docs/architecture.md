# Grid Architecture and Information Design

Status: Current architecture baseline

Repository artifacts are authoritative for implemented behavior. This document combines that implementation truth with the locked product design. When historical roadmap text, dormant prototype UI, or old tests conflict with this baseline, this baseline governs future Production work.

The platform-level BYOP, AUTO, offline intelligence, capability-gap, unknown-game, and CODE-to-publication contracts are defined in [platform-architecture.md](platform-architecture.md). Those contracts extend this baseline without weakening its deterministic evidence and authority boundaries.

## Status vocabulary

- **Implemented** — present in repository behavior and supported by current contracts/tests.
- **Partial** — some supporting implementation exists, but the complete user-facing capability is not composed or proven.
- **Dormant prototype** — code/tests/assets exist but are not current Production product authority.
- **Planned** — locked design or future architecture with no complete current implementation.
- **Prohibited** — behavior that must not be introduced under the current architecture.

See [capability-status.md](capability-status.md) for the matrix.

## Locked application information architecture

The main panel has no Home/Games/Workstation tab strip. Connected games are selected through circular game thumbnails. Selecting a game renders that game's mod/plugin workspace in the main panel.

A **game workspace** is the mod/plugin workspace. It owns the selected game's installation/profile context, mod/plugin evidence, native tool/output evidence, and related game-scoped surfaces. It is not nested under a separate global Workstation destination.

The left sidebar is contextual. When open and a game is selected, it becomes that user's game snapshot. **Library**, **Search**, and **Activity** are side-panel surfaces; they are not global main-panel tabs.

**Chat History** belongs to the assistant, not to global shell navigation.

See [ui-authority-boundaries.md](ui-authority-boundaries.md).

## Assistant, composer, and form

GRID has one assistant chat and one composer. The assistant may expose an optional structured data-intake form. `GAME/GRID` is visible only while that form is visible.

Game, Mods, Tools, and Class values are structured selections. Plain text is preserved as the user's claim/request and cannot silently become a technical target, diagnosis, or tool choice. A Class recipe may declare bounded, reviewable `intentPhrases` for a registered gameplay capability; exactly one matching phrase may preselect that exact Class and capability as structured intake. Ambiguous or unmatched prose selects nothing, and editing away an inferred phrase clears only the inferred selection.

The assistant has no diagnostic or repair authority. No model or AI may discover technical targets, create evidence, select a diagnosis, choose a remediation, authorize execution, execute a mutation, or verify success. Presentation may render deterministic immutable outputs without acquiring authority.

## Deterministic request and Class architecture

There are exactly 32 Class recipes beneath `scripts/health/classes/`. A Class is a routing and capability category: it defines selection policy, evidence requirements, supported games, and capability roots. It is not one giant per-Class script.

`Grid.RequestPlanner.ps1` validates Class recipes and their nonempty, unique gameplay-capability intent phrases, rejects machine paths/plugin identities/FormIDs embedded in recipes, and fails closed unless coverage and required context/evidence exist. `RequestPlanner.Tests.ps1` verifies explicit structured selections and that arbitrary plain-language edits do not change Class or invent Mods/Tools. App state separately verifies that one unambiguous catalog-owned intent phrase can bind only its declared gameplay capability.

Only registered deterministic coverage can reach collection. Unsupported Classes or tools report coverage gaps rather than guessing.

## Diagnostic subsystem

The reusable diagnostic subsystem is separately invoked repository/operator tooling. Its public result contract is exactly:

```text
Affected mod(s):
Mod role(s):
Finding:
Solution:
```

Unproved fields remain `UNRESOLVED`. Confidence is internal prioritization evidence and cannot independently resolve the result.

The canonical lifecycle is:

```text
Structured request + preserved prose
  -> request plan
  -> exact read authorization where required
  -> sealed evidence collection
  -> deterministic diagnosis/result
  -> inert proposal
  -> explicit authorization
  -> authorized executor
  -> verification / rollback
```

User objectives spanning more than one case are tracked in the fingerprinted problem ledger. A scan or sub-repair completing does not close its parent objective. Context drift reopens dependent ledger entries for evidence.

Diagnosis pipelines reject capabilities classified as external write or external process control.

## Shared capability versus game-native adapter boundary

Shared infrastructure belongs in `scripts/health/` and owns reusable semantics such as Class routing, capability composition, case/evidence contracts, fingerprints, confidence rules, authorization records, resource governance, deterministic result rendering, and lifecycle/verification contracts.

Game-native behavior belongs in `scripts/games/{game}/`. The game adapter owns native discovery, terminology, formats, collectors, tool invocation, and conversion of native observations to shared evidence. Skyrim-specific MO2, TES4/xEdit, plugin, FormID, NIF/DDS/BSA, Papyrus/SKSE, and related logic therefore stays under `scripts/games/skyrimspecialedition/`.

Shared code must not gain game-specific implementation ownership merely to make composition convenient.

## Execution authority

Implementation is not authority. Capability manifests classify side effects and required authority. Collectors are read-only; mutators are separate.

Known Skyrim mutation paths demonstrate the rule:

- `scripts/games/skyrimspecialedition/mo2/Set-GridPluginState.ps1` is a state-changing executor. Its authorized path is `scripts/games/skyrimspecialedition/health/actions/Invoke-GridAuthorizedPluginState.ps1`.
- `scripts/games/skyrimspecialedition/mo2/Set-GridModState.ps1` changes one exact `modlist.txt` marker. Its authorized path is `Invoke-GridAuthorizedModState.ps1`; the Production checkbox reaches it only through `Invoke-GridConfirmedModStateChange.ps1` after a fresh exact-target confirmation.
- mod-chain repair execution is mediated by `scripts/games/skyrimspecialedition/health/actions/Invoke-GridAuthorizedModChainRepair.ps1` and the shared `Invoke-GridModChainRepair.ps1` orchestration/lifecycle contracts.
- managed xEdit collector deployment is mediated by `Invoke-GridAuthorizedXEditCollectorProvisioning.ps1`; diagnosis cannot install tooling implicitly.

These scripts do not grant the assistant autonomous process-launch, game-write, mod-write, approval, or repair authority. Production WinUI has one deliberately narrow exception: a direct user click may enable or disable one authoritative non-foreign MO2 mod-list entry after an exact confirmation. That path must re-observe current state, mint and consume a one-use bound grant, create a backup, verify the marker, and roll back on failure. It grants no ordering, plugin, content, game-file, or general repair authority.

## Current application coverage

The repository contains WinUI/Core/MO2 implementation from earlier stages, including shell/workspace/assistant concepts and development fixtures. Those artifacts are useful implementation evidence, but portions of the UI/navigation model are **Dormant prototype** where they conflict with the locked shell described above.

The current assistant execution coverage is therefore **Partial**: UI/state scaffolding may exist, but no AI diagnostic/repair authority is permitted, and separately invoked repository scripts are not silently reclassified as assistant execution.

MO2 observation/discovery infrastructure is distinct from autonomous mod management. A script or service that can inspect or mutate under an explicit operator contract does not imply a complete autonomous Skyrim repair engine.

## Data and configuration boundary

Production must not embed user-specific installation/profile/modlist values. Runtime configuration and user data remain external to source. Documentation examples must use synthetic values. No task under this baseline may mutate runtime configuration or user data unless separately authorized.

## Historical roadmap status

Plans and earlier architecture text are historical evidence, not current Production authority. In particular, statements describing any of the following are superseded where they claim current or permanent Production behavior:

- Home as the permanent initial/main product surface;
- a game rail plus global History and Settings as the permanent navigation model;
- a separate global Library/profile manager;
- a global assistant docked on Home and all workspaces;
- a Home-owned Add Game flow as architectural law;
- History as a shell-global destination;
- any implication that repository executors make Production autonomous;
- any implication that AI may participate in diagnosis or repair.

The implementation may still contain dormant code/tests for those concepts. Future work must migrate implementation toward the locked architecture instead of restoring those obsolete claims.

## Conformance

`tests/ArchitectureConformance.Tests.ps1` mechanically checks documentation invariants that can be derived from repository artifacts. It does not replace Windows UI/build tests, live tool execution, or game verification.
