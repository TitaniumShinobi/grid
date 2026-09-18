# Migration from case-specific diagnostics and superseded architecture

## Diagnostic migration

Replace case-specific branches with reusable Class/capability routing. The 32 Classes are categories over reusable capability graphs, not per-Class monoliths. Remove concrete machine paths, plugin identities, FormIDs, or user-modlist values from Production recipes/manifests.

Plain prose remains a claim. Technical selections come from explicit structured input or deterministic authorized observation.

## Authority migration

Separate collectors from mutators. Route state-changing behavior through proposal + explicit authorization + authorized wrapper + verification/rollback. Do not document a direct executor as the authorized application path.

Repository scripts stay separate from Production composition until intentionally wired under the same contracts.

## UI/documentation migration

Historical Home/game-rail/global History/global docked-assistant statements are superseded by ADR 0003 and `docs/ui-authority-boundaries.md`. Existing source/tests may remain temporarily as dormant prototype implementation, but future work must not use them to reassert obsolete Product architecture.
