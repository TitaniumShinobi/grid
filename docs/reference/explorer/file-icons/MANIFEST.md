# GRID file-icon handoff v1 manifest

- `assets/*.svg`: 580 SVG files actually reachable through CODE's file-icon resolver with Material Icon Theme 5.33.1, React icon pack. No folder SVGs are included because CODE's Explorer folder rows render chevrons, not folder icons.
- `source/code-file-icon-map.json`: generated file-name, extension, language, icon-id-to-SVG maps, CODE's four filename overrides, exact longest-extension order, and package metadata.
- `source/CodeFileIconResolver.cs`: dependency-free .NET adaptation of CODE's file-icon selection order. GRID supplies its own asset loader and native image control.
- `LICENSE`: unmodified MIT license shipped in the installed upstream package.
- `PROVENANCE.md`: source and selection evidence.
- `INTEGRATION.md`: GRID-specific handoff boundary and licensing checklist.

No CODE shell, React component, Vite bundle, folder icon, media thumbnail, or non-icon asset is included. The ZIP is a handoff, not a compiled WinUI package.
