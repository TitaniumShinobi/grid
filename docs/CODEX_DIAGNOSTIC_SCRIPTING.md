# Grid Diagnostic Scripting Instructions for Codex

Status: Normative repository instructions  
Scope: All diagnostic, health, repair, inspection, and recovery work in Grid

Codex must follow this document together with the nearest applicable `AGENTS.md`.

## 1. Primary objective

Convert repeated diagnostic labor into reusable deterministic capabilities. Prefer extending a cohesive reusable script/capability over creating case-specific one-off automation.

Repository tooling must be suitable for explicit operator invocation and future authorized composition, but **existence is not Production application authority**.

## 2. Product authority boundary

The assistant and any AI/model have no diagnostic or repair authority. They must not:

- infer structured Game, Mods, Tools, or Class selections from prose;
- create technical targets or positive evidence;
- diagnose, choose a solution, authorize, execute, or verify a repair;
- treat a repository executor as callable by Production merely because the file exists.

A presentation surface may render deterministic immutable results. Structured selections must come from explicit UI/form/request fields or deterministic non-AI context already authorized by contract.

The locked assistant has one chat, one composer, and an optional structured data-intake form. `GAME/GRID` exists only while the form is visible. These UI facts do not grant the assistant diagnostic capability.

## 3. Canonical directory ownership

Shared, game-independent diagnostic infrastructure belongs in:

```text
scripts/health/
```

Game-specific adapters, collectors, and actions belong in:

```text
scripts/games/<canonical-game>/
```

For Skyrim Special Edition:

```text
scripts/games/skyrimspecialedition/
```

Do not duplicate game implementation beneath `scripts/health/`.

## 4. Shared engine versus adapter boundary

Shared health owns reusable semantics: request/Class routing, case lifecycle, capability composition, evidence schemas/provenance, fingerprints, confidence rules, result rendering, authorization records, resource governance, verification, rollback contracts, and audit history.

A game adapter owns native installation discovery, terminology, record/asset formats, collectors, tool invocation, and translation of native findings into shared evidence. MO2, xEdit/TES4, NIF, DDS, ESP/ESM/ESL, BSA, Papyrus, SKSE, FormID, or equivalent game-native logic stays in the owning game tree.

## 5. Classes are routing categories

Grid has 32 canonical Class recipes under `scripts/health/classes/*/class.v1.json`. A Class recipe is a routing/capability category, not a monolithic script.

Class recipes declare supported games, selection policy, evidence policy, pipeline roots, and coverage. They must not contain machine-specific paths, concrete plugin identities, or FormIDs. Unsupported coverage fails closed.

Game, Class, Mods, and Tools are structured selections. Plain text is preserved as a claim/request and must not create or alter those selections.

## 6. Script-first diagnostic workflow

1. Preserve the raw prompt verbatim.
2. Preserve reported symptoms, desired outcome, and constraints as claims.
3. Accept structured Game/Class/Mods/Tools only from structured inputs or deterministic authorized context.
4. Inspect existing capabilities/scripts before adding another.
5. Resolve a reusable Class/capability plan.
6. Gather only authorized deterministic evidence.
7. Record provenance and context fingerprints.
8. Generate/score hypotheses only through deterministic caller input or adapter rules.
9. Prefer the smallest reversible discriminating test.
10. Publish only evidence-backed four-field results.
11. Build an inert proposal before any mutation.
12. Require explicit authorization immediately before state change.
13. Verify postconditions and preserve rollback.

Do not ask the user to manually gather information that an existing authorized collector can collect.

## 7. Evidence and confidence

Every evidence item must carry a bounded value, human-readable claim, source type/identifier, collection time, current environment/profile fingerprint, verification status, and collector identity/version.

Evidence from a different profile/load order/generated-output state is stale until revalidated.

Confidence is evidence strength, not statistical probability. It cannot itself implicate a mod, assign a role, resolve a finding, or select a solution. Contradictions and missing direct evidence must cap confidence as defined by the shared engine.

## 8. Four-field public result

The default diagnostic result is exactly:

```text
Affected mod(s):
Mod role(s):
Finding:
Solution:
```

Each field resolves independently from current deterministic proof. Otherwise use `UNRESOLVED`. When the finding cannot resolve, the Solution identifies the smallest bounded next investigation step instead of guessing.

## 9. Safety, authority, and reversibility

Collectors are read-only. A diagnosis pipeline must never include `ExternalWrite` or `ExternalProcessControl` capability contracts.

State-changing actions require:

- exact bounded targets;
- an evidence-bound proposal;
- current one-use authorization as required by the contract;
- relevant process-safety checks;
- backup/rollback where applicable;
- atomic or staged writes where practical;
- deterministic postcondition verification;
- structured terminal output.

Executors must be invoked through their authorized lifecycle wrappers. For example, `Set-GridPluginState.ps1` is subordinate to `Invoke-GridAuthorizedPluginState.ps1`; callers must not bypass the wrapper to claim an authorized GRID repair.

## 10. Script quality

Use parameters instead of case-specific values. Emit actionable failures and structured output. Avoid secrets or machine-specific credentials. Preserve Unicode/spaces in paths. Support PowerShell 5.1 for Windows-facing scripts unless the repository explicitly changes that minimum. Add tests/fixtures for new parsing, scoring, authorization, or mutation logic.

## 11. Context efficiency

Lazy-load the requested game adapter, stream large inventories, retain compact evidence, reuse source hashes, bound caches, dispose processes/temp files, and do not send raw repetitive inventories to a model when deterministic reduction is available.

## 12. User interaction

When direct local execution is unavailable, create or extend reusable tooling when authorized, then give one bounded command that packages the necessary evidence. Ask only for information that cannot be collected deterministically.

## 13. Required handoff

Report exact changed scripts, canonical destinations, responsibility, read-only/state-changing classification, authorization path, backup/rollback behavior, structured output, verification actually performed, and limitations.

Never claim live Windows, MO2, xEdit, game, or Production verification unless it was actually run in that environment.

## Authorization security baseline

Public hashes, proposal/specification IDs, review digests, and deterministic identifiers are never executable approval credentials. Current read and mutation authority uses the durable random-secret grant lifecycle documented in `docs/security/authorization-and-receipt-binding.md`. Only protected secret proofs are persisted. Grant consumption must be exact-context, exclusive, one-use, and terminal. Historical v1 digest-based records are read-compatible only and must not be promoted into current authority.

Receipt reuse requires complete semantic revalidation. Workspace/request/submission, envelope, plan, authorization, capability/adapter versions, normalized input, output, and evidence bindings must remain exact; a compatible-looking transplanted receipt fails closed.
