# Locked UI and authority boundaries

Status: Locked product design

## Shell and main panel

The main panel has no Home/Games/Workstation tab strip. Game selection uses circular game thumbnails. Selecting a game renders that game’s mod/plugin workspace in the main panel.

A game workspace is the mod/plugin workspace. Do not introduce a second “Workstation” layer around it.

## Left sidebar

The left sidebar is contextual. When it is open and the selected game changes, the sidebar changes to that user's selected-game snapshot.

Library, Search, and Activity are side-panel surfaces. They must not be promoted into a permanent main-panel tab strip.

## Assistant

There is one assistant chat. Chat History belongs to the assistant.

There is one composer. The assistant may additionally show one optional structured data-intake form. `GAME/GRID` appears only while that form is visible.

The form carries explicit structured selections for Game, Mods, Tools, and Class. Prose in the composer is preserved as the user's claim/request and is not mined to populate or change those structured selections.

## Authority

The assistant is a presentation/intake surface, not a diagnostic authority.

AI/model output must never:

- select or invent Game/Mods/Tools/Class technical targets;
- create evidence or mark a claim verified;
- choose the deterministic finding or solution;
- authorize a read/write/process action;
- invoke a repair executor;
- mark verification successful.

Deterministic repository tooling may perform those roles only through its own contracts and authorization gates. A future UI may request/render those deterministic operations without transferring authority to AI.

## Production composition rule

A repository script, test fixture, dormant service, or source file does not become Production behavior merely because it exists. Production authority requires explicit composition plus the appropriate capability/authorization contract.

Historical Home/game-rail/global-History/global-assistant behavior is not a fallback design. It is superseded when it conflicts with this document.
