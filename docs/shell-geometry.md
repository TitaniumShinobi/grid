# GRID shell geometry contract

This document records the normalized GRID shell geometry introduced by the shell-normalization pass.

## Fixed shell dimensions

- Top bar: 38 px
- Footer/status bar: 28 px
- Collapsed activity/game rail: 48 px
- Default left content panel: 286 px
- Left resize gutter: 5 px
- Default assistant panel: 360 px
- Assistant resize gutter: 5 px
- Default bottom panel: 220 px
- Bottom resize gutter: 5 px

The top bar and footer are shell chrome, not bordered panels. The left rail is shell chrome, not a bordered panel.

## Canonical gutter and inset law

These values are architectural tokens, not page-level styling choices:

- Right edge of the shell to the final panel: 8 px
- Between peer shell panels: 4.25 px
- From a panel outline to its controls and child windows: 5 px (`ShellPanelContentInset`)
- From a panel outline to a floating tab card, and between adjacent tab cards: 3 px

The 5 px content inset applies on every side where content approaches a panel outline. In particular, workstation table windows and the rightmost Run control must not bypass it. A tab strip belongs to its containing panel; a table window begins at its column-header row beneath that strip.

The workstation context row owns the 5 px clearance beneath its controls. The workspace page does not add a second top inset below that row; its table windows extend upward to that shared clearance boundary.

The environment/plugin window outline includes its refresh-control row and ends exactly 5 px above its filter field.

The environment refresh action lives in the workstation context row immediately left of the tool selector. It invokes the same bounded read-only refresh operation as the environment view.

All Grid table headers use one rule: left-aligned, vertically centered, single-line text. Header labels reserve clearance from column resize handles and trim only when the usable column width is genuinely exhausted.

Workbench tables use a shared spreadsheet contract:

- The panel, table header, table body, filter fields, and separator rows share the workbench surface color.
- Header cells own hard bottom and vertical dividers; resize hit targets are transparent overlays on those dividers.
- Body cells own softer dividers at exactly the same column coordinates and use 5 px horizontal content padding.
- Workstation header, data, and section rows are 28 px high.
- Textual values align left; numeric and date/time values align right. Alignment never removes the 5 px cell padding.
- The existing em-dash null marker is centered; ordinary textual values are not.
- Flags columns are centered so one or more visible flag symbols remain grouped in the middle of their cell.
- Section/separator rows span the table without vertical dividers or rounded cards.
- Mod rows remain indented beneath their section row, and the checkbox-to-name clearance follows the 5 px content inset.

Popup selectors open edge-aligned immediately beneath their owning control. They must not align to the top of the application shell.

## Interior panel contract

Interior work surfaces fill the rectangle between the top bar and footer. Their child controls and windows observe the canonical 5 px panel-content inset above.

The default arrangement is:

- 48 px rail
- optional left content panel
- main content panel
- optional assistant panel

Only interior panel separators remain. Redundant hard borders on the top bar, rail, side panels, and footer are removed.

## Activity/game rail contract

The upper rail is reserved for games and onboarding:

1. Add game
2. Registered game icons, in an independently scrollable region

The fixed lower rail is:

1. Search
2. Explorer
3. Activity
4. Settings

Settings remains pinned at the bottom. A first-run user with no registered games sees only Add game in the upper rail.

The GRID `G` in the top bar is the canonical Home control and focuses/opens the singleton Home tab.

## Explorer contract

Explorer is a read-only projection of the selected backing installation. It enumerates the real local instance root and expands directories on demand. It does not mutate, move, rename, or launch external files.

## Bottom panel contract

Terminal / Output / Problems remains the bottom panel.

The panel header exposes a maximize/restore toggle. Maximized bottom-panel mode:

- preserves the top bar, 48 px rail, and footer;
- hides the left content panel, normal editor content, and assistant panel;
- fills the remaining interior shell rectangle with the selected bottom-panel surface;
- restores the previous shell arrangement when toggled off.

## Taskboard

This pass does not change Taskboard semantics or its four equal lanes.
