# MO2 dual-pane ordering contract

Status: Locked product design

## Two independent orders

Grid must model Mod Organizer 2's left and right panes as different systems. They are related, but neither is a projection of the other.

The left pane is the mod installation and virtual-file priority order. It includes enabled and disabled mods, separators, foreign entries, mod metadata, conflict flags, categories, versions, notes, Nexus metadata, and the priority that determines winning loose files and mod-provided archives.

The right pane is the plugin activation and load order. It includes ESM, ESP, and ESL files, enabled state, load index, flags, masters, warnings, and the mod that currently provides each plugin file.

A mod may provide no plugins, one plugin, or many plugins. A plugin has one winning file provider but can have overridden copies in lower-priority mods. Grid therefore maintains explicit many-to-many provenance between left-pane mods, virtual files, archives, and right-pane plugins.

## Ordering authority

LOOT is the primary general sorter for enabled plugins. Grid does not replace LOOT with an AI-generated load order.

Grid may propose a post-LOOT constraint only when deterministic evidence proves that the general result is insufficient. Examples include a required master relationship, explicit compatible metadata, a generated-output placement contract, or a verified record-winning requirement. Every exception records its evidence, affected plugins, before and after positions, and inverse operation. Unproved preferences remain advisory.

Left-pane priority is not sorted by LOOT. Grid reasons about it from virtual-file winners, installer choices, mod-author compatibility requirements, generated-output ownership, and explicit user intent. Moving a mod in the left pane must never silently move its plugins in the right pane, and sorting plugins must never silently reorder mods.

## Workspace behavior

- Mods remain the primary left surface.
- Plugins, Archives, Data, Saves, and Downloads remain right-side tabs when supported by current evidence.
- Each pane has its own selection and exact-name search. Secondary filters and commands stay out of the persistent table footprint.
- Selecting an entry may cross-highlight related entries without changing either order.
- Filters never become evidence of absence and never change canonical order.
- Connected MO2 observations remain read-only until a separately authorized mutation is composed.
- A future authorized reorder previews the exact delta and is grouped into persistent Undo/Redo history.

## Density and responsive layout

The main workspace prioritizes the two working lists. Secondary provenance and diagnostic detail belongs in on-demand inspection rather than consuming permanent list width. At widths too small to keep both panes useful, the panes stack instead of clipping or extending beyond the workspace.

The default list columns are intentionally compact:

- Mods: enabled state, name/status, conflict, installed version, priority.
- Plugins: name, flags, source priority, and runtime mod index.

Workspace presentation state is profile-scoped and durable. Text searches, expanded separator sections, pane width, column widths, and the selected right-side tab are restored on the next session. A profile with no saved presentation starts with separator sections collapsed.

Additional metadata remains available through filters, selection details, provider chains, and context commands.

## Progression

Right-click commands become available only when their deterministic implementation and authority path exist. Unavailable actions are labeled as unavailable; placeholder commands must not pretend to mutate MO2. The intended progression is observation, preview, authorized execution, verification, and persistent Undo/Redo.
