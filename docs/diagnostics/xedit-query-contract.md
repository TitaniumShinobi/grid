# Bounded xEdit query contract

The Skyrim adapter may issue only these read-only query operations:

- `AuditEslEligibility`
- `FindRecordByFormId`
- `FindRecordByEditorId`
- `TraceOverrides`
- `FindReferencesToBase`
- `InspectReferenceState`
- `InspectContainingCell`
- `ResolveWinningOverride`
- `InspectReferenceLinks`
- `InspectVmad`
- `TraceOverrideChain`
- `InspectScriptedReference`

The extended operations report the winning placed record's full record flags,
`NAME`, `XTEL`, `XESP`, containing cell, VMAD scripts/properties, and ordered
override providers. Form-valued VMAD properties remain record identities and
can be submitted as another bounded query; they are never treated as paths or
runtime truth.

Unknown operations, arbitrary Pascal, and mutation requests are rejected before xEdit starts. Each query is bound to the case and profile context and is subject to query, plugin, traversal, output-row, and output-byte limits.

The adapter computes a minimal plugin selection from explicit case targets plus the recursive masters read from actual TES4 headers. Required masters preserve active-profile load order. Missing masters, inactive targets, ambiguous targets, or an unreliable custom plugin-list handoff fail closed.

Every launch must use the verified `SSEEdit64.exe` configured through MO2/USVFS and the case-local `-P` plugin list. Full-profile autoload is forbidden. Query input, plugin selection, status telemetry, launch evidence, native output, and normalized evidence remain in the case directory.

`Trace-GridReference.pas` is a generic read-only collector. It must not edit records, save plugins, change load order, or install itself into an external tool directory.

Verified `FindReferencesToBase` evidence is reduced by `Resolve-GridSkyrimPlacedActorReference.ps1` when the caller needs an NPC's placed reference. The reducer accepts only `ACHR` rows, resolves only a sole candidate, fails closed on ambiguity, and emits `prid` diagnostics using the placed reference FormID. It never converts an `NPC_` base FormID into `player.placeatme` guidance.

Exact winning virtual `.psc` and `.pex` paths may be inspected from a loose
provider or one exact BSA member. PSC inspection reports declarations,
properties, routines, conditions, and calls. PEX inspection validates and
parses its bounded object, property, state, function, and instruction data. It
records routine-scoped calls and direct reference `Enable`/`Disable` actions,
but never treats a static branch as proof that it executed. Plugin `Initially
Disabled` is a default and is not evidence of the current save's runtime enabled
state.

`AuditEslEligibility` accepts one exact plugin filename and no FormID or
EditorID. It uses xEdit's loaded record model and the same technical limits as
xEdit's bundled "Find ESP plugins which could be turned into ESL" script. The
result distinguishes an existing light plugin, a header-flag-only candidate,
a plugin that would first require FormID compaction, and a plugin with too many
new records. It also records the ESM-plus-new-CELL engine-risk warning. This is
read-only evidence: it never adds the ESL flag and never compacts FormIDs.
