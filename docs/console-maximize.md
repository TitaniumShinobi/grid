# GRID console maximize contract

When the Terminal / Output / Problems panel is maximized, it becomes the full interior work surface.

- Top bar remains visible.
- Collapsed 48 px rail remains visible.
- Footer remains visible.
- Left content panel, editor content, and assistant panel are hidden.
- The normal editor-content star row collapses to 0.
- The bottom-panel row expands to `*` and consumes the remaining interior height.
- Restoring the panel returns the editor-content row to `*` and restores the prior shell arrangement.
- Terminal state, selected bottom surface, and content are preserved.

UI automation asserts that the maximized bottom-panel surface occupies at least 80% of the window height, in addition to verifying that the left content and assistant panels are not exposed.
