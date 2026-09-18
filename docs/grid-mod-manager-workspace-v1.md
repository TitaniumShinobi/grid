# GRID Mod Manager Workspace v1

## Startup

- GRID starts on Home.
- On a clean/default shell, left panel, Chat, and Console are closed.
- The G routes to Home.
- Persisted user panel preferences are a later shell-state persistence pass; this v1 establishes the clean default only.

## Successful MO2 onboarding

After a new profile is successfully connected:

1. first-run onboarding is marked complete when possible;
2. the connected game/profile becomes the selected GRID context;
3. the GRID Mod Manager workstation opens immediately;
4. the left panel opens to GAME SNAPSHOT;
5. Chat remains closed;
6. Console remains closed.

A subsequent ordinary application launch still starts on Home.

## Context title

- Game selected without a profile: search/context placeholder is the game name.
- Exact profile selected: placeholder is the profile name.
- The workstation context bar shows the selected profile and deterministic active-mod count.
- Tool selector remains backed by observed MO2 executable definitions.
- Run remains unavailable until an explicit verified launch route exists.

## Game rail

The generic game-controller glyph is removed. Until the separate deterministic game-media manifest is implemented, connected games use a neutral first-letter circular fallback. Runtime web image search is forbidden.

## MO2 workspace

The existing data-bound `GameWorkspacePage` remains authoritative. It already projects the real selected profile rather than fabricating rows. GRID must continue to leave MO2 canonical.

The MO2 audit establishes the target surface:
- Mods on the left.
- Plugins / Archives / Data / Saves / Downloads on the right as their deterministic observers become available.
- Left-pane mod/file priority and right-pane plugin load order remain separate typed orders with explicit provenance between them.
- LOOT is the primary general plugin sorter; any Grid-proposed post-LOOT constraint requires deterministic evidence and an undoable preview.
- Filters are presentation-only and must never become evidence of absence.
- Profile selection in GRID is an observation context, not a silent MO2 profile switch.

See [MO2 dual-pane ordering](mo2-dual-pane-ordering.md) for the full ordering and interaction contract.
