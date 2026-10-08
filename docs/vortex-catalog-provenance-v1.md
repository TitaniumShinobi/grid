# GRID Vortex catalog provenance

GRID may derive normalized catalog facts from a frozen Vortex upstream, but must not
treat upstream catalog membership as evidence that a game is installed or connected.

## Frozen bundled source

- Vortex v2.6.3
- commit `aa459b9499224bde07e5119d715b463bfae92c5e`
- 86 bundled `extensions/games/game-*` packages
- all 86 package manifests declare GPL-3.0

The normalized artifact records package-level names and provenance only. It does not
vendor Vortex JavaScript, UI, artwork, or runtime state.

## Identity

GRID-native canonical identities win over imported upstream keys. Editions that GRID
models distinctly must remain distinct even if an upstream source groups them.

## State boundary

Catalog entry != installed game.
Installed game != connected game.
Connected game/profile requires GRID's existing validated connection workflow.
