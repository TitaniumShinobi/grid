# ADR 0003: Production shell, game workspace, sidebar, and assistant are locked

Status: Accepted

## Context

Earlier documentation and prototype implementation described Home as the permanent shell surface, game navigation through a rail, global History/Settings destinations, and a globally docked assistant. Those concepts conflict with the locked product design and risk being restored by later work.

## Decision

Production architecture is locked as follows:

- no Home/Games/Workstation main-panel tab strip;
- circular game thumbnail selection renders the selected game's mod/plugin workspace in the main panel;
- the game workspace is the mod/plugin workspace;
- an open left sidebar changes to the selected user's game snapshot when game selection changes;
- Library, Search, and Activity are side-panel surfaces;
- Chat History belongs to the assistant;
- the assistant is one chat with one composer and one optional structured data-intake form;
- `GAME/GRID` appears only while the form is visible;
- Game, Mods, Tools, and Class values are structured selections and are not mined from prose.

Existing source/tests/assets that encode the old Home/game-rail/global-History/global-docked-assistant design are classified as **Dormant prototype** where they conflict with this ADR. They are implementation history, not authority to restore that design.

## Consequences

Future UI implementation must migrate toward this model. Documentation must not describe obsolete prototype navigation as current Production behavior. Architecture conformance tests may reject reintroduction of superseded claims in authoritative baseline documents.
