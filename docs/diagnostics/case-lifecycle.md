# Diagnostic case lifecycle

Each run writes into one case directory. A case contains the raw request, current context, InvestigationPlan, collected evidence, internal hypothesis ranking, deterministic diagnostic result, proposals, authorization records, verification results, and—when the request belongs to a durable multi-case objective—a `diagnosis/problem-ledger.v1.json` projection. A newly created InvestigationPlan uses schema v2 and binds the ordered resolved capability closure; v1 remains readable for historical replay. Large native output stays in the case directory; compact normalized evidence is used by deterministic resolvers.

The problem ledger preserves user claims and desired outcomes independently from a run's completion. Only evidence-bound authorities may advance its states. A changed selected context reopens dependent results to `NeedsEvidence`; a completed collector or sealed case never implies the entire ledger is resolved.

Supported lifecycle states are intentionally honest:

- `NeedsContext`: a game, installation, or profile cannot be resolved unambiguously.
- `NeedsEvidence`: the case exists but lacks verified targets or observations.
- `ReadyToCollect`: the plan contains sufficient bounded targets for a collector.
- `Diagnosed`: an evidence-backed finding and complete four-field diagnostic result were materialized.
- A typed failure state: collection, parsing, launch, staleness, authorization, mutation, verification, or rollback failed.

Continuation must preserve the case ID. A context or source fingerprint change makes dependent evidence, proposals, and authorizations stale rather than silently retargeting them.

Collecting evidence or scoring candidates does not by itself make a case `Diagnosed`. If proof obligations have not passed, the case remains `NeedsEvidence` or returns to `ReadyToCollect` with one bounded next operation. The public result still renders all four headings and marks unproved fields `UNRESOLVED`.

The semantic result fingerprint excludes the case ID and other run-local metadata. This permits separate runs against the same normalized state and resolver policy to produce the same semantic result without conflating their audit envelopes.

Case files must be written only beneath the selected case directory. External tools may write their ordinary logs, but Grid collectors must not use a game, profile, MO2, or tool directory as a report destination.

## Sealed baseline cases

The diagnostic store resolves from `GRID_DATA_ROOT` when configured and otherwise from the platform Grid application-data root. Baseline cases are assembled under `transactions/<transactionId>/`, then moved atomically to `cases/v1/<caseId>/` only after the run and `case-manifest.v1.json` have been hashed. Raw bytes that must be retained use `blobs/sha256/<prefix>/<SHA256>`. A sealed case is append-only; a retry creates a new run or case lineage instead of overwriting terminal evidence.

Baseline run state is independent of evidence gates and sufficiency. Gates are `InstallationBaseline`, `ProfileBaseline`, `PluginBaseline`, `AssetBaseline`, `PriorCaseBaseline`, `SymptomEvidenceBaseline`, and `RuntimeReferenceBaseline`. A run may be `Completed` while one or more gates remain incomplete and the sufficiency result is `InsufficientForRequestedDiagnosis`. This distinction prevents collection completion from being reported as diagnosis or repair.

Export packages use deterministic entry order and timestamps and are verified by digest during import. Case deletion is an explicit exact-case operation; shared blobs are garbage-collected only after all remaining sealed manifests have been validated and shown not to reference them.
