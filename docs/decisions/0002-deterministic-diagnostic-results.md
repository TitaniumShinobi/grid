# ADR 0002: Diagnostics publish deterministic four-field results without AI

Status: Accepted

## Context

Users need a stable evidence-backed result rather than a conversation or confidence dump. Confidence alone cannot prove an implicated mod, role, cause, or repair.

## Decision

GRID publishes exactly four default fields: **Affected mod(s)**, **Mod role(s)**, **Finding**, and **Solution**. Each field resolves only from current deterministic evidence; otherwise it is `UNRESOLVED`. When needed, Solution states the smallest bounded next investigation step.

Semantic result identity excludes run-local IDs, timestamps, paths, and collection order. Confidence remains internal evidence-strength information and cannot independently resolve a field.

No AI/model/assistant participates in structured target discovery, evidence creation, diagnosis, remediation selection, authorization, execution, or verification. A UI may render the immutable result without receiving those authorities.

## Consequences

- Same semantic context/evidence/policy produces the same semantic result.
- Ambiguity remains unresolved rather than selecting a speculative winner.
- A solution is inert until separate proposal/authorization/execution/verification contracts succeed.
- The assistant may present the result but cannot manufacture or alter it.
