# Grid documentation

This tree is the authoritative architecture baseline for current GRID work, subject to repository artifacts and root `AGENTS.md`.

- [Architecture](architecture.md) defines the application, workspace, adapter, and authority boundaries.
- [MO2 dual-pane ordering](mo2-dual-pane-ordering.md) keeps left-pane mod/file priority independent from right-pane plugin load order and defines LOOT's role.
- [UI and authority boundaries](ui-authority-boundaries.md) locks shell, sidebar, workspace, assistant, composer, and form behavior.
- [Capability status](capability-status.md) distinguishes Implemented, Partial, Dormant prototype, Planned, and Prohibited behavior.
- [Diagnostic scripting instructions](CODEX_DIAGNOSTIC_SCRIPTING.md) are normative for diagnostic, health, repair, inspection, and recovery work.
- [Diagnostics](diagnostics/README.md) defines deterministic Class routing, evidence, four-field results, proposals, authorization, verification, and rollback.
- [Architecture decisions](decisions/0001-runtime-investigation-plans.md) record stable decisions, including deterministic results/no-AI authority and the locked shell/capability boundaries.
- [Test lab](test-lab.md) covers isolated external-integration fixtures.

Repository scripts are not equivalent to Production application authority. A deterministic executor may exist and be tested while remaining outside Production composition. Historical roadmap or prototype UI statements are subordinate to the current baseline and must not be used to restore obsolete navigation.
- [Verification](diagnostics/verification.md) defines the repository-owned verification manifest, lanes, runner, receipts, and platform claims.
