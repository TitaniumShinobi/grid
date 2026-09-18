# Grid health diagnostics

The health subsystem is reusable operator tooling for evidence-backed diagnosis and narrowly authorized remediation. It is not part of the WinUI Production application's authority.

## Workflow

```text
User request -> InvestigationPlan -> Evidence -> Hypothesis -> Proposal
             -> Authorized action -> Verification result
```

Run from the repository root with an explicit game or an authoritative connected context:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\health\Invoke-GridHealth.ps1 `
  "Investigate the reported object-state conflict" `
  -Game SkyrimSpecialEdition
```

The dispatcher preserves the raw prompt, creates a case directory, resolves an installed adapter, and records an honest status. It does not mine arbitrary prose for plugin names, FormIDs, hypotheses, winners, or fixes. Supply those through explicit parameters or deterministic evidence collection.

Runtime operations are resolved from versioned semantic capability manifests rather than path-based recipes. New InvestigationPlans use schema v2 and persist the ordered `{ capabilityId, capabilityVersion }` dependency closure; schema v1 remains readable for historical replay. See the [capability contract](../../docs/diagnostics/capability-contracts.md).

## Structured Class requests

`Invoke-GridClassRequest.ps1` is the deterministic intake boundary for the future Grid form. It accepts one game, zero-to-many exact MO2 provider names, zero-to-many registered tool IDs, one explicit Class ID, and optional verbatim claim text. The claim is never parsed to invent a Class, mod, tool, plugin, FormID, hypothesis, or action.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\health\Invoke-GridClassRequest.ps1 `
  -Game skyrimspecialedition `
  -InstallationId <stable-installation-id> `
  -ProfileId <stable-profile-id> `
  -ClassId grid.class.installation-integrity `
  -ModNames 'Selected MO2 Provider One','Selected MO2 Provider Two' `
  -Request 'The exact user-stated symptom.' `
  -PassThru
```

Planning is the default and launches nothing. Add `-Execute` only to invoke the recipe's registered read-only diagnosis collector. This first slice registers Installation Integrity against the existing bounded Skyrim baseline capability. The other 31 Classes return `UnsupportedCoverage` until their deterministic collectors are implemented; they are never silently redirected to generic xEdit or a guessed tool.

`Invoke-GridRegisteredCoverage.ps1` reports the complete 32-Class coverage matrix. Its default is a preview. Its explicit `-Execute` form runs exactly one whole-profile CleanHouse baseline, reuses that case for the eight Classes backed by the same baseline capability, and explicitly defers the four registered collectors that need an exact provider, FormID, EditorID, or separately authorized tool input. It cannot enter proposal or mutation stages.

`grid.health.script-capability.admit` is the mandatory read-only boundary for proposed production scripting. It deterministically resolves reuse, composition, a reusable proposal, a private helper, runtime case data, or rejection. Incident identities, machine paths, plugins, FormIDs, and case-shaped filenames cannot become production implementations through this capability. Admission never writes files or grants implementation authority; an admitted proposal still requires a separate reviewed change.

`grid.health.capability-gap.resolve` is the pure routing boundary for an explicit capability the registry cannot currently satisfy. It compares the exact registry, caller-supplied composition, fresh evidenced compatible community candidates, missing structured inputs, and an optional CODE-admission result. Its routes are reuse, composition, acquisition review, candidate selection, structured input, runtime case data, rejection, or reusable creation proposal. Unsupported sealed Class diagnoses derive a reusable game/Class diagnosis-capability identity from structured fields and persist the route in `result/capability-gap-resolutions.v1.json`; they do not infer a target from claim prose. A creation route also seals `result/capability-proposal-drafts.v1.json`, an inert CODE design draft that names the canonical owner and the parameters, authority, terminal states, reusable examples, implementation files, and verification plan still required before admission. `AUTO` may advance inert planning only; resolution and drafting never query a provider, write code, acquire an artifact, register a capability, or authorize mutation.

`Grid.ToolEvidence.ps1` resolves repository-owned tool definitions and composes their existing capability roots in dependency order. A request may select more than one compatible tool; each receives an independent receipt and one failure does not erase another tool's evidence. Overall states are `EvidenceComplete`, `EvidencePartial`, `EvidenceUnavailable`, and `EvidenceFailed`. Selection never grants launch or mutation authority.

Skyrim registers MO2 context observation, SSEEdit's bounded reference collector, LOOT existing-output observation, and exact after-run report intake for TexGen x64, xLODGen x64, DynDOLOD x64, BodySlide, zEdit, Wrye Bash, Synthesis, and an SSEEdit full-profile report. The LOOT text-report collector classifies reported warnings with source lines and hashes; the other after-run collectors scan bounded text logs for messages and fingerprint selected artifacts. Unparsed formats remain explicitly `NotParsed`. None of these report collectors launches a tool, proves a report belongs to the selected current tool run, or grants repair authority. SSEEdit reference collection remains unsupported for Installation Integrity until a Class supplies exact bounded queries.

`Grid.FullHouseEvidenceCoverage.ps1` keeps report presence, parse coverage, context mismatch, and tool-run correlation separate. A caller-bound profile fingerprint is not a tool-run receipt. Its current result always requires further evidence/run correlation before FullHouse repair. ESL flagging and zEdit merging are independent decisions; neither after-run log collector establishes eligibility for the other.

`Invoke-GridRequestSubmission.ps1` is the fixed JSON-file boundary used by the application. `Prepare` resolves the persisted Skyrim/MO2 installation reference, verifies the selected stable profile, and returns an inert exact-read review. `Execute` requires that review's unchanged digest, writes a separate grant, checkpoints every bounded tool receipt, and seals the completed evidence case. `Resume` accepts only a task identity; it revalidates the saved request, plan, authorization, and receipt hashes before skipping completed collectors. `History` and `ReadTask` rebuild task views from validated sealed cases plus explicitly interrupted durable workspaces. A selected mod remains a user claim until evidence proves its role; evidence capture alone never asserts a diagnosis or repair.

For a selected-installation, read-only MO2 baseline, use `Invoke-GridBaseline.ps1`. It resolves exactly one persisted Skyrim/MO2 reference, verifies the active profile, and dispatches `grid.game.skyrimspecialedition.baseline.collect` through the adapter manifest. The configured store is `GRID_DATA_ROOT`, or the platform Grid application-data root when that variable is absent. Sealed cases live at `cases/v1/<caseId>/`; raw evidence is retained beside deterministic normalized inventories and a content-addressed `blobs/sha256/` store.

Baseline collection has its own run lifecycle (`Planned`, `Running`, `PausedAtCheckpoint`, `Completed`, `Failed`, `Cancelled`), seven independent evidence gates, and a separate sufficiency assessment. `Completed` means the bounded schedule ended; it does not mean the reported problem is diagnosed or playable. Protected before/after hashes support only `NoChangeObserved`, never a claim that another process could not change and restore bytes between observations.

For evidence-bound mod installation repair, the sealed baseline also retains bounded MO2 metadata provenance and checks only the exact `installationFile` archive and `.meta` sidecar for graph-relevant providers. It never recursively searches Downloads and never treats a filename as artifact identity. The repair workflow then separates immutable quarantine and inspection from a bound proposal and a separately authorized, recoverable journaled transaction. Original trees and source archives are preserved, replacements are staged outside the active mod tree, and every mutation receives a flushed receipt and verified rollback path. See [evidence-bound mod-chain repair](../../docs/diagnostics/mod-chain-repair.md).

Repair inspection derives its component closure from provider seeds and artifacts inside the sealed baseline. Missing Grid installation configuration is `InstallationContextUnresolved`/`ConfigurationRequired`; protected drift is `BaselineStale`; `ExternalDependencyRequired` is reserved for a proven-unavailable exact artifact. A no-change inspection may seal a zero-operation `InputsVerified` specification without issuing mutation authority.

Evidence-bound acquisition uses resumable Grid-managed inbox state and immutable SHA-256 quarantine objects. Authentication and pending user import are resumable states, not proof that an artifact is unavailable. Asset-only diagnoses explicitly make record patching `NotApplicable`. Runtime certification is bound to one exact sealed baseline/diagnosis/repair lineage. Pending acquisition stays `PreconditionsNotMet` with runtime `NotStarted` and readiness `NotEvaluated`; Skyrim is not launched. Once eligible, seven independently fingerprinted prelaunch gates, a verified rollback receipt, isolated profile, exact owned-process evidence, bounded screenshot/log audits, and an oracle covering every required claim are required. Incomplete runtime proof returns `NeedsRuntimeVerification` and leaves `ReadyToPlay` false.

## Public output contract

The default result is deliberately non-conversational:

```text
Affected mod(s):
Mod role(s):
Finding:
Solution:
```

Only current evidence may resolve a field. Unproved fields are `UNRESOLVED`; the solution then identifies the smallest bounded next collector or user observation. Confidence rankings remain internal and cannot publish a finding or remediation by themselves. Detailed evidence and telemetry stay in the case package unless explicitly requested.

The semantic payload is canonical for the same normalized context, evidence, resolver version, and policy version. Case IDs, timestamps, local paths, evidence GUIDs, and collection order are audit metadata rather than semantic inputs.

No conversational model or assistant participates in this path. It cannot supply technical targets, evidence, findings, solutions, authorization, or verification.

The generated evidence package is stored beneath the selected output root and may contain:

- `case.json` and the runtime InvestigationPlan;
- context and source fingerprints;
- bounded collector queries and case-local plugin selections;
- process, loader, and collector telemetry;
- normalized evidence with provenance;
- diagnosis inputs and reports;
- inert remediation proposals;
- authorization, verification, and rollback results when separately requested.

Shared infrastructure belongs here. Game-specific behavior belongs under `scripts/games/{game}/`; Skyrim-specific MO2, TES4, xEdit, plugin, and FormID behavior belongs under `scripts/games/skyrimspecialedition/`.

## Safety

- Collectors are read-only and never install themselves into external tools.
- xEdit collection requires verified SSEEdit64, MO2/USVFS, and a bounded case-local `-P` list; full-profile autoload is refused.
- Exact unwanted Skyrim `REFR`/`ACHR` placements can be reduced into an inert reference-suppression proposal only when a sealed record graph proves the current winner, source hash, active state, and absence of enable-parent/linked-reference hazards. The proposal requests only `Initially Disabled` overrides in a dedicated patch; it never edits the source plugin. The manifest-bound writer has its own reviewed one-use provisioning gate, and patch Apply plus exact-image Undo/Redo each require a fresh one-use authorization. MO2/Skyrim/xEdit must be closed; activation, post-write recollection, and runtime verification remain separate gates.
- The generic xEdit collector is inspected and provisioned separately. Missing or received-outdated versions require a typed one-use proposal; externally modified copies fail closed. The collector launcher never installs tooling implicitly.
- Unknown queries, missing masters, inactive targets, ambiguous contexts, and stale evidence fail closed.
- Mutation requires a current typed proposal and explicit authorization. It is never a side effect of diagnosis.
- Grid never terminates MO2, Skyrim, SKSE, the launcher, or xEdit automatically. A bounded xEdit request returns `NeedsProcessClosure` with the exact blocking process names and PIDs before any launch when one of those processes is already running.

See [`docs/diagnostics/`](../../docs/diagnostics/README.md) and the [diagnostic result contract](../../docs/diagnostics/diagnostic-result-contract.md) for the contracts and verification requirements.
