# Runtime InvestigationPlan

An InvestigationPlan is a deterministic, versioned representation of what evidence a structured request is allowed to collect and how capability dependencies are composed.

## Inputs

The plan binds explicit Game, Class, Mods, Tools, installation/profile context, preserved plain text, capability versions, and required evidence. Structured selections are not mined from prose.

## Class relationship

A Class recipe is a routing/capability category. It supplies policy and semantic capability roots; it is not a complete hardcoded diagnostic script. The planner refuses stale recipe versions, unsupported games/tools/Classes, missing context, and missing evidence.

## Authority

A diagnosis plan is read-only. Capability closure containing mutation authority is rejected. Exact read authorization and sealed request/evidence contracts govern bounded external reads where required.

Any later mutation is a separate proposal/authorization/execution lifecycle and is not granted by the InvestigationPlan.

## Replay and provenance

Historical plan schemas may remain readable for replay/audit. Active execution must bind current semantic capability versions and current context/evidence fingerprints. Run-local IDs/timestamps do not change semantic result identity.
