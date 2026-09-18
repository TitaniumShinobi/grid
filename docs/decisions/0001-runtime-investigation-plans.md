# ADR 0001: Runtime InvestigationPlans replace hardcoded diagnostic recipes

Status: Accepted

## Context

Case-specific dispatch that embeds plugin names, locations, queries, or hypotheses creates false generality. User prose cannot safely become verified technical input.

## Decision

GRID represents deterministic investigation with versioned request/Class routing and runtime InvestigationPlans. The raw request remains a claim. Structured Game, Class, Mods, and Tools are explicit inputs; they are not mined from prose. Technical targets enter evidence only through explicit structured input or deterministic authorized collection.

The 32 Classes are routing/capability categories, not monolithic scripts. A Class recipe selects policy and semantic capability roots. Shared health infrastructure owns lifecycle/evidence semantics; native implementation remains in the canonical game adapter.

Collectors are read-only. Remediation is a separate evidence/fingerprint-bound proposal followed by explicit authorization, authorized execution, verification, and rollback behavior.

## Consequences

- Unsupported Class/tool coverage fails closed instead of guessing.
- New symptoms normally require reusable evidence/capability support, not case-specific production branches.
- Plain-language edits cannot silently reclassify a request or create Mods/Tools.
- Case-specific fixtures stay synthetic and never become Production defaults.
- Repository tooling remains outside assistant/WinUI Production authority unless explicitly composed under its contracts.
