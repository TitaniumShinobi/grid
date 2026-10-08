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
- From a panel interior edge to its shared tab-and-controls header row: 3 px on the left, top, and right
- Between adjacent tab cards: 3 px

The 5 px content inset applies on every side where content approaches a panel outline. In particular, workstation table windows and the rightmost Run control must not bypass it. A tab strip belongs to its containing panel; a table window begins at its column-header row beneath that strip.

Main, Chat, Console, and plugin tabs live inside the surface they control. The first tab begins exactly 3 DIP from the surface interior left, and the shared header row begins exactly 3 DIP from the surface interior top. Where the row has right-side controls, its final control ends exactly 3 DIP from the surface interior right. Tabs and controls share one row and their vertical centers differ by no more than 1 DIP. A surface with no right-side controls does not invent them. Chat and Console own one full-row separator immediately beneath the unified header row; they do not position tabs and controls as separate rows. Negative margins and clipping are not valid ways to satisfy this contract.

The workstation context row owns the 5 px clearance beneath its controls. The workspace page does not add a second top inset below that row; its table windows extend upward to that shared clearance boundary.

The workspace page defines one authoritative content rectangle with real 5 px left and right layout columns and a real 5 px bottom row. Responsive pane widths are calculated against that same rectangle. Child tables and filter fields may not draw into or beyond it, and clipping is not a substitute for the reserved space.

The terminal 8 px shell column is shared by the main workbench, assistant/chat panel, and normal or maximized Console. Their one-pixel outline must finish before that column so the complete stroke and corner remain visibly separated from the Win32 client edge.

The environment/plugin window outline includes its refresh-control row and ends exactly 5 px above its filter field.

Each mod/plugin window footer presents its authoritative active count LCD as its leftmost element and its compact, right-aligned filter at the opposite edge. No visible label precedes the LCD; the accessible names remain `Active mods` and `Active plugins`. Counts use Grid's seven-segment numeric display with a fixed five-digit mod capacity and four-digit plugin capacity. In side-by-side mode each count aligns with its own window; in stacked mode it remains under that same window at the main-panel left edge.

Workstation filter fields are compact controls capped at 280 px. They remain right-aligned and may shrink below that cap when a narrow or stacked allocation cannot accommodate the preferred width.

In the wide workstation, the mod pane begins five pixels below the main editor-tab row and aligns with the tool row. The tool context bar owns only its right-aligned controls so it cannot paint over or intercept the visible mod header sharing that row. The plugin pane remains below the tool row. This deliberate asymmetry gives the grouped mod workspace more vertical space. Stacked layouts do not overlap the tool row.

The environment tab header and body share the right pane's allocated column. The visible first tab follows the universal 3 DIP interior-header inset; only the internal TabView header list is corrected for WinUI's built-in lead spacing. The pane owns one exterior outline and upper-right radius, while TabView's native line remains an internal tab/body divider. A tab-header offset may not cross the center divider or extend the pane beyond the authoritative content rectangle.

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

Game navigation is intentionally split: a Home game card opens only that game's workstation in the main panel, while an activity-rail game thumbnail opens only that game's snapshot in the side panel. The snapshot owns an explicit `Open workstation` action. Neither entry point implicitly opens the other surface.

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
