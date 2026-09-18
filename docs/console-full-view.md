# GRID Console full-view contract

Console full view mirrors assistant full view at the shell level.

- The full Console is a direct `WorkspaceHost` child in center column 3.
- It fills that center workspace vertically from immediately below the 38 px top bar to immediately above the 28 px footer.
- It does not stretch the ordinary 220 px bottom row.
- The 48 px rail remains usable.
- The left content panel remains independently toggleable.
- The right assistant panel remains independently toggleable.
- Closing a side panel releases horizontal space back to the center workspace.
- Restore returns the ordinary editor workspace and docked bottom panel.
- Terminal / Output / Problems state and text are synchronized between docked and full views.
