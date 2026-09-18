# GRID Explorer v2

Explorer v2 removes the premature Tools filesystem root from v1.

Reason:
- `ObservedExecutableSummary` is an observation summary and does not expose executable paths.
- Explorer must not infer or fabricate tool directories from summary metadata.
- The Tools root will be added only when GRID binds Explorer to the authoritative executable configuration/catalog contract that owns binary and working-directory paths.

Current contextual roots remain:
- Manager
- Mods
- Downloads
- Overwrite
- Profile / Profiles
- Saves

All existing read-only, lazy-expansion, Collapse folders, Refresh, and selection-path behavior remains unchanged.
