# Diagnostic architecture

## Responsibility boundaries

Shared health infrastructure owns structured request/Class routing, case identity, InvestigationPlans, semantic capability composition, evidence normalization/provenance, fingerprints, confidence policy, deterministic result rendering, authorization records, resource governance, verification, rollback contracts, and audit history.

Game adapters own native context discovery, terminology, formats, collectors, tools, and translation of native observations into shared evidence. Native Skyrim logic remains under `scripts/games/skyrimspecialedition/`.

## Request boundary

Game, Class, Mods, and Tools are structured selections. Plain text is preserved as the user's claim and cannot reclassify the request or invent technical targets. The request planner fails closed for unsupported coverage, missing context, or missing evidence.

The 32 Class recipes are routing/capability categories, not monolithic scripts.

## Data flow

1. Preserve raw prose and constraints as claims.
2. Bind explicit structured selections and authoritative context.
3. Resolve Class coverage and semantic capability dependencies.
4. Obtain exact read authorization where required.
5. Run bounded read-only collectors.
6. Seal provenance-bearing evidence under current context fingerprints.
7. Materialize the deterministic four-field result only from proof obligations.
8. Build an inert proposal for any supported state change.
9. Obtain explicit authorization immediately before mutation.
10. Invoke the authorized wrapper/executor path.
11. Verify postconditions and preserve rollback/audit records.

## Result boundary

The public result is exactly **Affected mod(s)**, **Mod role(s)**, **Finding**, and **Solution**. Confidence cannot independently resolve any field. Ambiguity remains `UNRESOLVED`.

## Capability composition

Capability IDs name semantic operations, not files. Shared and game-owned manifests provide versioned contracts, dependencies, side-effect classification, required authority, rollback mode, and terminal states. Missing or incompatible capability graphs fail closed.

## Authority boundary

Diagnosis pipelines cannot contain external-write or external-process-control capabilities. State-changing executors are not authorized entry points by themselves; callers must use the corresponding authorized lifecycle wrapper.

Repository tooling is not automatically composed into Production and does not grant assistant authority. AI/model output cannot enter technical targets, evidence, findings, solutions, authorization, execution, or verification.

## Authorization and resume security

Planning and review do not grant authority. Current authorization is an explicit durable grant with a random one-time secret, protected persisted proof, exact semantic binding, expiry, exclusive execution lease, and terminal `Consumed`/`Failed` state. Public digests and predictable IDs cannot be submitted as approval.

Tool checkpoint resume revalidates workspace, request, submission, envelope, plan, grant, authorization binding, capability/version, adapter/version, normalized input, output, evidence, and receipt digests. Sealed-case reuse additionally requires the current semantic baseline fingerprint and sealed request/plan/grant identities to match. See `docs/security/authorization-and-receipt-binding.md`.
