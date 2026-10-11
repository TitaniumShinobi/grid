# Operational instruction registration (LIF-10 / GR-20)

Status: candidate-only implementation. Publication state is always `NOT_PUBLISHED`.

The frozen GRID contract includes original instructions wherever sourced and a Registered Knowledge Graph powered by Discord, Fandom, Nexus, Reddit, and Steam. This slice demonstrates one reusable admission path on two unrelated fixtures. A production instruction document already tracked in the repository is not an acceptance prerequisite.

## Boundary

```text
provider/package evidence adapter
  -> admitted frozen source bytes, digest, revision and format coordinate
  -> adapter-owned source-format extraction at explicitly identified locators
  -> typed canonical instruction assertion and exact evidence
  -> Contract 2 CanonicalRegistrationCandidate (NOT_PUBLISHED)
```

`IRegistrationEvidenceAdapter.ExtractAsync` returns optional instruction claims in `RegistrationEvidenceSet`. Core does not choose a parser from a locator prefix, infer headings/categories/names, interpret guidance, or resolve compatibility. Provider fields, HTML coordinates, XML paths, API paths, archive members, PDF regions and text spans are opaque exact locators to Core. Each adapter supports only its declared formats and must fail closed for others. The two test adapters cover escaped provider-response fields and an unrelated installer/package text format with byte-span locators. They do not claim to implement live connectors for every provider.

A registration batch admits at most 256 instruction observations, each with at most 1,048,576 UTF-16 code units of exact text. Adapters additionally own bounded parsing of their admitted inputs. No network discovery, execution, publication, live-store import, Workstation Refresh or DIF change is added by this path.

## Canonical contracts reused

- `InstructionAssertion` / `InstructionAssertionId`: canonical identity, native subject, category mapping method/version, source scope and revision.
- `InstructionCategories`, `InstructionContent`, `InstructionSourceLocator`: existing categories, exact strict-UTF-8 content digest, opaque source-native locator.
- `CatalogSourceRecord`, `CatalogSourceRevisionRecord`, `SourceArtifactId`, `ContentDigest`, `KnowledgeFormatCoordinate`: immutable source closure.
- `CatalogFileEvidenceReceipt` (v2 receipt identity) and `CatalogReferenceEvidenceReceipt`: distinct evidence classes.
- `InstructionEvidenceBinding` v3 and `EvidenceClaimContentId`: exact assertion/evidence binding.
- `InstructionConflictGroup`: explicit adapter-supplied conflicts. Core validates member closure and identity and retains every member; it neither infers contradiction from unequal prose nor chooses a winner.

The registration envelopes add observation state and evidence linkage; they do not redefine canonical instruction identity. Existing canonical identity algorithms and golden vectors are unchanged.

The canonical assertion's source scope requires a separate metadata receipt from the same admitted source revision and native object, at a different locator from the instruction section. This is source context, not proof that following the guidance is safe, compatible, or applicable to a user's profile. Requested GameId/ProfileId applicability and supplied evidence references are retained as unresolved. The candidate exposes no resolved target applicability in this slice; section receipts are never promoted into applicability evidence. Canonical record/package applicability requires its existing independent linkage proof and is not fabricated here.

## Verification semantics

`CanonicalRegistrationInstructionValidation.Admit` recomputes content, assertion, receipt, binding, observation and conflict identities and validates source/revision/artifact/locator/evidence-class closure. Missing, explicit source absence, empty/unresolved content and present content remain separate. Empty/unresolved whitespace is retained exactly. CRLF, LF, blank lines, Unicode normalization forms and decoded provider escape sequences are not trimmed or rewritten.

`CanonicalRegistrationCandidateVerifier.Verify` re-evaluates the typed input and compares the complete candidate bytes. As with the existing registration contract, structural validation is not authentication of a freely edited input manifest.

`VerifyAgainstSourcesAsync` additionally reruns the supplied adapter against externally admitted frozen sources and compares the complete candidate. Use this for source-backed re-verification, including coordinated edits that recompute serialized claim hashes. It rejects a stale revision or fabricated content even when the edited candidate is internally self-consistent. The caller must supply the trusted adapter and admission manifest; hashes are not authorization credentials.

Null/empty instruction and conflict collections normalize to null and are omitted from JSON. Two independent golden vectors generated with unmodified `origin/main@3ff26d5` prove byte compatibility for empty and nonempty pre-PR source inputs.

## Exact implementation disposition

| File | Symbols / disposition |
| --- | --- |
| `src/Grid.Core/Models/CanonicalOperationalInstructionInspectionModels.cs` | Retains `OperationalInstructionSectionPresence`; rewrites `RegisteredOperationalInstruction` as a canonical-claim envelope and `OperationalInstructionInspection` as `Create`/`ClaimId`; adds source/receipt/scope envelopes. Removes `OperationalInstructionKind`, `OperationalInstructionSectionRequest`, and the separate `CanonicalOperationalInstructionCandidate`. |
| `src/Grid.Core/Models/CanonicalRegistrationModels.cs` | Retains optional `RegistrationEvidenceSet.Instructions` and `CanonicalRegistrationCandidate.Instructions`; adds optional canonical source closure and conflict groups. `RegistrationInstructionClaim` moves into the instruction envelope file and uses existing canonical contracts. |
| `src/Grid.Core/Services/CanonicalOperationalInstructionInspector.cs` | Removed, including Markdown/JSON grammar, newline rewriting, trimming and caller-driven absence options. |
| `src/Grid.Core/Services/CanonicalRegistrationInstructionValidation.cs` | New `Normalize`, `Admit`, and exact `ValidateReceipt` implementation, without format parsing or game branches. |
| `src/Grid.Core/Services/CanonicalRegistrationEngine.cs` | Retains instruction normalization and candidate integration in `Normalize`/`Evaluate`; adds value-based `ValidateDiscoveredSources`; removes section-request overload and `AdmitInstructionClaims` parsing bridge. |
| `src/Grid.Core/Services/CanonicalRelationshipRegistrationRealizations.cs` | `DiscoverEvidenceAsync` validates instruction source discovery against the admitted manifest, including value-equivalent reconstructed canonical metadata. |
| `src/Grid.Core/Services/CanonicalRegistrationCandidateVerifier.cs` | Retains deterministic `Verify`; adds `VerifyAgainstSourcesAsync`. |
| `src/Grid.Core/Services/CanonicalRelationshipRegistrationMdboComposer.cs` and `CanonicalRelationshipRegistrationPipeline.cs` | Retain ordinary adapter evidence discovery and candidate composition. Remove PR-only section-request overloads; final files match main because typed evidence already travels through these paths. |
| `tests/Grid.Core.Tests/CanonicalOperationalInstructionInspectionChecks.cs` | Replaces tests with two unrelated adapter fixtures and 78 acceptance checks. Removes `RunRealAdmittedSourceBoundary` and repository searches predicated on missing production documents. |
| `tests/Grid.Core.Tests/Program.cs` | Retains focused/default instruction checks; adds `--registration-instruction-regression` to run each complete relevant suite independently and return failure if any fails. Existing default assertions remain unchanged. |

## Validation observed on Windows, 2026-10-11

Commands run from the isolated PR worktree:

```powershell
dotnet build src/Grid.Core/Grid.Core.csproj -c Debug --nologo
dotnet build tests/Grid.Core.Tests/Grid.Core.Tests.csproj -c Debug --no-restore --nologo
dotnet run --project tests/Grid.Core.Tests/Grid.Core.Tests.csproj -c Debug --no-build --no-restore -- --operational-instruction-inspection
dotnet run --project tests/Grid.Core.Tests/Grid.Core.Tests.csproj -c Debug --no-build --no-restore -- --registration-instruction-regression
dotnet run --project tests/Grid.Core.Tests/Grid.Core.Tests.csproj -c Debug --no-build --no-restore -- --registration-engine-compatibility
dotnet run --project tests/Grid.Core.Tests/Grid.Core.Tests.csproj -c Debug --no-build --no-restore
dotnet run --project tests/Grid.Core.Tests/Grid.Core.Tests.csproj -c Debug --no-build --no-restore -- --registration-engine-proof . .tmp/grid-contract2-registration-20261003/proof/lif-10
```

| Check | Result |
| --- | --- |
| Core / test project builds | PASS; final build 0 warnings, 0 errors |
| GR-20 focused acceptance | PASS, 78 checks |
| Canonical identity/evidence kernel | PASS, 537 checks |
| Canonical selector/instruction/resolver | PASS, 89 checks |
| Registration refresh unit checks | PASS, 3 checks |
| Prepared navigation | PASS, 122 checks |
| Legacy registration compatibility | PASS, 3 checks |
| Universal knowledge contracts | PASS, 81 checks |
| Complete relationship MDBO suite / complete Core executable | FAIL at inherited development-build provenance construction |
| Complete catalog-import / compatibility entry | FAIL at inherited schema-v5 persistence expectation |
| Complete cross-source suite | FAIL at inherited schema-v5 persistence expectation |
| Registration-engine proof | FAIL at inherited GRID.md node-spelling oracle |

Each listed failure was independently reproduced on a detached, unmodified `origin/main@3ff26d5` worktree. The independent regression selector completes its suite list and returns exit 1 for the three failing suites rather than masking them. The default Core executable and original compatibility entry still stop at their existing failures; later tests in those entries cannot be reported as passed.

Failure references:
- `GtaVLocationHierarchyRegistrationRefreshSupport.BuildRegistrationRefreshProvenance` calls `CatalogBuildProvenance.CreateDevelopment` with no dirty/untracked build input.
- `CanonicalCatalogPackageImportChecks.RunAsync` line 85 expects the additive schema-v5 document boundary.
- `CrossSourceCanonicalAssertionChecks.RunAsync` line 444 expects schema v5.
- `CanonicalRegistrationEngineChecks.BuildOracle` expects the exact `Location/world/continent/country` spelling in GRID.md.

No frozen 98-node mold change, live-source acquisition, live-store import, publication, or game/runtime verification was performed. Test stores/proof outputs remain isolated. No script was changed. Git worktree isolation preserves the unrelated main-worktree changes, and the PR is not merged.

LIF-10's frozen two-fixture candidate-only acceptance contract is satisfied by the focused evidence above. A clean full-repository validation remains blocked by the baseline failures; this is not a missing-production-source blocker and does not authorize merging or publication.
