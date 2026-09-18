# Grid Repository Instructions

These instructions apply to the entire Grid repository unless a more-specific `AGENTS.md` exists deeper in the directory tree.

## Preserve existing work

- Inspect the repository, relevant documentation, and `git status` before changing files.
- Preserve unrelated user changes and do not assume the worktree is clean.
- Use existing project structure and naming before creating new directories or abstractions.
- Do not perform destructive Git or filesystem operations without explicit authorization.

## Diagnostic scripting

Read and follow `docs/CODEX_DIAGNOSTIC_SCRIPTING.md` for all diagnostic, health, inspection, repair, and recovery work. Its directory ownership, evidence, confidence, reversibility, script-first, context-ingestion, and user-interaction requirements are mandatory.

## Directory ownership

- Shared game-independent diagnosis infrastructure belongs in `scripts/health/`.
- Game-specific tools remain inside their existing canonical game roots.
- Game-specific tools belong in `scripts/games/{game}/`.
- Skyrim Special Edition tools belong in `scripts/games/skyrimspecialedition/`.
- Do not duplicate a game hierarchy beneath `scripts/health/` or another subsystem.

## Verification

- Verify every changed script using the safest available parser, tests, fixtures, or dry-run path.
- State what was verified and identify any validation that could not be performed.
- Do not report a diagnosis as confirmed unless the recorded evidence satisfies the confidence rules.
