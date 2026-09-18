# Grid diagnostics

GRID diagnostics are deterministic repository/operator tooling. They do not grant the Production WinUI application or assistant autonomous authority.

The canonical flow is:

```text
Structured Game/Class/Mods/Tools + preserved prose
  -> request plan
  -> authorized read/evidence collection
  -> deterministic result
  -> inert proposal
  -> explicit authorization
  -> authorized action
  -> verification / rollback
```

Prose starts/preserves the user's claim. It does not set structured Class, Mods, Tools, or technical targets.

## Public result

```text
Affected mod(s):
Mod role(s):
Finding:
Solution:
```

Unproved fields are `UNRESOLVED`. Confidence is internal and cannot publish a finding on its own.

## Classes and capability composition

The 32 Class recipes are routing/capability categories. They select policy and semantic capability roots; they are not 32 monolithic scripts. Only registered coverage may collect; unsupported coverage fails closed.

Shared game-independent capability semantics live in `scripts/health/`. Game-native behavior lives in `scripts/games/{game}/`.

## Authority

Collectors are read-only. Mutators are separate and must be reached through their evidence/proposal/authorization wrappers. `Set-GridPluginState.ps1`, for example, is subordinate to `Invoke-GridAuthorizedPluginState.ps1` for an authorized operation.

No AI/model/assistant participates in technical target discovery, evidence creation, diagnosis, solution selection, authorization, execution, or verification.

See [architecture](architecture.md), [capability contracts](capability-contracts.md), [InvestigationPlan](investigation-plan.md), [result contract](diagnostic-result-contract.md), [root-cause diagnosis](root-cause-diagnosis.md), [offline Papyrus state analysis](papyrus-state-analysis.md), [remediation/rollback](remediation-and-rollback.md), and [verification](verification.md).
