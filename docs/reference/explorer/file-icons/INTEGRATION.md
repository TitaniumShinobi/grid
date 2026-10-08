# GRID integration

Use `CodeFileIconResolver.FromJson` with the packaged `source/code-file-icon-map.json`. On each authorized Explorer **file** entry, call `SvgFileNameForFile(relativePath)` and resolve that filename under GRID's packaged `assets/` directory. Load SVG with GRID's native WinUI image pipeline. Never interpret this mapping as permission to read a file; the Explorer's authorized-root service owns that boundary.

Do not call the resolver on folders if the goal is exact CODE behavior. Render GRID's ordinary disclosure chevron for folders. The upstream package offers folder and open-folder icons, but CODE does not use them in the inspected Explorer path.

When an image thumbnail feature is added, CODE's precedence is: show Material icon immediately, then replace it with a successfully loaded thumbnail for supported media extensions. Keep thumbnail decoding bounded and under GRID's file authority; it is not included in this icon ZIP.

Preserve the included MIT license in any GRID distribution that ships these SVGs or substantial mapping material. A practical attribution line is: “File icons from Material Icon Theme © 2025 Material Extensions, MIT License.” Keep the full `LICENSE` with the shipped assets, e.g. in third-party notices. The package's MIT grant permits copying and redistribution, including commercial distribution, on those notice terms. Separately review brand/trademark use and any GRID store-distribution requirements; this handoff is not a legal opinion.

Theme note: this package reproduces CODE's base file-icon mapping only. It intentionally does not switch to upstream `_light` or high-contrast variants. If GRID requires accessible light/high-contrast icons, add those variants deliberately as a separately reviewed enhancement, rather than silently claiming parity.

Before shipping on Windows, compile the C# resolver against .NET 9, validate representative mappings (`README.md`, `package.json`, `.tsx`, compound extensions, unknown files), verify SVG rendering in WinUI, check attribution in the distributed app, and confirm no CODE branding or external file-launch behavior was introduced.
