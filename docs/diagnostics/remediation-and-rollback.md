# Remediation, authorization, and rollback

Diagnosis is read-only. A deterministic finding does not authorize repair.

## Required lifecycle

A state change requires:

1. current evidence and context fingerprints;
2. a bounded inert proposal identifying exact targets/actions;
3. explicit authorization under the capability contract;
4. invocation through the authorized wrapper rather than a bare executor;
5. backup/staging/rollback behavior appropriate to the operation;
6. deterministic postcondition verification;
7. a structured terminal/audit record.

Authorization is not supplied by prose, confidence, AI/model output, or the existence of an executor file.

## Executor reconciliation

`Set-GridPluginState.ps1` implements a narrow plugin-state mutation. The authorized GRID path is `Invoke-GridAuthorizedPluginState.ps1` plus its bound proposal/authorization/verification contract.

`Set-GridModState.ps1` implements the narrower single-entry MO2 mod enable/disable transition. Production WinUI may reach it only after the user confirms the displayed installation, profile, mod, current state, and desired state. `Invoke-GridConfirmedModStateChange.ps1` re-observes that state, creates evidence and an inert proposal, issues a random-secret one-use grant, and consumes it through `Invoke-GridAuthorizedModState.ps1`. The executor preserves a byte-for-byte backup, changes only the exact `+`/`-` marker, rereads the result, and restores the backup on verification failure. Foreign/core (`*`), separator, missing, duplicate, and stale entries are refused.

Mod-chain repair likewise uses the game-owned proposal/inspection/action implementation together with `Invoke-GridAuthorizedModChainRepair.ps1` and shared orchestration. Managed xEdit collector deployment has its own authorized provisioning wrapper.

Bypassing an authorized wrapper may test an implementation but must not be documented as an authorized Production repair.

## Production boundary

Repository/operator actions are not automatically exposed to the WinUI Production application or assistant. The sole current WinUI mutation exposure is the direct, user-confirmed, single-mod MO2 checkbox transition described above. No AI/model may approve, execute, or verify remediation.

## Durable mutation authorization

Current mutation wrappers consume a persisted random-secret grant rather than a token derived from a proposal/specification ID or public hash. The grant is bound to actor/session, workspace/request/submission, envelope/plan/scope, proposal or specification, capability/version, exact targets, normalized input, authority class, and expiry. The wrapper acquires an exclusive lease before mutation and ends the grant in `Consumed` or `Failed`; concurrent and replayed consumption is refused.

A `WhatIf` invocation may validate and consume the explicitly presented one-use grant while performing no external mutation. A subsequent real execution therefore requires a newly issued grant.
