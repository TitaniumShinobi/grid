# Deterministic capability contracts

GRID composes diagnostics from versioned semantic capabilities rather than case-specific path dispatch. A capability ID names one stable operation that can be composed, invoked, authorized, audited, resumed, and terminated under a contract. A helper/source file does not receive authority merely because it exists.

Shared contracts live in `scripts/health/capabilities.v1.json`; game-owned contracts live in `scripts/games/{game}/capabilities.v1.json`.

## Contract shape

A capability contract defines semantic ID/version, owner scope, implementation/public entry point, input/output schema, preconditions/dependencies, side-effect classification, required authority, rollback mode, and allowed terminal states.

Capability implementations remain in canonical ownership roots. Shared contracts must not import literal game implementation paths as a substitute for adapter composition.

## Classes consume capabilities

The 32 Class recipes are routing/capability categories. They may select one or more capability roots according to supported game, explicit structured selections, evidence policy, and registered coverage. They are not 32 monolithic scripts.

Class recipes may not embed machine paths, concrete plugin identities, or FormIDs. Unsupported coverage fails closed.

## Side effects and authority

Read-only diagnosis capability graphs must not include external writes or external process control. State-changing capabilities require explicit lifecycle authority defined by their contract.

Executor files are subordinate to authorized wrappers. Directly invoking an executor may exercise implementation, but it is not an authorized GRID action unless the proposal/authorization/verification contract is satisfied.

## Production composition

Repository registration does not mean WinUI/assistant composition. Production capability claims require explicit application composition. AI/model/assistant output cannot create capability authority.

## Architecture enforcement

`Test-GridCapabilityArchitecture.ps1` checks structural/capability ownership rules. `tests/ArchitectureConformance.Tests.ps1` additionally checks the current documentation and locked architecture invariants that can be verified statically.
