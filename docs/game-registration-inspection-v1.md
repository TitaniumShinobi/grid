# Game Registration Inspection Checklist v1

Status: **Proposed** review contract. Not live publication.

Machine-readable source: [`src/Grid.Core/Contracts/game-registration-inspection.v1.json`](../src/Grid.Core/Contracts/game-registration-inspection.v1.json)

This is the metadata-first **inspection** checklist for Contract 2 (LIF-8). It says what GRID must inspect and understand before a canonical projection is trustworthy. It is **not** the frozen 98-node DIF selector mold.

| Artifact | Role |
|---|---|
| `GRID.md` “Registered Knowledge Graph [Canonical Reference Library]” | Product authority |
| `src/Grid.Core/Contracts/game-registration-inspection.v1.json` | Inspection checklist (this contract) |
| `src/Grid.Core/Contracts/canonical-registration-checklist.v1.json` | DIF projection mold (exactly 98 nodes; Tool, Mod, Location, MissionQuest, Item, Actor) |
| `CanonicalRegistrationEngine` / `CanonicalRegistrationCandidateVerifier` | Implemented candidate engine; `publicationState` remains `NOT_PUBLISHED` |

`CanonicalRegistrationChecklist.Load()` embeds only the 98-node mold. This inspection JSON is **not** an `EmbeddedResource` and is **not** evaluated by the registration engine.

## Flow

```text
inspect + sealed evidence
  → canonical candidate + verified relationship graph
  → later DIF projection onto the 98-node mold
```

Candidate verification is not live publication, KnowledgeRebuild catalog import, Workstation Refresh publish, or populated live DIF.

## Coverage vocabulary

- **Implemented** — present in repository behavior and supported by current contracts/tests.
- **Partial** — some supporting implementation exists; the complete inspection capability is not proven.
- **Missing** — not an inspection/registration authority yet. The category stays visible.

DIF node presence is not inspection completeness.

## Evidence and provenance

GRID.md ruling tags: `FILE VERIFIED`, `REFERENCE VERIFIED`, `CORRELATED`, `UNRESOLVED`.

Contract 2 `EvidenceVerificationKind` is `FileVerified` or `ReferenceVerified`. Correlation is a `RegistrationOutcome`, not a file-verification upgrade. Digests are SHA-256. Unresolved stays explicit.

## Inspection categories

The JSON enumerates GR-01 through GR-21 (game identity through tools). Packages/DLC/updates, assets, overrides, and operational instructions remain first-class even where coverage is Partial or Missing.

## Proposed versus implemented

| Layer | Status in v1 |
|---|---|
| Physical / provider inspection | Partial, game-specific |
| Connection / catalog (Add Game) | Implemented; not this checklist |
| Contract 2 candidate engine | Implemented; candidate-only, `NOT_PUBLISHED` |
| 98-node DIF mold | Implemented projection scaffold |
| GTA knowledge adapters | Partial on some Location/Item/Actor/Mission evidence |
| Live publish / Refresh / DIF fill | Out of scope |

## Reversible assumptions

Path, schema placement, and follow-up breakdown are recorded in the JSON `assumptions` array. They can change without altering the 98-node mold or live publication.

## Product-intent escalations only

v1 recommendation is **No** on all three:

1. Candidate-only proof sets `publicationState` other than `NOT_PUBLISHED`.
2. Inspection skips File/Reference evidence or treats Unresolved as selectable truth.
3. A candidate graph triggers KnowledgeRebuild import, Workstation Refresh publish, or populated live DIF.
