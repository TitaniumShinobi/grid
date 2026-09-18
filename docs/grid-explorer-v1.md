# GRID Explorer v1

GRID Explorer adopts CODE's Explorer interaction grammar without sharing CODE source or repo semantics.

## Contract

- Explorer is a first-class GRID left-panel surface.
- It is contextual to the selected connected game/manager/profile.
- Top-level roots are semantic concerns, not an arbitrary machine root.
- Current MO2 roots are Manager, Mods, Downloads, Overwrite, Profile/Profiles, Saves, and Tools when deterministically available.
- Roots and filesystem children expand lazily.
- Collapse folders collapses the entire visible tree.
- Refresh rebuilds the contextual roots from current GRID selection/state.
- Selection exposes the exact backing path at the bottom of the panel.
- Explorer remains read-only. It does not rename, move, delete, launch, or mutate external files.
- Missing conventional roots are omitted rather than fabricated.
- The generic controller/game media work is outside this pass.
- Custom MO2 roots that are not represented in the current catalog remain a later exact-path projection improvement; v1 never guesses a nonexistent custom path.

## CODE reference translated

From CODE, GRID adopts:
- hierarchical file/folder tree;
- lazy expansion;
- explicit collapse-all;
- explicit refresh;
- selected-row/path state;
- clean contextual root;
- separation between shell sidebar and Explorer content.

GRID does not adopt CODE's arbitrary repository-root model. The selected game/profile remains the authority for Explorer scope.
