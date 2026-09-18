# ADR 0004: Classes route deterministic capabilities; executors do not create authority

Status: Accepted

## Context

GRID contains 32 Class recipes and a growing set of shared/game-native scripts. Without a clear boundary, file count can be mistaken for 32 monolithic diagnostic programs, and executor presence can be mistaken for Production or AI authority.

## Decision

A Class is a routing/capability category. The 32 canonical recipes under `scripts/health/classes/*/class.v1.json` declare supported games, selection/evidence policy, coverage, and semantic pipeline roots. A Class may compose multiple reusable capabilities; unsupported coverage fails closed.

Shared game-independent capabilities remain in `scripts/health/`. Native adapters/collectors/actions remain in `scripts/games/{game}/`.

Game, Class, Mods, and Tools are explicit structured selections. Prose cannot create or change them.

State-changing executor scripts are subordinate to authorized wrappers/lifecycle gates. For example, `Set-GridPluginState.ps1` is not the authority boundary; `Invoke-GridAuthorizedPluginState.ps1` plus its proposal/authorization/verification requirements is. The same principle applies to mod-chain repair and managed xEdit collector provisioning.

No AI/model/assistant may acquire diagnostic or repair authority. Repository tooling does not become Production behavior until explicitly composed under the same contracts.

## Consequences

- Adding a Class does not require a monolithic per-Class script.
- Reusable shared capabilities can serve multiple Classes/games without absorbing native logic.
- Native game behavior stays in game adapters.
- Direct executor invocation cannot be documented as an authorized GRID repair path.
- Production capability claims must identify actual composition, not merely implementation files.

## Authorization clarification

Authorized wrappers now require durable one-use grants. Grant secrets are random and returned only at issuance; public proposal/specification identifiers and digests cannot serve as approval. Grants are semantically bound and exclusively leased before execution. Current receipts and sealed-case reuse must validate the same context before reuse. Historical digest-based authorization artifacts remain readable but have no current execution authority.
