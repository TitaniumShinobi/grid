# Grid provenance baseline patch

This patch adds Grid's first formal provenance baseline and release-refusal layer. It does not choose or assert Grid's final license.

## What changes

- Classifies Grid source areas while preserving the distinction between backward-compatible interoperability and upstream code derivation.
- Records the absence of Git history in the supplied 2026-09-16 snapshot.
- Requires exact provenance records for every governed source/document and asset.
- Declares current NuGet dependencies for license review.
- Blocks distribution builds until source, assets, dependencies, and the repository license are reviewed.
- Adds deterministic inventory, upstream whole-file comparison, and sanitized attorney-export tools.

## Verify after overlay

```powershell
Set-Location "C:\Users\woods\Documents\GitHub\grid"

powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  ".\eng\Test-Grid.ps1" `
  -Lane WindowsSource `
  -Configuration Debug
```

The `provenance-contracts` suite must pass. Existing Windows suites must continue to pass.

The strict distribution gate is expected to fail until the attorney review is complete:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  ".\eng\provenance\Test-GridProvenance.ps1" `
  -Mode Distribution
```

That expected failure prevents accidental commercial packaging; it is not a source-development failure.

## Create the attorney package

Choose a new output filename each time because the exporter refuses to overwrite an existing archive:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  ".\eng\provenance\Export-GridAttorneyAudit.ps1" `
  -OutputPath "$env:USERPROFILE\Downloads\grid-attorney-audit-2026-09-16.zip"
```

The exporter writes the ZIP directly without creating a staging tree. It excludes generated output, local scratch material, common credential files, and embedded high-confidence secret patterns. Because this repository currently has no commits, its export manifest will disclose that no Git history was available.
