# GRID platform architecture

Status: Normative product direction with explicit implementation status

GRID is a bring-your-own-provider (BYOP) game-management platform. `AUTO` is available by default as an operating mode; it is not a bundled model account, a grant of mutation authority, or permission to guess. Users may connect supported model, community, artifact, and publishing providers. The deterministic GRID engine remains authoritative regardless of which providers are connected.

## Product invariant

GRID must be useful before a game is launched. It builds a versioned static world model from the selected installation, mod-manager state, archives, plugins, records, assets, scripts, generated outputs, tool outputs, and prior sealed evidence. Launching a game is not a prerequisite for inventory, conflict analysis, compatibility assessment, source recovery, proposal construction, or preflight validation.

Some claims describe runtime-only behavior. GRID may require a separately authorized isolated runtime verification when static evidence cannot prove the outcome. That limitation must remain visible in the problem ledger; it must never be disguised as a static diagnosis or a successful repair.

## Control planes

### Deterministic plane

The deterministic plane owns identities, context fingerprints, adapters, capability contracts, collection, normalized evidence, findings, proposals, authorization, execution, rollback, verification, packaging, and publication receipts. Only this plane can advance a problem to `Diagnosed`, `Applied`, `Resolved`, or `AccountedFor`.

### Provider plane

BYOP connections can supply model assistance, current community observations, authenticated artifact access, build services, and publication destinations. Provider output is untrusted input until a registered adapter normalizes it and records source, time, identity, terms, freshness, and verification status. A model may explain deterministic results or suggest candidate work; it cannot manufacture evidence or cross an authority boundary.

### AUTO plane

`AUTO` schedules safe work that is already admitted by deterministic policy: refresh stale observations, inspect existing offline outputs, reconcile source identities, update problem blockers, and prepare inert proposals. AUTO pauses at missing credentials, ambiguous identity, unsupported capability, external process safety, mutation authorization, or inadequate verification. AUTO being enabled never means automatic approval.

## Durable problem ledger

Every reported symptom, desired outcome, and previously verified repair is represented by a stable ledger entry. The state machine is:

```text
Claimed -> IntakeBound -> NeedsEvidence -> Diagnosed -> SolutionProposed
        -> Authorized -> Applied -> VerificationRequired -> Resolved
```

`NeedsContext` and `AccountedFor` are explicit terminal or waiting branches. `AccountedFor` means a bounded blocker or deliberate disposition is recorded; it does not mean fixed. A changed game/profile/load-order/generated-output context reopens dependent entries to `NeedsEvidence`. Ledger history and semantic content are fingerprinted, and a valid ledger is stored as `diagnosis/problem-ledger.v1.json` in the sealed case lineage.

This contract prevents a completed scan, successful build, or repaired subcomponent from being reported as completion of the larger game objective.

## Offline world model and alert index

The world model is adapter-neutral at its shared boundary and game-native within each adapter. It must represent:

- installations, profiles, mod and plugin order, enabled state, and foreign ownership;
- virtual paths and complete provider chains, including loose/archive/generated winners;
- plugin records, masters, overrides, references, scripts, meshes, textures, and other native dependencies;
- executable/tool versions, prior outputs, warnings, errors, timestamps, and source fingerprints;
- artifact lineage, installer selections, community compatibility observations, and their freshness;
- unresolved ambiguity and partial coverage without fabricated winners.

Tool adapters ingest existing MO2, LOOT, xEdit, SKSE, BodySlide, behavior-generation, LOD-generation, archive, and other registered outputs into one normalized offline alert index. Hover cards read the local index, so explanation and provenance are instant and available offline. Network refresh augments the index but is not required to display already collected alerts.

## Current community and compatibility intelligence

Community intelligence is an observation stream, not executable truth. Each observation binds a provider/source identity, URL or opaque provider key, observed version, publication/update time when available, retrieval time, compatibility assertions, dependencies, and expiry policy. Conflicts or stale observations resolve to `Unresolved` until a deterministic compatibility rule or explicit review reconciles them.

AUTO may refresh connected sources on policy-defined schedules. Offline operation uses the last sealed observation and displays its age. Credentials are opaque handles outside repository and case artifacts.

## Capability-gap resolution

When no registered capability can satisfy a ledger entry, GRID records a capability gap instead of guessing. The gap resolver may deterministically choose one of these reviewable paths:

1. compose existing registered capabilities;
2. acquire a verified compatible tool or artifact through a connected provider;
3. generate a reusable adapter, collector, patch, or mod proposal in CODE;
4. request the smallest missing structured input or runtime observation;
5. account for the issue with a bounded blocker.

`CapabilityRequired` is not a terminal dead end. Sealed diagnosis successors derive a reusable missing diagnosis-capability identity only from the selected game, Class, and pipeline stage; preserve the explicit structured desired outcome; resolve the gap in `AUTO` mode; and seal every route in `result/capability-gap-resolutions.v1.json`. Creation routes additionally seal `result/capability-proposal-drafts.v1.json`, which carries the exact gap into CODE and explicitly lists the missing design evidence required before script admission. The application displays the route with the request result. A route or draft can authorize inert planning only: neither authorizes provider access, code implementation, registration, external execution, or profile mutation.

Generated code has no authority merely because it compiles. It must pass admission, parser/build tests, fixtures, capability conformance, side-effect classification, review, authorization where needed, and verification before registration.

## Previously unknown moddable games

Unknown-game support starts with a declarative game manifest rather than hardcoded UI behavior. A candidate manifest identifies installation fingerprints, executable identities, content roots, mod/plugin concepts, ordering rules, archive and record formats, tool integrations, writable boundaries, and validation probes. Sandboxed read-only probes produce a capability report. Unsupported native formats remain explicit gaps.

A generated or authored game adapter becomes available only after manifest/schema validation, deterministic fixture replay, bounded-resource checks, authority classification, and conformance tests. Registration makes the adapter discoverable; it does not grant mutation or publication authority.

## Repair, creation, packaging, and publication

GRID uses the same evidence lineage for repair and creation:

```text
source/input identities
  -> deterministic build graph
  -> immutable outputs
  -> compatibility and policy validation
  -> package manifest and reproducible digest
  -> explicit publication authorization
  -> provider publication receipt
```

Repairs and created mods preserve exact sources, toolchain versions, installer decisions, file/record ownership, rollback material, and postconditions. ESL flagging, plugin compaction, merges, record patches, asset replacement, and generated outputs are separate capabilities with native preconditions; none is a generic “fix” switch. Publication is refused when licensing/redistribution metadata, required sources, validation, destination identity, or authorization is unresolved.

### Skyrim FullHouse tool-cycle contract

A FullHouse run is a profile-bound dependency graph, not a single CleanHouse scan. LOOT sorting, SSEEdit whole-profile loading, zEdit merging, Wrye Bash patch building, Synthesis generation, BodySlide batch building, and the xLODGen/TexGen/DynDOLOD generation sequence each need an exact before/after inventory, tool version, input fingerprint, run receipt, output fingerprint, and native warning/error interpretation. Each completed stage changes the inputs to downstream stages; previous findings are stale when plugin order, asset winners, or generated outputs change. GRID must reconcile all final outputs with the selected MO2 profile and repeat the relevant probes before asserting a repair. Missing or unparsed reports, unmatched run receipts, and unresolved alerts stay explicit gaps, not healthy results.

ESL suitability and merge suitability form two independent verified assessments. An ESL-eligible plugin may be merge-ineligible, and a merge-eligible plugin may be ESL-ineligible. Neither status can be inferred from the other's audit or from an unclassified tool log. FormID compaction, existing-save references, masters/dependents, scripts, generated outputs, and tool-specific merge constraints require separate checks before either action; a proposed action needs its own scoped authorization, rollback, and postcondition proof.

### Exact placed-reference suppression

An unwanted placed-reference repair starts with a sealed record graph. GRID may propose setting `Initially Disabled` only for exact active `REFR`/`ACHR` winners whose source bytes are fingerprinted and which have no observed enable parent or linked-reference hazard. The proposal targets a separate compatibility plugin and cannot edit the source mod, delete records, change transforms/navmesh/assets, enable the patch, or reorder plugins. The manifest-bound xEdit writer is provisioned under its own exact one-use authorization; patch execution requires a second exact one-use authorization and refuses to run while MO2, Skyrim, SKSE, the launcher, or xEdit is open. Apply preserves a byte-exact after-image for independently authorized Undo/Redo. Post-write graph recollection, activation/load-order integration, and an isolated runtime visual check remain distinct gates.

## Native performance boundary

GRID's application and current domain services are C#/.NET. C++ is appropriate behind a stable native boundary only when measurement shows a bounded parser, archive, hashing, graph, image, or binary-transformation workload cannot meet its resource target in managed code. Language choice does not weaken evidence, authorization, sandbox, rollback, or reproducibility contracts.

## Delivery order

1. Durable problem ledger and case integration.
2. Complete offline alert normalization and workstation projection.
3. Source/community observation refresh plus authenticated acquisition.
4. Capability-gap resolver and CODE admission/build loop.
5. Unknown-game manifest, probe, and adapter conformance kit.
6. Reproducible package and provider publication pipeline.
7. Evidence-backed closure of every selected-profile problem-ledger entry.

Implementation status is tracked in [capability-status.md](capability-status.md). The selected user's problem values remain runtime data and must not be copied into Production defaults.
